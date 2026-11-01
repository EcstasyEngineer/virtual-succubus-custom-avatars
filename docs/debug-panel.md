# Avatar debug panel

All debug tooling lives in one in-game panel (`src/DebugPanel.cs`). There are no file triggers, request files, config-reload watchers or automatic captures; nothing is written unless you press a button.

## Opening and closing

- **Shapeshifting → Avatar model → Debug panel** opens it for the current game run.
- `[Diagnostics] Enabled = true` starts every run in debug mode, with a small **Avatar debug** launcher in the top-left corner. The panel never writes this setting.
- **Close** hides the panel and leaves the launcher, so it can be reopened in a session where the Shapeshifting menu is unavailable. **Exit debug mode** removes the launcher, stops any probe and restores pose transfer. Debug mode always ends when the game exits.

## Face probes

Face probes need a bound original/replacement pair and the recognized native `Face` renderer; otherwise the panel says why and the other tools still work. Select Neutral, Happy/Joy, Furious/Angry, Surprised, Relaxed/Fun, Disgust (the game's `ALL_Sorrow` channel, which reads as disgust), an individual/bilateral blink, or one of the five native mouth shapes. **Capture live** holds the current native expression mixture. The visible recipe and strength identify what is being tested.

Probe buttons are synthetic morph probes; they do not invoke native expression FSMs or establish the full game's furious/shocked recipe. Neutral zeros the 34 recognized expression channels while retaining source morphology. Happy uses `ALL_Joy`; Relaxed uses `ALL_Fun`. They remain separate inputs.

**A: ORIGINAL** shows the native model; **B: REPLACEMENT** shows the replacement under the same held facial input. These are visibility switches, not model reloads. Camera, head/body animation and native material effects continue independently, so a held recipe alone does not guarantee identical pose, lighting or shader state in sequential screenshots.

Strength is 0–100%. Selecting a probe, changing strength or switching A/B renews a maximum 30-second hold. **Stop probe**, **Close**, expiry, exiting debug mode, scene change or rebind restores ordinary mapping and the replacement's visibility. The panel restores its prior native weights in early Update, then applies a fresh held recipe before late pose/facial transfer. It does not overwrite a newer game-owned value during cleanup and does not restore stale morph indices onto a changed mesh.

The scope is limited to recognized native Face expression morphs and visibility. It does not change game FSMs, body gestures, source morphology, gaze targets, shader effects, dialogue, audio or saves. `BepInEx/avatar-diagnostics/paired-face-state.json` records the last probe transition (recipe, strength, visibility, lease timing); it is not a continuous trace.

Missing destination capability is not a passed comparison. VRM 0.x has no surprise preset, so a model without a custom "Surprised" clip shows the open-mouth/wide-eye fallback instead of an authored expression; a model with no expressions at all stays neutral.

## Other tools

| Button | Writes | Notes |
|---|---|---|
| **Dump scene** | `avatar-diagnostics/animators-N.json` (8 rotating slots) | Animator/renderer/camera structure and visibility metadata for every loaded scene; about 1 MB. |
| **Dump face** | `avatar-diagnostics/face-capture.json` | Facial bones, Head/Eye/Jaw muscles and bounded blendshape weights of the bound pair, captured in LateUpdate after pose transfer. Needs a bound replacement. |
| **Transfer pose** | nothing | Toggles body pose transfer for this run. OFF shows the replacement's rest pose at the source root for visibility diagnosis; facial mapping still runs. |

None of these dumps contain dialogue, UI text, media or saves. Check the timestamp and scene before using an older dump.

## Native facial synchronization

Synchronization means transferring the original character's already-computed facial and mouth weights to the replacement through the ordinary adapter. The native `MTH_A/I/U/E/O` mapping remains; face probes can temporarily hold a static vowel morph for inspection. Nothing synthesizes or plays audio, analyzes dialogue, estimates phonemes or provides a speech-input API.

Verify native synchronization by comparing source and replacement expression weights and visible mouth motion during the same original animation. Mapping arithmetic and offline morph probes cannot establish timing or appearance across every native animation; runtime captures must identify the tested DLL, model and scene.

## Follow-ups

A simultaneous side-by-side face preview needs a separately owned preview rig/camera/render texture and verified cleanup, without duplicating gameplay scripts or changing the live camera. The panel currently switches sequentially between original and replacement.
