using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx.Logging;
using UnityEngine;

namespace EcstasyEngineer.AvatarReplacement
{
    // Reads the supplied pair only. Never drives bones, muscles or blendshape weights.
    internal static class FacialDiagnostics
    {
        private const int RenderersPerRole = 8, ShapesPerRole = 128, MaxScannedPerRole = 4096;
        private const int MaxBytes = 4 * 1024 * 1024, MaxErrors = 64, MaxPath = 2048, MaxName = 256;
        private static readonly HumanBodyBones[] FaceBones = { HumanBodyBones.Head, HumanBodyBones.LeftEye, HumanBodyBones.RightEye, HumanBodyBones.Jaw };

        [DataContract] private sealed class Report
        {
            [DataMember] public int schemaVersion = 1;
            [DataMember] public string capturedUtc, unityVersion;
            [DataMember] public int frame;
            [DataMember] public string scope = "Read-only currently bound Animator pair: facial bone rotations, selected Humanoid muscles and blendshape metadata/weights. No UI text, dialogue, animation clips, media, preferences or saves.";
            [DataMember] public int maximumRenderersTotal = RenderersPerRole * 2, maximumBlendshapesTotal = ShapesPerRole * 2, maximumBytes = MaxBytes;
            [DataMember] public string budgetPolicy = "Each role receives 8 renderers and 128 blendshape records; active/enabled facial-named meshes are prioritized. Unused budget is not transferred between roles. Counts exclude explicitly uninspected renderers.";
            [DataMember] public AnimatorEntry source, replacement;
        }
        [DataContract] private sealed class AnimatorEntry
        {
            [DataMember] public string role, scene, path, name, avatarName;
            [DataMember] public bool present, activeInHierarchy, enabled, human, avatarValid;
            [DataMember] public int instanceId, totalSkinnedRendererCount, uninspectedSkinnedRendererCount;
            [DataMember] public int matchingBlendshapeRendererCount, omittedBlendshapeRendererCount, matchingBlendshapeCount, omittedBlendshapeCount;
            [DataMember] public int omittedErrorCount;
            [DataMember] public bool stringsTruncated;
            [DataMember] public string[] errors = Array.Empty<string>();
            [DataMember] public BoneEntry[] bones = Array.Empty<BoneEntry>();
            [DataMember] public MuscleEntry[] muscles = Array.Empty<MuscleEntry>();
            [DataMember] public RendererEntry[] renderers = Array.Empty<RendererEntry>();
        }
        [DataContract] private sealed class BoneEntry
        {
            [DataMember] public string bone, relativePath, indexedPath, error;
            [DataMember] public bool present;
            [DataMember] public float[] localRotationXyzw, worldRotationXyzw;
        }
        [DataContract] private sealed class MuscleEntry
        {
            [DataMember] public int index;
            [DataMember] public string name, error;
            [DataMember] public float? value;
        }
        [DataContract] private sealed class RendererEntry
        {
            [DataMember] public string name, relativePath, indexedPath, meshName, error;
            [DataMember] public int instanceId, blendshapeCount, omittedBlendshapeCount;
            [DataMember] public bool activeInHierarchy, enabled, forceRenderingOff;
            [DataMember] public ShapeEntry[] blendshapes = Array.Empty<ShapeEntry>();
        }
        [DataContract] private sealed class ShapeEntry
        {
            [DataMember] public int index;
            [DataMember] public string name, error;
            [DataMember] public float? weight;
        }
        private sealed class Candidate
        {
            public SkinnedMeshRenderer renderer;
            public Mesh mesh;
            public int count;
            public string path;
            public bool active, facialName;
        }
        private sealed class Errors
        {
            public readonly List<string> values = new List<string>();
            public int omitted;
            public void Add(string value) { if (values.Count < MaxErrors) values.Add(Safe(value, 512)); else omitted++; }
        }

        // Call from LateUpdate, after pose transfer, so both faces show this frame's final weights.
        // Returns the written path, or null after logging a failure.
        internal static string Capture(string directory, Animator source, Animator replacement, ManualLogSource log)
        {
            try
            {
                var report = new Report
                {
                    capturedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), unityVersion = Application.unityVersion,
                    frame = Time.frameCount, source = Inspect(source, "source"), replacement = Inspect(replacement, "replacement")
                };
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(Report)).WriteObject(stream, report);
                    if (stream.Length > MaxBytes) throw new InvalidDataException("Facial report exceeds 4 MiB; no report replaced.");
                    Directory.CreateDirectory(directory);
                    var output = Path.Combine(directory, "face-capture.json");
                    File.WriteAllBytes(output, stream.ToArray());
                    log?.LogInfo("Facial metadata captured for the bound Animator pair; no expression state was changed.");
                    return output;
                }
            }
            catch (Exception error) { log?.LogWarning("Facial capture failed: " + Safe(error.Message, 512)); return null; }
        }

        private static AnimatorEntry Inspect(Animator animator, string role)
        {
            var result = new AnimatorEntry { role = role };
            var errors = new Errors();
            try
            {
                result.present = animator;
                if (!animator) { errors.Add("No currently bound " + role + " Animator was supplied."); return result; }
                result.name = Bounded(animator.name, MaxName, result);
                result.path = Bounded(HierarchyPaths.Full(animator.transform), MaxPath, result);
                result.scene = Bounded(animator.gameObject.scene.name, MaxName, result);
                result.instanceId = animator.GetInstanceID(); result.enabled = animator.enabled;
                result.activeInHierarchy = animator.gameObject.activeInHierarchy;
                result.human = animator.isHuman;
                result.avatarValid = animator.avatar && animator.avatar.isValid;
                result.avatarName = animator.avatar ? Bounded(animator.avatar.name, MaxName, result) : null;
                if (result.human && result.avatarValid)
                {
                    result.bones = FaceBones.Select(b => ReadBone(animator, b, result)).ToArray();
                    result.muscles = ReadMuscles(animator, result, errors);
                }
                else errors.Add("Facial Humanoid bones/muscles unavailable: avatar is missing, invalid or not Humanoid.");
                InspectRenderers(animator, result, errors);
            }
            catch (Exception error) { errors.Add("Animator inspection: " + error.Message); }
            finally { result.errors = errors.values.ToArray(); result.omittedErrorCount = errors.omitted; }
            return result;
        }

        private static BoneEntry ReadBone(Animator animator, HumanBodyBones bone, AnimatorEntry owner)
        {
            var entry = new BoneEntry { bone = bone.ToString() };
            try
            {
                var transform = animator.GetBoneTransform(bone);
                entry.present = transform;
                if (!transform) return entry;
                entry.relativePath = Bounded(HierarchyPaths.Relative(transform, animator.transform), MaxPath, owner);
                entry.indexedPath = Bounded(HierarchyPaths.IndexedRelative(transform, animator.transform), MaxPath, owner);
                entry.localRotationXyzw = Rotation(transform.localRotation);
                entry.worldRotationXyzw = Rotation(transform.rotation);
            }
            catch (Exception error) { entry.error = Safe(error.Message, 512); }
            return entry;
        }

        private static MuscleEntry[] ReadMuscles(Animator animator, AnimatorEntry owner, Errors errors)
        {
            try
            {
                var pose = new HumanPose();
                using (var handler = new HumanPoseHandler(animator.avatar, animator.transform)) handler.GetHumanPose(ref pose);
                var names = HumanTrait.MuscleName;
                var entries = new List<MuscleEntry>();
                for (var i = 0; i < names.Length; i++)
                {
                    // Unity Humanoid face channels are Head, Eye and Jaw muscles;
                    // keep their original index/name, not a guessed expression map.
                    if (!(names[i].StartsWith("Head ", StringComparison.OrdinalIgnoreCase) ||
                        names[i].IndexOf("Eye ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        names[i].StartsWith("Jaw ", StringComparison.OrdinalIgnoreCase))) continue;
                    var entry = new MuscleEntry { index = i, name = Bounded(names[i], MaxName, owner) };
                    if (pose.muscles == null || i >= pose.muscles.Length) entry.error = "Muscle sample missing.";
                    else if (!Finite(pose.muscles[i])) entry.error = "Muscle sample is not finite.";
                    else entry.value = pose.muscles[i];
                    entries.Add(entry);
                }
                if (entries.Count == 0) errors.Add("No Head/Eye/Jaw channels matched the runtime HumanTrait muscle names.");
                return entries.ToArray();
            }
            catch (Exception error) { errors.Add("Humanoid muscle inspection: " + error.Message); return Array.Empty<MuscleEntry>(); }
        }

        private static void InspectRenderers(Animator animator, AnimatorEntry owner, Errors errors)
        {
            var renderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            owner.totalSkinnedRendererCount = renderers.Length;
            owner.uninspectedSkinnedRendererCount = Math.Max(0, renderers.Length - MaxScannedPerRole);
            var candidates = new List<Candidate>();
            foreach (var renderer in renderers.Take(MaxScannedPerRole))
            {
                try
                {
                    if (!renderer) { owner.uninspectedSkinnedRendererCount++; continue; }
                    var mesh = renderer.sharedMesh;
                    if (!mesh || mesh.blendShapeCount == 0) continue;
                    var path = HierarchyPaths.Relative(renderer.transform, animator.transform);
                    candidates.Add(new Candidate { renderer = renderer, mesh = mesh, count = mesh.blendShapeCount,
                        path = path, active = renderer.enabled && renderer.gameObject.activeInHierarchy,
                        facialName = FacialName(renderer.name) || FacialName(mesh.name) });
                }
                catch (Exception error) { owner.uninspectedSkinnedRendererCount++; errors.Add("Renderer discovery: " + error.Message); }
            }
            owner.matchingBlendshapeRendererCount = candidates.Count;
            owner.matchingBlendshapeCount = candidates.Sum(c => c.count);
            var selected = candidates.OrderByDescending(c => c.active).ThenByDescending(c => c.facialName)
                .ThenBy(c => c.path, StringComparer.Ordinal).ThenBy(c => c.renderer.GetInstanceID()).Take(RenderersPerRole).ToArray();
            owner.omittedBlendshapeRendererCount = candidates.Count - selected.Length;
            var records = new List<RendererEntry>();
            int shapesLeft = ShapesPerRole, capturedShapes = 0;
            foreach (var candidate in selected)
            {
                var entry = new RendererEntry { blendshapeCount = candidate.count, omittedBlendshapeCount = candidate.count };
                try
                {
                    var renderer = candidate.renderer;
                    entry.name = Bounded(renderer.name, MaxName, owner); entry.meshName = Bounded(candidate.mesh.name, MaxName, owner);
                    entry.relativePath = Bounded(candidate.path, MaxPath, owner);
                    entry.indexedPath = Bounded(HierarchyPaths.IndexedRelative(renderer.transform, animator.transform), MaxPath, owner);
                    entry.instanceId = renderer.GetInstanceID(); entry.activeInHierarchy = renderer.gameObject.activeInHierarchy;
                    entry.enabled = renderer.enabled; entry.forceRenderingOff = renderer.forceRenderingOff;
                    var shapes = new List<ShapeEntry>();
                    for (var index = 0; index < candidate.count && shapesLeft > 0; index++)
                    {
                        var shape = new ShapeEntry { index = index };
                        try
                        {
                            shape.name = Bounded(candidate.mesh.GetBlendShapeName(index), MaxName, owner);
                            var weight = renderer.GetBlendShapeWeight(index);
                            if (Finite(weight)) shape.weight = weight; else shape.error = "Blendshape weight is not finite.";
                        }
                        catch (Exception error) { shape.error = Safe(error.Message, 512); }
                        shapes.Add(shape); shapesLeft--; capturedShapes++;
                    }
                    entry.blendshapes = shapes.ToArray(); entry.omittedBlendshapeCount = candidate.count - shapes.Count;
                }
                catch (Exception error) { entry.error = Safe(error.Message, 512); }
                records.Add(entry);
            }
            owner.renderers = records.ToArray();
            owner.omittedBlendshapeCount = owner.matchingBlendshapeCount - capturedShapes;
        }

        private static bool FacialName(string value) => new[] { "face", "head", "eye", "mouth", "jaw" }.Any(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float[] Rotation(Quaternion value)
        {
            var result = new[] { value.x, value.y, value.z, value.w };
            if (result.Any(x => !Finite(x))) throw new InvalidDataException("Quaternion contains non-finite components.");
            return result;
        }
        private static string Bounded(string value, int limit, AnimatorEntry owner)
        {
            if (value != null && value.Length > limit) owner.stringsTruncated = true;
            return Safe(value, limit);
        }
        private static string Safe(string value, int limit) => value == null ? null : new string(value.Where(c => !char.IsControl(c)).Take(limit).ToArray());
    }
}
