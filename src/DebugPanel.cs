using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace EcstasyEngineer.AvatarReplacement
{
    // The plugin's only debug surface. Debug mode is a runtime switch: Diagnostics.Enabled
    // only sets its initial value, and nothing here writes configuration.
    // Paired face probes temporarily override native Face expression blendshapes before
    // the ordinary clone mapping. Writes are restored before ordinary game Update; no
    // game FSM, animation or audio is touched.
    [DefaultExecutionOrder(-10000)]
    internal sealed class DebugPanel : MonoBehaviour
    {
        private const string Prefix = "Face.M_F00_000_00_Fcl_";
        private static readonly string[] Channels = {
            "ALL_Angry", "ALL_Fun", "ALL_Joy", "ALL_Sorrow", "ALL_Surprised",
            "BRW_Angry", "BRW_Fun", "BRW_Joy", "BRW_Sorrow", "BRW_Surprised",
            "EYE_Natural", "EYE_Angry", "EYE_Close", "EYE_Close_R", "EYE_Close_L",
            "EYE_Joy", "EYE_Joy_R", "EYE_Joy_L", "EYE_Sorrow", "EYE_Surprised", "EYE_Extra",
            "MTH_Up", "MTH_Down", "MTH_Angry", "MTH_Neutral", "MTH_Fun", "MTH_Joy",
            "MTH_Sorrow", "MTH_Surprised", "MTH_A", "MTH_I", "MTH_U", "MTH_E", "MTH_O"
        };
        private readonly Dictionary<string, int> indices = new Dictionary<string, int>();
        private readonly Dictionary<int, float> previous = new Dictionary<int, float>();
        private readonly Dictionary<int, float> written = new Dictionary<int, float>();
        private Dictionary<string, float> recipe = new Dictionary<string, float>();
        private SkinnedMeshRenderer face;
        private Mesh faceMesh;
        private Animator source, clone;
        private Action<bool> visibility;
        private ManualLogSource log;
        private string outputDirectory, preset = "None", provenance = "No override", faceProblem, lastAction;
        private float expires, strength = 100, nextReadout;
        private bool debugMode, holding, showOriginal, faceDumpRequested, buildFailed;
        private RectTransform overlay, panel, launcher;
        private Text status, strengthText, transferText;
        private Font font;

        internal Func<string> DumpScene;
        internal Func<bool> TransferPose;
        internal Action ToggleTransferPose;
        internal Action ExitDebug;
        internal bool DebugMode => debugMode;
        internal bool IsHolding => holding && source && clone && face && Time.unscaledTime < expires;
        internal bool ShowOriginal => IsHolding && showOriginal;

        [Serializable] private sealed class State
        {
            public int schemaVersion = 1;
            public string capturedUtc, reason, preset, provenance, visibleModel;
            public int frame;
            public bool active;
            public float strengthPercent, secondsRemaining;
            public Weight[] nativeExpressionWeights;
            public string scope = "Temporary native Face expression blendshapes before ordinary clone mapping. Morphology, native material effects, body gestures, FSMs and audio are not overridden.";
        }
        [Serializable] private sealed class Weight { public string channel; public float weight; }

        internal void Initialize(ManualLogSource logger, string directory) { log = logger; outputDirectory = directory; }

        // The canvas is built lazily from Update/Open: a canvas created during plugin
        // Awake (before the first scene) was never drawn in the tested game.
        internal void SetDebugMode(bool value)
        {
            if (debugMode == value) return;
            debugMode = value;
            if (!value) Close();
            if (overlay) overlay.gameObject.SetActive(value);
            log?.LogInfo("Avatar debug mode " + (value ? "on." : "off."));
        }
        internal void Open()
        {
            SetDebugMode(true);
            if (!EnsureOverlay()) return;
            panel.gameObject.SetActive(true); launcher.gameObject.SetActive(false); nextReadout = 0;
            log?.LogInfo("Avatar debug panel opened.");
        }
        private bool EnsureOverlay()
        {
            if (overlay) { overlay.gameObject.SetActive(debugMode); return true; }
            try { Build(); overlay.gameObject.SetActive(debugMode); log?.LogInfo("Avatar debug panel built."); return true; }
            catch (Exception error)
            {
                log?.LogWarning("Avatar debug panel unavailable: " + error.Message);
                if (overlay) Destroy(overlay.gameObject);
                overlay = null;
                return false;
            }
        }
        internal void Close()
        {
            Stop("closed");
            if (panel) panel.gameObject.SetActive(false);
            if (launcher) launcher.gameObject.SetActive(true);
        }

        internal void Bind(Animator original, Animator replacement, Action<bool> setVisibility)
        {
            Unbind();
            source = original; clone = replacement; visibility = setVisibility;
            var candidates = original.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.name == "Face" && r.sharedMesh && Channels.All(c => r.sharedMesh.GetBlendShapeIndex(Prefix + c) >= 0)).ToArray();
            if (candidates.Length != 1) { faceProblem = "Face probes need one recognized native Face renderer; found " + candidates.Length + "."; return; }
            face = candidates[0]; faceMesh = face.sharedMesh; faceProblem = null;
            foreach (var channel in Channels) indices.Add(channel, face.sharedMesh.GetBlendShapeIndex(Prefix + channel));
        }
        internal void Unbind()
        {
            Stop("unbound"); source = null; clone = null; face = null; faceMesh = null; visibility = null; indices.Clear();
            faceProblem = "No replacement is bound.";
        }

        // Plugin.LateUpdate performs the face dump after pose transfer.
        internal bool ConsumeFaceDumpRequest() { var value = faceDumpRequested; faceDumpRequested = false; return value; }
        internal void Report(string action) { lastAction = action; nextReadout = 0; }

        private void Update()
        {
            try
            {
                RestoreWrites();
                if (debugMode && !overlay && !buildFailed) buildFailed = !EnsureOverlay();
                if (holding && (!debugMode || !source || !clone || !face || face.sharedMesh != faceMesh || !source.gameObject.activeInHierarchy || Time.unscaledTime >= expires))
                    Stop("expired-or-unavailable");
                if (status && panel.gameObject.activeSelf && Time.unscaledTime >= nextReadout)
                {
                    nextReadout = Time.unscaledTime + .1f;
                    var probe = IsHolding ? preset + " | " + (ShowOriginal ? "A: ORIGINAL" : "B: REPLACEMENT") +
                        " | " + Mathf.CeilToInt(expires - Time.unscaledTime) + "s\n" + RecipeDescription() + "\n" + provenance
                        : faceProblem ?? "Ordinary mapping. Select a probe to hold both faces for 30 seconds.";
                    status.text = probe + (lastAction == null ? "" : "\n" + lastAction);
                    if (transferText && TransferPose != null) transferText.text = "Transfer pose: " + (TransferPose() ? "ON" : "OFF");
                }
            }
            catch (Exception error) { Fail(error); }
        }
        internal void ApplyBeforePose()
        {
            if (!IsHolding) return;
            if (face.sharedMesh != faceMesh) { Stop("native-mesh-changed"); return; }
            try
            {
                RestoreWrites();
                foreach (var item in indices)
                {
                    var value = recipe.TryGetValue(item.Key, out var selected) ? selected * strength / 100 : 0;
                    previous.Add(item.Value, face.GetBlendShapeWeight(item.Value));
                    written.Add(item.Value, value);
                    face.SetBlendShapeWeight(item.Value, value);
                }
            }
            catch (Exception error) { Fail(error); }
        }
        private void RestoreWrites()
        {
            try
            {
                if (face && face.sharedMesh == faceMesh)
                    foreach (var item in previous)
                        // A later game writer owns its new value; never overwrite it.
                        if (Mathf.Abs(face.GetBlendShapeWeight(item.Key) - written[item.Key]) < .0001f)
                            face.SetBlendShapeWeight(item.Key, item.Value);
            }
            finally { previous.Clear(); written.Clear(); }
        }
        private void Hold(string name, string evidence, Dictionary<string, float> values)
        {
            if (!face || !source || !clone) { Report(faceProblem); return; }
            RestoreWrites();
            preset = name; provenance = evidence; recipe = values;
            holding = true; expires = Time.unscaledTime + 30;
            visibility?.Invoke(showOriginal); nextReadout = 0; Record("selected");
        }
        private void CaptureLive()
        {
            RestoreWrites();
            if (!face) { Report(faceProblem); return; }
            Hold("Captured live face", "Snapshot of current native expression channels; material state remains live.",
                indices.Where(p => Mathf.Abs(face.GetBlendShapeWeight(p.Value)) > .0001f)
                    .ToDictionary(p => p.Key, p => face.GetBlendShapeWeight(p.Value)));
        }
        private void SelectVisibility(bool original)
        {
            if (!IsHolding) return;
            showOriginal = original; expires = Time.unscaledTime + 30;
            visibility?.Invoke(original); nextReadout = 0; Record("visibility");
        }
        private void Stop(string reason)
        {
            var wasHolding = holding;
            holding = false; RestoreWrites(); showOriginal = false; visibility?.Invoke(false);
            if (wasHolding) Record(reason);
        }
        private void Fail(Exception error)
        {
            log?.LogWarning("Debug panel action stopped: " + error.Message);
            Report("Error: " + error.Message);
            Stop("error");
        }
        private string RecipeDescription() => recipe.Count == 0 ? "All 34 expression channels = 0; morphology preserved."
            : string.Join(", ", recipe.Select(p => p.Key + "=" + (p.Value * strength / 100).ToString("0.#"))) + "; other expression channels = 0.";
        private void Record(string reason)
        {
            log?.LogInfo("Paired face check: " + reason + "; " + preset + "; " + (ShowOriginal ? "original" : "replacement") + "; " + RecipeDescription());
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var value = new State { capturedUtc = DateTime.UtcNow.ToString("O"), reason = reason, preset = preset,
                    provenance = provenance, frame = Time.frameCount, active = IsHolding, strengthPercent = strength,
                    visibleModel = ShowOriginal ? "original" : "replacement", secondsRemaining = IsHolding ? Mathf.Max(0, expires - Time.unscaledTime) : 0,
                    nativeExpressionWeights = Channels.Select(c => new Weight { channel = c, weight = recipe.TryGetValue(c, out var w) ? w * strength / 100 : 0 }).ToArray() };
                File.WriteAllText(Path.Combine(outputDirectory, "paired-face-state.json"), JsonUtility.ToJson(value, true));
            }
            catch (Exception error) { log?.LogWarning("Paired face state report failed: " + error.Message); }
        }
        private void Build()
        {
            font = UiKit.FindFont();
            overlay = UiKit.Overlay("EcstasyEngineer.DebugPanel", 31001);
            launcher = UiKit.Button(font, "Launcher", "Avatar debug", overlay, 20, 20, 200, 36, Open, Fail).GetComponent<RectTransform>();
            panel = UiKit.Panel("Panel", overlay, 20, 20, 620, 610);
            Label("Title", "Avatar debug", 18, 14, 590, 28, 21);
            Label("Scope", "Face probes are temporary facial morphs only; each click renews a 30-second hold.\nPure-channel probes are not claimed to be full game expression recipes.", 18, 50, 585, 48, 15);
            Probe("Neutral", 18, 108, "Neutral probe", null);
            Probe("Happy / Joy", 216, 108, "Happy probe", "ALL_Joy");
            Probe("Furious / Angry", 414, 108, "Angry probe", "ALL_Angry");
            Probe("Surprised", 18, 151, "Surprised probe", "ALL_Surprised");
            Probe("Relaxed / Fun", 216, 151, "Fun probe", "ALL_Fun");
            Probe("Disgust (Sorrow)", 414, 151, "Disgust probe (ALL_Sorrow)", "ALL_Sorrow");
            Probe("Blink left", 18, 194, "Left blink probe", "EYE_Close_L");
            Probe("Blink right", 216, 194, "Right blink probe", "EYE_Close_R");
            Probe("Blink both", 414, 194, "Bilateral blink probe", "EYE_Close");
            Probe("Mouth A", 18, 237, "Mouth A probe", "MTH_A");
            Probe("Mouth I", 216, 237, "Mouth I probe", "MTH_I");
            Probe("Mouth U", 414, 237, "Mouth U probe", "MTH_U");
            Probe("Mouth E", 18, 280, "Mouth E probe", "MTH_E");
            Probe("Mouth O", 216, 280, "Mouth O probe", "MTH_O");
            Button("Capture live", 414, 280, CaptureLive);
            strengthText = Label("Strength", "Strength: 100%", 18, 334, 175, 26, 16);
            var track = UiKit.Rect("Strength slider", panel); UiKit.Place(track, 200, 333, 398, 23);
            track.gameObject.AddComponent<Image>().color = new Color(.15f, .19f, .25f);
            var handle = UiKit.Rect("Handle", track); handle.sizeDelta = new Vector2(20, 30); handle.gameObject.AddComponent<Image>().color = Color.white;
            var slider = track.gameObject.AddComponent<Slider>(); slider.handleRect = handle; slider.targetGraphic = handle.GetComponent<Image>();
            slider.minValue = 0; slider.maxValue = 100; slider.wholeNumbers = true; slider.SetValueWithoutNotify(strength);
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.onValueChanged.AddListener(value => { strength = value; strengthText.text = "Strength: " + value.ToString("0") + "%"; if (IsHolding) { expires = Time.unscaledTime + 30; Record("strength"); } nextReadout = 0; });
            Button("A: ORIGINAL", 18, 374, () => SelectVisibility(true));
            Button("B: REPLACEMENT", 216, 374, () => SelectVisibility(false));
            Button("Stop probe", 414, 374, () => Stop("stopped"));
            Button("Dump scene", 18, 417, () => { var path = DumpScene?.Invoke(); Report(path == null ? "Scene dump failed; see log." : "Scene dump: " + path); });
            Button("Dump face", 216, 417, () => { faceDumpRequested = true; Report("Face dump requested; needs a bound replacement."); });
            transferText = Button("Transfer pose", 414, 417, () => ToggleTransferPose?.Invoke()).GetComponentInChildren<Text>();
            Button("Close", 18, 460, Close);
            Button("Exit debug mode", 216, 460, () => ExitDebug?.Invoke());
            status = Label("Status", "", 18, 508, 584, 96, 15);
            panel.gameObject.SetActive(false);
        }
        private void Probe(string title, float x, float y, string name, string channel)
        {
            Button(title, x, y, () => Hold(name, "Synthetic native morph probe, not a recovered game preset.",
                channel == null ? new Dictionary<string, float>() : new Dictionary<string, float> { { channel, 100 } }));
        }
        private Text Label(string name, string content, float x, float y, float width, float height, int size)
            => UiKit.Label(font, name, content, panel, x, y, width, height, size);
        private Button Button(string title, float x, float y, Action clicked)
            => UiKit.Button(font, title, title, panel, x, y, 188, 36, clicked, Fail);
        private void OnDisable() { Close(); }
        private void OnDestroy() { Unbind(); if (overlay) Destroy(overlay.gameObject); }
    }
}
