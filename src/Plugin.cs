using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EcstasyEngineer.AvatarReplacement
{
    [DefaultExecutionOrder(10000)]
    [BepInPlugin("engineer.ecstasy.avatarreplacement", "Avatar Replacement Prototype", "0.3.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> activeConfig;
        private ConfigEntry<bool> diagnosticsEnabled;
        private bool transferPose = true;
        private ConfigEntry<bool> facialEnabled;
        private ConfigEntry<bool> normalizeHeight;
        private float heightScale = 1;
        private AniFacialAdapter facialAdapter;
        private VrmFacialAdapter vrmFacialAdapter;
        private DebugPanel debugPanel;
        private string facialStatus = "Facial mapping disabled.";
        private RuntimeDiagnostics diagnostics;
        private ModelMenu modelMenu;
        private string lastFailure;
        private ConfigEntry<string> scene, animatorPath, bundlePath, prefabName, rendererPaths;
        private ConfigEntry<bool> automaticTargets, characterMeshes;
        private ConfigEntry<string> sessionScene, sessionPath, sessionContainer, sessionCharacterName;
        private float nextTargetCheck;
        private Animator original, replacement;
        private GameObject instance;
        private Renderer[] replacementRenderers = Array.Empty<Renderer>();
        private bool[] replacementRenderingOff = Array.Empty<bool>();
        private AssetBundle bundle;
        private VrmRuntimeLoader vrmLoader;
        private HumanPoseHandler readPose, writePose;
        private HumanPose pose;
        private AnimatorCullingMode originalCulling;
        private bool changedCulling;
        private readonly Dictionary<Renderer, bool> hidden = new Dictionary<Renderer, bool>();
        private float nextAttempt;
        private volatile bool configChanged;
        private bool observedUpdate;
        private string DiagnosticsDirectory => Path.Combine(Paths.BepInExRootPath, "avatar-diagnostics");

        private void Awake()
        {
            activeConfig = Config.Bind("Replacement", "Enabled", false, "Off by default; select a local VRM or experimental prepared FBX bundle.");
            diagnosticsEnabled = Config.Bind("Diagnostics", "Enabled", false, "Start with the in-game avatar debug launcher shown. The panel can also be opened from Options > Avatar model > Debug panel. Dumps go to BepInEx/avatar-diagnostics; no dialogue, media or saves.");
            facialEnabled = Config.Bind("Face", "Enabled", false, "Use authored expressions from the loaded VRM (0.x or 1.0), or experimental Ani FBX mapping. Missing facial controls stay inactive; gaze currently follows Humanoid pose transfer.");
            diagnostics = new RuntimeDiagnostics(DiagnosticsDirectory, Logger);
            scene = Config.Bind("Target", "Scene", "", "Exact loaded scene name. Blank refuses binding.");
            animatorPath = Config.Bind("Target", "AnimatorPath", "", "Exact scene-root/child path of the original humanoid Animator.");
            rendererPaths = Config.Bind("Target", "RendererPaths", "", "Semicolon-separated exact paths relative to AnimatorPath. Only Renderers directly on these transforms are hidden. Use . for the Animator transform.");
            automaticTargets = Config.Bind("Target", "AutomaticSession", false, "Prefer the exact session target when active, otherwise use the configured lobby target.");
            characterMeshes = Config.Bind("Target", "CharacterMeshes", false, "Hide skinned character surfaces and rigid meshes attached under the humanoid hips, including inactive outfit alternatives. Keeps enabled flags under game control.");
            sessionScene = Config.Bind("Target", "SessionScene", "SessionScene", "Exact session scene name.");
            sessionPath = Config.Bind("Target", "SessionAnimatorPath", "Root/SceneContainer/Scene1 (Basic Scene)/SuccuPosition/GirlCharacter", "Exact session character Animator path.");
            sessionContainer = Config.Bind("Target", "SessionContainer", "Root/SceneContainer", "Allow session reparenting only below this exact hierarchy prefix; blank uses the exact session path only.");
            sessionCharacterName = Config.Bind("Target", "SessionCharacterName", "GirlCharacter", "Exact Humanoid Animator object name beneath SessionContainer. Ambiguous matches refuse binding.");
            bundlePath = Config.Bind("Replacement", "BundlePath", "", "Local .vrm file (VRM 0.x or 1.0, imported directly), or experimental prepared Windows AssetBundle. Legacy setting name retained for compatibility; raw FBX requires preparation.");
            prefabName = Config.Bind("Replacement", "Prefab", "assets/avatarreplacement/ani-kimono.prefab", "Exact prefab asset name inside the bundle.");
            normalizeHeight = Config.Bind("Replacement", "NormalizeHeight", true, "Scale the replacement so its rest-pose head top matches the original. Hair and accessories on child bones of the head (ahoge, ears) are ignored.");
            Config.SettingChanged += SettingsChanged;
            EnsureMenus();
            debugPanel.SetDebugMode(diagnosticsEnabled.Value);
            SceneManager.sceneUnloaded += SceneUnloaded;
            SceneManager.sceneLoaded += SceneLoaded;
            Logger.LogInfo("Prototype loaded, disabled by default. No game or asset files are patched.");
        }

        private void SettingsChanged(object sender, SettingChangedEventArgs args)
        {
            if (args.ChangedSetting == diagnosticsEnabled) debugPanel?.SetDebugMode(diagnosticsEnabled.Value);
            else configChanged = true;
        }
        private void EnsureMenus()
        {
            if (!debugPanel)
            {
                debugPanel = gameObject.AddComponent<DebugPanel>();
                debugPanel.Initialize(Logger, DiagnosticsDirectory);
                debugPanel.DumpScene = () => diagnostics.Capture("debug-panel");
                debugPanel.TransferPose = () => transferPose;
                debugPanel.ToggleTransferPose = () => transferPose = !transferPose;
                debugPanel.ExitDebug = () => { transferPose = true; debugPanel.SetDebugMode(false); };
            }
            if (modelMenu == null)
            {
                modelMenu = new ModelMenu(Paths.BepInExRootPath, SelectModel, ModelStatus, Logger);
                modelMenu.DebugPanelRequested = () => debugPanel.Open();
            }
        }
        private void SelectModel(ModelCatalog.Entry entry)
        {
            if (!isActiveAndEnabled) return;
            Restore();
            activeConfig.Value = false;
            if (entry != null)
            {
                bundlePath.Value = entry.BundlePath;
                prefabName.Value = entry.Prefab;
                if (entry.Format == "vrm") facialEnabled.Value = true;
                activeConfig.Value = true;
            }
            Config.Save();
            lastFailure = null;
            configChanged = true;
        }
        private string ModelStatus()
        {
            if (!activeConfig.Value) return "Original model selected.";
            if (instance && original) return "Custom model active in " + original.gameObject.scene.name + ". " + facialStatus;
            if (vrmLoader != null && !vrmLoader.Loading.IsCompleted) return "Loading local VRM; original remains visible.";
            return "Original visible. " + (lastFailure ?? "Waiting for the configured character.");
        }
        private void SceneUnloaded(Scene value) { Restore(keepModel: true); nextAttempt = Time.unscaledTime + 2; }
        private void SceneLoaded(Scene value, LoadSceneMode mode) { Restore(keepModel: true); nextAttempt = Time.unscaledTime + 2; }
        private void Update()
        {
            EnsureMenus();
            if (!observedUpdate) { observedUpdate = true; Logger.LogInfo("Main-thread update active; diagnostics=" + diagnosticsEnabled.Value + "."); }
            if (configChanged) { configChanged = false; Restore(); nextAttempt = 0; }
            modelMenu?.Tick();
            if (!activeConfig.Value) { if (instance || bundle || vrmLoader != null) Restore(); return; }
            // External destruction of our object must not leave the original hidden until retry.
            if ((!instance || !instance.activeInHierarchy) && hidden.Count != 0) { Restore(); nextAttempt = Time.unscaledTime + 2; }
            if (instance && original && original.gameObject.activeInHierarchy)
            {
                if (!automaticTargets.Value || Time.unscaledTime < nextTargetCheck) return;
                nextTargetCheck = Time.unscaledTime + 1;
                var preferred = FindTarget();
                if (preferred == original) return;
                Restore(keepModel: true); nextAttempt = 0;
            }
            if (instance) Restore(keepModel: true);
            if (Time.unscaledTime < nextAttempt) return;
            // Import the selected VRM while the game is still starting, so it is ready when the character appears.
            if (vrmLoader == null && IsDirectVrm && File.Exists(bundlePath.Value)) StartVrmLoad();
            if (vrmLoader != null && !vrmLoader.Loading.IsCompleted) return;
            if (!FindTarget()) { nextAttempt = Time.unscaledTime + 1; lastFailure = "Waiting for the configured character."; return; }
            nextAttempt = Time.unscaledTime + 10;
            try { Bind(); lastFailure = null; }
            catch (Exception e)
            {
                Restore();
                // Full type and stack once per distinct failure; a bare message cannot locate importer errors.
                if (e.Message != lastFailure) Logger.LogWarning("Replacement not activated: " + e);
                lastFailure = e.Message;
            }
        }

        private bool IsDirectVrm => Path.IsPathRooted(bundlePath.Value) &&
            string.Equals(Path.GetExtension(bundlePath.Value), ".vrm", StringComparison.OrdinalIgnoreCase);
        private void StartVrmLoad()
        {
            vrmLoader = new VrmRuntimeLoader(bundlePath.Value, Path.Combine(Path.GetDirectoryName(Info.Location), "vrm-runtime"),
                Path.Combine(Paths.CachePath, "avatar-textures"), Logger.LogInfo);
            Logger.LogInfo("Loading local VRM directly; no avatar conversion or bundle is created.");
        }

        private void Bind()
        {
            if (string.IsNullOrWhiteSpace(scene.Value) || string.IsNullOrWhiteSpace(animatorPath.Value))
                throw new InvalidOperationException("Exact Scene and AnimatorPath are required.");
            original = FindTarget();
            if (!original) throw new InvalidOperationException("No unique active configured target Animator.");
            if (!original.isHuman || !original.avatar || !original.avatar.isValid)
                throw new InvalidOperationException("Target has no valid Humanoid avatar. Generic rigs are unsupported.");
            var paths = rendererPaths.Value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Distinct().ToArray();
            if (paths.Length == 0 && !characterMeshes.Value) throw new InvalidOperationException("Explicit RendererPaths or the reviewed character-mesh policy are required.");
            var selected = new List<Renderer>();
            if (characterMeshes.Value)
            {
                var hips = original.GetBoneTransform(HumanBodyBones.Hips);
                selected.AddRange(original.GetComponentsInChildren<Renderer>(true).Where(r =>
                    r is SkinnedMeshRenderer || (r is MeshRenderer && hips && r.transform.IsChildOf(hips))));
            }
            else foreach (var p in paths)
            {
                var transforms = original.GetComponentsInChildren<Transform>(true).Where(t =>
                    (p.StartsWith("@", StringComparison.Ordinal) ? HierarchyPaths.IndexedRelative(t, original.transform) :
                    HierarchyPaths.Relative(t, original.transform)) == p).ToArray();
                if (transforms.Length != 1) throw new InvalidOperationException("Renderer transform missing or ambiguous: " + p);
                var renderers = transforms[0].GetComponents<Renderer>();
                if (renderers.Length == 0) throw new InvalidOperationException("No renderer at: " + p);
                selected.AddRange(renderers);
            }
            // Rigid accessories may use a separate effects layer. The avatar follows
            // the visible skinned surfaces, while both kinds remain hidden below.
            var layers = selected.OfType<SkinnedMeshRenderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).Select(r => r.gameObject.layer).Distinct().ToArray();
            if (layers.Length != 1)
                throw new InvalidOperationException("Active source skinned meshes must identify one camera layer.");
            if (!Path.IsPathRooted(bundlePath.Value) || !File.Exists(bundlePath.Value))
                throw new InvalidOperationException("BundlePath must name an existing absolute file.");
            var directVrm = IsDirectVrm;
            if (directVrm)
            {
                if (vrmLoader == null) StartVrmLoad();
                if (!vrmLoader.Loading.IsCompleted) return;
                vrmLoader.Loading.GetAwaiter().GetResult();
                instance = vrmLoader.Root;
                if (!instance) throw new InvalidOperationException("VRM import was cancelled or produced no model.");
            }
            else
            {
            bundle = AssetBundle.LoadFromFile(bundlePath.Value);
            if (!bundle) throw new InvalidOperationException("AssetBundle could not be loaded.");
            var prefab = bundle.LoadAsset<GameObject>(prefabName.Value);
            if (!prefab) throw new InvalidOperationException("Configured prefab not found in bundle.");
            // Inspect the asset before Instantiate: arbitrary MonoBehaviour Awake must not run.
            if (prefab.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
                throw new InvalidOperationException("Prefab contains scripts. Export a model-only prefab.");
            if (prefab.GetComponentsInChildren<Animator>(true).Length != 1 || !prefab.GetComponent<Animator>())
                throw new InvalidOperationException("Prefab must have exactly one Animator, on its root.");
            var avatar = prefab.GetComponent<Animator>().avatar;
            if (!avatar || !avatar.isValid || !avatar.isHuman)
                throw new InvalidOperationException("Replacement prefab has no valid Humanoid avatar.");
            if (prefab.activeSelf) throw new InvalidOperationException("Export the prefab root inactive to avoid a visible setup frame.");
            instance = Instantiate(prefab);
            }
            instance.name = "EcstasyEngineer.AvatarReplacement";
            replacementRenderers = instance.GetComponentsInChildren<Renderer>(true);
            replacementRenderingOff = replacementRenderers.Select(r => r.forceRenderingOff).ToArray();
            // Game cameras may exclude the prefab's authored Default layer.
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true)) r.gameObject.layer = layers[0];
            // A directly loaded VRM stays in DontDestroyOnLoad so scene changes can reuse it
            // instead of importing the file again; bundles follow the original's scene.
            if (directVrm || original.gameObject.scene.name == "DontDestroyOnLoad") DontDestroyOnLoad(instance);
            else SceneManager.MoveGameObjectToScene(instance, original.gameObject.scene);
            replacement = instance.GetComponent<Animator>();
            replacement.runtimeAnimatorController = null;
            replacement.applyRootMotion = false;
            replacement.enabled = false;
            readPose = new HumanPoseHandler(original.avatar, original.transform);
            writePose = new HumanPoseHandler(replacement.avatar, replacement.transform);
            heightScale = 1;
            if (normalizeHeight.Value)
            {
                try
                {
                    heightScale = AvatarHeight.Factor(AvatarHeight.Read(original), AvatarHeight.Read(replacement), out var heightDetail);
                    Logger.LogInfo(heightDetail + " humanScale ratio " + (original.humanScale / replacement.humanScale).ToString("F3") + ".");
                }
                catch (Exception error) { heightScale = 1; Logger.LogWarning("Height normalization skipped: " + error.Message); }
            }
            originalCulling = original.cullingMode;
            original.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            changedCulling = true;
            facialStatus = "Facial mapping disabled.";
            if (facialEnabled.Value)
            {
                try
                {
                    if (directVrm)
                    {
                        vrmFacialAdapter = VrmFacialAdapter.Bind(original, replacement, vrmLoader.FacialProfile);
                        facialStatus = vrmFacialAdapter.Status;
                    }
                    else
                    {
                        var profileId = FacialProfile.VerifyBundle(bundlePath.Value);
                        facialAdapter = AniFacialAdapter.Bind(original, replacement);
                        facialStatus = "FBX experimental face mapping active: " + profileId + ".";
                    }
                }
                catch (Exception error) { facialStatus = "Facial mapping unavailable."; Logger.LogWarning("Facial adapter refused: " + error.Message); }
            }
            CopyPose();
            instance.SetActive(true);
            // activeSelf on a leaf does not account for disabled intermediate parents.
            // Check the complete hierarchy before hiding any source renderer.
            if (!instance.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled && r.gameObject.activeInHierarchy))
                throw new InvalidOperationException("Replacement has no active, enabled renderer.");
            foreach (var r in selected.Distinct()) { hidden.Add(r, r.forceRenderingOff); r.forceRenderingOff = true; }
            try { debugPanel.Bind(original, replacement, SetPairedVisibility); }
            catch (Exception error) { debugPanel.Unbind(); Logger.LogWarning("Debug panel face probes unavailable: " + error.Message); }
            Logger.LogInfo("Humanoid pose replacement activated at " + original.gameObject.scene.name + "/" + HierarchyPaths.Full(original.transform) + "; hidden surfaces=" + hidden.Count + ". " + facialStatus + " Secondary motion is not mapped.");
        }

        private Animator FindTarget()
        {
            var all = Resources.FindObjectsOfTypeAll<Animator>().Where(a => a.gameObject.scene.IsValid() && a.gameObject.activeInHierarchy).ToArray();
            if (automaticTargets.Value)
            {
                var session = all.Where(a => a.gameObject.scene.name == sessionScene.Value && a.isHuman &&
                    (HierarchyPaths.Full(a.transform) == sessionPath.Value ||
                     (!string.IsNullOrWhiteSpace(sessionContainer.Value) && a.name == sessionCharacterName.Value &&
                      HierarchyPaths.Full(a.transform).StartsWith(sessionContainer.Value.TrimEnd('/') + "/", StringComparison.Ordinal)))).ToArray();
                if (session.Length > 1) return null;
                if (session.Length == 1) return session[0];
            }
            var menu = all.Where(a => a.gameObject.scene.name == scene.Value && HierarchyPaths.Full(a.transform) == animatorPath.Value).ToArray();
            return menu.Length == 1 ? menu[0] : null;
        }

        private void CopyPose()
        {
            if (debugPanel) debugPanel.ApplyBeforePose();
            var scale = original.transform.lossyScale;
            if (scale.x <= 0 || Mathf.Abs(scale.x - scale.y) > 0.001f || Mathf.Abs(scale.x - scale.z) > 0.001f)
                throw new InvalidOperationException("Positive uniform source scale is required.");
            var matrix = original.transform.localToWorldMatrix;
            var x = ((Vector3)matrix.GetColumn(0)).normalized;
            var y = ((Vector3)matrix.GetColumn(1)).normalized;
            var z = ((Vector3)matrix.GetColumn(2)).normalized;
            if (Mathf.Abs(Vector3.Dot(x, y)) > 0.001f || Mathf.Abs(Vector3.Dot(x, z)) > 0.001f || Mathf.Abs(Vector3.Dot(y, z)) > 0.001f)
                throw new InvalidOperationException("Sheared source transforms are unsupported.");
            instance.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
            instance.transform.localScale = scale * heightScale;
            if (!transferPose) { facialAdapter?.Apply(); vrmFacialAdapter?.Apply(); return; }
            readPose.GetHumanPose(ref pose);
            // GetHumanPose is expressed in the source parent's space, normalized by
            // humanScale. SetHumanPose expects avatar-root-relative coordinates.
            // Scene-root-only tests conceal this distinction because local == world.
            var inverseRotation = Quaternion.Inverse(original.transform.localRotation);
            pose.bodyPosition = inverseRotation * (pose.bodyPosition - original.transform.localPosition / original.humanScale);
            pose.bodyRotation = inverseRotation * pose.bodyRotation;
            writePose.SetHumanPose(ref pose);
            facialAdapter?.Apply();
            vrmFacialAdapter?.Apply();
        }

        private void LateUpdate()
        {
            if (!instance) return;
            if (!instance.activeInHierarchy) { Restore(); nextAttempt = Time.unscaledTime + 2; return; }
            if (!original || !original.gameObject.activeInHierarchy || !activeConfig.Value) { Restore(keepModel: activeConfig.Value); return; }
            try
            {
                CopyPose();
                SetPairedVisibility(debugPanel && debugPanel.ShowOriginal);
                if (debugPanel && debugPanel.ConsumeFaceDumpRequest())
                {
                    var path = FacialDiagnostics.Capture(DiagnosticsDirectory, original, replacement, Logger);
                    debugPanel.Report(path == null ? "Face dump failed; see log." : "Face dump: " + path);
                }
            }
            catch (Exception e) { Logger.LogError("Pose update failed; restoring original: " + e.Message); Restore(); nextAttempt = Time.unscaledTime + 10; }
        }

        // keepModel parks an imported VRM (inactive, DontDestroyOnLoad) for the next Bind
        // instead of destroying it; model changes, errors and disabling still release it.
        private void Restore(bool keepModel = false)
        {
            try { if (debugPanel) debugPanel.Unbind(); }
            catch (Exception error) { Logger.LogWarning("Debug panel cleanup: " + error.Message); }
            try { facialAdapter?.Dispose(); }
            catch (Exception error) { Logger.LogWarning("Clone face cleanup: " + error.Message); }
            facialAdapter = null;
            try { vrmFacialAdapter?.Dispose(); }
            catch (Exception error) { Logger.LogWarning("VRM face cleanup: " + error.Message); }
            vrmFacialAdapter = null;
            foreach (var pair in hidden) if (pair.Key) pair.Key.forceRenderingOff = pair.Value;
            hidden.Clear();
            heightScale = 1;
            if (original && changedCulling) original.cullingMode = originalCulling;
            changedCulling = false;
            // A loader that is still importing is kept too, so a scene load during the startup
            // preload does not throw the import away.
            if (instance)
            {
                instance.SetActive(false);
                if (!(keepModel && vrmLoader != null && instance == vrmLoader.Root)) Destroy(instance);
            }
            if (!keepModel) { vrmLoader?.Dispose(); vrmLoader = null; }
            instance = null; original = null; replacement = null;
            replacementRenderers = Array.Empty<Renderer>();
            replacementRenderingOff = Array.Empty<bool>();
            readPose?.Dispose(); writePose?.Dispose(); readPose = null; writePose = null;
            if (bundle) bundle.Unload(true);
            bundle = null;
        }

        private void SetPairedVisibility(bool showOriginal)
        {
            foreach (var pair in hidden) if (pair.Key) pair.Key.forceRenderingOff = showOriginal ? pair.Value : true;
            if (instance) for (var i = 0; i < replacementRenderers.Length; i++)
                if (replacementRenderers[i]) replacementRenderers[i].forceRenderingOff = showOriginal || replacementRenderingOff[i];
        }

        private void OnDisable()
        {
            Logger.LogInfo("Plugin disabled; restoring original avatar."); Restore();
            modelMenu?.Dispose(); modelMenu = null;
        }
        private void OnDestroy()
        {
            Logger.LogInfo("Plugin destroyed; restoring original avatar.");
            Config.SettingChanged -= SettingsChanged;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            SceneManager.sceneLoaded -= SceneLoaded;
            modelMenu?.Dispose();
            Restore();
        }
    }
}
