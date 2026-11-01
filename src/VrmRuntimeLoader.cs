using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using UniVRM10;
using VRM;

namespace EcstasyEngineer.AvatarReplacement
{
    // Loads the user's original VRM directly. No model conversion, generated
    // prefab, per-avatar bundle or facial sidecar is involved.
    internal sealed class VrmRuntimeLoader : IDisposable
    {
        private static AssetBundle shaderBundle;
        // Strong references: the game's Resources.UnloadUnusedAssets on scene changes otherwise
        // unloads bundle shaders no live material uses yet (e.g. MToon10 before the first VRM 1.0),
        // and Shader.Find then returns null inside the importer.
        private static Shader[] bundleShaders;
        private static Material[] bundleMaterials;
        private RuntimeGltfInstance loaded;
        private volatile bool disposed;
        internal Task Loading { get; }
        internal GameObject Root => loaded ? loaded.Root : null;
        internal VrmFacialAdapter.Profile FacialProfile { get; private set; }
        internal string Version { get; private set; }

        internal VrmRuntimeLoader(string path, string runtimeDirectory, string textureCacheDirectory, Action<string> log)
        {
            Loading = Load(path, runtimeDirectory, textureCacheDirectory, log);
        }

        private async Task Load(string path, string runtimeDirectory, string textureCacheDirectory, Action<string> log)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var startFrame = Time.frameCount;
            if (!string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathRooted(path) || !File.Exists(path))
                throw new InvalidDataException("Select an existing local .vrm file.");
            var size = new FileInfo(path).Length;
            if (size < 20 || size > 256L * 1024 * 1024) throw new InvalidDataException("VRM file must be between 20 bytes and 256 MiB.");
            if (!shaderBundle)
            {
                shaderBundle = AssetBundle.LoadFromFile(Path.Combine(runtimeDirectory, "vrm-runtime-shaders.bundle"));
                if (!shaderBundle) throw new InvalidDataException("VRM runtime shader pack is missing. Install the complete mod runtime package.");
                bundleShaders = shaderBundle.LoadAllAssets<Shader>();
                bundleMaterials = shaderBundle.LoadAllAssets<Material>();
            }
            var caller = new BoundedAwaitCaller(() => disposed);
            var textures = new TextureCache(textureCacheDirectory);
            long parsedMs = 0;
            try
            {
                // File read and JSON parsing are plain C#; keep them off the main thread,
                // falling back to the main thread if a Unity API refuses the worker.
                GltfData parsed;
                try { parsed = await caller.Run(() => new GlbFileParser(path).Parse()); }
                catch (UnityException) { parsed = new GlbFileParser(path).Parse(); }
                using (var data = parsed)
                {
                    // Self-contained files only: do not follow external resources.
                    if ((data.GLTF.buffers != null && data.GLTF.buffers.Any(b => !string.IsNullOrEmpty(b.uri))) ||
                        (data.GLTF.images != null && data.GLTF.images.Any(i => !string.IsNullOrEmpty(i.uri))))
                        throw new InvalidDataException("VRM must embed its buffers and textures; external URIs are not loaded.");
                    VRMData vrm0 = null;
                    Vrm10Data vrm1 = null;
                    try
                    {
                        try { vrm0 = await caller.Run(() => new VRMData(data)); }
                        catch (UnityException) { vrm0 = new VRMData(data); }
                    }
                    catch (NotVrm0Exception)
                    {
                        vrm1 = Vrm10Data.Parse(data);
                        if (vrm1 == null) throw new InvalidDataException("File is neither VRM 0.x nor VRM 1.0.");
                    }
                    parsedMs = timer.ElapsedMilliseconds;
                    if (vrm0 != null)
                        using (var importer = new OwnedImporter(vrm0, textures))
                        {
                            try { loaded = await importer.LoadAsync(caller); }
                            catch
                            {
                                // UniVRM's Dispose releases its assets, but not a Root
                                // created before LoadAsync returns its resource owner.
                                importer.DestroyPartialRoot();
                                throw;
                            }
                        }
                    else
                        // No control rig: the game drives the raw Humanoid bones, exactly as for VRM0.
                        using (var importer = new Vrm10Importer(vrm1, textureDeserializer: textures, useControlRig: false))
                            loaded = await importer.LoadAsync(caller);
                    Version = vrm0 != null ? "0.x" : "1.0";
                }
                if (disposed) { Release(); return; }
                if (!loaded || !Root) throw new InvalidDataException("VRM importer produced no model.");
                Root.SetActive(false);
                var animator = Root.GetComponent<Animator>();
                if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                    throw new InvalidDataException("VRM does not contain a valid Humanoid avatar.");
                FacialProfile = Version == "1.0" ? ReadExpressions10(Root) : ReadExpressions(Root);
                // The game supplies pose and face timing. Keep the resource owner
                // alive for cleanup, while preventing imported drivers competing.
                foreach (var behaviour in Root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour != loaded) behaviour.enabled = false;
                loaded.EnableUpdateWhenOffscreen();
                loaded.ShowMeshes();
                log?.Invoke("VRM " + Version + " imported in " + timer.ElapsedMilliseconds + " ms over " + (Time.frameCount - startFrame) + " frames (parse " + parsedMs +
                    " ms); textures: " + textures.Hits + " from cache, " + textures.Stored + " decoded and cached, " + textures.Uncached + " decoded uncached.");
            }
            catch (OperationCanceledException) when (disposed) { Release(); }
            catch { Release(); throw; }
        }

        private sealed class OwnedImporter : VRMImporterContext
        {
            internal OwnedImporter(VRMData data, ITextureDeserializer textures) : base(data, textureDeserializer: textures) { }
            internal void DestroyPartialRoot()
            {
                if (!Root) return;
                Root.SetActive(false);
                UnityEngine.Object.Destroy(Root);
                Root = null;
            }
        }

        // Cooperatively cancel at UniVRM's own checkpoints, without abandoning a
        // worker that still uses importer resources. A synchronous import step is
        // not preempted; the next checkpoint enforces cancellation / the budget.
        private sealed class BoundedAwaitCaller : IAwaitCaller
        {
            // 50 ms of import work per frame instead of the default 1/60 s: loads finish in roughly
            // half the wall time, while the game still renders during the import.
            private readonly IAwaitCaller inner = new RuntimeOnlyAwaitCaller(0.05f);
            private readonly Func<bool> cancelled;
            private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            internal BoundedAwaitCaller(Func<bool> cancelled) { this.cancelled = cancelled; }
            private void Check()
            {
                if (cancelled()) throw new OperationCanceledException("VRM import was superseded.");
                if (elapsed.Elapsed.TotalSeconds > 120) throw new TimeoutException("VRM import exceeded its 120-second processing budget.");
            }
            // UniGLTF requests a fresh frame after nearly every mesh, material and texture; measured
            // at ~250 frames for one model, the waiting dominated load time. Yield only once the
            // per-frame work budget is spent.
            public async Task NextFrame() { Check(); await inner.NextFrameIfTimedOut(); Check(); }
            public async Task NextFrameIfTimedOut() { Check(); await inner.NextFrameIfTimedOut(); Check(); }
            public Task Run(Action action)
            {
                Check();
                return inner.Run(() => { Check(); action(); });
            }
            public Task<T> Run<T>(Func<T> action)
            {
                Check();
                // Let the importer receive/own a successful result before the
                // next cancellation check; otherwise returned assets can leak.
                return inner.Run(() => { Check(); return action(); });
            }
        }

        private static readonly Dictionary<BlendShapePreset, string> Semantics = new Dictionary<BlendShapePreset, string> {
            {BlendShapePreset.Angry,"emotion.angry"}, {BlendShapePreset.Joy,"emotion.joy"},
            {BlendShapePreset.Fun,"emotion.fun"}, {BlendShapePreset.Sorrow,"emotion.sorrow"},
            {BlendShapePreset.A,"mouth.a"}, {BlendShapePreset.I,"mouth.i"}, {BlendShapePreset.U,"mouth.u"},
            {BlendShapePreset.E,"mouth.e"}, {BlendShapePreset.O,"mouth.o"},
            {BlendShapePreset.Blink,"blink.both"}, {BlendShapePreset.Blink_L,"blink.left"}, {BlendShapePreset.Blink_R,"blink.right"}
        };
        private static VrmFacialAdapter.Profile ReadExpressions(GameObject root)
        {
            var proxy = root.GetComponent<VRMBlendShapeProxy>();
            var clips = proxy && proxy.BlendShapeAvatar ? proxy.BlendShapeAvatar.Clips : null;
            var result = new List<VrmFacialAdapter.Expression>();
            var unsupported = new List<string>();
            if (clips != null) foreach (var clip in clips)
            {
                if (!clip) continue;
                var provenance = "authored-vrm0-runtime";
                if (!Semantics.TryGetValue(clip.Preset, out var semantic))
                {
                    // VRM0 has no surprise preset; VRoid exports carry a custom clip named "Surprised".
                    if (clip.Preset != BlendShapePreset.Unknown || !string.Equals(clip.BlendShapeName, "Surprised", StringComparison.OrdinalIgnoreCase)) continue;
                    semantic = "emotion.surprised"; provenance = "inferred-vroid-name";
                }
                if (result.Any(e => e.semantic == semantic)) continue;
                var binds = (clip.Values ?? Array.Empty<BlendShapeBinding>()).Where(b => b.Weight > 0).Select(b =>
                    new VrmFacialAdapter.Morph { path = string.IsNullOrEmpty(b.RelativePath) ? "." : b.RelativePath, index = b.Index, weight = b.Weight }).ToArray();
                if (clip.MaterialValues != null && clip.MaterialValues.Length != 0) unsupported.Add(semantic + ": material bindings unavailable");
                if (binds.Length == 0) continue;
                result.Add(new VrmFacialAdapter.Expression { semantic = semantic, isBinary = clip.IsBinary, provenance = provenance, binds = binds });
            }
            if (clips != null)
                AddBrowDown(result, clips.Where(c => c && c.Preset == BlendShapePreset.Unknown && BrowDownNames.Contains(c.BlendShapeName ?? ""))
                    .SelectMany(c => c.Values ?? Array.Empty<BlendShapeBinding>()).Select(b => (b.RelativePath, b.Index, b.Weight)));
            return Profile("vrm0-presets-v1", result, unsupported);
        }

        private static readonly Dictionary<ExpressionPreset, string> Semantics10 = new Dictionary<ExpressionPreset, string> {
            {ExpressionPreset.angry,"emotion.angry"}, {ExpressionPreset.happy,"emotion.joy"},
            {ExpressionPreset.relaxed,"emotion.fun"}, {ExpressionPreset.sad,"emotion.sorrow"}, {ExpressionPreset.surprised,"emotion.surprised"},
            {ExpressionPreset.aa,"mouth.a"}, {ExpressionPreset.ih,"mouth.i"}, {ExpressionPreset.ou,"mouth.u"},
            {ExpressionPreset.ee,"mouth.e"}, {ExpressionPreset.oh,"mouth.o"},
            {ExpressionPreset.blink,"blink.both"}, {ExpressionPreset.blinkLeft,"blink.left"}, {ExpressionPreset.blinkRight,"blink.right"}
        };
        private static VrmFacialAdapter.Profile ReadExpressions10(GameObject root)
        {
            var instance = root.GetComponent<Vrm10Instance>();
            var result = new List<VrmFacialAdapter.Expression>();
            var unsupported = new List<string>();
            if (instance && instance.Vrm && instance.Vrm.Expression != null) foreach (var (preset, clip) in instance.Vrm.Expression.Clips)
            {
                if (!clip || !Semantics10.TryGetValue(preset, out var semantic)) continue;
                // VRM 1.0 stores weights as 0..1; the adapter uses Unity's 0..100 blendshape scale.
                var binds = (clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>()).Where(b => b.Weight > 0).Select(b =>
                    new VrmFacialAdapter.Morph { path = string.IsNullOrEmpty(b.RelativePath) ? "." : b.RelativePath, index = b.Index,
                        weight = Mathf.Min(100, b.Weight * MorphTargetBinding.VRM_TO_UNITY) }).ToArray();
                if ((clip.MaterialColorBindings?.Length ?? 0) + (clip.MaterialUVBindings?.Length ?? 0) != 0) unsupported.Add(semantic + ": material bindings unavailable");
                if (binds.Length == 0) continue;
                result.Add(new VrmFacialAdapter.Expression { semantic = semantic, isBinary = clip.IsBinary, provenance = "authored-vrm1-runtime", binds = binds });
            }
            if (instance && instance.Vrm && instance.Vrm.Expression?.CustomClips != null)
                AddBrowDown(result, instance.Vrm.Expression.CustomClips.Where(c => c && BrowDownNames.Contains(c.name))
                    .SelectMany(c => c.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>())
                    .Select(b => (b.RelativePath, b.Index, b.Weight * MorphTargetBinding.VRM_TO_UNITY)));
            return Profile("vrm1-presets-v1", result, unsupported);
        }

        // ARKit-named brow clips (common in VRoid/VSeeFace exports) give the angry brow
        // contribution somewhere to land; VRM has no standard brow preset.
        private static readonly HashSet<string> BrowDownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BrowDownLeft", "BrowDownRight" };
        private static void AddBrowDown(List<VrmFacialAdapter.Expression> result, IEnumerable<(string path, int index, float weight)> binds)
        {
            var merged = binds.Where(b => b.weight > 0)
                .GroupBy(b => (path: string.IsNullOrEmpty(b.path) ? "." : b.path, b.index))
                .Select(g => new VrmFacialAdapter.Morph { path = g.Key.path, index = g.Key.index, weight = Mathf.Min(100, g.Max(b => b.weight)) }).ToArray();
            if (merged.Length == 0 || result.Any(e => e.semantic == "brows.angry")) return;
            result.Add(new VrmFacialAdapter.Expression { semantic = "brows.angry", provenance = "inferred-arkit-name", binds = merged });
        }

        private static VrmFacialAdapter.Profile Profile(string mapping, List<VrmFacialAdapter.Expression> result, List<string> unsupported)
        {
            // Validation operates on the in-memory metadata from this very import;
            // the zero digest is a sentinel, never a claimed file hash.
            var profile = new VrmFacialAdapter.Profile { schemaVersion = 1, mapping = mapping, modelId = "runtime-vrm",
                bundleSha256 = new string('0',64), expressions = result.ToArray(), unsupportedFeatures = unsupported.ToArray() };
            VrmFacialAdapter.Validate(profile, profile.bundleSha256);
            return profile;
        }

        private void Release()
        {
            if (loaded && loaded.Root) { loaded.Root.SetActive(false); UnityEngine.Object.Destroy(loaded.Root); }
            loaded = null;
        }
        public void Dispose() { disposed = true; Release(); }
    }
}
