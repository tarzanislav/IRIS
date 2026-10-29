using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Platform;
using Iris.Assistant;

namespace Iris.App;

/// <summary>
/// The quick translation next to the mouse: the text selected in another application, translated. Enter copies the
/// translation, Esc or a click elsewhere closes it, "Correct" opens the main window with the text.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly string? _source;
    private AppTranslation? _translation;
    private bool _closing;

    public MiniWindow(string? source)
    {
        InitializeComponent();
        _source = source;
        var settings = App.Current.Settings;
        Direction.Text = $"{settings.SourceLanguage.ToUpperInvariant()} → {settings.TargetLanguage.ToUpperInvariant()}";
        CopyButton.IsEnabled = false;
        CorrectButton.IsEnabled = source is not null;
        SourceInitialized += (_, _) => WindowsTheme.TitleBar(this, App.Current.IsDark);
    }

    /// <summary>Shows the window next to the mouse, inside the screen, and starts the translation.</summary>
    public void ShowNearCursor()
    {
        Native.GetCursorPos(out var cursor);
        Show();
        var scale = VisualTreeHelper.GetDpi(this);
        var x = cursor.X / scale.DpiScaleX + 12;
        var y = cursor.Y / scale.DpiScaleY + 16;
        Left = Math.Min(x, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 8);
        Top = Math.Min(y, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Math.Max(ActualHeight, 180) - 8);
        Activate();
        _ = TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        if (_source is null)
        {
            Translation.Text = Localizer.Instance["mini.nothing"];
            return;
        }

        var settings = App.Current.Settings;
        if (!App.Current.Service.CanTranslate(settings.SourceLanguage, settings.TargetLanguage))
        {
            Translation.Text = Localizer.Instance["translate.noPack"];
            return;
        }

        Translation.Text = Localizer.Instance["translate.translating"];
        try
        {
            var (from, to, beams) = (settings.SourceLanguage, settings.TargetLanguage, settings.Quick ? 1 : (int?)null);
            _translation = await Task.Run(() => App.Current.Service.Translate(_source, from, to, beams));
            Translation.Text = _translation.Text;
            CopyButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or FormatException or InvalidOperationException)
        {
            Translation.Text = Localizer.Instance.Format("common.failed", ex.Message);
        }
    }

    private void Copy()
    {
        if (_translation is null || _source is null)
        {
            return;
        }

        Clipboard.SetText(_translation.Text);
        var settings = App.Current.Settings;
        if (settings.SaveHistory)
        {
            App.Current.History.Add($"{settings.SourceLanguage}-{settings.TargetLanguage}", _source, _translation.Text);
        }

        Close();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Copy();
                break;
            case Key.Escape:
                Close();
                break;
        }
    }

    private void CopyClick(object sender, RoutedEventArgs e) => Copy();

    private void CorrectClick(object sender, RoutedEventArgs e)
    {
        if (_source is not null)
        {
            var text = _source;
            Close();
            App.Current.ShowMain().OpenTranslation(text);
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Lost(object? sender, EventArgs e)
    {
        if (IsLoaded && !_closing)
        {
            Close();
        }
    }
}
