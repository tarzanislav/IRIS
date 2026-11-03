using System.Collections.Concurrent;
using System.Diagnostics;
using Iris.Vision.Training;

namespace Iris.Lab;

/// <summary>What a computer did in one round.</summary>
public sealed record RoundWork(int Steps, long Lines, double Loss, double Seconds, bool Stopped);

/// <summary>
/// The learning on one computer of the classroom: lines are drawn on a thread of their own (free fonts and the fonts
/// of the computer), the network learns from batches of them at the rate the hub gives, and after each step it rests
/// as long as the share of time asks (a duty of 0.5 = as long as it worked), so the computer stays cool and quiet.
/// Adam keeps its moments from round to round; new moments start with a short warm-up.
/// </summary>
public sealed class LabLearner : IDisposable
{
    private readonly OcrWeights _weights;
    private readonly AdamW _adam = new();
    private readonly LineArtist _artist;
    private readonly BlockingCollection<TrainingLine> _lines = new(boundedCapacity: 128);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _drawers;
    private readonly OcrNetwork _network;
    private readonly OcrWeights _gradients;
    private int _steps;
    private long _linesTotal;

    public LabLearner(OcrWeights weights, LineArtist artist, IReadOnlyDictionary<string, string[]> sentences, int seed, int drawingThreads = 1)
    {
        _weights = weights;
        _artist = artist;
        _network = new OcrNetwork(weights);
        _gradients = new OcrWeights(weights.Shape);
        _drawers = [.. Enumerable.Range(0, Math.Max(1, drawingThreads)).Select(worker => Task.Factory.StartNew(() =>
        {
            var texts = new LineTexts(weights.Shape, sentences, seed * 131 + worker);
            var random = new Random(seed * 7919 + worker);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (_artist.Line(texts.Next(), weights.Shape, random) is { } line)
                    {
                        _lines.Add(line, _stop.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, TaskCreationOptions.LongRunning))];
    }

    /// <summary>The weights it learns (the agent puts the hub's model into them before each round).</summary>
    public OcrWeights Weights => _weights;

    public int Threads
    {
        get => _network.Threads;
        set => _network.Threads = Math.Max(1, value);
    }

    /// <summary>The share of time it works (0.05 – 1).</summary>
    public double Duty { get; set; } = 0.5;

    public int BatchSize { get; set; } = 32;

    /// <summary>Lines learned since the learner was made (read at any time, from any thread).</summary>
    public long LinesTotal => Interlocked.Read(ref _linesTotal);

    /// <summary>
    /// Learns until the time is over, it is cancelled, or mayGoOn says no (someone works at the computer, the time of
    /// learning ends); returns what it did. The weights change in place.
    /// </summary>
    public RoundWork Learn(TimeSpan time, double rate, Func<bool> mayGoOn, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        int steps = 0;
        long lines = 0;
        double losses = 0;
        var stopped = false;
        try
        {
            while (watch.Elapsed < time && !cancel.IsCancellationRequested)
            {
                if (!mayGoOn())
                {
                    stopped = true;
                    break;
                }

                var started = watch.Elapsed;
                var batch = new LineBatch(Enumerable.Range(0, BatchSize).Select(_ => _lines.Take(cancel)));
                _gradients.Clear();
                losses += _network.Learn(batch, _gradients);
                AdamW.Clip(_gradients, 5);
                var warmUp = Math.Min(1.0, (_steps + 1) / 100.0);
                _adam.Step(_weights, _gradients, (float)(rate * warmUp));
                _steps++;
                steps++;
                lines += batch.Count;
                Interlocked.Add(ref _linesTotal, batch.Count);
                if (Duty < 1)
                {
                    var rest = (watch.Elapsed - started) * (1 / Math.Max(0.05, Duty) - 1);
                    if (rest > TimeSpan.Zero && cancel.WaitHandle.WaitOne(rest))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return new RoundWork(steps, lines, steps > 0 ? losses / steps : double.NaN, watch.Elapsed.TotalSeconds, stopped || cancel.IsCancellationRequested);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            Task.WaitAll(_drawers, TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        _network.Dispose();
        _lines.Dispose();
        _stop.Dispose();
    }
}
