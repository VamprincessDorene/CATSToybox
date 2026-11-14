using System.Collections.Generic;
using System.Numerics;

namespace SignSpeak.Signs;

/// <summary>
/// The ASL manual alphabet and number handshapes.
///
/// Shapes are described in the rig-independent curl/spread vocabulary of
/// <see cref="FingerPose"/>, so they hold up across every race and skeleton in
/// the game. Curl runs 0 (straight) to 1 (fully folded). Spread runs -1 (toward
/// the thumb) to 1 (away from the thumb), so within a hand the index carries
/// the most negative splay and the pinky the most positive.
///
/// Descriptions are written for the right hand with the palm facing away from
/// the signer, which is the usual orientation for fingerspelling.
/// </summary>
public static class Alphabet
{
    private static FingerPose F(float curlA, float curlB, float spread = 0f) => new(curlA, curlB, spread);

    /// <summary>Fully folded, used for every fisted finger.</summary>
    private static FingerPose Fist => F(1f, 1f);

    /// <summary>The thumb lying across the front of a closed fist.</summary>
    private static FingerPose ThumbAcross => F(0.55f, 0.70f, -0.65f);

    private static HandPose Shape(
        FingerPose thumb, FingerPose index, FingerPose middle, FingerPose ring, FingerPose pinky)
        => new(thumb, index, middle, ring, pinky);

    private static readonly Dictionary<char, Sign> Signs = Build();

    /// <summary>Looks up a single character. Case insensitive.</summary>
    public static bool TryGet(char character, out Sign sign)
        => Signs.TryGetValue(char.ToUpperInvariant(character), out sign!);

    public static IReadOnlyDictionary<char, Sign> All => Signs;

    /// <summary>
    /// Every supported character in display order, A-Z then 0-9. Dictionary
    /// enumeration order is not contractual, so the UI grid uses this instead.
    /// </summary>
    public static readonly char[] Ordered =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private static Dictionary<char, Sign> Build()
    {
        var signs = new Dictionary<char, Sign>();

        void Add(char key, HandPose pose, string description, float hold = 0.18f)
            => signs[key] = Sign.Static(key.ToString(), pose, description, hold);

        // ---- Letters ---------------------------------------------------------

        Add('A', Shape(F(0.05f, 0.05f, -0.20f), Fist, Fist, Fist, Fist),
            "Closed fist, thumb straight up alongside the index finger.");

        Add('B', Shape(F(0.55f, 0.75f, -1.00f), F(0f, 0f, -0.10f), F(0f, 0f, 0f), F(0f, 0f, 0.02f), F(0f, 0f, 0.05f)),
            "Flat palm, four fingers straight and together, thumb folded across the palm.");

        Add('C', Shape(F(0.30f, 0.25f, -0.30f), F(0.45f, 0.40f), F(0.45f, 0.40f), F(0.45f, 0.40f), F(0.45f, 0.40f)),
            "Whole hand curved into the shape of a C.");

        Add('D', Shape(F(0.30f, 0.45f, -0.20f), F(0f, 0f), F(0.80f, 0.85f), F(0.85f, 0.85f), F(0.85f, 0.85f)),
            "Index finger straight up, the other three fingertips meeting the thumb.");

        Add('E', Shape(F(0.70f, 0.85f, -0.60f), F(0.55f, 1f), F(0.55f, 1f), F(0.55f, 1f), F(0.55f, 1f)),
            "Fingers bent down onto the thumb, which lies across the palm.");

        Add('F', Shape(F(0.50f, 0.55f, -0.15f), F(0.55f, 0.70f, -0.10f), F(0f, 0f, -0.05f), F(0f, 0f, 0.05f), F(0f, 0f, 0.15f)),
            "Thumb and index form a circle, the other three fingers straight up and apart.");

        Add('G', Shape(F(0.10f, 0.10f, -0.40f), F(0f, 0f, -0.05f), Fist, Fist, Fist)
                .With(wrist: new Vector3(0f, 0f, -55f)),
            "Index and thumb extended parallel, pointing sideways.");

        Add('H', Shape(ThumbAcross, F(0f, 0f, 0.02f), F(0f, 0f, -0.02f), Fist, Fist)
                .With(wrist: new Vector3(0f, 0f, -55f)),
            "Index and middle extended together, pointing sideways.");

        Add('I', Shape(ThumbAcross, Fist, Fist, Fist, F(0f, 0f, 0f)),
            "Pinky straight up, everything else closed.");

        // J is I plus a hook traced in the air, so it needs several keys.
        signs['J'] = new Sign
        {
            Name = "J",
            Description = "Pinky up, then swept down and hooked to trace a J.",
            Keys =
            [
                new SignKey(Shape(ThumbAcross, Fist, Fist, Fist, F(0f, 0f, 0f)), 0.12f, 0.06f),
                new SignKey(Shape(ThumbAcross, Fist, Fist, Fist, F(0f, 0f, 0f))
                    .With(wrist: new Vector3(0f, 0f, -28f)), 0.11f, 0.02f),
                new SignKey(Shape(ThumbAcross, Fist, Fist, Fist, F(0f, 0f, 0f))
                    .With(wrist: new Vector3(0f, -22f, -42f)), 0.11f, 0.10f),
            ],
        };

        Add('K', Shape(F(0.20f, 0.30f, 0.10f), F(0f, 0f, -0.35f), F(0f, 0f, 0.25f), Fist, Fist),
            "Index and middle up in a V, thumb pressed between them.");

        Add('L', Shape(F(0f, 0f, -1.00f), F(0f, 0f), Fist, Fist, Fist),
            "Index straight up and thumb straight out, forming an L.");

        Add('M', Shape(F(0.65f, 0.90f, 0.20f), F(0.90f, 1f), F(0.90f, 1f), F(0.90f, 1f), Fist),
            "Thumb tucked under the index, middle and ring fingers.");

        Add('N', Shape(F(0.60f, 0.85f, 0.15f), F(0.90f, 1f), F(0.90f, 1f), Fist, Fist),
            "Thumb tucked under the index and middle fingers.");

        Add('O', Shape(F(0.50f, 0.50f, -0.20f), F(0.60f, 0.70f), F(0.60f, 0.70f), F(0.60f, 0.70f), F(0.60f, 0.70f)),
            "All fingertips meeting the thumb in a round O.");

        Add('P', Shape(F(0.20f, 0.30f, 0.10f), F(0f, 0f, -0.35f), F(0f, 0f, 0.25f), Fist, Fist)
                .With(wrist: new Vector3(-55f, 0f, 0f)),
            "The K handshape rotated to point downward.");

        Add('Q', Shape(F(0.10f, 0.10f, -0.40f), F(0f, 0f, -0.05f), Fist, Fist, Fist)
                .With(wrist: new Vector3(-55f, 0f, 0f)),
            "The G handshape rotated to point downward.");

        Add('R', Shape(ThumbAcross, F(0f, 0f, 0.30f), F(0f, 0f, -0.30f), Fist, Fist),
            "Index and middle fingers crossed over one another.");

        Add('S', Shape(F(0.55f, 0.65f, -0.80f), Fist, Fist, Fist, Fist),
            "Closed fist with the thumb across the front of the fingers.");

        Add('T', Shape(F(0.50f, 0.55f, 0.10f), F(0.85f, 1f), Fist, Fist, Fist),
            "Thumb poking up between the index and middle fingers.");

        Add('U', Shape(ThumbAcross, F(0f, 0f, 0.02f), F(0f, 0f, -0.02f), Fist, Fist),
            "Index and middle straight up, held together.");

        Add('V', Shape(ThumbAcross, F(0f, 0f, -0.40f), F(0f, 0f, 0.30f), Fist, Fist),
            "Index and middle straight up, spread into a V.");

        Add('W', Shape(F(0.70f, 0.85f, -0.50f), F(0f, 0f, -0.35f), F(0f, 0f, 0f), F(0f, 0f, 0.35f), Fist),
            "Index, middle and ring straight up and spread apart.");

        Add('X', Shape(ThumbAcross, F(0.20f, 1f), Fist, Fist, Fist),
            "Index finger crooked into a hook.");

        Add('Y', Shape(F(0f, 0f, -1.00f), Fist, Fist, Fist, F(0f, 0f, 0.35f)),
            "Thumb and pinky extended, the middle three closed.");

        // Z is drawn in the air with the index finger.
        var zHand = Shape(ThumbAcross, F(0f, 0f), Fist, Fist, Fist);
        signs['Z'] = new Sign
        {
            Name = "Z",
            Description = "Index finger extended, drawing a Z in the air.",
            Keys =
            [
                new SignKey(zHand.With(wrist: new Vector3(0f, -26f, 12f)), 0.12f, 0.03f),
                new SignKey(zHand.With(wrist: new Vector3(0f, 26f, 12f)), 0.09f, 0.01f),
                new SignKey(zHand.With(wrist: new Vector3(0f, -26f, -16f)), 0.10f, 0.01f),
                new SignKey(zHand.With(wrist: new Vector3(0f, 26f, -16f)), 0.09f, 0.08f),
            ],
        };

        // ---- Numbers ---------------------------------------------------------

        Add('0', Shape(F(0.50f, 0.50f, -0.20f), F(0.60f, 0.70f), F(0.60f, 0.70f), F(0.60f, 0.70f), F(0.60f, 0.70f)),
            "Round O shape, same as the letter O.");

        Add('1', Shape(ThumbAcross, F(0f, 0f), Fist, Fist, Fist),
            "Index finger straight up.");

        Add('2', Shape(ThumbAcross, F(0f, 0f, -0.40f), F(0f, 0f, 0.30f), Fist, Fist),
            "Index and middle up in a V.");

        Add('3', Shape(F(0f, 0f, -0.80f), F(0f, 0f, -0.30f), F(0f, 0f, 0.15f), Fist, Fist),
            "Thumb, index and middle extended.");

        Add('4', Shape(F(0.60f, 0.80f, -0.90f), F(0f, 0f, -0.25f), F(0f, 0f, -0.08f), F(0f, 0f, 0.08f), F(0f, 0f, 0.25f)),
            "Four fingers up and apart, thumb across the palm.");

        Add('5', Shape(F(0f, 0f, -0.90f), F(0f, 0f, -0.30f), F(0f, 0f, -0.10f), F(0f, 0f, 0.10f), F(0f, 0f, 0.30f)),
            "Open hand, all five digits spread.");

        Add('6', Shape(F(0.45f, 0.50f, 0.10f), F(0f, 0f, -0.25f), F(0f, 0f, -0.05f), F(0f, 0f, 0.15f), F(0.60f, 0.70f)),
            "Pinky touching the thumb, other three up.");

        Add('7', Shape(F(0.45f, 0.50f, 0.15f), F(0f, 0f, -0.25f), F(0f, 0f, -0.05f), F(0.60f, 0.70f), F(0f, 0f, 0.25f)),
            "Ring finger touching the thumb, other three up.");

        Add('8', Shape(F(0.45f, 0.50f, 0.10f), F(0f, 0f, -0.25f), F(0.60f, 0.70f), F(0f, 0f, 0.10f), F(0f, 0f, 0.25f)),
            "Middle finger touching the thumb, other three up.");

        Add('9', Shape(F(0.50f, 0.55f, -0.15f), F(0.55f, 0.70f, -0.10f), F(0f, 0f, -0.05f), F(0f, 0f, 0.05f), F(0f, 0f, 0.15f)),
            "Index touching the thumb, other three up. Same shape as the letter F.");

        return signs;
    }
}
