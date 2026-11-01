# Model catalog filesystem tests

From the repository root on Windows:

```powershell
dotnet run --project tests/model-catalog/ModelCatalog.Tests.csproj -c Release '-p:GameDir=D:\Games\Virtual Succubus'
```

`VIRTUAL_GAME_DIR` can supply the default instead. The test resolves the installed BepInEx type definitions; no dependencies are redistributed and no Unity engine method runs.

Fixtures are uniquely created under the temporary directory and removed afterward. The tests cover manifest-free `.vrm` discovery, extension matching, ordering, top-level scope, file/count limits, legacy FBX manifests, duplicate names/IDs/targets, stale-state Refresh, unsafe paths, missing files and malformed manifests. File/directory symlink checks run when Windows permits creating links; otherwise the output explicitly reports skipped scenarios. Large boundary fixtures use file length changes, not an in-memory model allocation.

The raw files are inert size fixtures, not valid VRM models. Catalog acceptance is not import validation, visual correctness, asynchronous-load cancellation or runtime acceptance. Those remain separate in-game checks.
