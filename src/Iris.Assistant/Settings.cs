using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Assistant;

/// <summary>The look of the application.</summary>
public enum Theme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// The settings of the application, kept as JSON in the IRIS folder. A missing or damaged file gives the defaults, and
/// values out of range fall back to their default, so the application always starts.
/// </summary>
public sealed record Settings
{
    /// <summary>Language of the interface: "sk", "cs", "en", or null = the language of the system.</summary>
    public string? InterfaceLanguage { get; init; }

    public Theme Theme { get; init; } = Theme.System;

    public string SourceLanguage { get; init; } = "en";

    public string TargetLanguage { get; init; } = "sk";

    /// <summary>Beam search (best translation, the default) or greedy (about twice as fast).</summary>
    public bool Quick { get; init; }

    /// <summary>Whether translations are kept in the history.</summary>
    public bool SaveHistory { get; init; } = true;

    /// <summary>The shortcut that translates the selected text in any application.</summary>
    public string TranslateShortcut { get; init; } = "Ctrl+Shift+T";

    public static Settings Load(string path)
    {
        try
        {
            var settings = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), SettingsJson.Default.Settings) : null;
            return (settings ?? new Settings()).Checked();
        }
        catch (JsonException)
        {
            return new Settings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SettingsJson.Default.Settings), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private Settings Checked()
    {
        static bool Code(string? c) => c is { Length: 2 } && c.All(char.IsAsciiLetterLower);
        var defaults = new Settings();
        var languagesOk = Code(SourceLanguage) && Code(TargetLanguage) && SourceLanguage != TargetLanguage;
        return this with
        {
            InterfaceLanguage = InterfaceLanguage is "sk" or "cs" or "en" ? InterfaceLanguage : null,
            Theme = Enum.IsDefined(Theme) ? Theme : Theme.System,
            SourceLanguage = languagesOk ? SourceLanguage : defaults.SourceLanguage,
            TargetLanguage = languagesOk ? TargetLanguage : defaults.TargetLanguage,
            TranslateShortcut = string.IsNullOrWhiteSpace(TranslateShortcut) ? defaults.TranslateShortcut : TranslateShortcut,
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

[JsonSerializable(typeof(HistoryEntry))]
internal sealed partial class HistoryJson : JsonSerializerContext;
