# Prepared VRM facial adapter tests

Run from the repository root with an explicit compatible local game path:

```powershell
dotnet run --project tests/vrm-face/VrmFace.Tests.csproj -c Release '-p:GameDir=D:\Games\Virtual Succubus'
```

Alternatively set `VIRTUAL_GAME_DIR`. Unity references resolve locally for type definitions; engine binaries and build output are not distributed.

To additionally check a real newly prepared bundle and its adjacent `.vrmface.json`, append `-- --bundle '<prepared bundle path>'` to the command. This hashes the bundle bytes and validates the exported JSON; it does not load the model or prove that its renderer paths and morph indices match the geometry.

These tests exercise profile bounds, semantic uniqueness, morph binding validation, required JSON properties, nested serialization roundtrips, exporter-shaped JSON, actual file hash pairing, and continuous/binary normalized weights. The binary midpoint expectation follows the pinned VRM0 implementation's `Mathf.Round` behavior on 0..1 inputs: exactly 0.5 becomes zero.

No Unity engine methods, game scenes, rendering, model import or avatar assets are used. Passing does not establish correct morph indices in a real model, expression appearance, material support, runtime cleanup, gaze or live integration.
