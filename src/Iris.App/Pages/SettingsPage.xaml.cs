using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Iris.Assistant;
using Iris.Assistant.Packs;
using Iris.Translation;
using Microsoft.Win32;

namespace Iris.App.Pages;

/// <summary>A language pack on the settings page: installed or offered, and what can be done with it.</summary>
public sealed class PackRow(LanguagePair pair, PackInfo? offered) : INotifyPropertyChanged
{
    private bool _busy;
    private double _percent;
    private string _status = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public LanguagePair Pair { get; } = pair;

    public PackInfo? Offered { get; } = offered;

    public string Name => $"{new LanguageItem(Pair.Source).Name} → {new LanguageItem(Pair.Target).Name}";

    public bool Installed => App.Current.Service.Packs.IsInstalled(Pair);

    public string ActionText => Installed
        ? Localizer.Instance["settings.remove"]
        : Localizer.Instance.Format("settings.download", Math.Round((Offered?.Size ?? 0) / 1e6));

    public bool CanAct => !_busy && (Installed || Offered is not null);

    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            Changed(nameof(CanAct));
            Changed(nameof(ProgressVisibility));
        }
    }

    public double Percent { get => _percent; set => Set(ref _percent, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    public Visibility ProgressVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        Status = Installed ? Localizer.Instance["settings.installed"] : "";
        Changed(nameof(ActionText));
        Changed(nameof(CanAct));
        Changed(nameof(Name));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        field = value;
        Changed(name);
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Settings: interface language, look, privacy, language packs and the version of IRIS.</summary>
public partial class SettingsPage : UserControl
{
    private bool _filling;

    public SettingsPage()
    {
        InitializeComponent();
        Fill();
        App.Current.SettingsChanged += Fill;
        Version.Text = Localizer.Instance.Format("settings.version", App.Current.Version);
        NoCatalog.Visibility = App.Current.Catalog is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Fill()
    {
        _filling = true;
        var settings = App.Current.Settings;
        var languages = new[] { (Code: (string?)null, Name: Localizer.Instance["settings.languageSystem"]) }
            .Concat(Assistant.Strings.Texts.Languages.Select(code => ((string?)code, Assistant.Strings.Texts.For(code)[$"lang.{code}"])))
            .ToList();
        InterfaceLanguage.ItemsSource = languages.Select(l => l.Name).ToList();
        InterfaceLanguage.SelectedIndex = languages.FindIndex(l => l.Code == settings.InterfaceLanguage);
        ThemeChoice.SelectedIndex = (int)settings.Theme;
        SaveHistory.IsChecked = settings.SaveHistory;
        Shortcut.Text = settings.TranslateShortcut;
        Version.Text = Localizer.Instance.Format("settings.version", App.Current.Version);
        FillPacks();
        _filling = false;
    }

    private void FillPacks()
    {
        var offered = App.Current.Catalog?.Packs ?? [];
        var pairs = offered.Select(p => p.Pair).Concat(App.Current.Service.Packs.Installed()).Distinct()
            .OrderBy(p => p.ToString(), StringComparer.Ordinal);
        var rows = pairs.Select(p => new PackRow(p, offered.FirstOrDefault(o => o.Pair == p))).ToList();
        rows.ForEach(r => r.Refresh());
        Packs.ItemsSource = rows;
    }

    private void InterfaceLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || InterfaceLanguage.SelectedIndex < 0)
        {
            return;
        }

        var code = InterfaceLanguage.SelectedIndex == 0 ? null : Assistant.Strings.Texts.Languages[InterfaceLanguage.SelectedIndex - 1];
        App.Current.Update(s => s with { InterfaceLanguage = code });
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && ThemeChoice.SelectedIndex >= 0)
        {
            App.Current.Update(s => s with { Theme = (Theme)ThemeChoice.SelectedIndex });
        }
    }

    private void SaveHistoryChanged(object sender, RoutedEventArgs e) =>
        App.Current.Update(s => s with { SaveHistory = SaveHistory.IsChecked == true });

    private void DeleteAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!, Localizer.Instance["settings.deleteConfirm"], "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        App.Current.History.Clear();
        UserData.DeleteAll(IrisPaths.Home);
        App.Current.Service.ForgetUserData();
        App.Current.Update(_ => new Settings());
    }

    private async void PackAction(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PackRow row)
        {
            return;
        }

        var store = App.Current.Service.Packs;
        PackMessage.Text = "";
        if (row.Installed)
        {
            App.Current.Service.Unload(row.Pair);
            store.Remove(row.Pair);
            Changed();
            return;
        }

        if (row.Offered is not { } pack)
        {
            return;
        }

        row.Busy = true;
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                row.Percent = 100.0 * p.Done / p.Total;
                row.Status = $"{p.Done / 1e6:0} / {p.Total / 1e6:0} MB";
            });
            await store.InstallAsync(pack, App.Current.Http, App.Current.ReleaseFolder, progress);
            App.Current.Service.Unload(row.Pair);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            PackMessage.Text = Localizer.Instance.Format("common.failed", ex.Message);
        }
        finally
        {
            row.Busy = false;
            Changed();
        }
    }

    // a pack the user has as a file (e.g. copied from another computer): checked against the catalog, or by loading it
    private async void InstallFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "IRIS|*.iris" };
        if (dialog.ShowDialog(Window.GetWindow(this)!) != true)
        {
            return;
        }

        PackMessage.Text = "";
        var path = dialog.FileName;
        var name = Path.GetFileNameWithoutExtension(path);
        var found = Regex.Matches(name, "(?<![a-z])([a-z]{2})-([a-z]{2})(?![a-z])").LastOrDefault();
        if (found is null || !LanguagePair.TryParse($"{found.Groups[1].Value}-{found.Groups[2].Value}", out var pair))
        {
            PackMessage.Text = Localizer.Instance["settings.packName"];
            return;
        }

        try
        {
            var pack = App.Current.Catalog?.Find(pair) ?? await Task.Run(() =>
            {
                // not in the catalog: it must load as a translation model of the core
                _ = Translator.Load(path);
                using var stream = File.OpenRead(path);
                return new PackInfo(pair, Path.GetFileName(path), stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)), "", "file");
            });
            await using var source = File.OpenRead(path);
            await App.Current.Service.Packs.InstallAsync(pack, source);
            App.Current.Service.Unload(pair);
            Changed();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or InvalidOperationException)
        {
            PackMessage.Text = Localizer.Instance.Format("settings.badPack", pair.ToString());
        }
    }

    // packs changed: every page that lists languages refreshes
    private void Changed()
    {
        App.Current.Update(s => s);
        ((MainWindow)Window.GetWindow(this)!).ShowPacks();
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
