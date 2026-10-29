using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Pages;
using Iris.App.Platform;

namespace Iris.App;

/// <summary>The main window: the menu on the left, the page on the right. Closing it hides IRIS to the notification area.</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.Ordinal);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.TitleBar(this, App.Current.IsDark);
        App.Current.SettingsChanged += ShowPacks;
        ShowPacks();
        NavTranslate.IsChecked = true;
    }

    /// <summary>Opens the translation page with a text (from the quick window).</summary>
    public void OpenTranslation(string text)
    {
        NavTranslate.IsChecked = true;
        ((TranslatePage)PageOf("translate")).Load(text);
    }

    /// <summary>Opens the settings at the language packs (when a direction has no pack).</summary>
    public void OpenPacks() => NavSettings.IsChecked = true;

    /// <summary>Called by pages after packs were installed or removed.</summary>
    public void ShowPacks() =>
        Packs.Text = string.Join(", ", App.Current.Service.Packs.Installed().Select(p => $"{p.Source.ToUpperInvariant()}→{p.Target.ToUpperInvariant()}"));

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Current.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void Navigate(object sender, RoutedEventArgs e) =>
        Page.Content = PageOf((string)((RadioButton)sender).Tag);

    private UserControl PageOf(string name)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "translate" => new TranslatePage(),
                "files" => new FilesPage(),
                "memory" => new MemoryPage(),
                "settings" => new SettingsPage(),
                "voice" => new SoonPage("nav.voice"),
                _ => new SoonPage("nav.learning"),
            };
            _pages[name] = page;
        }

        return page;
    }
}
