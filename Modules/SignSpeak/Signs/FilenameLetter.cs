using System;
using System.Collections.Generic;

namespace SignSpeak.Signs;

/// <summary>
/// Works out which character a pose file is meant to be, from its filename.
///
/// This exists so a folder of hand-authored poses can be imported in one go.
/// Real filenames in the wild look like "Letter A .livepose", "Letter B",
/// "asl_c", "D-right hand" or just "E", so the rule is: throw away the words
/// that only describe what the file is, and if exactly one single-character
/// token survives, that is the answer.
///
/// Anything ambiguous returns null rather than guessing, because silently
/// mapping a pose onto the wrong letter is much worse than asking.
/// </summary>
public static class FilenameLetter
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "letter", "letters", "sign", "signs", "asl", "hand", "hands", "pose", "poses",
        "livepose", "right", "left", "final", "fixed", "new", "old", "copy",
        "v1", "v2", "v3", "test", "wip", "edit", "edited", "number", "digit",

        // Note the absence of "r" and "l". They are tempting as abbreviations
        // for right and left, but they are also the letters R and L, and
        // treating them as noise would make those two impossible to import.
    };

    public static char? Detect(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        var tokens = Tokenise(filename);
        var candidates = new List<char>();

        foreach (var token in tokens)
        {
            if (Noise.Contains(token))
                continue;

            if (token.Length != 1)
                continue;

            var candidate = char.ToUpperInvariant(token[0]);
            if (Alphabet.TryGet(candidate, out _))
                candidates.Add(candidate);
        }

        // Exactly one surviving single character is a confident match. Zero
        // means we cannot tell; more than one means the name is ambiguous
        // (something like "A and B"), and both deserve a manual choice rather
        // than a guess that quietly maps a pose onto the wrong letter.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static List<string> Tokenise(string filename)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in filename)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        // A run-together name like "LetterA" hides the character inside a
        // token, so also offer the trailing character of any token that is a
        // noise word plus one more character.
        var extra = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length < 2)
                continue;

            var head = token[..^1];
            if (Noise.Contains(head))
                extra.Add(token[^1..]);
        }

        tokens.AddRange(extra);
        return tokens;
    }
}
