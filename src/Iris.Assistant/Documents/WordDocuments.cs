using System.IO.Compression;
using System.Xml.Linq;

namespace Iris.Assistant.Documents;

/// <summary>
/// Word documents (.docx): the text of each paragraph (body, tables, headers, footers, footnotes) is translated and the
/// rest of the file – styles, tables, pictures, layout – is copied unchanged into a new file. A paragraph is translated
/// as a whole, so its text lands in its first run: formatting that changed inside a paragraph (one bold word) takes
/// the look of that first run.
/// </summary>
public static class WordDocuments
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Parts with text: the body and its headers, footers, footnotes and endnotes.</summary>
    private static bool HasText(string entry) =>
        entry == "word/document.xml" ||
        (entry.StartsWith("word/", StringComparison.Ordinal) && entry.EndsWith(".xml", StringComparison.Ordinal) &&
         (entry.StartsWith("word/header", StringComparison.Ordinal) || entry.StartsWith("word/footer", StringComparison.Ordinal) ||
          entry is "word/footnotes.xml" or "word/endnotes.xml"));

    /// <summary>The number of paragraphs with text (for progress).</summary>
    public static int CountParagraphs(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Where(e => HasText(e.FullName)).Sum(e =>
        {
            using var stream = e.Open();
            return XDocument.Load(stream).Descendants(W + "p").Count(p => TextOf(p).Trim().Length > 0);
        });
    }

    /// <summary>
    /// Writes a translated copy of a document: translate is called for the text of every paragraph that has text.
    /// The source file is only read.
    /// </summary>
    public static void Translate(string source, string target, Func<string, string> translate, CancellationToken cancellation = default)
    {
        using var input = ZipFile.OpenRead(source);
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var copy = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            copy.LastWriteTime = entry.LastWriteTime;
            using var from = entry.Open();
            using var to = copy.Open();
            if (!HasText(entry.FullName))
            {
                from.CopyTo(to);
                continue;
            }

            var document = XDocument.Load(from, LoadOptions.PreserveWhitespace);
            foreach (var paragraph in document.Descendants(W + "p").ToList())
            {
                cancellation.ThrowIfCancellationRequested();
                var text = TextOf(paragraph);
                if (text.Trim().Length > 0)
                {
                    Replace(paragraph, translate(text));
                }
            }

            document.Save(to, SaveOptions.DisableFormatting);
        }
    }

    // the text of a paragraph's own runs (not of paragraphs nested in it, e.g. in a text box)
    private static string TextOf(XElement paragraph) =>
        string.Concat(OwnTexts(paragraph).Select(t => t.Value));

    private static IEnumerable<XElement> OwnTexts(XElement paragraph) =>
        paragraph.Descendants(W + "t").Where(t => t.Ancestors(W + "p").First() == paragraph);

    // the translation goes into the first text element, the others are emptied (their runs keep their properties)
    private static void Replace(XElement paragraph, string translation)
    {
        var texts = OwnTexts(paragraph).ToList();
        texts[0].Value = translation;
        texts[0].SetAttributeValue(Xml + "space", "preserve");
        foreach (var other in texts.Skip(1))
        {
            other.Value = "";
        }
    }
}
