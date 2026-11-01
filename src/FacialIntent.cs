using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EcstasyEngineer.AvatarReplacement
{
    // Facial intent is independent of rig bones, morph names, body animation,
    // gaze and audio. Channels may coexist; target adapters resolve conflicts.
    internal enum FacialChannel
    {
        EmotionAngry, BrowsAngry, EyesAngry, BlinkBoth, BlinkLeft, BlinkRight,
        MouthAngry, MouthA, MouthI, MouthU, MouthE, MouthO,
        EmotionFun, EmotionJoy, EmotionSorrow, EmotionSurprised
    }

    internal sealed class FacialIntent
    {
        internal sealed class Definition
        {
            public FacialChannel Channel { get; }
            public string Id { get; }
            public string Group { get; }
            public string Meaning { get; }
            internal Definition(FacialChannel channel, string id, string group, string meaning)
            { Channel = channel; Id = id; Group = group; Meaning = meaning; }
        }

        // Fun and Joy intentionally remain independent. A target may use an
        // approximate relaxed preset for Fun and happy for Joy, but those names
        // alone do not prove equivalent deformation or calibrated appearance.
        private static readonly ReadOnlyCollection<Definition> Definitions = Array.AsReadOnly(new[] {
            new Definition(FacialChannel.EmotionAngry, "emotion.angry", "expression", "Whole-face anger."),
            new Definition(FacialChannel.BrowsAngry, "brows.angry", "brows", "Angry brow contribution, independently controllable."),
            new Definition(FacialChannel.EyesAngry, "eyes.angry", "eyes", "Angry eyelid contribution; does not rotate the eyes."),
            new Definition(FacialChannel.BlinkBoth, "blink.both", "blink", "Bilateral eyelid closure."),
            new Definition(FacialChannel.BlinkLeft, "blink.left", "blink", "Additional left eyelid closure."),
            new Definition(FacialChannel.BlinkRight, "blink.right", "blink", "Additional right eyelid closure."),
            new Definition(FacialChannel.MouthAngry, "mouth.angry", "mouth", "Angry mouth contribution, independently controllable."),
            new Definition(FacialChannel.MouthA, "mouth.a", "mouth", "Existing A mouth shape; no speech or timing is generated."),
            new Definition(FacialChannel.MouthI, "mouth.i", "mouth", "Existing I mouth shape; no speech or timing is generated."),
            new Definition(FacialChannel.MouthU, "mouth.u", "mouth", "Existing U mouth shape; no speech or timing is generated."),
            new Definition(FacialChannel.MouthE, "mouth.e", "mouth", "Existing E mouth shape; no speech or timing is generated."),
            new Definition(FacialChannel.MouthO, "mouth.o", "mouth", "Existing O mouth shape; no speech or timing is generated."),
            new Definition(FacialChannel.EmotionFun, "emotion.fun", "expression", "Fun/smiling intent. A relaxed preset can be an approximate target; not identical to Joy."),
            new Definition(FacialChannel.EmotionJoy, "emotion.joy", "expression", "Joy intent. A happy preset can be an approximate target; kept separate from Fun."),
            new Definition(FacialChannel.EmotionSorrow, "emotion.sorrow", "expression", "Sorrow/sad intent."),
            new Definition(FacialChannel.EmotionSurprised, "emotion.surprised", "expression", "Whole-face surprise.")
        });
        private readonly double[] values = new double[Definitions.Count];
        internal static IReadOnlyList<Definition> Channels => Definitions;
        internal double this[FacialChannel channel]
        {
            get => values[(int)channel];
            set => values[(int)channel] = double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(1, value));
        }
        internal void Clear() => Array.Clear(values, 0, values.Length);
    }

    // Only this source adapter knows the original game's exact morph names.
    // Missing/nonfinite native inputs become zero; native 0..100 becomes 0..1.
    // Reusing a destination buffer allocates no managed memory per frame.
    internal static class NativeFacialInput
    {
        internal const string Prefix = "Face.M_F00_000_00_Fcl_";
        private static readonly string[] Names = {
            Prefix + "ALL_Angry", Prefix + "BRW_Angry", Prefix + "EYE_Angry", Prefix + "EYE_Close", Prefix + "EYE_Close_L", Prefix + "EYE_Close_R",
            Prefix + "MTH_Angry", Prefix + "MTH_A", Prefix + "MTH_I", Prefix + "MTH_U", Prefix + "MTH_E", Prefix + "MTH_O",
            Prefix + "ALL_Fun", Prefix + "ALL_Joy", Prefix + "ALL_Sorrow", Prefix + "ALL_Surprised"
        };
        internal static IReadOnlyList<string> RequiredSourceNames { get; } = Array.AsReadOnly(Names);
        internal static string SourceName(FacialChannel channel) => Names[(int)channel];
        internal static void ReadInto(IReadOnlyDictionary<string, float> source, FacialIntent destination)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            for (var i = 0; i < Names.Length; i++)
            {
                var value = source.TryGetValue(Names[i], out var raw) && !float.IsNaN(raw) && !float.IsInfinity(raw) ? raw : 0f;
                // Double storage preserves the precision of existing native
                // float inputs through normalized conversion and target gains.
                destination[(FacialChannel)i] = value / 100d;
            }
        }
    }
}
