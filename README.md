# Virtual Succubus custom avatars

A BepInEx 5 plugin that replaces the Virtual Succubus character with your own **VRM 0.x or 1.0** model at runtime. Body animation is retargeted through Unity Humanoid, facial expressions/blinks/mouth shapes are mapped from the native face onto the model's VRM expressions, and the model is scaled so its head height matches the original.

No avatar is included; bring your own `.vrm`.

## Compatibility

| | |
|---|---|
| Game | **Tested against Virtual Succubus 0.45.** Not tested against 0.46 or later. |
| Engine | Unity 2022.3.8f1 (Mono, Windows x64) |
| Loader | BepInEx 5.4.23.2 (x64). BepInEx 6 / IL2CPP is not supported. |
| Models | Self-contained `.vrm` (VRM 0.x or 1.0) with a valid Humanoid rig, up to 256 MiB. `.vroid` project files and raw `.fbx` are not loadable. |

## Install

1. Close the game. Install [BepInEx 5 x64](https://docs.bepinex.dev/articles/user_guide/installation/index.html) beside `Virtual Succubus.exe` if you don't already have it, launch once, then quit.
2. In `BepInEx/config/BepInEx.cfg`, set:
   ```ini
   [Chainloader]
   HideManagerGameObject = true
   ```
   Without this the game destroys the plugin shortly after it loads.
3. Download `virtual-succubus-custom-avatars-v*.zip` from [Releases](https://github.com/EcstasyEngineer/virtual-succubus-custom-avatars/releases/latest) and copy its `BepInEx` folder into the game folder, merging with the existing one. You should end up with:
   ```text
   BepInEx/
     config/engineer.ecstasy.avatarreplacement.cfg
     plugins/
       EcstasyEngineer.AvatarReplacement.dll
       vrm-runtime/        (12 DLLs, vrm-runtime-shaders.bundle, licenses)
   ```
   The supplied config contains the game target paths the plugin needs; the auto-generated one leaves them blank. Updating replaces it, which resets your model selection.
4. Create `BepInEx/avatars/` and put your `.vrm` files directly in it (no subfolders).

## Use

Open the lobby's **Shapeshifting** menu and click **Avatar model** under the native entries. Pick a model; the original stays visible until the import finishes. The first load of a model decodes its textures and caches them under `BepInEx/cache/avatar-textures`, so later loads are faster. **Original (game model)** switches back. The selection persists across launches.

The picker's **Debug panel** button opens an in-game panel for comparing faces (A/B the original and replacement, hold individual expressions, dump the scene or face state). See [docs/debug-panel.md](docs/debug-panel.md).

### Config

| Setting | Default | Meaning |
|---|---|---|
| `[Replacement] Enabled` / `BundlePath` | set by picker | Whether a replacement is active, and which file. |
| `[Replacement] NormalizeHeight` | `true` | Match the original's rest-pose head height. Hair, ears and ahoge are ignored. |
| `[Face] Enabled` | set by picker | Map native facial expressions onto the model. |
| `[Diagnostics] Enabled` | `false` | Start each run with the debug launcher shown. |
| `[Target] *` | keep supplied | Game hierarchy paths for the lobby and session characters. |

### Faces

Expressions use the model's VRM presets (joy, angry, sorrow, fun, surprised, blink, A/I/U/E/O). A few native expressions are approximated on top of the presets: disgust adds a squint and a slight "U" mouth, relaxed and angry narrow the eyelids, angry adds brow-down when the model has `BrowDownLeft/Right` clips, and surprise falls back to an open mouth with widened eyes when the model has no surprised expression. Models without expressions animate their body only.

## Known limits

- Spring bones / cloth physics, gaze, native material effects and outfit customization are not carried over.
- Large or unusually proportioned models can clip with the scene.
- Coverage beyond the lobby and the standing session is limited.

## Uninstall

Delete `BepInEx/plugins/EcstasyEngineer.AvatarReplacement.dll` and `BepInEx/plugins/vrm-runtime/`. Your models, config and texture cache can stay or go.

## Building

The plugin compiles with the .NET SDK against your local game install and a UniVRM runtime build:

```powershell
dotnet build .\AvatarReplacement.csproj -c Release `
  '-p:GameDir=D:\Games\Virtual Succubus' `
  '-p:VrmRuntimeDir=D:\AvatarBuilds\vrm-runtime-output\managed'
```

The Release build is deterministic, path-mapped and ships no PDB.

The VRM runtime (UniVRM 0.131.3 at commit `9750dadc592f421c356bf0376c73e61abbe323e3`, plus its shaders) is built once with a Unity 2022.3.8f1 Editor:

```powershell
.\scripts\build-vrm-runtime.ps1 `
  -Editor 'C:\Program Files\Unity\Hub\Editor\2022.3.8f1\Editor\Unity.exe' `
  -ProjectPath 'D:\AvatarBuilds\vrm-runtime-project' `
  -OutputPath 'D:\AvatarBuilds\vrm-runtime-output' `
  -CreateProject
```

Tests are plain .NET console projects under `tests/`; set `VIRTUAL_GAME_DIR` (or pass `-p:GameDir`) and `dotnet run --project` each one.

`scripts/build-ani-bundle.ps1` and `unity-editor/BuildAniBundle.cs` support an experimental prepared-bundle route (`examples/model.example.json`) for FBX models converted in Unity ahead of time.

## Credits

The VRM runtime redistributes [UniVRM](https://github.com/vrm-c/UniVRM) (MIT) and [Unity.Mathematics](https://github.com/Unity-Technologies/Unity.Mathematics); their license texts ship in `vrm-runtime/licenses/` and `THIRD-PARTY-NOTICES.txt`. No game or Unity binaries are distributed. Only use models you have the right to use.
