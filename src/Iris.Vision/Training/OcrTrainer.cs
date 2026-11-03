using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Iris.Vision.Training;

/// <summary>How a learning run goes.</summary>
public sealed record OcrTrainingOptions
{
    /// <summary>Minutes of learning in this run.</summary>
    public double Minutes { get; init; } = 60;

    /// <summary>Minutes of the whole plan from now (several runs; the rate falls along all of them); null = this run, or the plan of the file.</summary>
    public double? Plan { get; init; }

    /// <summary>The highest rate of a new plan.</summary>
    public double Rate { get; init; } = 1e-3;

    public int BatchSize { get; init; } = 32;

    /// <summary>Threads of the network.</summary>
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Threads that draw lines while the network learns.</summary>
    public int DrawingThreads { get; init; } = 1;

    /// <summary>
    /// The share of time the learning works (1 = all the time; 0.5 = after each step a pause as long as the step), so
    /// a computer in a classroom stays cool and quiet.
    /// </summary>
    public double Duty { get; init; } = 1;

    public float ClipNorm { get; init; } = 5;

    /// <summary>Lines of the fixed test set in fonts it learns from (half as many in the unseen font).</summary>
    public int TestLines { get; init; } = 600;

    public double TestEveryMinutes { get; init; } = 20;

    public int Seed { get; init; } = 1;

    /// <summary>The longest line picture (wider ones are drawn again).</summary>
    public int MaxWidth { get; init; } = 1600;
}

/// <summary>One report of the learning: where it is and how well it reads.</summary>
public sealed record OcrProgress(int Step, double Minutes, double Loss, double Rate, double LinesPerSecond, double? CerKnown = null, double? CerUnseen = null);

/// <summary>
/// The learning of the line reader in C#: lines are drawn on their own threads (own fonts, own spoiling) while the
/// network learns from the previous batch; each step is forward, CTC, backward, clipping and AdamW at the rate of
/// the plan. Every so often it reads the fixed test sets (fonts it learns from and a font it never sees) and keeps
/// a checkpoint, so a run can stop at any moment and go on later – on this computer or another.
/// </summary>
public sealed class OcrTrainer
{
    private readonly OcrWeights _weights;
    private readonly AdamW _adam;
    private readonly LineArtist _learn;
    private readonly LineArtist? _unseen;
    private readonly IReadOnlyDictionary<string, string[]> _sentences;
    private readonly OcrTrainingOptions _options;
    private OcrLearningState _state;

    public OcrTrainer(OcrWeights weights, AdamW adam, OcrLearningState state, LineArtist learn, LineArtist? unseen,
        IReadOnlyDictionary<string, string[]> sentences, OcrTrainingOptions options)
    {
        (_weights, _adam, _state, _learn, _unseen, _sentences, _options) = (weights, adam, state, learn, unseen, sentences, options);
    }

    public OcrLearningState State => _state;

    /// <summary>The reports of the test sets so far.</summary>
    public List<OcrProgress> History { get; } = [];

    /// <summary>Learns for the given minutes (or until cancelled), keeping checkpoints in the folder; returns the last report.</summary>
    public OcrProgress Run(string folder, IProgress<OcrProgress>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var shape = _weights.Shape;
        var spent = _state.Minutes;
        var planEnd = _options.Plan is { } plan ? spent + plan : _state.PlanEnd > spent + 1 ? _state.PlanEnd : spent + _options.Minutes;
        if (_options.Plan is not null || _state.PlanEnd <= spent + 1)
        {
            _state = _state with { PlanStart = spent, PlanEnd = planEnd, PlanRate = _options.Rate };
        }

        var until = Math.Min(spent + _options.Minutes, _state.PlanEnd);
        var known = TestSet(_learn, 7777, _options.TestLines);
        var unseen = _unseen is null ? [] : TestSet(_unseen, 8888, _options.TestLines / 2);

        using var network = new OcrNetwork(_weights) { Threads = _options.Threads };
        var gradients = new OcrWeights(shape);
        using var lines = new BlockingCollection<TrainingLine>(boundedCapacity: Math.Max(64, 4 * _options.BatchSize));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var drawers = Enumerable.Range(0, Math.Max(1, _options.DrawingThreads)).Select(worker => Task.Factory.StartNew(() =>
        {
            var seed = _options.Seed * 100_003 + _state.Step * 31 + worker;
            var texts = new LineTexts(shape, _sentences, seed);
            var random = new Random(seed ^ 0x5bd1e995);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (_learn.Line(texts.Next(), shape, random, _options.MaxWidth) is { } line)
                    {
                        lines.Add(line, stop.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        // how well it reads at the start (on these lines, drawn by this code)
        progress?.Report(Test(network, known, unseen, double.NaN, 0, 0));

        var watch = Stopwatch.StartNew();
        var lastTest = watch.Elapsed.TotalMinutes;
        var stepsOfRun = 0;
        double loss = 0, rate = 0, losses = 0;
        var linesDone = 0;
        var report = new OcrProgress(_state.Step, spent, double.NaN, 0, 0);
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var minutes = spent + watch.Elapsed.TotalMinutes;
                if (minutes >= until)
                {
                    break;
                }

                var batch = new LineBatch(Enumerable.Range(0, _options.BatchSize).Select(_ => lines.Take(cancel)));
                var started = watch.Elapsed;
                gradients.Clear();
                loss = network.Learn(batch, gradients);
                AdamW.Clip(gradients, _options.ClipNorm);
                rate = _state.Rate(minutes, stepsOfRun);
                _adam.Step(_weights, gradients, (float)rate);
                stepsOfRun++;
                linesDone += batch.Count;
                losses += loss;
                _state = _state with { Step = _state.Step + 1, Minutes = spent + watch.Elapsed.TotalMinutes };
                if (_state.Step % 50 == 0)
                {
                    report = new OcrProgress(_state.Step, _state.Minutes, losses / 50, rate, linesDone / watch.Elapsed.TotalSeconds);
                    progress?.Report(report);
                    losses = 0;
                }

                if (watch.Elapsed.TotalMinutes - lastTest >= _options.TestEveryMinutes)
                {
                    lastTest = watch.Elapsed.TotalMinutes;
                    report = Test(network, known, unseen, loss, rate, linesDone / watch.Elapsed.TotalSeconds);
                    progress?.Report(report);
                    Save(folder);
                }

                if (_options.Duty < 1)
                {
                    // rest as long as the share of time asks for (a quiet, cool computer)
                    var work = watch.Elapsed - started;
                    Thread.Sleep(TimeSpan.FromTicks((long)(work.Ticks * (1 / Math.Max(0.05, _options.Duty) - 1))));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(drawers);
        }

        _state = _state with { Minutes = spent + watch.Elapsed.TotalMinutes };
        report = Test(network, known, unseen, loss, rate, linesDone / Math.Max(1e-9, watch.Elapsed.TotalSeconds));
        progress?.Report(report);
        Save(folder);
        return report;
    }

    /// <summary>The character error rate (%) of the network on lines: edit distance of what it reads to the text, per character.</summary>
    public static double Cer(OcrNetwork network, IReadOnlyList<TrainingLine> lines, int batchSize = 32)
    {
        long errors = 0, characters = 0;
        for (var start = 0; start < lines.Count; start += batchSize)
        {
            var batch = new LineBatch(lines.Skip(start).Take(batchSize));
            var logits = network.Read(batch);
            for (var i = 0; i < batch.Count; i++)
            {
                errors += Distance(network.Decode(logits[i]), batch.Lines[i].Text);
                characters += batch.Lines[i].Text.Length;
            }
        }

        return 100.0 * errors / Math.Max(1, characters);
    }

    /// <summary>Levenshtein distance (insertions, deletions, substitutions).</summary>
    public static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private List<TrainingLine> TestSet(LineArtist artist, int seed, int count)
    {
        var texts = new LineTexts(_weights.Shape, _sentences, seed);
        var random = new Random(seed);
        var result = new List<TrainingLine>(count);
        while (result.Count < count)
        {
            if (artist.Line(texts.Next(), _weights.Shape, random, _options.MaxWidth) is { } line)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private OcrProgress Test(OcrNetwork network, List<TrainingLine> known, List<TrainingLine> unseen, double loss, double rate, double speed)
    {
        var cerKnown = Math.Round(Cer(network, known), 2);
        double? cerUnseen = unseen.Count > 0 ? Math.Round(Cer(network, unseen), 2) : null;
        var report = new OcrProgress(_state.Step, _state.Minutes, loss, rate, speed, cerKnown, cerUnseen);
        History.Add(report);
        return report;
    }

    private void Save(string folder)
    {
        var provenance = new JsonObject
        {
            ["source"] = "IRIS OCR, learned in C# on lines drawn with own font code",
            ["step"] = _state.Step,
            ["minutes"] = Math.Round(_state.Minutes),
        };
        OcrFiles.SaveTraining(Path.Combine(folder, "iris-ocr-train.iris"), _weights, _adam, _state, provenance);
        OcrFiles.SaveReader(Path.Combine(folder, "iris-ocr.iris"), _weights, provenance);
    }
}
