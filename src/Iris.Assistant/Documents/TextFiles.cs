using System.Text;

namespace Iris.Assistant.Documents;

/// <summary>A text file read with the encoding it was written in.</summary>
public sealed record TextFile(string Text, Encoding Encoding, bool HasBom);

/// <summary>
/// Reading and writing plain text files the way people have them: UTF-8 (with or without BOM), UTF-16 with BOM, and
/// older files in the Central European code page (Windows-1250, common for Slovak and Czech subtitles).
/// </summary>
public static class TextFiles
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static TextFile Read(string path) => Decode(File.ReadAllBytes(path));

    public static TextFile Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return new TextFile(StrictUtf8.GetString(bytes, 3, bytes.Length - 3), Encoding.UTF8, true);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return new TextFile(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, true);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return new TextFile(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, true);
        }

        try
        {
            return new TextFile(StrictUtf8.GetString(bytes), Encoding.UTF8, false);
        }
        catch (DecoderFallbackException)
        {
            var legacy = Encoding.GetEncoding(1250);
            return new TextFile(legacy.GetString(bytes), legacy, false);
        }
    }

    /// <summary>Writes a new file in UTF-8 (with a BOM when asked – subtitle players read that most reliably).</summary>
    public static void Write(string path, string text, bool bom) =>
        File.WriteAllText(path, text, new UTF8Encoding(bom));
}
