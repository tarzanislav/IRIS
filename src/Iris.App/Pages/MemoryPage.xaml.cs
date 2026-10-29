using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Iris.Assistant.Packs;

namespace Iris.App.Pages;

/// <summary>A row of the memory page: two texts, a time and an action (forget, remove).</summary>
public sealed record MemoryRow(string First, string Second, string When, string Action, Action? Run)
{
    public Visibility ActionVisibility => Run is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The corrections of a direction, its glossary of names and titles, and the history of translations. The memory of
/// en → sk is shared with UNIVERSUM.
/// </summary>
public partial class MemoryPage : UserControl
{
    private bool _filling;

    public MemoryPage()
    {
        InitializeComponent();
        FillDirections();
        TabCorrections.IsChecked = true;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Show();
            }
        };
        App.Current.SettingsChanged += () =>
        {
            FillDirections();
            Show();
        };
    }

    private LanguagePair Pair => Direction.SelectedItem is DirectionItem item ? item.Pair : new LanguagePair("en", "sk");

    private void FillDirections()
    {
        _filling = true;
        var settings = App.Current.Settings;
        var current = new LanguagePair(settings.SourceLanguage, settings.TargetLanguage);
        var pairs = App.Current.Service.Packs.Installed().Append(current).Distinct().OrderBy(p => p.ToString(), StringComparer.Ordinal).ToList();
        var items = pairs.Select(p => new DirectionItem(p)).ToList();
        var selected = Direction.SelectedItem is DirectionItem old ? old.Pair : current;
        Direction.ItemsSource = items;
        Direction.SelectedItem = items.FirstOrDefault(i => i.Pair == selected) ?? items.First(i => i.Pair == current);
        _filling = false;
    }

    private void DirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            Show();
        }
    }

    private void TabChanged(object sender, RoutedEventArgs e) => Show();

    private void SearchChanged(object sender, TextChangedEventArgs e) => Show();

    private void Show()
    {
        if (TabCorrections is null || Rows is null)
        {
            return;
        }

        var service = App.Current.Service;
        var memory = service.MemoryFor(Pair);
        var glossary = service.GlossaryFor(Pair);
        TabCorrections.Content = Localizer.Instance.Format("memory.corrections", memory.Count);
        TabTerms.Content = Localizer.Instance.Format("memory.terms", glossary.Count);
        var search = Search.Text.Trim();
        bool Matches(string a, string b) =>
            search.Length == 0 || a.Contains(search, StringComparison.CurrentCultureIgnoreCase) || b.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        var forget = Localizer.Instance["correct.forget"];
        var remove = Localizer.Instance["settings.remove"];
        List<MemoryRow> rows;
        if (TabCorrections.IsChecked == true)
        {
            rows = [.. memory.Entries.Where(m => Matches(m.Source, m.Target)).OrderByDescending(m => m.Updated)
                .Select(m => new MemoryRow(m.Source, m.Target, When(m.Updated), forget, () => memory.Forget(m.Source)))];
        }
        else if (TabTerms.IsChecked == true)
        {
            rows = [.. glossary.Entries.Where(t => Matches(t.Source, t.Output)).OrderBy(t => t.Source, StringComparer.CurrentCulture)
                .Select(t => new MemoryRow(t.Source, t.Target ?? "—", "", remove, () =>
                {
                    glossary.Remove(t.Source);
                    SaveGlossary();
                }))];
        }
        else
        {
            rows = [.. App.Current.History.Entries(search.Length == 0 ? null : search)
                .Select(h => new MemoryRow(h.Source, h.Target, When(h.Time), "", null))];
        }

        Rows.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddTerm.Visibility = TabTerms.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryActions.Visibility = TabHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Shared.Visibility = Pair == new LanguagePair("en", "sk") && TabHistory.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("d. M. yyyy", CultureInfo.CurrentCulture);
    }

    private void RowAction(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is MemoryRow { Run: { } run })
        {
            run();
            Show();
        }
    }

    private void AddTermClick(object sender, RoutedEventArgs e)
    {
        var term = Term.Text.Trim();
        if (term.Length == 0 || term.Contains('\t', StringComparison.Ordinal) || TermTarget.Text.Contains('\t', StringComparison.Ordinal))
        {
            return;
        }

        App.Current.Service.GlossaryFor(Pair).Add(term, TermTarget.Text.Trim().Length == 0 ? null : TermTarget.Text.Trim());
        SaveGlossary();
        Term.Text = TermTarget.Text = "";
        Show();
    }

    private void SaveGlossary()
    {
        Directory.CreateDirectory(IrisPaths.Home);
        App.Current.Service.GlossaryFor(Pair).Save(IrisPaths.Glossary(Pair.ToString()));
    }

    private void ClearHistory(object sender, RoutedEventArgs e)
    {
        App.Current.History.Clear();
        Show();
    }

    private sealed record DirectionItem(LanguagePair Pair)
    {
        public override string ToString() => $"{new LanguageItem(Pair.Source).Name} → {new LanguageItem(Pair.Target).Name}";
    }
}
