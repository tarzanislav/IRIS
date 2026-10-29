using System.Windows.Controls;
using Iris.Assistant.Packs;

namespace Iris.App.Pages;

/// <summary>A language in a picker: its code and its name in the interface language.</summary>
public sealed record LanguageItem(string Code)
{
    public string Name
    {
        get
        {
            var name = Localizer.Instance[$"lang.{Code}"];
            return name == $"lang.{Code}" ? Code.ToUpperInvariant() : name;
        }
    }

    public override string ToString() => Name;
}

/// <summary>The languages IRIS knows: the interface's three and every language of an installed or offered pack.</summary>
public static class Languages
{
    public static IReadOnlyList<LanguageItem> All()
    {
        var app = App.Current;
        IEnumerable<LanguagePair> pairs = app.Service.Packs.Installed();
        if (app.Catalog is not null)
        {
            pairs = pairs.Concat(app.Catalog.Packs.Select(p => p.Pair));
        }

        return [.. new[] { "en", "sk", "cs" }
            .Concat(pairs.SelectMany(p => new[] { p.Source, p.Target }))
            .Distinct(StringComparer.Ordinal)
            .Select(code => new LanguageItem(code))];
    }

    /// <summary>Fills a picker and selects a language.</summary>
    public static void Fill(ComboBox picker, string selected)
    {
        var items = All();
        picker.ItemsSource = items;
        picker.SelectedItem = items.FirstOrDefault(i => i.Code == selected) ?? items[0];
    }

    public static string CodeOf(ComboBox picker) => ((LanguageItem)picker.SelectedItem).Code;
}
