namespace Iris.Assistant;

/// <summary>
/// The files of the user's own data in the IRIS folder – corrections, glossaries, history, settings – as opposed to the
/// language packs. "Delete all my data" deletes exactly these.
/// </summary>
public static class UserData
{
    public static string SettingsPath => Path.Combine(IrisPaths.Home, "settings.json");

    public static string HistoryPath => Path.Combine(IrisPaths.Home, "history.jsonl");

    /// <summary>The files of the user's data that exist now.</summary>
    public static IReadOnlyList<string> Files(string home)
    {
        if (!Directory.Exists(home))
        {
            return [];
        }

        static bool Own(string name) =>
            name.StartsWith("memory-", StringComparison.Ordinal) || name.StartsWith("glossary-", StringComparison.Ordinal) ||
            name.StartsWith("history.", StringComparison.Ordinal) || name.StartsWith("settings.", StringComparison.Ordinal);
        return [.. Directory.EnumerateFiles(home).Where(f => Own(Path.GetFileName(f))).Order(StringComparer.Ordinal)];
    }

    /// <summary>Deletes the user's data (not the language packs); returns how many files were deleted.</summary>
    public static int DeleteAll(string home)
    {
        var files = Files(home);
        foreach (var file in files)
        {
            File.Delete(file);
        }

        return files.Count;
    }
}
