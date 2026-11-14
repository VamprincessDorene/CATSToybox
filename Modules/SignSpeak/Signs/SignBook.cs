using System.Collections.Generic;
using System.Text;

namespace SignSpeak.Signs;

/// <summary>
/// One entry in a queued message.
///
/// Deliberately just a character and a pause: which shape that character maps
/// to is <see cref="SignLibrary"/>'s business, so a letter the user has
/// hand-authored and one using the built-in shape are indistinguishable here.
/// </summary>
public readonly struct SignStep
{
    /// <summary>Extra rest inserted after this step, in seconds at 1x speed.</summary>
    public float GapSeconds { get; init; }

    /// <summary>The character to sign.</summary>
    public char Source { get; init; }
}

/// <summary>
/// Turns typed text into a sequence of signs.
///
/// Right now this is pure fingerspelling: every letter and digit becomes its
/// manual-alphabet handshape. Whole-word lexical signs slot in at the top of
/// <see cref="Translate"/> later, with fingerspelling remaining the fallback for
/// anything not in the dictionary, which is exactly how a signer handles an
/// unfamiliar word.
/// </summary>
public static class SignBook
{
    /// <summary>Rest inserted between words.</summary>
    public const float WordGapSeconds = 0.34f;

    /// <summary>
    /// Extra rest inserted between two identical letters in a row. Without it
    /// the hand simply holds still and the doubled letter reads as one.
    /// </summary>
    public const float DoubleLetterGapSeconds = 0.14f;

    /// <summary>Rest inserted at a sentence boundary.</summary>
    public const float SentenceGapSeconds = 0.55f;

    public static List<SignStep> Translate(string text)
    {
        var steps = new List<SignStep>();
        if (string.IsNullOrWhiteSpace(text))
            return steps;

        var previous = '\0';

        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw))
            {
                AppendGap(steps, WordGapSeconds);
                previous = '\0';
                continue;
            }

            if (raw is '.' or '!' or '?' or ',' or ';' or ':')
            {
                AppendGap(steps, SentenceGapSeconds);
                previous = '\0';
                continue;
            }

            var upper = char.ToUpperInvariant(raw);
            if (!Alphabet.TryGet(upper, out _))
            {
                // Unsupported character. Skip it rather than stalling the message.
                previous = '\0';
                continue;
            }

            // Doubled letters need a visible beat *between* them, so the pause
            // belongs on the letter already queued, not the one being added.
            // Without this the hand simply holds still and LL reads as one L.
            if (upper == previous && steps.Count > 0)
            {
                var last = steps[^1];
                if (last.GapSeconds < DoubleLetterGapSeconds)
                    steps[^1] = last with { GapSeconds = DoubleLetterGapSeconds };
            }

            steps.Add(new SignStep { GapSeconds = 0f, Source = upper });
            previous = upper;
        }

        return steps;
    }

    /// <summary>
    /// Merges a pause into the previous step where possible, so the queue never
    /// carries a run of empty steps.
    /// </summary>
    private static void AppendGap(List<SignStep> steps, float seconds)
    {
        if (steps.Count == 0)
            return;

        var last = steps[^1];
        steps[^1] = new SignStep
        {
            GapSeconds = last.GapSeconds > seconds ? last.GapSeconds : seconds,
            Source = last.Source,
        };
    }

    /// <summary>Whether SignSpeak has any shape at all for this character.</summary>
    public static bool CanSign(char character) => Alphabet.TryGet(character, out _);

    /// <summary>
    /// Lists the distinct characters of <paramref name="text"/> that cannot be
    /// signed yet, for the warning line in the UI.
    /// </summary>
    public static string Unsupported(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var seen = new HashSet<char>();
        var skipped = new StringBuilder();

        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw) || raw is '.' or '!' or '?' or ',' or ';' or ':')
                continue;

            if (!Alphabet.TryGet(raw, out _) && seen.Add(raw))
                skipped.Append(raw);
        }

        return skipped.ToString();
    }
}
