namespace Iris;

/// <summary>
/// Where IRIS keeps its files on this computer, shared by every program that uses IRIS (the IRIS application,
/// UNIVERSUM), so that a correction made in one is known in the other. The folder is the user's local application
/// data (Windows: %LOCALAPPDATA%\IRIS, Linux: ~/.local/share/IRIS, macOS: ~/Library/Application Support/IRIS);
/// the environment variable IRIS_HOME moves it elsewhere. Nothing here is created until it is written.
/// </summary>
public static class IrisPaths
{
    /// <summary>The folder of IRIS's own files.</summary>
    public static string Home =>
        Environment.GetEnvironmentVariable("IRIS_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "IRIS");

    /// <summary>Model files (.iris).</summary>
    public static string Models => Path.Combine(Home, "models");

    /// <summary>The OCR model (reads text from photos and scans of pages).</summary>
    public static string Ocr => Path.Combine(Models, "iris-ocr.iris");

    /// <summary>The translation memory of a language pair, e.g. "en-sk".</summary>
    public static string Memory(string pair) => Path.Combine(Home, $"memory-{Pair(pair)}.jsonl");

    /// <summary>The glossary of names and titles of a language pair, e.g. "en-sk".</summary>
    public static string Glossary(string pair) => Path.Combine(Home, $"glossary-{Pair(pair)}.tsv");

    /// <summary>Verified pairs of sentences the owner wants IRIS to learn from (the study room), e.g. "en-sk".</summary>
    public static string Study(string pair) => Path.Combine(Home, $"study-{Pair(pair)}.jsonl");

    /// <summary>What IRIS learned for a language pair (an adapter of its model) and its report.</summary>
    public static string Learned(string pair) => Path.Combine(Home, $"learned-{Pair(pair)}.iris");

    // a pair is two language codes: letters only, so a pair can never point outside the folder
    private static string Pair(string pair)
    {
        var parts = pair.Split('-');
        return parts.Length == 2 && parts.All(p => p.Length is >= 2 and <= 3 && p.All(char.IsAsciiLetterLower))
            ? pair
            : throw new ArgumentException($"a language pair is like \"en-sk\", not \"{pair}\"");
    }
}
