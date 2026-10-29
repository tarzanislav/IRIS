using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Iris.Assistant.Documents;
using Iris.Translation;
using Microsoft.Win32;

namespace Iris.App.Pages;

/// <summary>One file to translate: waiting, running with its progress, done with its new file, or failed.</summary>
public sealed class FileJob(string source, string from, string to) : INotifyPropertyChanged
{
    private double _percent;
    private string _state = Localizer.Instance["common.waiting"];
    private string? _result;
    private bool _running;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Source { get; } = source;

    public string From { get; } = from;

    public string To { get; } = to;

    public string Name => Path.GetFileName(Source);

    public string Direction => $"{From.ToUpperInvariant()} → {To.ToUpperInvariant()}";

    public CancellationTokenSource Cancellation { get; } = new();

    public bool Finished { get; private set; }

    public double Percent { get => _percent; set => Set(ref _percent, value); }

    public string State { get => _state; set => Set(ref _state, value); }

    public string? Result
    {
        get => _result;
        set
        {
            Set(ref _result, value);
            Changed(nameof(OpenVisibility));
        }
    }

    public bool Running
    {
        get => _running;
        set
        {
            Set(ref _running, value);
            Changed(nameof(StopVisibility));
            Changed(nameof(ProgressVisibility));
        }
    }

    public Visibility OpenVisibility => Result is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility StopVisibility => Running || !Finished ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProgressVisibility => Running ? Visibility.Visible : Visibility.Collapsed;

    public void Finish(string state, string? result = null)
    {
        Finished = true;
        Running = false;
        State = state;
        Result = result;
        Changed(nameof(StopVisibility));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        field = value;
        Changed(name);
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Translation of files: dropped or chosen files wait in a queue and are translated one after another into new
/// files next to the originals.
/// </summary>
public partial class FilesPage : UserControl
{
    private readonly ObservableCollection<FileJob> _jobs = [];
    private bool _working;
    private bool _filling;

    public FilesPage()
    {
        InitializeComponent();
        Jobs.ItemsSource = _jobs;
        FillPickers();
        App.Current.SettingsChanged += FillPickers;
    }

    private void FillPickers()
    {
        _filling = true;
        Languages.Fill(SourceLanguage, App.Current.Settings.SourceLanguage);
        Languages.Fill(TargetLanguage, App.Current.Settings.TargetLanguage);
        _filling = false;
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || SourceLanguage.SelectedItem is null || TargetLanguage.SelectedItem is null)
        {
            return;
        }

        var (from, to) = (Languages.CodeOf(SourceLanguage), Languages.CodeOf(TargetLanguage));
        if (from != to)
        {
            App.Current.Update(s => s with { SourceLanguage = from, TargetLanguage = to });
        }
        else
        {
            FillPickers();
        }
    }

    private void DragFiles(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            Add(paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p) : new[] { p }));
        }
    }

    private void ChooseFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "TXT, SRT, DOCX|*.txt;*.srt;*.docx;*.md" };
        if (dialog.ShowDialog(Window.GetWindow(this)!) == true)
        {
            Add(dialog.FileNames);
        }
    }

    private void Add(IEnumerable<string> paths)
    {
        var (from, to) = (App.Current.Settings.SourceLanguage, App.Current.Settings.TargetLanguage);
        var skipped = 0;
        foreach (var path in paths)
        {
            if (DocumentTranslator.KindOf(path) is null)
            {
                skipped++;
                continue;
            }

            _jobs.Add(new FileJob(path, from, to));
        }

        Message.Text = skipped > 0 ? Localizer.Instance["files.unsupported"] : "";
        if (!App.Current.Service.CanTranslate(from, to))
        {
            Message.Text = Localizer.Instance["translate.noPack"];
        }

        _ = WorkAsync();
    }

    // one file after another, so the processor serves one translation at full speed
    private async Task WorkAsync()
    {
        if (_working)
        {
            return;
        }

        _working = true;
        try
        {
            while (_jobs.FirstOrDefault(j => !j.Finished && !j.Running) is { } job)
            {
                if (job.Cancellation.IsCancellationRequested)
                {
                    job.Finish(Localizer.Instance["common.cancel"]);
                    continue;
                }

                job.Running = true;
                job.State = Localizer.Instance["translate.translating"];
                var progress = new Progress<TranslationProgress>(p =>
                {
                    job.Percent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                    job.State = $"{p.Done} / {p.Total}";
                });
                var beams = App.Current.Settings.Quick ? 1 : (int?)null;
                var watch = Stopwatch.StartNew();
                try
                {
                    var result = await Task.Run(() => DocumentTranslator.Translate(App.Current.Service, job.Source, job.From, job.To, beams,
                        job.Cancellation.Token, progress));
                    job.Finish($"{Localizer.Instance["common.done"]} · {watch.Elapsed:m\\:ss}", result);
                }
                catch (OperationCanceledException)
                {
                    job.Finish(Localizer.Instance["common.cancel"]);
                }
                catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or InvalidOperationException
                                               or NotSupportedException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    job.Finish(Localizer.Instance.Format("common.failed", ex.Message));
                }
            }
        }
        finally
        {
            _working = false;
        }
    }

    private void StopJob(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileJob job)
        {
            job.Cancellation.Cancel();
            if (!job.Running)
            {
                job.Finish(Localizer.Instance["common.cancel"]);
            }
        }
    }

    private void OpenResult(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileJob { Result: { } result } && File.Exists(result))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result}\"") { UseShellExecute = false });
        }
    }
}
