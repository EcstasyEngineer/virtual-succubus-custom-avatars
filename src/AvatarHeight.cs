using System;
using System.Collections.Generic;
using UnityEngine;

namespace EcstasyEngineer.AvatarReplacement
{
    // Rest-pose head height in Animator-root space, used to scale the replacement
    // so both heads top out at the same height. Only vertices whose dominant bone
    // is the Head bone itself count: ahoge, ears, hair chains and accessories are
    // normally skinned to child bones and are excluded. Bind-pose vertex offsets
    // are rigid with the head, so the measurement does not depend on the pose the
    // game is currently animating.
    internal static class AvatarHeight
    {
        internal const float MinFactor = 0.5f, MaxFactor = 2f;

        internal struct Measure
        {
            public float headBone;   // rest Head bone height
            public float? headTop;   // null when no readable head-dominant vertices exist
            public int unreadableMeshes;
        }

        internal static Measure Read(Animator animator)
        {
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (!head) throw new InvalidOperationException("Humanoid Head bone is missing.");
            var restHead = RestRootToBone(animator, head);
            var result = new Measure { headBone = restHead.GetColumn(3).y };
            float top = float.NegativeInfinity;
            foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                // Inactive outfit alternatives must not raise the measured head. The
                // replacement root itself is still inactive while it is being bound.
                if (!smr.enabled || !ActiveBelow(smr.transform, animator.transform) || !smr.sharedMesh) continue;
                var headIndex = Array.IndexOf(smr.bones, head);
                if (headIndex < 0) continue;
                var mesh = smr.sharedMesh;
                if (!mesh.isReadable) { result.unreadableMeshes++; continue; }
                var bindposes = mesh.bindposes;
                if (headIndex >= bindposes.Length) continue;
                var toRoot = restHead * bindposes[headIndex];
                var weights = mesh.boneWeights;
                var vertices = mesh.vertices;
                // Unity stores BoneWeight sorted by weight, so index 0 is dominant.
                for (var i = 0; i < weights.Length && i < vertices.Length; i++)
                    if (weights[i].boneIndex0 == headIndex)
                        top = Mathf.Max(top, toRoot.MultiplyPoint3x4(vertices[i]).y);
            }
            if (!float.IsNegativeInfinity(top)) result.headTop = top;
            return result;
        }

        private static bool ActiveBelow(Transform t, Transform root)
        {
            for (; t && t != root; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return t == root;
        }

        // Compose rest-pose local transforms from the Head bone up to (excluding) the
        // Animator root. The Avatar's skeleton is the pose Unity built the rig from;
        // live transforms are only a fallback for names it does not list.
        private static Matrix4x4 RestRootToBone(Animator animator, Transform bone)
        {
            var rest = new Dictionary<string, SkeletonBone>();
            foreach (var entry in animator.avatar.humanDescription.skeleton ?? Array.Empty<SkeletonBone>())
                if (!rest.ContainsKey(entry.name)) rest.Add(entry.name, entry);
            var matrix = Matrix4x4.identity;
            var t = bone;
            for (; t && t != animator.transform; t = t.parent)
                matrix = (rest.TryGetValue(t.name, out var s) ? Matrix4x4.TRS(s.position, s.rotation, s.scale)
                    : Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale)) * matrix;
            if (t != animator.transform) throw new InvalidOperationException("Head bone is not below the Animator root.");
            return matrix;
        }

        // Returns the uniform scale for the replacement root, or 1 when the
        // measurements cannot be trusted. Both sides always use the same metric.
        internal static float Factor(Measure source, Measure replacement, out string detail)
        {
            var useTop = source.headTop.HasValue && replacement.headTop.HasValue;
            var s = useTop ? source.headTop.Value : source.headBone;
            var r = useTop ? replacement.headTop.Value : replacement.headBone;
            var metric = useTop ? "head top" : "head bone (head-top vertices unavailable: source unreadable=" +
                source.unreadableMeshes + ", replacement unreadable=" + replacement.unreadableMeshes + ")";
            var factor = r > 0.01f && s > 0.01f ? s / r : 0;
            detail = "Height by " + metric + ": source " + s.ToString("F3") + ", replacement " + r.ToString("F3");
            if (factor < MinFactor || factor > MaxFactor)
            {
                detail += "; factor " + factor.ToString("F3") + " outside " + MinFactor + "-" + MaxFactor + ", not normalized.";
                return 1;
            }
            detail += "; scale x" + factor.ToString("F3") + ".";
            return factor;
        }
    }
}
