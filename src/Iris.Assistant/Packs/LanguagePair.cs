namespace Iris.Assistant.Packs;

/// <summary>A direction of translation, e.g. en → sk; written "en-sk". Codes are ISO 639-1, lower case.</summary>
public readonly record struct LanguagePair(string Source, string Target)
{
    /// <summary>The language every pack can be reached through when there is no direct one.</summary>
    public const string Pivot = "en";

    public static LanguagePair Parse(string text) =>
        TryParse(text, out var pair) ? pair : throw new FormatException($"a language pair is like \"en-sk\", not \"{text}\"");

    public static bool TryParse(string? text, out LanguagePair pair)
    {
        pair = default;
        if (text?.Split('-') is not [var source, var target] || !IsCode(source) || !IsCode(target) || source == target)
        {
            return false;
        }

        pair = new LanguagePair(source, target);
        return true;
    }

    public LanguagePair Reversed => new(Target, Source);

    public override string ToString() => $"{Source}-{Target}";

    private static bool IsCode(string code) => code.Length == 2 && code.All(char.IsAsciiLetterLower);
}
