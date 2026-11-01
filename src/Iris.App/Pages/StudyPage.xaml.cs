using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Iris.Assistant.Packs;
using Iris.Assistant.Study;
using Iris.Learning;

namespace Iris.App.Pages;

/// <summary>A pair of sentences found on the study page: taken in or not (the owner decides), and how sure the pairing is.</summary>
public sealed class FoundPair(AlignedPair pair) : INotifyPropertyChanged
{
    private bool _take = pair.IsSure;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Source => pair.Source;

    public string Target => pair.Target;

    public bool Take
    {
        get => _take;
        set
        {
            _take = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Take)));
        }
    }

    public string Sureness => $"{Math.Max(0, Math.Round(pair.Confidence)):0}";

    public Brush SurenessBrush => (Brush)Application.Current.FindResource(pair.IsSure ? "Good" : "Warn");
}

/// <summary>
/// The study room: an original, another translation only to compare with (IRIS never learns from it), and the
/// verified translation from a book. IRIS pairs their sentences, the owner checks them, and IRIS learns from the
/// verified pairs and the corrections in its memory – with a check before what it learned is used.
/// </summary>
public partial class StudyPage : UserControl
{
    private CancellationTokenSource? _learning;
    private bool _filling;

    public StudyPage()
    {
        InitializeComponent();
        Fill();
        App.Current.SettingsChanged += Fill;
    }

    private LanguagePair Pair => Direction.SelectedItem is DirectionChoice choice ? choice.Pair : new LanguagePair("en", "sk");

    private StudyRoom Room => StudyRoom.Open(IrisPaths.Study(Pair.ToString()));

    private void Fill()
    {
        _filling = true;
        var settings = App.Current.Settings;
        Off.Visibility = settings.StudyRoom ? Visibility.Collapsed : Visibility.Visible;
        On.Visibility = settings.StudyRoom ? Visibility.Visible : Visibility.Collapsed;
        UseLearned.IsChecked = settings.UseLearned;
        var current = new LanguagePair(settings.SourceLanguage, settings.TargetLanguage);
        var installed = App.Current.Service.Packs.Installed().ToList();
        List<LanguagePair> pairs = installed.Count == 0 ? [current] : installed;
        var selected = Direction.SelectedItem is DirectionChoice old ? old.Pair : current;
        var items = pairs.OrderBy(p => p.ToString(), StringComparer.Ordinal).Select(p => new DirectionChoice(p)).ToList();
        Direction.ItemsSource = items;
        Direction.SelectedItem = items.FirstOrDefault(i => i.Pair == selected) ?? items[0];
        _filling = false;
        ShowCounts();
    }

    private void DirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            Pairs.ItemsSource = null;
            PairsCard.Visibility = Visibility.Collapsed;
            ShowCounts();
        }
    }

    private void TurnOn(object sender, RoutedEventArgs e) => App.Current.Update(s => s with { StudyRoom = true });

    private void UseLearnedChanged(object sender, RoutedEventArgs e)
    {
        var use = UseLearned.IsChecked == true;
        App.Current.Service.UseLearned = use;
        App.Current.Update(s => s with { UseLearned = use });
    }

    // the original translated by IRIS into the middle box (to compare; IRIS does not learn from it)
    private async void FillIris(object sender, RoutedEventArgs e)
    {
        var (text, pair) = (Original.Text, Pair);
        if (text.Trim().Length == 0 || !App.Current.Service.CanTranslate(pair.Source, pair.Target))
        {
            return;
        }

        Message.Text = Localizer.Instance["translate.translating"];
        try
        {
            Other.Text = (await Task.Run(() => App.Current.Service.Translate(text, pair.Source, pair.Target, beams: 1))).Text;
            Message.Text = "";
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            Message.Text = Localizer.Instance.Format("common.failed", ex.Message);
        }
    }

    // the text of photos or scans of pages (several at once, in the order chosen), read by the OCR of IRIS
    private async void FromPicture(object sender, RoutedEventArgs e)
    {
        var box = (string)((FrameworkElement)sender).Tag == "Original" ? Original : Verified;
        if (!File.Exists(IrisPaths.Ocr))
        {
            Message.Text = Localizer.Instance["study.noOcr"];
            var model = new Microsoft.Win32.OpenFileDialog { Filter = "IRIS OCR|*.iris" };
            if (model.ShowDialog(Window.GetWindow(this)!) != true)
            {
                return;
            }

            try
            {
                Iris.Vision.Ocr.LineReader.Load(model.FileName);
                Directory.CreateDirectory(IrisPaths.Models);
                File.Copy(model.FileName, IrisPaths.Ocr, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
            {
                Message.Text = Localizer.Instance.Format("common.failed", ex.Message);
                return;
            }
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Localizer.Instance["study.pictures"] + "|*.jpg;*.jpeg;*.png", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)!) != true)
        {
            return;
        }

        var files = dialog.FileNames;
        Message.Text = Localizer.Instance["study.reading"];
        try
        {
            var pages = await Task.Run(() =>
            {
                var reader = Iris.Vision.Ocr.LineReader.Load(IrisPaths.Ocr);
                return files.Select(f => Iris.Vision.Ocr.PageOcr.Read(Iris.Vision.Imaging.ImageFile.Read(f), reader)).ToList();
            });
            var text = string.Join("\n\n", pages.Select(p => p.Text));
            box.Text = box.Text.Trim().Length == 0 ? text : box.Text.TrimEnd() + "\n\n" + text;
            Message.Text = Localizer.Instance.Format("study.readPages", pages.Count, pages.Sum(p => p.Lines.Count));
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or NotSupportedException)
        {
            Message.Text = Localizer.Instance.Format("common.failed", ex.Message);
        }
    }

    private async void PairSentences(object sender, RoutedEventArgs e)
    {
        var (original, verified, pair) = (Original.Text, Verified.Text, Pair);
        if (original.Trim().Length == 0 || verified.Trim().Length == 0)
        {
            Message.Text = Localizer.Instance["study.needBoth"];
            return;
        }

        if (!App.Current.Service.Packs.IsInstalled(pair))
        {
            Message.Text = Localizer.Instance["study.needPack"];
            return;
        }

        PairButton.IsEnabled = false;
        Message.Text = Localizer.Instance["study.pairing"];
        try
        {
            var found = await Task.Run(() => SentenceAligner.Align(original, verified,
                s => App.Current.Service.Translate(s, pair.Source, pair.Target, beams: 1).Text));
            Pairs.ItemsSource = found.Select(p => new FoundPair(p)).ToList();
            PairsCard.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Message.Text = found.Count == 0 ? Localizer.Instance["study.nothingFound"] : Localizer.Instance.Format("study.found", found.Count);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            Message.Text = Localizer.Instance.Format("common.failed", ex.Message);
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private void SavePairs(object sender, RoutedEventArgs e)
    {
        if (Pairs.ItemsSource is not IEnumerable<FoundPair> found)
        {
            return;
        }

        var added = Room.Add(found.Where(p => p.Take).Select(p => (p.Source, p.Target)), BookLabel.Text);
        Message.Text = Localizer.Instance.Format("study.saved", added);
        Pairs.ItemsSource = null;
        PairsCard.Visibility = Visibility.Collapsed;
        Original.Clear();
        Other.Clear();
        Verified.Clear();
        ShowCounts();
    }

    private async void Learn(object sender, RoutedEventArgs e)
    {
        var pair = Pair;
        var service = App.Current.Service;
        if (!service.Packs.IsInstalled(pair) || service.LearnedPath(pair) is not { } learned)
        {
            Message.Text = Localizer.Instance["study.needPack"];
            return;
        }

        var pairs = TrainingPairs(pair);
        if (pairs.Count < Learner.MinimumPairs)
        {
            Message.Text = Localizer.Instance.Format("study.tooFew", Learner.MinimumPairs);
            return;
        }

        _learning = new CancellationTokenSource();
        var cancellation = _learning.Token;
        LearnButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        LearnProgress.Visibility = Visibility.Visible;
        LearnProgress.Value = 0;
        Last.Text = Localizer.Instance["study.learning"];
        var progress = new Progress<LearningProgress>(p =>
        {
            LearnProgress.Value = 100.0 * p.Step / p.Steps;
            Last.Text = Localizer.Instance.Format("study.step", p.Step, p.Steps);
        });
        try
        {
            var path = service.Packs.PathOf(pair);
            var report = await Task.Run(() => Learner.Learn(pair, path, pairs, learned, null, progress, cancellation), cancellation);
            if (report.Accepted)
            {
                service.Unload(pair);
            }
        }
        catch (OperationCanceledException)
        {
            Message.Text = Localizer.Instance["study.stopped"];
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            Message.Text = Localizer.Instance.Format("common.failed", ex.Message);
        }
        finally
        {
            _learning.Dispose();
            _learning = null;
            LearnButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Collapsed;
            LearnProgress.Visibility = Visibility.Collapsed;
            ShowCounts();
        }
    }

    private void Stop(object sender, RoutedEventArgs e) => _learning?.Cancel();

    // the verified pairs of the study room and the corrections of the memory – never another program's translations
    private List<(string Source, string Target)> TrainingPairs(LanguagePair pair) =>
    [
        .. StudyRoom.Open(IrisPaths.Study(pair.ToString())).Pairs.Select(p => (p.Source, p.Target)),
        .. App.Current.Service.MemoryFor(pair).Entries.Select(e => (e.Source, e.Target)),
    ];

    private void ShowCounts()
    {
        if (Counts is null)
        {
            return;
        }

        var pair = Pair;
        var studied = StudyRoom.Open(IrisPaths.Study(pair.ToString())).Count;
        var corrections = App.Current.Service.MemoryFor(pair).Count;
        Counts.Text = Localizer.Instance.Format("study.counts", studied, corrections);
        LearnButton.IsEnabled = _learning is null && studied + corrections >= Learner.MinimumPairs;
        if (_learning is not null)
        {
            return;
        }

        var report = App.Current.Service.LearnedPath(pair) is { } learned ? Learner.LastReport(learned) : null;
        Last.Text = report is null
            ? Localizer.Instance.Format("study.never", Learner.MinimumPairs)
            : Localizer.Instance.Format(report.Accepted ? "study.lastUsed" : "study.lastRejected",
                report.Learned.LocalDateTime.ToString("d", CultureInfo.CurrentCulture), report.Pairs, report.Checked,
                report.ChrfBefore.ToString("0.0", CultureInfo.CurrentCulture), report.ChrfAfter.ToString("0.0", CultureInfo.CurrentCulture),
                Math.Max(1, Math.Round(report.Seconds / 60)));
    }

    private sealed record DirectionChoice(LanguagePair Pair)
    {
        public override string ToString() => $"{new LanguageItem(Pair.Source).Name} → {new LanguageItem(Pair.Target).Name}";
    }
}
