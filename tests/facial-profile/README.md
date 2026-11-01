# Facial-profile checks

Provide the compatible local game directory through `VIRTUAL_GAME_DIR` or `-p:GameDir=...`, then run from the repository root:

```powershell
dotnet run --project tests/facial-profile/FacialProfile.Tests.csproj -c Release
```

These checks validate schema/model/mapping/jaw-policy restrictions and a serialized profile paired to actual temporary bytes. Missing or oversized sidecars and changed bundle bytes must fail. The temporary bundle is inert hash-test data, not a Unity AssetBundle. No game or Unity Editor is launched. Reference assemblies remain in ignored output and must not be shipped. Audio generation, playback and timing tests have been removed; native facial transfer is verified separately.
