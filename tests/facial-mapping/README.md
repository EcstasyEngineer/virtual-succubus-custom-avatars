# Pure facial mapping checks

Run from the repository root using the .NET 8 SDK and the compatible local Unity-managed game assemblies:

```powershell
dotnet run --project .\tests\facial-mapping\FacialMapping.Tests.csproj -c Release
# Alternate existing installation:
dotnet run --project .\tests\facial-mapping\FacialMapping.Tests.csproj -c Release '-p:GameDir=D:\Games\Virtual Succubus'
```

The project compiles the actual `AniFacialAdapter.cs` source. It invokes only the pure `Evaluate` function and mapping metadata; it does not call Bind/Apply/Dispose, run Unity, launch the game, read saves or change assets. No test framework or NuGet package is required. Local Unity reference assemblies are copied only to the ignored test `bin` directory so .NET can resolve the adapter's types. They are not committed or included in the POC archive.

The 2,038 assertions cover owned-channel clearing, unilateral/bilateral blink, additive saturation, anger/fun/joy/sorrow/surprise, all five vowels, smile/frown arbitration, jaw maximum arbitration, ignored global neutral, unowned teeth, unknown channels, null input, input immutability, NaN/infinities/extreme values, and 1,000 seeded combinations. They also call the exact reusable evaluator used by `Apply`: it allocated **0 managed bytes across 10,000 warmed calls** in this .NET test runtime and correctly cleared old expression values. This is not a Unity/Mono allocation benchmark. Failure exits nonzero.

These checks verify mapping arithmetic and ownership of output channels, not calibrated expression appearance, blendshape compatibility, jaw-rest capture, Unity execution order or runtime disposal/restoration. Those require separately recorded Unity/game checks. Running this project writes only its ignored test build output; it does not rebuild or replace the plugin DLL used by the game.

## Provisional mapping and aggregation

All source names below have the exact prefix `Face.M_F00_000_00_Fcl_`. Gains multiply Unity's 0–100 blendshape weights; anger/surprise were adjusted after offline frontal Model 01 inspection, while all gains remain provisional for runtime use. `Bind` now requires all 16 named source channels and the 21 owned target channels on the audited Ani Head mesh. Bundle eligibility remains required through the historical audited hash or a validated hash-paired profile.

| Source suffix | Target contribution and gain |
|---|---|
| `ALL_Angry` | `browDownLeft/Right` ×0.80; `eyeSquintLeft/Right` ×0.50; `mouthFrownLeft/Right` ×0.70 |
| `BRW_Angry` | `browDownLeft/Right` ×0.80 |
| `EYE_Angry` | `eyeSquintLeft/Right` ×0.50 |
| `MTH_Angry` | `mouthFrownLeft/Right` ×0.70 |
| `EYE_Close` | `eyeBlinkLeft/Right` ×1.00 |
| `EYE_Close_L`, `EYE_Close_R` | Corresponding unilateral `eyeBlinkLeft` or `eyeBlinkRight` ×1.00 |
| `ALL_Fun` | `mouthSmileLeft/Right` ×0.35; `browOuterUpLeft/Right` ×0.10 |
| `ALL_Joy` | `mouthSmileLeft/Right` ×0.50; `eyeSquintLeft/Right` ×0.20; jaw maximum candidate ×0.10 |
| `ALL_Sorrow` | `mouthFrownLeft/Right` ×0.40; `browInnerUp` ×0.25 |
| `ALL_Surprised` | `eyeWideLeft/Right` ×0.50; `browOuterUpLeft/Right` ×0.30; `browInnerUp` ×0.15; jaw maximum candidate ×0.40 |
| `MTH_A` | Vowel jaw sum ×0.65 |
| `MTH_I` | Vowel jaw sum ×0.15; `mouthStretchLeft/Right` ×0.50 |
| `MTH_U` | Vowel jaw sum ×0.20; `mouthPucker` ×0.65 |
| `MTH_E` | Vowel jaw sum ×0.35; `mouthStretchLeft/Right` ×0.40 |
| `MTH_O` | Vowel jaw sum ×0.45; `mouthFunnel` ×0.65 |

Missing and nonfinite input weights become zero; each other input is clamped to [0,100]. Ordinary contributions are summed **without intermediate output clamping**. Smile/frown use a signed difference independently for each side:

```text
d = sum(smile contributions) - sum(frown contributions)
mouthSmile = max(d, 0)
mouthFrown = max(-d, 0)
jawOpen = max(sum(vowel jaw contributions), 0.10 * ALL_Joy, 0.40 * ALL_Surprised)
```

Every owned output is then clamped to [0,100]. For example, smile sum 85 and frown sum 180 produce smile 0/frown 95, not frown 15 from prematurely clipped sums. Joy/surprise jaw candidates do not add to vowel opening. `mouthClose` stays zero: a global compensating offset was not supported by the offline appearance checks. `ALL_Neutral` is ignored and never zeros active expressions or blink. Teeth and all other unowned channels are not written. Empty inputs clear only the adapter's owned output channels; they are not a command to reset every blendshape on the mesh.

`Mapping` entries expose their source, target, gain and aggregation rule; `AggregationRules` exposes the full equations for diagnostic consumers. Live `Apply` reuses input/output dictionaries and a sum array, and updates its status string only when the invalid-input count changes. The pure public-to-the-assembly `Evaluate` method returns an independent dictionary for tests and callers.
