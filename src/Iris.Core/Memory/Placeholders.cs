using System.Globalization;
using System.Text;

namespace Iris.Memory;

/// <summary>A placeholder put into a sentence instead of a term, and what replaces it in the translation.</summary>
public readonly record struct Placeholder(string Mark, string Output);

/// <summary>
/// Hides glossary terms from the model: each term becomes a four-digit number (numbers are copied by the model as
/// they are), and after translation the number is replaced by the wanted form of the term. A translation is accepted
/// only when every number comes back exactly once.
/// </summary>
public static class Placeholders
{
    private static readonly int[] Bases = [7301, 8401, 9501, 6201, 5101];

    /// <summary>The sentence with its terms replaced; no placeholders when it has no terms.</summary>
    public static (string Text, List<Placeholder> Marks) Protect(string sentence, IReadOnlyList<TermMatch> matches)
    {
        if (matches.Count == 0)
        {
            return (sentence, []);
        }

        var start = Bases.FirstOrDefault(b => Enumerable.Range(b, matches.Count).All(n => !sentence.Contains(Mark(n), StringComparison.Ordinal)));
        if (start == 0)
        {
            return (sentence, []);
        }

        var text = new StringBuilder();
        var marks = new List<Placeholder>();
        var at = 0;
        foreach (var match in matches)
        {
            var mark = Mark(start + marks.Count);
            text.Append(sentence, at, match.Start - at).Append(mark);
            marks.Add(new Placeholder(mark, match.Entry.Output));
            at = match.Start + match.Length;
        }

        text.Append(sentence, at, sentence.Length - at);
        return (text.ToString(), marks);
    }

    /// <summary>Puts the terms back; false when a placeholder is missing or doubled in the translation.</summary>
    public static bool TryRestore(string translation, IReadOnlyList<Placeholder> marks, out string restored)
    {
        restored = translation;
        foreach (var mark in marks)
        {
            var places = Places(restored, mark.Mark);
            if (places.Count != 1)
            {
                restored = translation;
                return false;
            }

            restored = string.Concat(restored.AsSpan(0, places[0]), mark.Output, restored.AsSpan(places[0] + mark.Mark.Length));
        }

        return true;
    }

    private static string Mark(int number) => number.ToString(CultureInfo.InvariantCulture);

    // where the number stands on its own (not inside a longer number)
    private static List<int> Places(string text, string number)
    {
        var places = new List<int>();
        for (var i = text.IndexOf(number, StringComparison.Ordinal); i >= 0; i = text.IndexOf(number, i + 1, StringComparison.Ordinal))
        {
            var end = i + number.Length;
            if ((i == 0 || !char.IsDigit(text[i - 1])) && (end == text.Length || !char.IsDigit(text[end])))
            {
                places.Add(i);
            }
        }

        return places;
    }
}
