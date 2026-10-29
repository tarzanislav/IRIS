using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Assistant.Documents;

/// <summary>One subtitle: its number, its time line exactly as written and its lines of text.</summary>
public sealed record Subtitle(string Number, string Time, IReadOnlyList<string> Lines);

/// <summary>
/// SubRip subtitles (.srt): numbers and times are kept exactly; only the text is translated. A subtitle is translated as
/// one text (its lines joined) and wrapped again into at most two lines; italics around a whole subtitle are kept.
/// </summary>
public static partial class Subtitles
{
    /// <summary>The longest line a subtitle should have.</summary>
    public const int LineLength = 42;

    public static List<Subtitle> Parse(string text)
    {
        var subtitles = new List<Subtitle>();
        var blocks = BlankLines().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n', '﻿'));
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            if (lines.Length >= 2 && TimeLine().IsMatch(lines[1]))
            {
                subtitles.Add(new Subtitle(lines[0].Trim(), lines[1].Trim(), lines[2..]));
            }
            else if (block.Trim().Length > 0)
            {
                throw new FormatException($"not a SubRip subtitle: \"{lines[0]}\"");
            }
        }

        return subtitles;
    }

    public static string Write(IEnumerable<Subtitle> subtitles)
    {
        var text = new StringBuilder();
        foreach (var subtitle in subtitles)
        {
            text.Append(subtitle.Number).Append("\r\n").Append(subtitle.Time).Append("\r\n");
            foreach (var line in subtitle.Lines)
            {
                text.Append(line).Append("\r\n");
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>The text of a subtitle to translate (lines joined, tags removed) and whether it was all in italics.</summary>
    public static (string Text, bool Italic) TextOf(Subtitle subtitle)
    {
        var joined = string.Join(' ', subtitle.Lines.Select(l => l.Trim())).Trim();
        var italic = joined.StartsWith("<i>", StringComparison.OrdinalIgnoreCase) && joined.EndsWith("</i>", StringComparison.OrdinalIgnoreCase);
        var plain = Tags().Replace(joined, "");
        return (Spaces().Replace(plain, " ").Trim(), italic);
    }

    /// <summary>A translated text as subtitle lines: at most two, broken between words near the middle when too long.</summary>
    public static IReadOnlyList<string> Lines(string text, bool italic)
    {
        var lines = text.Length <= LineLength || !text.Contains(' ', StringComparison.Ordinal) ? [text] : Split(text);
        return italic ? [.. lines.Select(l => $"<i>{l}</i>")] : lines;
    }

    // two lines broken at the space nearest to the middle
    private static string[] Split(string text)
    {
        var middle = text.Length / 2;
        var best = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle)))
            {
                best = i;
            }
        }

        return [text[..best], text[(best + 1)..]];
    }

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"^\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>|\{\\[^}]*\}")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
