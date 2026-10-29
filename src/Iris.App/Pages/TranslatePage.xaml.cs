using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Assistant;
using Iris.Assistant.Packs;
using Iris.Translation;
using Microsoft.Win32;

namespace Iris.App.Pages;

/// <summary>
/// Translation of a typed or pasted text: the translation appears by itself a moment after typing stops. Names from
/// the glossary and sentences from the memory are marked; a click on a sentence opens its correction.
/// </summary>
public partial class TranslatePage : UserControl
{
    private readonly DispatcherTimer _pause = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _running;
    private AppTranslation? _last;
    private string _lastSource = "";
    private int _selected = -1;
    private bool _filling;

    public TranslatePage()
    {
        InitializeComponent();
        _pause.Tick += (_, _) =>
        {
            _pause.Stop();
            _ = TranslateAsync();
        };
        FillPickers();
        App.Current.SettingsChanged += () =>
        {
            FillPickers();
            ShowCounts();
        };
        ShowCounts();
        ShowStatus(Localizer.Instance["translate.empty"]);
    }

    private static App Application => App.Current;

    private string From => Languages.CodeOf(SourceLanguage);

    private string To => Languages.CodeOf(TargetLanguage);

    /// <summary>Shows a text and translates it at once (from the quick window).</summary>
    public void Load(string text)
    {
        Source.Text = text;
        _pause.Stop();
        _ = TranslateAsync();
    }

    private void FillPickers()
    {
        _filling = true;
        Languages.Fill(SourceLanguage, Application.Settings.SourceLanguage);
        Languages.Fill(TargetLanguage, Application.Settings.TargetLanguage);
        Mode.SelectedIndex = Application.Settings.Quick ? 1 : 0;
        SourceCaption.Text = ((LanguageItem)SourceLanguage.SelectedItem).Name.ToUpper(CultureInfo.CurrentCulture);
        TargetCaption.Text = ((LanguageItem)TargetLanguage.SelectedItem).Name.ToUpper(CultureInfo.CurrentCulture);
        _filling = false;
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || SourceLanguage.SelectedItem is null || TargetLanguage.SelectedItem is null)
        {
            return;
        }

        var (from, to) = (From, To);
        if (from == to)
        {
            // the same language on both sides: the other side takes the language this one had
            (from, to) = sender == SourceLanguage ? (from, Application.Settings.SourceLanguage) : (Application.Settings.TargetLanguage, to);
        }

        Application.Update(s => s with { SourceLanguage = from, TargetLanguage = to });
        _ = TranslateAsync();
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && Mode.SelectedIndex >= 0)
        {
            Application.Update(s => s with { Quick = Mode.SelectedIndex == 1 });
            _ = TranslateAsync();
        }
    }

    private void SwapLanguages(object sender, RoutedEventArgs e)
    {
        var translation = _last?.Text;
        Application.Update(s => s with { SourceLanguage = s.TargetLanguage, TargetLanguage = s.SourceLanguage });
        if (translation is not null)
        {
            Source.Text = translation;
        }

        _ = TranslateAsync();
    }

    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        ShowCounts();
        _pause.Stop();
        _pause.Start();
    }

    private void ShowCounts()
    {
        var text = Source.Text;
        Counts.Text = Localizer.Instance.Format("translate.counts", Segmenter.Sentences(text).Count(), text.Length);
    }

    private async Task TranslateAsync()
    {
        _running?.Cancel();
        var text = Source.Text;
        CloseCorrection(this, new RoutedEventArgs());
        if (text.Trim().Length == 0)
        {
            _last = null;
            Target.Document = new FlowDocument();
            ShowStatus(Localizer.Instance["translate.empty"]);
            return;
        }

        var (from, to, beams) = (From, To, Application.Settings.Quick ? 1 : (int?)null);
        if (!Application.Service.CanTranslate(from, to))
        {
            _last = null;
            Target.Document = new FlowDocument();
            ShowStatus(Localizer.Instance["translate.noPack"], install: true);
            return;
        }

        var running = new CancellationTokenSource();
        _running = running;
        ShowStatus(Localizer.Instance["translate.translating"]);
        var watch = Stopwatch.StartNew();
        try
        {
            var translation = await Task.Run(() => Application.Service.Translate(text, from, to, beams, running.Token), running.Token);
            if (running.IsCancellationRequested)
            {
                return;
            }

            _last = translation;
            _lastSource = text;
            Render();
            ShowStatus(null);
            var mode = Localizer.Instance[beams == 1 ? "translate.quick" : "translate.best"].ToLower(CultureInfo.CurrentCulture);
            var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
            Timing.Text = Localizer.Instance.Format("translate.time", mode, seconds) +
                          (translation.ThroughPivot ? " · " + Localizer.Instance["translate.pivot"] : "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or InvalidOperationException)
        {
            _last = null;
            Target.Document = new FlowDocument();
            ShowStatus(Localizer.Instance.Format("common.failed", ex.Message));
        }
    }

    private void ShowStatus(string? text, bool install = false)
    {
        Status.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = text ?? "";
        InstallPack.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = SaveButton.IsEnabled = text is null && _last is not null;
        if (text is not null)
        {
            Timing.Text = "";
        }
    }

    // the translation with the layout of the source: sentences as clickable spans, the space between them as written
    private void Render()
    {
        if (_last is null)
        {
            return;
        }

        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 26 };
        var index = 0;
        foreach (var segment in Segmenter.Split(_lastSource))
        {
            if (!segment.IsSentence)
            {
                AddText(paragraph.Inlines, segment.Text);
                continue;
            }

            if (index < _last.Sentences.Count)
            {
                paragraph.Inlines.Add(SentenceSpan(index, _last.Sentences[index]));
            }

            index++;
        }

        Target.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0), FontFamily = Target.FontFamily, FontSize = Target.FontSize };
    }

    private Span SentenceSpan(int index, SentenceTranslation sentence)
    {
        var span = new Span { Tag = index, Cursor = Cursors.Hand };
        if (sentence.Origin == TranslationOrigin.Memory)
        {
            span.TextDecorations = new TextDecorationCollection
            {
                new TextDecoration(TextDecorationLocation.Underline, new Pen((Brush)FindResource("Accent"), 1.5) { DashStyle = DashStyles.Dot }, 3,
                    TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel),
            };
        }

        if (index == _selected)
        {
            span.SetResourceReference(TextElement.BackgroundProperty, "WarnSoft");
        }

        foreach (var (text, term) in Pieces(sentence))
        {
            var run = new Run(text);
            if (term)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, "GoodSoft");
                run.SetResourceReference(TextElement.ForegroundProperty, "Good");
            }

            span.Inlines.Add(run);
        }

        span.MouseLeftButtonDown += (_, e) =>
        {
            SelectSentence(index);
            e.Handled = true;
        };
        return span;
    }

    // the target cut into plain pieces and glossary terms (the wanted forms of the terms found in the source)
    private IEnumerable<(string Text, bool Term)> Pieces(SentenceTranslation sentence)
    {
        var target = sentence.Target;
        if (sentence.Origin != TranslationOrigin.Glossary || _last is null)
        {
            yield return (target, false);
            yield break;
        }

        var terms = Application.Service.GlossaryFor(_last.Route[0]).Find(sentence.Source)
            .Select(m => m.Entry.Output).Distinct(StringComparer.Ordinal).OrderByDescending(t => t.Length).ToList();
        var i = 0;
        while (i < target.Length)
        {
            var (at, term) = terms.Select(t => (At: target.IndexOf(t, i, StringComparison.Ordinal), Term: t))
                .Where(x => x.At >= 0).OrderBy(x => x.At).FirstOrDefault(x => true, (-1, ""));
            if (at < 0)
            {
                yield return (target[i..], false);
                yield break;
            }

            if (at > i)
            {
                yield return (target[i..at], false);
            }

            yield return (term, true);
            i = at + term.Length;
        }
    }

    private static void AddText(InlineCollection inlines, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (lines[i].Length > 0)
            {
                inlines.Add(new Run(lines[i]));
            }
        }
    }

    private void SelectSentence(int index)
    {
        if (_last is null || index >= _last.Sentences.Count)
        {
            return;
        }

        _selected = index;
        var sentence = _last.Sentences[index];
        CorrectionTitle.Text = Localizer.Instance.Format("correct.title", index + 1, _last.Sentences.Count);
        CorrectionSource.Text = sentence.Source;
        CorrectionText.Text = sentence.Target;
        ForgetButton.Visibility = sentence.Origin == TranslationOrigin.Memory ? Visibility.Visible : Visibility.Collapsed;
        Correction.Visibility = Visibility.Visible;
        Render();
        CorrectionText.Focus();
        CorrectionText.SelectAll();
    }

    private void Remember(object sender, RoutedEventArgs e)
    {
        if (_last is null || _selected < 0 || CorrectionText.Text.Trim().Length == 0)
        {
            return;
        }

        Application.Service.MemoryFor(new LanguagePair(From, To)).Remember(_last.Sentences[_selected].Source, CorrectionText.Text);
        _ = TranslateAsync();
    }

    private void Forget(object sender, RoutedEventArgs e)
    {
        if (_last is not null && _selected >= 0)
        {
            Application.Service.MemoryFor(new LanguagePair(From, To)).Forget(_last.Sentences[_selected].Source);
            _ = TranslateAsync();
        }
    }

    private void CorrectionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Remember(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            CloseCorrection(sender, e);
        }
    }

    private void CloseCorrection(object sender, RoutedEventArgs e)
    {
        Correction.Visibility = Visibility.Collapsed;
        if (_selected >= 0)
        {
            _selected = -1;
            Render();
        }
    }

    private void Paste(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            Source.Text = Clipboard.GetText();
        }
    }

    private void Copy(object sender, RoutedEventArgs e)
    {
        if (_last is null)
        {
            return;
        }

        Clipboard.SetText(_last.Text);
        AddToHistory(_last);
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (_last is null)
        {
            return;
        }

        var dialog = new SaveFileDialog { Filter = "TXT|*.txt", FileName = $"{Localizer.Instance["translate.target"]}.txt", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)!) == true)
        {
            File.WriteAllText(dialog.FileName, _last.Text, new UTF8Encoding(false));
            AddToHistory(_last);
        }
    }

    private void AddToHistory(AppTranslation translation)
    {
        if (Application.Settings.SaveHistory)
        {
            Application.History.Add($"{From}-{To}", _lastSource, translation.Text);
        }
    }

    private void OpenPacks(object sender, RoutedEventArgs e) => ((MainWindow)Window.GetWindow(this)!).OpenPacks();
}
