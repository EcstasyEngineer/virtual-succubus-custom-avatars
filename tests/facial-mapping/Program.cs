using System;
using System.Collections.Generic;
using System.Linq;
using EcstasyEngineer.AvatarReplacement;

internal static class Program
{
    private static int assertions;
    private static Dictionary<string, float> Inputs(params (string Name, float Value)[] values) =>
        values.ToDictionary(p => AniFacialAdapter.SourcePrefix + p.Name, p => p.Value);
    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .001f;
    private static void Require(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + description);
        assertions++;
    }

    // Reference the pre-refactor native-percent arithmetic. Existing explicit
    // gain tests above independently pin the actual mapping constants.
    private static Dictionary<string, float> LegacyEvaluate(IReadOnlyDictionary<string, float> inputs)
    {
        var sums = AniFacialAdapter.OwnedTargetNames.ToDictionary(n => n, n => 0d);
        double jawMaximum = 0;
        foreach (var mapping in AniFacialAdapter.Mapping)
        {
            var value = inputs.TryGetValue(mapping.Source, out var raw) && float.IsFinite(raw) ? Math.Max(0f, Math.Min(100f, raw)) : 0f;
            var contribution = value * (double)mapping.Gain;
            if (mapping.JawMaximumCandidate) jawMaximum = Math.Max(jawMaximum, contribution);
            else sums[mapping.Target] += contribution;
        }
        foreach (var side in new[] { "Left", "Right" })
        {
            var difference = sums["mouthSmile" + side] - sums["mouthFrown" + side];
            sums["mouthSmile" + side] = Math.Max(difference, 0d); sums["mouthFrown" + side] = Math.Max(-difference, 0d);
        }
        sums["jawOpen"] = Math.Max(sums["jawOpen"], jawMaximum);
        return sums.ToDictionary(p => p.Key, p => (float)Math.Max(0d, Math.Min(100d, p.Value)));
    }

    private static void Main()
    {
        var intent = new FacialIntent();
        Require(FacialIntent.Channels.Count == 16 && FacialIntent.Channels.Select(c => c.Id).Distinct().Count() == 16, "common channels have unique stable semantic identifiers");
        for (var channel = 0; channel < FacialIntent.Channels.Count; channel++)
            Require((int)FacialIntent.Channels[channel].Channel == channel, "common descriptor ordering matches channel index");
        NativeFacialInput.ReadInto(Inputs(("ALL_Fun", 30), ("ALL_Joy", 70), ("EYE_Close_L", 20), ("BRW_Angry", 50), ("MTH_A", 40)), intent);
        Require(intent[FacialChannel.EmotionFun] == .3 && intent[FacialChannel.EmotionJoy] == .7, "Fun and Joy normalize independently without collapsing");
        Require(intent[FacialChannel.BlinkLeft] == .2 && intent[FacialChannel.BlinkRight] == 0 && intent[FacialChannel.BrowsAngry] == .5 && intent[FacialChannel.MouthA] == .4, "blink, brows and mouth are separate normalized inputs");
        var semanticOnly = AniFacialAdapter.EvaluateIntent(intent);
        Require(Near(semanticOnly["eyeBlinkLeft"], 20) && Near(semanticOnly["eyeBlinkRight"], 0) && Near(semanticOnly["jawOpen"], 26), "target adapter consumes common intent without native source morph names");
        intent[FacialChannel.EmotionJoy] = double.NaN; intent[FacialChannel.EmotionFun] = double.PositiveInfinity;
        intent[FacialChannel.BlinkLeft] = -1; intent[FacialChannel.BlinkRight] = 2;
        Require(intent[FacialChannel.EmotionJoy] == 0 && intent[FacialChannel.EmotionFun] == 0 && intent[FacialChannel.BlinkLeft] == 0 && intent[FacialChannel.BlinkRight] == 1, "common intent clamps invalid direct input");
        NativeFacialInput.ReadInto(Inputs(("ALL_Angry", float.NaN), ("ALL_Fun", float.PositiveInfinity), ("ALL_Joy", -5), ("MTH_A", float.MaxValue)), intent);
        Require(intent[FacialChannel.EmotionAngry] == 0 && intent[FacialChannel.EmotionFun] == 0 && intent[FacialChannel.EmotionJoy] == 0 && intent[FacialChannel.MouthA] == 1, "native input normalization handles nonfinite, negative and excessive values");
        NativeFacialInput.ReadInto(Inputs(), intent);
        Require(FacialIntent.Channels.All(c => intent[c.Channel] == 0), "native buffer reuse clears stale common intent");
        intent[FacialChannel.BlinkBoth] = .5; intent.Clear();
        Require(FacialIntent.Channels.All(c => intent[c.Channel] == 0), "common clear resets every channel");
        var neutral = AniFacialAdapter.Evaluate(Inputs());
        Require(neutral.Count == 21 && neutral.Values.All(v => v == 0), "absent expression inputs clear only owned channels");
        var left = AniFacialAdapter.Evaluate(Inputs(("EYE_Close_L", 100)));
        var right = AniFacialAdapter.Evaluate(Inputs(("EYE_Close_R", 100)));
        Require(left["eyeBlinkLeft"] == 100 && left["eyeBlinkRight"] == 0, "left blink remains unilateral");
        Require(right["eyeBlinkRight"] == 100 && right["eyeBlinkLeft"] == 0, "right blink remains unilateral");
        var combined = AniFacialAdapter.Evaluate(Inputs(("EYE_Close", 60), ("EYE_Close_L", 70)));
        Require(combined["eyeBlinkLeft"] == 100 && combined["eyeBlinkRight"] == 60, "blink sums then saturates");

        var anger = AniFacialAdapter.Evaluate(Inputs(("ALL_Angry", 100)));
        Require(Near(anger["browDownLeft"], 80) && Near(anger["eyeSquintLeft"], 50) && Near(anger["mouthFrownRight"], 70), "offline-reviewed whole-anger gains");
        var halfAnger = AniFacialAdapter.Evaluate(Inputs(("ALL_Angry", 50)));
        Require(Near(halfAnger["browDownLeft"], 40) && Near(halfAnger["eyeSquintRight"], 25) && Near(halfAnger["mouthFrownLeft"], 35), "reviewed anger remains proportional at half intensity");
        var specialized = AniFacialAdapter.Evaluate(Inputs(("BRW_Angry", 50), ("EYE_Angry", 80), ("MTH_Angry", 60)));
        Require(Near(specialized["browDownRight"], 40) && Near(specialized["eyeSquintRight"], 40) && Near(specialized["mouthFrownLeft"], 42), "specialized anger channels remain scoped");
        var angerBoth = AniFacialAdapter.Evaluate(Inputs(("ALL_Angry", 100), ("BRW_Angry", 100)));
        Require(angerBoth["browDownLeft"] == 100, "combined anger clamps output");

        var extreme = AniFacialAdapter.Evaluate(Inputs(("EYE_Close", float.MaxValue), ("MTH_A", float.NaN), ("MTH_I", float.PositiveInfinity), ("MTH_O", -100)));
        Require(extreme.Values.All(v => float.IsFinite(v) && v >= 0 && v <= 100), "extreme/invalid input produces finite bounded output");
        Require(extreme["jawOpen"] == 0 && extreme["eyeBlinkLeft"] == 100, "nonfinite/negative inputs become zero; high finite input clamps");
        var negativeInfinity = AniFacialAdapter.Evaluate(Inputs(("ALL_Angry", float.NegativeInfinity)));
        Require(negativeInfinity.Values.All(v => v == 0), "negative infinity cannot propagate");
        var a = AniFacialAdapter.Evaluate(Inputs(("MTH_A", 100)));
        Require(Near(a["jawOpen"], 65) && a["mouthClose"] == 0, "vowel A has no blanket mouth-close compensation");
        var i = AniFacialAdapter.Evaluate(Inputs(("MTH_I", 100)));
        Require(Near(i["jawOpen"], 15) && Near(i["mouthStretchLeft"], 50) && Near(i["mouthStretchRight"], 50), "vowel I mapping");
        var u = AniFacialAdapter.Evaluate(Inputs(("MTH_U", 100)));
        Require(Near(u["jawOpen"], 20) && Near(u["mouthPucker"], 65), "vowel U mapping");
        var e = AniFacialAdapter.Evaluate(Inputs(("MTH_E", 100)));
        Require(Near(e["jawOpen"], 35) && Near(e["mouthStretchLeft"], 40) && Near(e["mouthStretchRight"], 40), "vowel E mapping");
        var o = AniFacialAdapter.Evaluate(Inputs(("MTH_O", 100)));
        Require(Near(o["jawOpen"], 45) && Near(o["mouthFunnel"], 65) && o["mouthPucker"] == 0, "vowel O mapping");
        Require(AniFacialAdapter.Evaluate(Inputs()).Values.All(v => v == 0), "ended expression does not stick");
        Require(AniFacialAdapter.Evaluate(Inputs(("UNKNOWN", 100))).Values.All(v => v == 0), "unmapped expression has no effect");
        var unchanged = Inputs(("EYE_Close", 55));
        AniFacialAdapter.Evaluate(unchanged);
        Require(unchanged.Count == 1 && unchanged.Values.Single() == 55, "pure evaluation does not mutate caller inputs");
        bool rejectedNull = false;
        try { AniFacialAdapter.Evaluate(null); } catch (ArgumentNullException) { rejectedNull = true; }
        Require(rejectedNull, "null input rejected explicitly");

        var fun = AniFacialAdapter.Evaluate(Inputs(("ALL_Fun", 100)));
        Require(Near(fun["mouthSmileLeft"], 35) && Near(fun["mouthSmileRight"], 35) && Near(fun["browOuterUpLeft"], 10) && fun["jawOpen"] == 0, "fun gains");
        var joy = AniFacialAdapter.Evaluate(Inputs(("ALL_Joy", 100)));
        Require(Near(joy["mouthSmileLeft"], 50) && Near(joy["eyeSquintRight"], 20) && Near(joy["jawOpen"], 10), "joy gains");
        var sorrow = AniFacialAdapter.Evaluate(Inputs(("ALL_Sorrow", 100)));
        Require(Near(sorrow["mouthFrownLeft"], 40) && Near(sorrow["browInnerUp"], 25) && sorrow["mouthSmileLeft"] == 0, "sorrow gains");
        var surprise = AniFacialAdapter.Evaluate(Inputs(("ALL_Surprised", 100)));
        Require(Near(surprise["eyeWideLeft"], 50) && Near(surprise["browOuterUpRight"], 30) && Near(surprise["browInnerUp"], 15) && Near(surprise["jawOpen"], 40), "surprise gains");
        var halfSurprise = AniFacialAdapter.Evaluate(Inputs(("ALL_Surprised", 50)));
        Require(Near(halfSurprise["eyeWideRight"], 25) && Near(halfSurprise["jawOpen"], 20), "reviewed surprise remains proportional at half intensity");
        var smileWins = AniFacialAdapter.Evaluate(Inputs(("ALL_Joy", 100), ("ALL_Sorrow", 100)));
        Require(Near(smileWins["mouthSmileLeft"], 10) && Near(smileWins["mouthSmileRight"], 10) && smileWins["mouthFrownLeft"] == 0 && smileWins["mouthFrownRight"] == 0, "signed difference favors net smile");
        var frownWins = AniFacialAdapter.Evaluate(Inputs(("ALL_Fun", 100), ("ALL_Sorrow", 100)));
        Require(Near(frownWins["mouthFrownLeft"], 5) && Near(frownWins["mouthFrownRight"], 5) && frownWins["mouthSmileLeft"] == 0, "signed difference favors net frown");
        var cancellation = AniFacialAdapter.Evaluate(Inputs(("ALL_Joy", 70), ("ALL_Angry", 50)));
        Require(Near(cancellation["mouthSmileLeft"], 0) && Near(cancellation["mouthFrownLeft"], 0), "equal smile and frown cancel");
        var preClampDifference = AniFacialAdapter.Evaluate(Inputs(("ALL_Angry", 100), ("MTH_Angry", 100), ("ALL_Sorrow", 100), ("ALL_Joy", 100), ("ALL_Fun", 100)));
        Require(Near(preClampDifference["mouthFrownLeft"], 95) && preClampDifference["mouthSmileLeft"] == 0, "arbitration uses unclamped sums, not prematurely clipped outputs");
        var jawVowelWins = AniFacialAdapter.Evaluate(Inputs(("MTH_A", 100), ("ALL_Joy", 100), ("ALL_Surprised", 100)));
        Require(Near(jawVowelWins["jawOpen"], 65), "jaw uses max vowel sum, joy and surprise instead of adding expressions");
        var jawSurpriseWins = AniFacialAdapter.Evaluate(Inputs(("MTH_A", 10), ("ALL_Joy", 100), ("ALL_Surprised", 100)));
        Require(Near(jawSurpriseWins["jawOpen"], 40), "surprise can dominate a smaller vowel sum");
        var jawSum = AniFacialAdapter.Evaluate(Inputs(("MTH_A", 100), ("MTH_O", 100), ("ALL_Joy", 100)));
        Require(jawSum["jawOpen"] == 100, "vowel contributions still sum and final jaw clamps");
        var globalNeutral = AniFacialAdapter.Evaluate(Inputs(("ALL_Neutral", 100), ("ALL_Joy", 100), ("EYE_Close", 30)));
        Require(Near(globalNeutral["mouthSmileLeft"], 50) && Near(globalNeutral["jawOpen"], 10) && Near(globalNeutral["eyeBlinkLeft"], 30), "ALL_Neutral never globally zeros expressions/blink");
        Require(!AniFacialAdapter.OwnedTargetNames.Any(n => n.IndexOf("teeth", StringComparison.OrdinalIgnoreCase) >= 0), "teeth channels remain unowned");
        Require(joy["mouthSmileLeft"] == 50 && neutral["mouthSmileLeft"] == 0, "independent pure results are not aliased");

        var random = new Random(417);
        for (var trial = 0; trial < 1000; trial++)
        {
            var inputs = AniFacialAdapter.RequiredSourceNames.ToDictionary(n => n, n => (float)(random.NextDouble() * 500 - 200));
            var output = AniFacialAdapter.Evaluate(inputs);
            var legacy = LegacyEvaluate(inputs);
            Require(output.All(p => BitConverter.SingleToInt32Bits(p.Value) == BitConverter.SingleToInt32Bits(legacy[p.Key])),
                "normalization refactor preserves all legacy float output bits, trial " + trial);
            Require(output.Count == 21 && output.Values.All(v => float.IsFinite(v) && v >= 0 && v <= 100) && output["mouthClose"] == 0,
                "seeded finite/bounded output and mouth-close invariant, trial " + trial);
            Require((output["mouthSmileLeft"] == 0 || output["mouthFrownLeft"] == 0) && (output["mouthSmileRight"] == 0 || output["mouthFrownRight"] == 0),
                "seeded smile/frown mutual exclusion, trial " + trial);
        }

        // Exercise the exact reusable evaluator used by Apply, without invoking Unity.
        var method = typeof(AniFacialAdapter).GetMethod("EvaluateInto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var evaluateInto = (Action<IReadOnlyDictionary<string, float>, FacialIntent, Dictionary<string, float>, double[]>)method.CreateDelegate(
            typeof(Action<IReadOnlyDictionary<string, float>, FacialIntent, Dictionary<string, float>, double[]>));
        var reusableInputs = Inputs(("ALL_Joy", 100), ("ALL_Sorrow", 100), ("MTH_A", 100));
        var reusableOutput = AniFacialAdapter.OwnedTargetNames.ToDictionary(n => n, n => 0f);
        var reusableSums = new double[AniFacialAdapter.OwnedTargetNames.Count];
        var reusableIntent = new FacialIntent();
        for (var trial = 0; trial < 1000; trial++) evaluateInto(reusableInputs, reusableIntent, reusableOutput, reusableSums);
        var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        for (var trial = 0; trial < 10000; trial++) evaluateInto(reusableInputs, reusableIntent, reusableOutput, reusableSums);
        var evaluationBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        Require(evaluationBytes == 0, "reused evaluator allocates no managed memory in 10000 warmed calls");
        Require(Near(reusableOutput["mouthSmileLeft"], 10) && Near(reusableOutput["jawOpen"], 65), "reused evaluator matches pure arbitration result");
        reusableInputs.Clear(); evaluateInto(reusableInputs, reusableIntent, reusableOutput, reusableSums);
        Require(reusableOutput.Values.All(v => v == 0), "reused buffers clear stale expression values");
        Console.WriteLine("Reusable evaluator allocated " + evaluationBytes + " bytes over 10000 warmed calls (.NET test runtime only).");
        Console.WriteLine(assertions + " pure facial-mapping assertions passed. No game/Unity scene was run.");
    }
}
