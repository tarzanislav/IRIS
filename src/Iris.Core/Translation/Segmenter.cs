using System.Text;

namespace Iris.Translation;

/// <summary>A piece of a text: a sentence to translate, or what lies between sentences (spaces, line breaks) and is kept.</summary>
public readonly record struct Segment(string Text, bool IsSentence);

/// <summary>
/// Cuts a text into sentences – the model translates sentence by sentence – and keeps everything between them, so the
/// translation keeps the layout (lines, paragraphs, spaces). A sentence ends with . ! ? or … (and closing quotes or
/// brackets) before a space and a capital letter, digit or opening quote; not after an abbreviation (Mr., Dr., e.g.,
/// U.S., např. …) or an initial ("J. K."). Every line break ends a sentence.
/// </summary>
public static class Segmenter
{
    // abbreviations after which a capital letter does not start a new sentence ("Mr. Hale", "Gen. Morrow", "napr. Film")
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "vs", "e.g", "i.e", "u.s", "u.k", "u.n", "inc", "ltd", "corp",
        "dept", "gen", "gov", "sen", "rep", "sgt", "capt", "lt", "col", "mt", "a.m", "p.m",
        // Czech and Slovak
        "např", "napr", "tzv", "resp", "tj", "t.j", "mj", "pozn", "příp", "prip", "ing", "mgr", "bc", "doc", "phdr", "mudr",
        "judr", "rndr", "sv", "ul", "nám",
    };

    // abbreviations that stand before a number ("No. 5", "Jan. 12", "č. 7", "min. 3"); before a word they end a sentence
    private static readonly HashSet<string> NumberAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "nos", "vol", "fig", "est", "approx", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct",
        "nov", "dec", "p", "pp", "ch", "č", "čís", "str", "obr", "kap", "tab", "tel", "min", "max",
    };

    private const string Closers = "\"'”’»)]";
    private const string Openers = "\"'“‘«([";

    /// <summary>The segments of a text; joined together they give the text back exactly.</summary>
    public static List<Segment> Split(string text)
    {
        var segments = new List<Segment>();
        var i = 0;
        while (i < text.Length)
        {
            // what lies between sentences: whitespace
            var start = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i > start)
            {
                segments.Add(new Segment(text[start..i], false));
            }

            if (i == text.Length)
            {
                break;
            }

            // a sentence: up to its end, a line break or the end of the text
            start = i;
            var end = SentenceEnd(text, i);
            segments.Add(new Segment(text[start..end], true));
            i = end;
        }

        return segments;
    }

    /// <summary>The sentences of a text (without what lies between them).</summary>
    public static IEnumerable<string> Sentences(string text) => Split(text).Where(s => s.IsSentence).Select(s => s.Text);

    // the index just after the sentence that starts at `start` (trailing spaces not included)
    private static int SentenceEnd(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\n' or '\r')
            {
                return TrimEnd(text, start, i);
            }

            if (c is '.' or '!' or '?' or '…')
            {
                var stop = i + 1;
                while (stop < text.Length && (text[stop] is '.' or '!' or '?' or '…' || Closers.Contains(text[stop])))
                {
                    stop++;
                }

                if (stop == text.Length)
                {
                    return stop;
                }

                if (char.IsWhiteSpace(text[stop]) && StartsSentence(text, stop, out var next) && !(c == '.' && Abbreviation(text, start, i, next)))
                {
                    return stop;
                }

                i = stop;
                continue;
            }

            i++;
        }

        return TrimEnd(text, start, text.Length);
    }

    private static int TrimEnd(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end;
    }

    // after the spaces comes a capital letter or something that is not a letter (a digit, a quote, a bracket), or the
    // text ends or a line breaks; next is the character that comes ('\0' at the end)
    private static bool StartsSentence(string text, int i, out char next)
    {
        next = '\0';
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            if (text[i] is '\n' or '\r')
            {
                return true;
            }

            i++;
        }

        if (i == text.Length)
        {
            return true;
        }

        next = text[i];
        return char.IsUpper(next) || (!char.IsLetter(next) && next is not (',' or ';' or ':'));
    }

    // the word before the dot at `dot` is an abbreviation or an initial ("J. K.")
    private static bool Abbreviation(string text, int start, int dot, char next)
    {
        var w = dot;
        while (w > start && !char.IsWhiteSpace(text[w - 1]) && !Openers.Contains(text[w - 1]))
        {
            w--;
        }

        var word = text[w..dot];
        return (word.Length == 1 && char.IsLetter(word[0])) ||
               Abbreviations.Contains(word) ||
               (NumberAbbreviations.Contains(word) && char.IsDigit(next)) ||
               (word.Length <= 4 && word.Contains('.', StringComparison.Ordinal) && word.Count(char.IsLetter) <= 3);
    }

    /// <summary>Puts translated sentences back in place of the sentences of the segments.</summary>
    public static string Join(IReadOnlyList<Segment> segments, IReadOnlyList<string> translations)
    {
        var text = new StringBuilder();
        var next = 0;
        foreach (var segment in segments)
        {
            text.Append(segment.IsSentence ? translations[next++] : segment.Text);
        }

        return text.ToString();
    }
}
