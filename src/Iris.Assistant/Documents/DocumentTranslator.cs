using Iris.Translation;

namespace Iris.Assistant.Documents;

/// <summary>The kinds of files IRIS translates.</summary>
public enum DocumentKind
{
    Text,
    Subtitles,
    Word,
}

/// <summary>
/// Translates a file into a new file next to it (the original is never changed): "film.en.srt" → "film.sk.srt",
/// "notes.txt" → "notes.sk.txt"; an existing file is never overwritten ("notes.sk (2).txt").
/// </summary>
public static class DocumentTranslator
{
    /// <summary>The kind of a file by its extension, or null when IRIS does not translate it.</summary>
    public static DocumentKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" => DocumentKind.Text,
        ".srt" => DocumentKind.Subtitles,
        ".docx" => DocumentKind.Word,
        _ => null,
    };

    /// <summary>The new file for a translation into a language: next to the source, never an existing file.</summary>
    public static string TargetPath(string source, string sourceLanguage, string targetLanguage)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var extension = Path.GetExtension(source);
        var stem = Path.GetFileNameWithoutExtension(source);
        var languageSuffix = "." + sourceLanguage;
        if (stem.EndsWith(languageSuffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^languageSuffix.Length];
        }

        var name = $"{stem}.{targetLanguage}";
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        }

        return path;
    }

    /// <summary>
    /// Translates a file; returns the new file. Progress counts the units of the file (sentences of a text,
    /// subtitles, paragraphs of a document).
    /// </summary>
    public static string Translate(TranslationService service, string source, string sourceLanguage, string targetLanguage,
        int? beams = null, CancellationToken cancellation = default, IProgress<TranslationProgress>? progress = null)
    {
        var kind = KindOf(source) ?? throw new NotSupportedException($"IRIS does not translate {Path.GetExtension(source)} files");
        if (!service.CanTranslate(sourceLanguage, targetLanguage))
        {
            throw new InvalidOperationException($"no language pack translates {sourceLanguage} → {targetLanguage}");
        }

        string Text(string text) => service.Translate(text, sourceLanguage, targetLanguage, beams, cancellation).Text;
        var target = TargetPath(source, sourceLanguage, targetLanguage);
        var temporary = target + ".part";
        try
        {
            switch (kind)
            {
                case DocumentKind.Text:
                    {
                        var file = TextFiles.Read(source);
                        var translation = service.Translate(file.Text, sourceLanguage, targetLanguage, beams, cancellation, progress);
                        TextFiles.Write(temporary, translation.Text, file.HasBom);
                        break;
                    }

                case DocumentKind.Subtitles:
                    {
                        var subtitles = Subtitles.Parse(TextFiles.Read(source).Text);
                        var translated = new List<Subtitle>(subtitles.Count);
                        foreach (var subtitle in subtitles)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var (text, italic) = Subtitles.TextOf(subtitle);
                            translated.Add(subtitle with { Lines = text.Length == 0 ? [] : Subtitles.Lines(Text(text), italic) });
                            progress?.Report(new TranslationProgress(translated.Count, subtitles.Count));
                        }

                        TextFiles.Write(temporary, Subtitles.Write(translated), bom: true);
                        break;
                    }

                case DocumentKind.Word:
                    {
                        var total = WordDocuments.CountParagraphs(source);
                        var done = 0;
                        WordDocuments.Translate(source, temporary, text =>
                        {
                            var result = Text(text);
                            progress?.Report(new TranslationProgress(++done, total));
                            return result;
                        }, cancellation);
                        break;
                    }
            }

            File.Move(temporary, target);
            return target;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
