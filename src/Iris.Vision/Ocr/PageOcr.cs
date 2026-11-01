using System.Text;
using Iris.Vision.Imaging;
using Iris.Vision.Layout;

namespace Iris.Vision.Ocr;

/// <summary>The text read from a picture of a page: the lines as read, the page text joined from them, the slant.</summary>
public sealed record PageText(IReadOnlyList<string> Lines, string Text, double Skew);

/// <summary>
/// Reads a whole page: straightens it, finds its lines, reads them (in parallel) and joins them into running text –
/// a word split at the end of a line ("informá-" + "cie") is put back together, other lines are joined by a space,
/// because a book line ends where the paper ends, not where the sentence does.
/// </summary>
public static class PageOcr
{
    public static PageText Read(GrayImage picture, LineReader reader)
    {
        var page = PageReader.Prepare(picture);
        var lines = new string[page.Lines.Count];
        Parallel.For(0, lines.Length, i => lines[i] = reader.Read(page.Lines[i].Image).Text.Trim());
        var kept = lines.Where(l => l.Length > 0).ToList();
        return new PageText(kept, Join(kept), page.Skew);
    }

    /// <summary>Lines of a page as running text.</summary>
    public static string Join(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length == 0)
            {
                text.Append(line);
                continue;
            }

            // a hyphen at the end of a line, a small letter at the start of the next: one word cut in two
            var last = text[^1];
            var hyphen = last is '-' or '­' && text.Length > 1 && char.IsLetter(text[^2]);
            if (hyphen && char.IsLower(line[0]))
            {
                text.Length--;
                text.Append(line);
            }
            else
            {
                text.Append(' ').Append(line);
            }
        }

        return text.ToString();
    }
}
