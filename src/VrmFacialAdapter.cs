using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using UnityEngine;

namespace EcstasyEngineer.AvatarReplacement
{
    // Prepared VRM0 bindings exported by UniVRM in the Editor. No avatar scripts
    // or arbitrary file commands are executed by this runtime adapter.
    internal sealed class VrmFacialAdapter : IDisposable
    {
        [DataContract] internal sealed class Profile
        {
            [DataMember(IsRequired = true)] public int schemaVersion { get; set; }
            [DataMember(IsRequired = true)] public string mapping { get; set; }
            [DataMember(IsRequired = true)] public string modelId { get; set; }
            [DataMember(IsRequired = true)] public string bundleSha256 { get; set; }
            [DataMember] public string sourceVrmSha256 { get; set; }
            [DataMember(IsRequired = true)] public Expression[] expressions { get; set; }
            [DataMember] public string[] unsupportedFeatures { get; set; }
        }
        [DataContract] internal sealed class Expression
        {
            [DataMember(IsRequired = true)] public string semantic { get; set; }
            [DataMember] public bool isBinary { get; set; }
            [DataMember] public string provenance { get; set; }
            [DataMember(IsRequired = true)] public Morph[] binds { get; set; }
        }
        [DataContract] internal sealed class Morph
        {
            [DataMember(IsRequired = true)] public string path { get; set; }
            [DataMember(IsRequired = true)] public int index { get; set; }
            [DataMember(IsRequired = true)] public float weight { get; set; }
        }
        private sealed class Target
        {
            internal SkinnedMeshRenderer renderer;
            internal Mesh mesh;
            internal int index;
            internal float rest, sum, min;
        }
        private sealed class ResolvedExpression
        {
            internal FacialChannel channel;
            internal bool binary;
            internal Target[] targets;
            internal float[] gains;
        }
        private readonly SkinnedMeshRenderer source;
        private readonly Mesh sourceMesh;
        private readonly int[] sourceIndices;
        private readonly Dictionary<string, float> inputs = NativeFacialInput.RequiredSourceNames.ToDictionary(n => n, n => 0f);
        private readonly FacialIntent intent = new FacialIntent();
        private readonly List<Target> targets = new List<Target>();
        private readonly List<ResolvedExpression> expressions = new List<ResolvedExpression>();
        private readonly Transform jaw;
        private readonly Quaternion jawRest;
        private readonly bool hasAuthoredSurprise;
        private readonly ResolvedExpression blink;
        private bool disposed;
        internal string Status { get; }

        internal static Profile ReadProfile(string bundlePath)
        {
            Profile profile;
            using (var stream = File.OpenRead(bundlePath + ".vrmface.json"))
            {
                if (stream.Length < 1 || stream.Length > 1024 * 1024) throw new InvalidDataException("VRM face profile exceeds size budget.");
                profile = (Profile)new DataContractJsonSerializer(typeof(Profile), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 20000 }).ReadObject(stream);
            }
            string hash;
            using (var stream = File.OpenRead(bundlePath)) using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            Validate(profile, hash);
            return profile;
        }
        internal static void Validate(Profile profile, string hash)
        {
            if (profile == null || profile.schemaVersion != 1 || (profile.mapping != "vrm0-presets-v1" && profile.mapping != "vrm1-presets-v1") ||
                string.IsNullOrWhiteSpace(profile.modelId) || profile.modelId.Length > 128 ||
                hash == null || hash.Length != 64 || !string.Equals(profile.bundleSha256, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported or mismatched VRM face profile.");
            if (profile.expressions == null || profile.expressions.Length > 64) throw new InvalidDataException("Invalid VRM expression count.");
            var supported = new HashSet<string>(FacialIntent.Channels.Select(c => c.Id), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var expression in profile.expressions)
            {
                if (expression == null || !supported.Contains(expression.semantic) || !seen.Add(expression.semantic) || expression.binds == null || expression.binds.Length > 256)
                    throw new InvalidDataException("Unsupported, duplicate or oversized VRM expression.");
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bind in expression.binds)
                {
                    if (bind == null || string.IsNullOrWhiteSpace(bind.path) || bind.path.Length > 2048 || bind.path.Any(char.IsControl) ||
                        bind.index < 0 || bind.index > 65535 || !Finite(bind.weight) || bind.weight < 0 || bind.weight > 100 ||
                        !unique.Add(bind.path + "\n" + bind.index)) throw new InvalidDataException("Invalid or duplicate VRM morph binding.");
                }
            }
        }
        internal static float EffectiveWeight(double value, bool binary)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            value = Math.Max(0, Math.Min(1, value));
            return binary ? (value > .5 ? 1 : 0) : (float)value;
        }

        // Gains for the native-face recipes below, tuned from side-by-side comparisons (2026-10-31).
        // Intended eyelid order, most to least closed: joy (authored) > disgust > relaxed = furious
        // > neutral > surprised.
        internal const double DisgustMouthU = .2, DisgustLidClosure = .55, RelaxedLidClosure = .45,
            AngryLidClosure = .45, AngryBrowDown = .3, SurpriseFallbackMouthO = .55, SurpriseLidOpening = .2;

        // The game's emotion channels are composite faces; VRM presets only cover part of each:
        // - ALL_Sorrow reads as disgust: furrowed brow, squint, small pursed open mouth. The sorrow
        //   preset supplies brow and frown; partial blink squints and a little U purses the mouth.
        // - ALL_Fun is a relaxed face with half-lidded "bedroom" eyes; relaxed/fun presets rarely
        //   lower the lids, so partial blink is added.
        // - ALL_Angry narrows the eyes like relaxed and pulls the brows down further (brows.angry
        //   only reaches models with brow clips, e.g. ARKit-named BrowDown*).
        // - ALL_Surprised opens the mouth and the eyes. Models without an authored surprise get an
        //   O mouth and, through LidOpening, lids raised past rest; authored surprise is left alone.
        internal static void ApplyNativeRecipes(FacialIntent intent, bool hasAuthoredSurprise)
        {
            intent[FacialChannel.MouthU] += DisgustMouthU * intent[FacialChannel.EmotionSorrow];
            intent[FacialChannel.BlinkBoth] += DisgustLidClosure * intent[FacialChannel.EmotionSorrow]
                + RelaxedLidClosure * intent[FacialChannel.EmotionFun] + AngryLidClosure * intent[FacialChannel.EmotionAngry];
            intent[FacialChannel.BrowsAngry] += AngryBrowDown * intent[FacialChannel.EmotionAngry];
            if (!hasAuthoredSurprise) intent[FacialChannel.MouthO] += SurpriseFallbackMouthO * intent[FacialChannel.EmotionSurprised];
        }

        // Fraction of the blink shape to drive negative (lids above rest). Intent channels are 0..1,
        // so this is applied by the adapter directly to the blink morphs.
        internal static double LidOpening(FacialIntent intent, bool hasAuthoredSurprise) =>
            hasAuthoredSurprise ? 0 : SurpriseLidOpening * intent[FacialChannel.EmotionSurprised];

        internal static VrmFacialAdapter Bind(Animator original, Animator replacement, Profile profile) => new VrmFacialAdapter(original, replacement, profile);
        private VrmFacialAdapter(Animator original, Animator replacement, Profile profile)
        {
            if (!original || !replacement || original == replacement || original.transform.IsChildOf(replacement.transform) || replacement.transform.IsChildOf(original.transform))
                throw new InvalidOperationException("VRM face requires distinct model roots.");
            var sources = original.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r && r.name == "Face" && r.sharedMesh &&
                NativeFacialInput.RequiredSourceNames.All(n => r.sharedMesh.GetBlendShapeIndex(n) >= 0)).ToArray();
            if (sources.Length != 1) throw new InvalidOperationException("Expected exactly one native face source.");
            source = sources[0]; sourceMesh = source.sharedMesh;
            sourceIndices = NativeFacialInput.RequiredSourceNames.Select(sourceMesh.GetBlendShapeIndex).ToArray();
            var renderers = replacement.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var byBinding = new Dictionary<string, Target>(StringComparer.Ordinal);
            foreach (var expression in profile.expressions)
            {
                var resolved = new ResolvedExpression { channel = FacialIntent.Channels.Single(c => c.Id == expression.semantic).Channel,
                    binary = expression.isBinary, targets = new Target[expression.binds.Length], gains = new float[expression.binds.Length] };
                for (var i = 0; i < expression.binds.Length; i++)
                {
                    var bind = expression.binds[i];
                    var key = bind.path + "\n" + bind.index;
                    if (!byBinding.TryGetValue(key, out var target))
                    {
                        var matches = renderers.Where(r => RelativePath(r.transform, replacement.transform) == bind.path).ToArray();
                        if (matches.Length != 1 || !matches[0].sharedMesh || bind.index >= matches[0].sharedMesh.blendShapeCount)
                            throw new InvalidDataException("VRM binding does not uniquely resolve in the prepared model.");
                        target = new Target { renderer = matches[0], mesh = matches[0].sharedMesh, index = bind.index, rest = matches[0].GetBlendShapeWeight(bind.index) };
                        if (!Finite(target.rest)) throw new InvalidDataException("Nonfinite initial VRM morph.");
                        byBinding.Add(key, target); targets.Add(target);
                    }
                    resolved.targets[i] = target; resolved.gains[i] = bind.weight;
                }
                expressions.Add(resolved);
            }
            if (targets.Count == 0) throw new InvalidDataException("VRM profile has no bound facial morphs.");
            jaw = !original.GetBoneTransform(HumanBodyBones.Jaw) ? replacement.GetBoneTransform(HumanBodyBones.Jaw) : null;
            if (jaw) jawRest = jaw.localRotation;
            hasAuthoredSurprise = profile.expressions.Any(e => e.semantic == "emotion.surprised");
            // Only blink morphs may go below zero, and only as far as the surprise lid opening reaches.
            blink = expressions.FirstOrDefault(e => e.channel == FacialChannel.BlinkBoth);
            if (blink != null && !hasAuthoredSurprise)
                for (var i = 0; i < blink.targets.Length; i++)
                    blink.targets[i].min = Math.Min(blink.targets[i].min, -(float)SurpriseLidOpening * blink.gains[i]);
            Status = "VRM authored face bindings: " + expressions.Count + " channels. " +
                (!hasAuthoredSurprise ? "Surprise uses an open-mouth fallback. " : "") +
                (profile.expressions.Any(e => e.provenance != null && e.provenance.StartsWith("inferred", StringComparison.Ordinal)) ? "Includes labeled VRoid-name fallbacks. " : "") +
                "Missing channels are inactive; secondary motion and custom material effects are not transferred.";
        }
        internal void Apply()
        {
            if (disposed || !source || source.sharedMesh != sourceMesh) throw new InvalidOperationException("VRM facial source was destroyed or changed.");
            for (var i = 0; i < sourceIndices.Length; i++) inputs[NativeFacialInput.RequiredSourceNames[i]] = source.GetBlendShapeWeight(sourceIndices[i]);
            NativeFacialInput.ReadInto(inputs, intent);
            ApplyNativeRecipes(intent, hasAuthoredSurprise);
            foreach (var target in targets) target.sum = 0;
            foreach (var expression in expressions)
            {
                var value = EffectiveWeight(intent[expression.channel], expression.binary);
                for (var i = 0; i < expression.targets.Length; i++) expression.targets[i].sum += expression.gains[i] * value;
            }
            var opening = (float)LidOpening(intent, hasAuthoredSurprise);
            if (blink != null) for (var i = 0; i < blink.targets.Length; i++) blink.targets[i].sum -= blink.gains[i] * opening;
            if (jaw) jaw.localRotation = jawRest;
            foreach (var target in targets)
            {
                if (!target.renderer || target.renderer.sharedMesh != target.mesh) throw new InvalidOperationException("VRM facial destination changed.");
                target.renderer.SetBlendShapeWeight(target.index, Mathf.Clamp(target.sum, target.min, 100));
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var target in targets) if (target.renderer && target.renderer.sharedMesh == target.mesh) target.renderer.SetBlendShapeWeight(target.index, target.rest);
            if (jaw) jaw.localRotation = jawRest;
        }
        private static string RelativePath(Transform value, Transform root)
        {
            if (value == root) return ".";
            var parts = new List<string>();
            while (value && value != root) { parts.Add(value.name); value = value.parent; }
            if (value != root) throw new InvalidOperationException("Renderer outside model root.");
            parts.Reverse(); return string.Join("/", parts);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
