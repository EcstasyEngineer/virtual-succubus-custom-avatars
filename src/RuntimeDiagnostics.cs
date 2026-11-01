using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EcstasyEngineer.AvatarReplacement
{
    // On-demand scene dump from the debug panel, on the Unity main thread.
    // No animations, renderers, transforms or gameplay state are mutated here.
    internal sealed class RuntimeDiagnostics
    {
        private const int MaxAnimators = 128, MaxRenderersPerAnimator = 2048, MaxRenderersTotal = 8192, MaxCameras = 32;
        private readonly string directory;
        private readonly ManualLogSource log;
        private int slot;

        [DataContract] private sealed class Report
        {
            [DataMember]
            public int schemaVersion = 1;
            [DataMember]
            public string capturedUtc, unityVersion, trigger;
            [DataMember]
            public string scope = "Animator/Renderer/Camera structure and visibility metadata only; no dialogue, textures, media, animation clips, saved settings or player data.";
            [DataMember]
            public int matchingAnimatorCount, omittedAnimatorCount, omittedRendererCount;
            [DataMember]
            public SceneEntry[] scenes;
            [DataMember]
            public AnimatorEntry[] animators;
            [DataMember]
            public int matchingActiveCameraCount, omittedActiveCameraCount;
            [DataMember]
            public CameraEntry[] cameras;
        }
        [DataContract] private sealed class SceneEntry { [DataMember] public string name; [DataMember] public bool loaded; }
        [DataContract] private sealed class AnimatorEntry
        {
            [DataMember]
            public string scene, path, avatarName, cullingMode, rootParentPath, poseInspectionError;
            [DataMember]
            public int sceneHandle, instanceId;
            [DataMember]
            public bool activeSelf, activeInHierarchy, enabled, avatarPresent, avatarValid, avatarHuman, animatorHuman, applyRootMotion;
            [DataMember]
            public float[] rootWorldPosition, rootWorldRotation, rootLossyScale, activeRendererBoundsCenter, activeRendererBoundsExtents;
            [DataMember]
            public float[] rootLocalPosition, rootLocalRotation, animatorBodyPosition, animatorBodyRotation, hipsWorldPosition, hipsWorldRotation;
            [DataMember]
            public float humanScale;
            [DataMember]
            public bool hasActiveRendererBounds;
            [DataMember]
            public RendererEntry[] renderers;
            [DataMember]
            public string inspectionError;
        }
        [DataContract] private sealed class RendererEntry
        {
            [DataMember]
            public string relativePath, indexedPath, type, meshName;
            [DataMember]
            public string[] materialNames, shaderNames;
            [DataMember]
            public int[] renderQueues;
            [DataMember]
            public int instanceId, layer;
            [DataMember]
            public bool enabled, activeSelf, activeInHierarchy, isVisible, forceRenderingOff;
            [DataMember]
            public float[] boundsCenter, boundsExtents;
        }
        [DataContract] private sealed class CameraEntry
        {
            [DataMember]
            public string scene, path, targetTextureName, inspectionError;
            [DataMember]
            public int instanceId, cullingMask;
            [DataMember]
            public bool enabled, activeInHierarchy, orthographic, rendersToTexture;
            [DataMember]
            public float nearClip, farClip, depth;
            [DataMember]
            public float[] position, rotation, viewportRect;
            [DataMember]
            public CameraProjection[] humanAnimatorProjections;
        }
        [DataContract] private sealed class CameraProjection
        {
            [DataMember]
            public string animatorScene, animatorPath;
            [DataMember]
            public int animatorInstanceId;
            [DataMember]
            public bool animatorActiveInHierarchy;
            [DataMember]
            public float[] rootViewportPosition, activeRendererBoundsViewportPosition;
        }

        internal RuntimeDiagnostics(string directory, ManualLogSource log) { this.directory = directory; this.log = log; }
        // Returns the written path, or null after logging a failure.
        internal string Capture(string reason)
        {
            try { return Write(reason); }
            catch (Exception error) { log.LogWarning("Animator diagnostics failed: " + error.Message); return null; }
        }

        private static string MeshName(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned) return skinned.sharedMesh ? skinned.sharedMesh.name : null;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter && filter.sharedMesh ? filter.sharedMesh.name : null;
        }

        private static float[] Values(Vector3 value) { return new[] { value.x, value.y, value.z }; }
        private static float[] Values(Quaternion value) { return new[] { value.x, value.y, value.z, value.w }; }
        private static Vector3 Vector(float[] value) { return new Vector3(value[0], value[1], value[2]); }

        private string Write(string reason)
        {
            var all = Resources.FindObjectsOfTypeAll<Animator>().Where(a => a && a.gameObject.scene.IsValid())
                .OrderByDescending(a => a.avatar && a.avatar.isHuman).ThenByDescending(a => a.gameObject.activeInHierarchy)
                .ThenBy(a => a.gameObject.scene.name, StringComparer.Ordinal).ThenBy(a => HierarchyPaths.Full(a.transform), StringComparer.Ordinal).ToArray();
            var scenes = new List<SceneEntry>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new SceneEntry { name = scene.name, loaded = scene.isLoaded });
            }
            var report = new Report
            {
                capturedUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion, trigger = reason,
                matchingAnimatorCount = all.Length, omittedAnimatorCount = Math.Max(0, all.Length - MaxAnimators), scenes = scenes.ToArray()
            };
            var entries = new List<AnimatorEntry>();
            var remaining = MaxRenderersTotal;
            foreach (var animator in all.Take(MaxAnimators))
            {
                var entry = new AnimatorEntry();
                try
                {
                    entry.scene = animator.gameObject.scene.name; entry.sceneHandle = animator.gameObject.scene.handle;
                    entry.path = HierarchyPaths.Full(animator.transform); entry.instanceId = animator.GetInstanceID();
                    entry.activeSelf = animator.gameObject.activeSelf; entry.activeInHierarchy = animator.gameObject.activeInHierarchy;
                    entry.enabled = animator.enabled; entry.applyRootMotion = animator.applyRootMotion;
                    entry.cullingMode = animator.cullingMode.ToString(); entry.animatorHuman = animator.isHuman;
                    entry.rootWorldPosition = Values(animator.transform.position);
                    entry.rootWorldRotation = Values(animator.transform.rotation);
                    entry.rootLossyScale = Values(animator.transform.lossyScale);
                    entry.rootLocalPosition = Values(animator.transform.localPosition);
                    entry.rootLocalRotation = Values(animator.transform.localRotation);
                    entry.rootParentPath = animator.transform.parent ? HierarchyPaths.Full(animator.transform.parent) : null;
                    entry.humanScale = animator.isHuman ? animator.humanScale : 0;
                    if (animator.isHuman)
                    {
                        try
                        {
                            entry.animatorBodyPosition = Values(animator.bodyPosition);
                            entry.animatorBodyRotation = Values(animator.bodyRotation);
                            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                            if (hips)
                            {
                                entry.hipsWorldPosition = Values(hips.position);
                                entry.hipsWorldRotation = Values(hips.rotation);
                            }
                        }
                        catch (Exception error) { entry.poseInspectionError = error.GetType().Name; }
                    }
                    entry.avatarPresent = animator.avatar;
                    if (animator.avatar)
                    {
                        entry.avatarName = animator.avatar.name; entry.avatarValid = animator.avatar.isValid; entry.avatarHuman = animator.avatar.isHuman;
                    }
                    var renderers = animator.GetComponentsInChildren<Renderer>(true);
                    var aggregateBounds = new Bounds();
                    foreach (var renderer in renderers)
                    {
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        if (!entry.hasActiveRendererBounds) { aggregateBounds = renderer.bounds; entry.hasActiveRendererBounds = true; }
                        else aggregateBounds.Encapsulate(renderer.bounds);
                    }
                    if (entry.hasActiveRendererBounds)
                    {
                        entry.activeRendererBoundsCenter = Values(aggregateBounds.center);
                        entry.activeRendererBoundsExtents = Values(aggregateBounds.extents);
                    }
                    var limit = Math.Min(MaxRenderersPerAnimator, remaining);
                    entry.renderers = renderers.Take(limit).Select(r => new RendererEntry
                    {
                        relativePath = HierarchyPaths.Relative(r.transform, animator.transform),
                        indexedPath = HierarchyPaths.IndexedRelative(r.transform, animator.transform),
                        layer = r.gameObject.layer, meshName = MeshName(r),
                        materialNames = r.sharedMaterials.Select(m => m ? m.name : null).ToArray(),
                        shaderNames = r.sharedMaterials.Select(m => m && m.shader ? m.shader.name : null).ToArray(),
                        renderQueues = r.sharedMaterials.Select(m => m ? m.renderQueue : -1).ToArray(),
                        boundsCenter = Values(r.bounds.center), boundsExtents = Values(r.bounds.extents), isVisible = r.isVisible, forceRenderingOff = r.forceRenderingOff,
                        type = r.GetType().Name, instanceId = r.GetInstanceID(), enabled = r.enabled,
                        activeSelf = r.gameObject.activeSelf, activeInHierarchy = r.gameObject.activeInHierarchy
                    }).ToArray();
                    report.omittedRendererCount += Math.Max(0, renderers.Length - limit);
                    remaining -= entry.renderers.Length;
                }
                catch (Exception error) { entry.inspectionError = error.GetType().Name; }
                entries.Add(entry);
            }
            report.animators = entries.ToArray();
            // Active hierarchy cameras include disabled components, sorted after enabled cameras.
            // Viewport positions are [x,y,forward-distance]; they do not prove a renderer is drawn.
            var cameras = Resources.FindObjectsOfTypeAll<Camera>().Where(c => c && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy)
                .OrderByDescending(c => c.enabled).ThenByDescending(c => c.depth).ThenBy(c => HierarchyPaths.Full(c.transform), StringComparer.Ordinal).ToArray();
            report.matchingActiveCameraCount = cameras.Length;
            report.omittedActiveCameraCount = Math.Max(0, cameras.Length - MaxCameras);
            var cameraEntries = new List<CameraEntry>();
            foreach (var camera in cameras.Take(MaxCameras))
            {
                var entry = new CameraEntry();
                try
                {
                    entry.scene = camera.gameObject.scene.name; entry.path = HierarchyPaths.Full(camera.transform); entry.instanceId = camera.GetInstanceID();
                    entry.enabled = camera.enabled; entry.activeInHierarchy = camera.gameObject.activeInHierarchy; entry.cullingMask = camera.cullingMask;
                    entry.nearClip = camera.nearClipPlane; entry.farClip = camera.farClipPlane; entry.depth = camera.depth; entry.orthographic = camera.orthographic;
                    entry.position = Values(camera.transform.position); entry.rotation = Values(camera.transform.rotation);
                    entry.viewportRect = new[] { camera.rect.x, camera.rect.y, camera.rect.width, camera.rect.height };
                    entry.rendersToTexture = camera.targetTexture; entry.targetTextureName = camera.targetTexture ? camera.targetTexture.name : null;
                    entry.humanAnimatorProjections = entries.Where(a => (a.animatorHuman || a.avatarHuman) && a.rootWorldPosition != null).Select(a => new CameraProjection
                    {
                        animatorScene = a.scene, animatorPath = a.path, animatorInstanceId = a.instanceId, animatorActiveInHierarchy = a.activeInHierarchy,
                        rootViewportPosition = Values(camera.WorldToViewportPoint(Vector(a.rootWorldPosition))),
                        activeRendererBoundsViewportPosition = a.hasActiveRendererBounds ? Values(camera.WorldToViewportPoint(Vector(a.activeRendererBoundsCenter))) : null
                    }).ToArray();
                }
                catch (Exception error) { entry.inspectionError = error.GetType().Name; }
                cameraEntries.Add(entry);
            }
            report.cameras = cameraEntries.ToArray();
            string json;
            // Unity's player serializer omitted nested DTO arrays in this installed build.
            // Explicit contracts preserve the full bounded report without a bundled dependency.
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Report)).WriteObject(stream, report);
                json = Encoding.UTF8.GetString(stream.ToArray());
            }
            if (Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new InvalidOperationException("Report exceeded the 4 MiB UTF-8 cap; no report written.");
            Directory.CreateDirectory(directory);
            // Eight rotating files bound disk use. Contents overwrite only this plugin's report names.
            var output = Path.Combine(directory, "animators-" + (slot++ % 8) + ".json");
            File.WriteAllText(output, json);
            log.LogInfo("Animator metadata written to " + output + "; captured " + entries.Count + " of " + all.Length + " Animators.");
            return output;
        }
    }
}
