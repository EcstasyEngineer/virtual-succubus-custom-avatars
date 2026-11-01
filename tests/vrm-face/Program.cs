using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using EcstasyEngineer.AvatarReplacement;

internal static class Program
{
    private static int assertions;
    private static readonly string Digest = new string('a', 64);
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException("FAIL: " + label); assertions++; }
    private static void Reject<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException("FAIL: " + label);
    }
    private static VrmFacialAdapter.Profile Valid() => new VrmFacialAdapter.Profile {
        schemaVersion = 1, mapping = "vrm0-presets-v1", modelId = "fixture-avatar", bundleSha256 = Digest,
        sourceVrmSha256 = new string('b', 64), unsupportedFeatures = new[] { "fixture material effect omitted" },
        expressions = new[] { new VrmFacialAdapter.Expression {
            semantic = "emotion.joy", isBinary = false, provenance = "authored-vrm0",
            binds = new[] { new VrmFacialAdapter.Morph { path = "Body/Face", index = 2, weight = 75 } }
        } }
    };
    private static void Invalid(Action<VrmFacialAdapter.Profile> change, string label)
    { var profile = Valid(); change(profile); Reject<InvalidDataException>(() => VrmFacialAdapter.Validate(profile, Digest), label); }
    private static string Serialize(VrmFacialAdapter.Profile profile)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(VrmFacialAdapter.Profile)).WriteObject(stream, profile);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static VrmFacialAdapter.Profile Deserialize(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (VrmFacialAdapter.Profile)new DataContractJsonSerializer(typeof(VrmFacialAdapter.Profile)).ReadObject(stream);
    }
    private static void Main(string[] args)
    {
        VrmFacialAdapter.Validate(Valid(), Digest.ToUpperInvariant()); assertions++;
        Reject<InvalidDataException>(() => VrmFacialAdapter.Validate(null, Digest), "null profile accepted");
        Reject<InvalidDataException>(() => VrmFacialAdapter.Validate(Valid(), null), "null computed hash accepted");
        Reject<InvalidDataException>(() => VrmFacialAdapter.Validate(Valid(), new string('b', 64)), "different bundle hash accepted");
        Reject<InvalidDataException>(() => VrmFacialAdapter.Validate(Valid(), Digest + "a"), "wrong-length computed hash accepted");
        Invalid(p => p.schemaVersion = 2, "unknown schema accepted");
        Invalid(p => p.mapping = "vrm1", "unsupported mapping accepted");
        Invalid(p => p.mapping = null, "null mapping accepted");
        Invalid(p => p.modelId = null, "null model id accepted");
        Invalid(p => p.modelId = "  ", "blank model id accepted");
        Invalid(p => p.modelId = new string('x', 129), "oversized model id accepted");
        Invalid(p => p.bundleSha256 = null, "null profile hash accepted");
        Invalid(p => p.bundleSha256 = Digest + "\n", "digest newline accepted");
        Invalid(p => p.expressions = null, "null expressions accepted");
        Invalid(p => p.expressions = new VrmFacialAdapter.Expression[65], "expression count overflow accepted");
        Invalid(p => p.expressions[0] = null, "null expression accepted");
        Invalid(p => p.expressions[0].semantic = "unrecognized.channel", "unknown semantic accepted");
        Invalid(p => p.expressions[0].semantic = "Emotion.Joy", "semantic case mismatch accepted");
        Invalid(p => p.expressions[0].semantic = null, "null semantic accepted");
        Invalid(p => p.expressions = new[] { p.expressions[0], p.expressions[0] }, "duplicate semantic accepted");
        Invalid(p => p.expressions[0].binds = null, "null bindings accepted");
        Invalid(p => p.expressions[0].binds = new VrmFacialAdapter.Morph[257], "binding count overflow accepted");
        Invalid(p => p.expressions[0].binds[0] = null, "null binding accepted");
        foreach (var path in new[] { null, "", " ", "Face\nChild", "Face\0Child", new string('x', 2049) })
            Invalid(p => p.expressions[0].binds[0].path = path, "invalid renderer path accepted");
        foreach (var index in new[] { -1, 65536 })
            Invalid(p => p.expressions[0].binds[0].index = index, "invalid morph index accepted");
        foreach (var weight in new[] { -1f, 100.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Invalid(p => p.expressions[0].binds[0].weight = weight, "invalid morph weight accepted");
        Invalid(p => p.expressions[0].binds = new[] { p.expressions[0].binds[0], p.expressions[0].binds[0] }, "duplicate binding accepted within one expression");

        var edges = Valid(); edges.modelId = new string('x', 128);
        edges.expressions[0].binds = new[] {
            new VrmFacialAdapter.Morph { path = ".", index = 0, weight = 0 },
            new VrmFacialAdapter.Morph { path = new string('x', 2048), index = 65535, weight = 100 }
        };
        VrmFacialAdapter.Validate(edges, Digest); assertions++;
        var full = Valid();
        full.expressions = FacialIntent.Channels.Select(c => new VrmFacialAdapter.Expression {
            semantic = c.Id, binds = new[] { new VrmFacialAdapter.Morph { path = "Face", index = 0, weight = 100 } }
        }).ToArray();
        VrmFacialAdapter.Validate(full, Digest); assertions++;
        var maximumBinds = Valid();
        maximumBinds.expressions[0].binds = Enumerable.Range(0, 256)
            .Select(i => new VrmFacialAdapter.Morph { path = "Face", index = i, weight = 1 }).ToArray();
        VrmFacialAdapter.Validate(maximumBinds, Digest); assertions++;

        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d })
        {
            Check(VrmFacialAdapter.EffectiveWeight(value, false) == 0, "invalid/low continuous value clamps to zero");
            Check(VrmFacialAdapter.EffectiveWeight(value, true) == 0, "invalid/low binary value clamps to zero");
        }
        Check(VrmFacialAdapter.EffectiveWeight(.25, false) == .25f, "continuous quarter strength preserved");
        Check(VrmFacialAdapter.EffectiveWeight(.5, false) == .5f, "continuous half strength preserved");
        Check(VrmFacialAdapter.EffectiveWeight(.5, true) == 0, "VRM0 binary half rounds to zero");
        Check(VrmFacialAdapter.EffectiveWeight(Math.BitIncrement(.5), true) == 1, "binary next value above half selects one");
        Check(VrmFacialAdapter.EffectiveWeight(Math.BitDecrement(.5), true) == 0, "binary next value below half selects zero");
        Check(VrmFacialAdapter.EffectiveWeight(1, true) == 1 && VrmFacialAdapter.EffectiveWeight(2, false) == 1, "high normalized weights clamp to one");
        var random = new Random(7241);
        for (var i = 0; i < 1000; i++)
        {
            var input = random.NextDouble() * 4 - 1.5;
            var continuous = VrmFacialAdapter.EffectiveWeight(input, false);
            var binary = VrmFacialAdapter.EffectiveWeight(input, true);
            Check(float.IsFinite(continuous) && continuous >= 0 && continuous <= 1 && (binary == 0 || binary == 1), "seeded effective weights remain bounded");
        }

        var encoded = Serialize(Valid()); var decoded = Deserialize(encoded);
        VrmFacialAdapter.Validate(decoded, Digest);
        Check(decoded.modelId == "fixture-avatar" && decoded.sourceVrmSha256 == new string('b', 64), "profile properties survive JSON roundtrip");
        Check(decoded.expressions.Single().semantic == "emotion.joy" && decoded.expressions[0].binds[0].path == "Body/Face" &&
            decoded.expressions[0].binds[0].index == 2 && decoded.expressions[0].binds[0].weight == 75, "nested property bindings survive JSON roundtrip");
        Check(decoded.unsupportedFeatures.Single() == "fixture material effect omitted", "unsupported-feature metadata survives roundtrip");
        var binaryProfile = Valid(); binaryProfile.expressions[0].isBinary = true;
        Check(Deserialize(Serialize(binaryProfile)).expressions[0].isBinary, "binary flag survives JSON roundtrip");
        foreach (var name in new[] { "schemaVersion", "mapping", "modelId", "bundleSha256", "expressions" })
        {
            var altered = System.Text.Json.Nodes.JsonNode.Parse(encoded).AsObject(); altered.Remove(name);
            Reject<SerializationException>(() => Deserialize(altered.ToJsonString()), "missing required profile field accepted: " + name);
        }
        foreach (var name in new[] { "semantic", "binds" })
        {
            var altered = System.Text.Json.Nodes.JsonNode.Parse(encoded);
            altered["expressions"][0].AsObject().Remove(name);
            Reject<SerializationException>(() => Deserialize(altered.ToJsonString()), "missing required expression field accepted: " + name);
        }
        foreach (var name in new[] { "path", "index", "weight" })
        {
            var altered = System.Text.Json.Nodes.JsonNode.Parse(encoded);
            altered["expressions"][0]["binds"][0].AsObject().Remove(name);
            Reject<SerializationException>(() => Deserialize(altered.ToJsonString()), "missing required morph field accepted: " + name);
        }
        // JsonUtility exporter shape: named properties and informational members
        // not required by the runtime. This fixture exercises no Unity serializer.
        var helperJson = "{\"schemaVersion\":1,\"mapping\":\"vrm0-presets-v1\",\"modelId\":\"fixture-avatar\",\"bundleSha256\":\"" + Digest +
            "\",\"expressions\":[{\"semantic\":\"emotion.joy\",\"isBinary\":true,\"provenance\":\"authored-vrm0\",\"binds\":[{\"path\":\"Face\",\"shape\":\"Happy\",\"index\":3,\"weight\":62.5}],\"unsupportedMaterialBindCount\":0}]}";
        var helperProfile = Deserialize(helperJson); VrmFacialAdapter.Validate(helperProfile, Digest);
        Check(helperProfile.expressions[0].isBinary && helperProfile.expressions[0].binds[0].weight == 62.5f, "exporter-shaped JSON deserializes into runtime properties");

        var scratch = Path.Combine(Path.GetTempPath(), "avatar-vrm-face-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var bundle = Path.Combine(scratch, "fixture.bundle"); var sidecar = bundle + ".vrmface.json";
        try
        {
            var bytes = Encoding.ASCII.GetBytes("inert profile hash fixture; not an AssetBundle"); File.WriteAllBytes(bundle, bytes);
            var profile = Valid(); profile.bundleSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            File.WriteAllText(sidecar, Serialize(profile));
            Check(VrmFacialAdapter.ReadProfile(bundle).modelId == "fixture-avatar", "ReadProfile accepts actual hash-paired serialized properties");
            File.AppendAllText(bundle, "modified");
            Reject<InvalidDataException>(() => VrmFacialAdapter.ReadProfile(bundle), "ReadProfile accepts stale bundle sidecar");
            File.WriteAllBytes(bundle, bytes);
            File.WriteAllText(sidecar, ""); Reject<InvalidDataException>(() => VrmFacialAdapter.ReadProfile(bundle), "zero-byte sidecar accepted");
            File.WriteAllText(sidecar, new string(' ', 1024 * 1024 + 1));
            Reject<InvalidDataException>(() => VrmFacialAdapter.ReadProfile(bundle), "oversized sidecar accepted");
            File.WriteAllText(sidecar, "{malformed}");
            Reject<SerializationException>(() => VrmFacialAdapter.ReadProfile(bundle), "malformed sidecar accepted");
            File.Delete(sidecar); Reject<FileNotFoundException>(() => VrmFacialAdapter.ReadProfile(bundle), "missing sidecar accepted");
        }
        finally { File.Delete(sidecar); File.Delete(bundle); Directory.Delete(scratch); }
        // Native recipes: disgust adds a little U, relaxed lowers the lids, surprise opens the mouth only without authored surprise.
        var recipe = new FacialIntent();
        recipe[FacialChannel.EmotionSorrow] = 1; recipe[FacialChannel.EmotionFun] = 1; recipe[FacialChannel.EmotionSurprised] = 1;
        VrmFacialAdapter.ApplyNativeRecipes(recipe, hasAuthoredSurprise: false);
        Check(Math.Abs(recipe[FacialChannel.MouthU] - VrmFacialAdapter.DisgustMouthU) < 1e-9, "disgust recipe U weight");
        Check(recipe[FacialChannel.BlinkBoth] == 1, "combined lid closure stays clamped to 1");
        Check(Math.Abs(recipe[FacialChannel.MouthO] - VrmFacialAdapter.SurpriseFallbackMouthO) < 1e-9, "surprise fallback O weight");
        Check(Math.Abs(VrmFacialAdapter.LidOpening(recipe, hasAuthoredSurprise: false) - VrmFacialAdapter.SurpriseLidOpening) < 1e-9, "surprise fallback opens the lids");
        var authored = new FacialIntent(); authored[FacialChannel.EmotionSurprised] = 1;
        VrmFacialAdapter.ApplyNativeRecipes(authored, hasAuthoredSurprise: true);
        Check(authored[FacialChannel.MouthO] == 0, "authored surprise must not get the O fallback");
        Check(VrmFacialAdapter.LidOpening(authored, hasAuthoredSurprise: true) == 0, "authored surprise must not get the lid fallback");
        // Eyelid order from the side-by-side review: disgust > relaxed = furious > neutral > surprised.
        double Lids(FacialChannel emotion) { var f = new FacialIntent(); f[emotion] = 1; VrmFacialAdapter.ApplyNativeRecipes(f, false); return f[FacialChannel.BlinkBoth] - VrmFacialAdapter.LidOpening(f, false); }
        Check(Lids(FacialChannel.EmotionSorrow) > Lids(FacialChannel.EmotionFun), "disgust squints more than relaxed");
        Check(Lids(FacialChannel.EmotionFun) == Lids(FacialChannel.EmotionAngry), "relaxed and furious lids tie");
        Check(Lids(FacialChannel.EmotionAngry) > 0 && Lids(FacialChannel.EmotionSurprised) < 0, "surprised opens past neutral");
        var angry = new FacialIntent(); angry[FacialChannel.EmotionAngry] = 1; VrmFacialAdapter.ApplyNativeRecipes(angry, true);
        Check(Math.Abs(angry[FacialChannel.BrowsAngry] - VrmFacialAdapter.AngryBrowDown) < 1e-9, "furious adds brow-down");
        var spoken = new FacialIntent(); spoken[FacialChannel.MouthU] = .95; spoken[FacialChannel.EmotionSorrow] = 1;
        VrmFacialAdapter.ApplyNativeRecipes(spoken, hasAuthoredSurprise: true);
        Check(spoken[FacialChannel.MouthU] == 1, "recipes stay clamped to 1");
        var idle = new FacialIntent(); VrmFacialAdapter.ApplyNativeRecipes(idle, hasAuthoredSurprise: false);
        Check(FacialIntent.Channels.All(c => idle[c.Channel] == 0), "recipes add nothing at neutral");
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--bundle") throw new ArgumentException("Optional argument: --bundle <prepared bundle path>");
            var actual = VrmFacialAdapter.ReadProfile(args[1]);
            Check(actual.expressions.Any(e => e.binds.Length > 0), "provided prepared profile has no usable morph bindings");
            Console.WriteLine("Provided bundle hash/profile verified: " + actual.expressions.Length + " expression records. Model geometry was not loaded.");
        }
        Console.WriteLine(assertions + " VRM face profile/weight assertions passed. No Unity scene, renderer, game or model import ran.");
    }
}
