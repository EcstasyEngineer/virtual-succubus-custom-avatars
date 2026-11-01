using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace EcstasyEngineer.AvatarReplacement
{
    // Ani-only provisional mapping. The caller MUST verify bundle/profile eligibility,
    // opt in, and Bind before the first HumanPose write to the fresh clone.
    // Apply belongs immediately after CopyPose on the Unity main thread.
    internal sealed class AniFacialAdapter : IDisposable
    {
        internal const string ApprovedBundleSha256 = "d3d3319bfb54bad1a72662425c72fac1552b1b94b9441aac3927008a5c0bd195";
        internal const string SourcePrefix = NativeFacialInput.Prefix;
        internal const string CalibrationStatus = "Anger/surprise gains reviewed in offline frontal Model 01 renders only; no full runtime calibration. Blink, anger, fun, joy, sorrow, surprise and vowels only. Gaze, teeth and other expression channels are unmapped.";
        internal const string AggregationRules = "Inputs: missing/nonfinite=0, otherwise clamp to [0,100]. Add ordinary gain contributions without intermediate output clamping. Per side, d=sum(smile)-sum(frown); smile=max(d,0), frown=max(-d,0). jawOpen=max(sum(vowel contributions),0.10*Joy,0.40*Surprised). Finally clamp each owned output to [0,100]. mouthClose=0. ALL_Neutral is ignored and never suppresses another expression. Unowned channels, including teeth, are never written.";

        internal sealed class MappingEntry
        {
            public string Source { get; }
            public FacialChannel Channel { get; }
            public string Target { get; }
            public float Gain { get; }
            public string Aggregation { get; }
            internal int TargetIndex { get; }
            internal bool JawMaximumCandidate { get; }
            internal MappingEntry(FacialChannel channel, string target, float gain, bool jawMaximumCandidate = false)
            {
                Channel = channel; Source = NativeFacialInput.SourceName(channel); Target = target; Gain = gain;
                TargetIndex = Array.IndexOf(TargetNames, target); JawMaximumCandidate = jawMaximumCandidate;
                Aggregation = jawMaximumCandidate ? "jaw maximum candidate" : target == "jawOpen" ? "vowel sum candidate for jaw maximum" :
                    target.StartsWith("mouthSmile", StringComparison.Ordinal) || target.StartsWith("mouthFrown", StringComparison.Ordinal) ? "sum then signed smile-minus-frown arbitration" : "sum then output clamp";
            }
        }

        private static readonly string[] TargetNames = {
            "browDownLeft", "browDownRight", "eyeSquintLeft", "eyeSquintRight", "eyeBlinkLeft", "eyeBlinkRight",
            "mouthFrownLeft", "mouthFrownRight", "jawOpen", "mouthClose", "mouthFunnel", "mouthPucker", "mouthStretchLeft", "mouthStretchRight",
            "mouthSmileLeft", "mouthSmileRight", "browOuterUpLeft", "browOuterUpRight", "browInnerUp", "eyeWideLeft", "eyeWideRight"
        };
        private static readonly int JawIndex = Array.IndexOf(TargetNames, "jawOpen");
        private static readonly int SmileLeftIndex = Array.IndexOf(TargetNames, "mouthSmileLeft"), SmileRightIndex = Array.IndexOf(TargetNames, "mouthSmileRight");
        private static readonly int FrownLeftIndex = Array.IndexOf(TargetNames, "mouthFrownLeft"), FrownRightIndex = Array.IndexOf(TargetNames, "mouthFrownRight");
        private static readonly ReadOnlyCollection<MappingEntry> MappingTable = Array.AsReadOnly(new[] {
            // Whole-face anger and the specialized channels contribute additively.
            // Anger/surprise adjusted from offline Model 01 frontal comparisons only.
            // Runtime/multi-angle calibration remains open.
            new MappingEntry(FacialChannel.EmotionAngry, "browDownLeft", .80f), new MappingEntry(FacialChannel.EmotionAngry, "browDownRight", .80f),
            new MappingEntry(FacialChannel.EmotionAngry, "eyeSquintLeft", .50f), new MappingEntry(FacialChannel.EmotionAngry, "eyeSquintRight", .50f),
            new MappingEntry(FacialChannel.EmotionAngry, "mouthFrownLeft", .70f), new MappingEntry(FacialChannel.EmotionAngry, "mouthFrownRight", .70f),
            new MappingEntry(FacialChannel.BrowsAngry, "browDownLeft", .80f), new MappingEntry(FacialChannel.BrowsAngry, "browDownRight", .80f),
            new MappingEntry(FacialChannel.EyesAngry, "eyeSquintLeft", .50f), new MappingEntry(FacialChannel.EyesAngry, "eyeSquintRight", .50f),
            new MappingEntry(FacialChannel.MouthAngry, "mouthFrownLeft", .70f), new MappingEntry(FacialChannel.MouthAngry, "mouthFrownRight", .70f),
            new MappingEntry(FacialChannel.BlinkBoth, "eyeBlinkLeft", 1f), new MappingEntry(FacialChannel.BlinkBoth, "eyeBlinkRight", 1f),
            new MappingEntry(FacialChannel.BlinkLeft, "eyeBlinkLeft", 1f), new MappingEntry(FacialChannel.BlinkRight, "eyeBlinkRight", 1f),
            new MappingEntry(FacialChannel.MouthA, "jawOpen", .65f),
            new MappingEntry(FacialChannel.MouthI, "jawOpen", .15f), new MappingEntry(FacialChannel.MouthI, "mouthStretchLeft", .50f), new MappingEntry(FacialChannel.MouthI, "mouthStretchRight", .50f),
            new MappingEntry(FacialChannel.MouthU, "jawOpen", .20f), new MappingEntry(FacialChannel.MouthU, "mouthPucker", .65f),
            new MappingEntry(FacialChannel.MouthE, "jawOpen", .35f), new MappingEntry(FacialChannel.MouthE, "mouthStretchLeft", .40f), new MappingEntry(FacialChannel.MouthE, "mouthStretchRight", .40f),
            new MappingEntry(FacialChannel.MouthO, "jawOpen", .45f), new MappingEntry(FacialChannel.MouthO, "mouthFunnel", .65f),
            new MappingEntry(FacialChannel.EmotionFun, "mouthSmileLeft", .35f), new MappingEntry(FacialChannel.EmotionFun, "mouthSmileRight", .35f),
            new MappingEntry(FacialChannel.EmotionFun, "browOuterUpLeft", .10f), new MappingEntry(FacialChannel.EmotionFun, "browOuterUpRight", .10f),
            new MappingEntry(FacialChannel.EmotionJoy, "mouthSmileLeft", .50f), new MappingEntry(FacialChannel.EmotionJoy, "mouthSmileRight", .50f),
            new MappingEntry(FacialChannel.EmotionJoy, "eyeSquintLeft", .20f), new MappingEntry(FacialChannel.EmotionJoy, "eyeSquintRight", .20f),
            new MappingEntry(FacialChannel.EmotionJoy, "jawOpen", .10f, true),
            new MappingEntry(FacialChannel.EmotionSorrow, "mouthFrownLeft", .40f), new MappingEntry(FacialChannel.EmotionSorrow, "mouthFrownRight", .40f),
            new MappingEntry(FacialChannel.EmotionSorrow, "browInnerUp", .25f),
            new MappingEntry(FacialChannel.EmotionSurprised, "eyeWideLeft", .50f), new MappingEntry(FacialChannel.EmotionSurprised, "eyeWideRight", .50f),
            new MappingEntry(FacialChannel.EmotionSurprised, "browOuterUpLeft", .30f), new MappingEntry(FacialChannel.EmotionSurprised, "browOuterUpRight", .30f),
            new MappingEntry(FacialChannel.EmotionSurprised, "browInnerUp", .15f), new MappingEntry(FacialChannel.EmotionSurprised, "jawOpen", .40f, true)
        });
        // mouthClose is owned/reset to zero, not guessed compensation. Prefab-rest
        // jaw restoration handles the absent source Jaw channel independently.
        internal static IReadOnlyList<MappingEntry> Mapping => MappingTable;
        internal static IReadOnlyList<string> OwnedTargetNames { get; } = Array.AsReadOnly(TargetNames);
        internal static IReadOnlyList<string> RequiredSourceNames => NativeFacialInput.RequiredSourceNames;

        private readonly SkinnedMeshRenderer source, target;
        private readonly Mesh sourceMesh, targetMesh;
        private readonly Transform jaw;
        private readonly Quaternion jawRestRotation;
        private readonly Dictionary<string, int> sourceIndices, targetIndices;
        private readonly Dictionary<string, float> originalWeights;
        private readonly Dictionary<string, float> inputBuffer = RequiredSourceNames.ToDictionary(n => n, n => 0f, StringComparer.Ordinal);
        private readonly Dictionary<string, float> outputBuffer = TargetNames.ToDictionary(n => n, n => 0f, StringComparer.Ordinal);
        private readonly double[] sumBuffer = new double[TargetNames.Length];
        private readonly FacialIntent intentBuffer = new FacialIntent();
        private bool disposed;
        private int lastStatusInvalidCount = -1;
        internal string Status { get; private set; } = "Bound; provisional mapping has not yet been applied.";
        internal int LastNonFiniteInputCount { get; private set; }

        private AniFacialAdapter(SkinnedMeshRenderer source, SkinnedMeshRenderer target, Transform jaw,
            Dictionary<string, int> sourceIndices, Dictionary<string, int> targetIndices)
        {
            this.source = source; this.target = target; this.jaw = jaw;
            sourceMesh = source.sharedMesh; targetMesh = target.sharedMesh;
            this.sourceIndices = sourceIndices; this.targetIndices = targetIndices;
            jawRestRotation = jaw.localRotation;
            if (!Finite(jawRestRotation.x) || !Finite(jawRestRotation.y) || !Finite(jawRestRotation.z) || !Finite(jawRestRotation.w))
                throw new InvalidOperationException("Ani prefab-rest jaw rotation is not finite.");
            originalWeights = targetIndices.ToDictionary(p => p.Key, p => target.GetBlendShapeWeight(p.Value), StringComparer.Ordinal);
            if (originalWeights.Values.Any(w => !Finite(w))) throw new InvalidOperationException("Ani initial owned blendshape weights are not finite.");
        }

        internal static AniFacialAdapter Bind(Animator sourceAnimator, Animator targetAnimator)
        {
            if (!sourceAnimator || !targetAnimator || sourceAnimator == targetAnimator)
                throw new ArgumentException("Distinct source and replacement Animators are required.");
            if (sourceAnimator.transform.IsChildOf(targetAnimator.transform) || targetAnimator.transform.IsChildOf(sourceAnimator.transform))
                throw new InvalidOperationException("Source and replacement hierarchies must be separate.");
            if (!sourceAnimator.isHuman || !targetAnimator.isHuman || !sourceAnimator.avatar || !targetAnimator.avatar ||
                !sourceAnimator.avatar.isValid || !targetAnimator.avatar.isValid)
                throw new InvalidOperationException("Both facial-adapter Animators require valid Humanoid avatars.");
            if (sourceAnimator.GetBoneTransform(HumanBodyBones.Jaw))
                throw new InvalidOperationException("Source unexpectedly has a Jaw bone; Ani rest-jaw policy must be reviewed before use.");
            var jaw = targetAnimator.GetBoneTransform(HumanBodyBones.Jaw);
            if (!jaw || !jaw.IsChildOf(targetAnimator.transform)) throw new InvalidOperationException("Ani target Jaw bone is missing or outside the clone.");
            var sources = sourceAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r && r.name == "Face" && ContainsAll(r.sharedMesh, RequiredSourceNames)).ToArray();
            var targets = targetAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r && r.sharedMesh && r.sharedMesh.name == "Head" && ContainsAll(r.sharedMesh, TargetNames)).ToArray();
            if (sources.Length != 1) throw new InvalidOperationException("Expected one source Face renderer with every required exact expression channel; found " + sources.Length + ".");
            if (targets.Length != 1) throw new InvalidOperationException("Expected one target Head mesh with every required ARKit channel; found " + targets.Length + ".");
            return new AniFacialAdapter(sources[0], targets[0], jaw,
                ExactIndices(sources[0].sharedMesh, RequiredSourceNames), ExactIndices(targets[0].sharedMesh, TargetNames));
        }

        // Pure map: inputs and outputs use Unity 0..100 weight units. Missing and
        // nonfinite inputs become zero. AggregationRules documents expression arbitration.
        // Returns every owned channel each frame, so an ended blink/anger cannot stick.
        internal static Dictionary<string, float> Evaluate(IReadOnlyDictionary<string, float> inputs)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            var output = TargetNames.ToDictionary(name => name, name => 0f, StringComparer.Ordinal);
            EvaluateInto(inputs, new FacialIntent(), output, new double[TargetNames.Length]);
            return output;
        }

        internal static Dictionary<string, float> EvaluateIntent(FacialIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            var output = TargetNames.ToDictionary(name => name, name => 0f, StringComparer.Ordinal);
            EvaluateIntentInto(intent, output, new double[TargetNames.Length]);
            return output;
        }

        private static void EvaluateInto(IReadOnlyDictionary<string, float> inputs, FacialIntent intent, Dictionary<string, float> output, double[] sums)
        {
            NativeFacialInput.ReadInto(inputs, intent);
            EvaluateIntentInto(intent, output, sums);
        }

        private static void EvaluateIntentInto(FacialIntent intent, Dictionary<string, float> output, double[] sums)
        {
            Array.Clear(sums, 0, sums.Length);
            double jawMaximum = 0;
            for (var index = 0; index < MappingTable.Count; index++)
            {
                var mapping = MappingTable[index];
                // Explicit float conversion recovers the exact native float weight
                // after 0..1 normalization, preserving existing gain arithmetic.
                var value = (float)(intent[mapping.Channel] * 100d);
                var contribution = value * (double)mapping.Gain;
                if (mapping.JawMaximumCandidate) jawMaximum = Math.Max(jawMaximum, contribution);
                else sums[mapping.TargetIndex] += contribution;
            }
            ArbitrateSmileFrown(sums, SmileLeftIndex, FrownLeftIndex);
            ArbitrateSmileFrown(sums, SmileRightIndex, FrownRightIndex);
            sums[JawIndex] = Math.Max(sums[JawIndex], jawMaximum);
            for (var i = 0; i < TargetNames.Length; i++) output[TargetNames[i]] = (float)Math.Max(0d, Math.Min(100d, sums[i]));
        }

        private static void ArbitrateSmileFrown(double[] sums, int smileIndex, int frownIndex)
        {
            var difference = sums[smileIndex] - sums[frownIndex];
            sums[smileIndex] = Math.Max(difference, 0d);
            sums[frownIndex] = Math.Max(-difference, 0d);
        }

        internal void Apply()
        {
            if (disposed) throw new ObjectDisposedException(nameof(AniFacialAdapter));
            if (!source || !target || !jaw || source.sharedMesh != sourceMesh || target.sharedMesh != targetMesh)
            {
                Status = "Facial mapping unavailable: bound renderer/mesh/jaw changed or was destroyed.";
                throw new InvalidOperationException(Status);
            }
            LastNonFiniteInputCount = 0;
            foreach (var input in sourceIndices)
            {
                var value = source.GetBlendShapeWeight(input.Value);
                inputBuffer[input.Key] = value;
                if (!Finite(value)) LastNonFiniteInputCount++;
            }
            EvaluateInto(inputBuffer, intentBuffer, outputBuffer, sumBuffer);
            // Only the clone is written. Eye rotations from HumanPose are untouched.
            jaw.localRotation = jawRestRotation;
            foreach (var value in outputBuffer) target.SetBlendShapeWeight(targetIndices[value.Key], value.Value);
            if (lastStatusInvalidCount != LastNonFiniteInputCount)
            {
                lastStatusInvalidCount = LastNonFiniteInputCount;
                Status = "Applied provisional Ani facial mapping; nonfinite source channels reset to zero: " + LastNonFiniteInputCount + ".";
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var errors = new List<Exception>();
            if (target && target.sharedMesh == targetMesh)
                foreach (var weight in originalWeights)
                    try { target.SetBlendShapeWeight(targetIndices[weight.Key], weight.Value); } catch (Exception error) { errors.Add(error); }
            if (jaw) try { jaw.localRotation = jawRestRotation; } catch (Exception error) { errors.Add(error); }
            Status = errors.Count == 0 ? "Disposed; owned clone weights and jaw restored where objects still exist." : "Disposed; clone restoration reported errors.";
            if (errors.Count != 0) throw new AggregateException("Ani facial clone restoration failed.", errors);
        }

        private static bool ContainsAll(Mesh mesh, IEnumerable<string> names) => mesh && names.All(name => mesh.GetBlendShapeIndex(name) >= 0);
        private static Dictionary<string, int> ExactIndices(Mesh mesh, IEnumerable<string> names)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                int match = -1, count = 0;
                for (var i = 0; i < mesh.blendShapeCount; i++) if (mesh.GetBlendShapeName(i) == name) { match = i; count++; }
                if (count != 1) throw new InvalidOperationException("Missing/duplicate exact blendshape: " + name);
                result.Add(name, match);
            }
            return result;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
