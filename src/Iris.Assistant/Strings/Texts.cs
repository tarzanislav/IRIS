using System.Globalization;
using System.Text.Json;

namespace Iris.Assistant.Strings;

/// <summary>
/// The texts of the user interface in Slovak, Czech and English (Strings/sk.json, cs.json, en.json, built into the
/// library). Another interface language is one more file. A missing text falls back to English, then to its key, so a
/// window never shows an empty label.
/// </summary>
public sealed class Texts
{
    /// <summary>The interface languages.</summary>
    public static readonly IReadOnlyList<string> Languages = ["sk", "cs", "en"];

    private static readonly Dictionary<string, Texts> Loaded = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    private readonly Dictionary<string, string> _texts;
    private readonly Texts? _fallback;

    private Texts(string language, Dictionary<string, string> texts, Texts? fallback)
    {
        Language = language;
        _texts = texts;
        _fallback = fallback;
    }

    public string Language { get; }

    /// <summary>The keys of this language's file.</summary>
    public IEnumerable<string> Keys => _texts.Keys;

    /// <summary>The text of a key.</summary>
    public string this[string key] => _texts.TryGetValue(key, out var text) ? text : _fallback?[key] ?? key;

    /// <summary>The text of a key with values put in its {0}, {1} … places.</summary>
    public string Format(string key, params object[] values) => string.Format(CultureInfo.GetCultureInfo(Language), this[key], values);

    /// <summary>The texts of a language: "sk", "cs", "en", or null = the language of the system (English when it is neither).</summary>
    public static Texts For(string? language)
    {
        language ??= CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (!Languages.Contains(language))
        {
            language = "en";
        }

        lock (Gate)
        {
            if (!Loaded.TryGetValue(language, out var texts))
            {
                texts = new Texts(language, Read(language), language == "en" ? null : For("en"));
                Loaded[language] = texts;
            }

            return texts;
        }
    }

    private static Dictionary<string, string> Read(string language)
    {
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream($"Iris.Assistant.Strings.{language}.json")
            ?? throw new InvalidOperationException($"the texts of {language} are missing from the library");
        return JsonSerializer.Deserialize(stream, TextsJson.Default.DictionaryStringString) ?? [];
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TextsJson : System.Text.Json.Serialization.JsonSerializerContext;
