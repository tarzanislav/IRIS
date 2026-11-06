using Iris.Numerics;

namespace Iris.Vision.Training;

/// <summary>A line to learn from: ink values (1 = ink, 0 = paper) of the model's height and some width, and its text.</summary>
public sealed record TrainingLine(float[] Ink, int Width, string Text, int[] Labels);

/// <summary>
/// A batch of lines, from the widest to the narrowest (the LSTM runs the lines still going at each step as the first
/// rows). Every line keeps its own width – nothing is padded, the reader sees each line as it will see it in use.
/// </summary>
public sealed class LineBatch
{
    public LineBatch(IEnumerable<TrainingLine> lines) => Lines = [.. lines.OrderByDescending(l => l.Width)];

    public TrainingLine[] Lines { get; }

    public int Count => Lines.Length;
}

/// <summary>
/// The line reader for learning: forward through convolutions, LSTM and output layer to CTC, and the whole way back
/// to the gradient of every weight. One network object is reused for every batch (its buffers stay allocated).
/// </summary>
public sealed unsafe class OcrNetwork : IDisposable
{
    private readonly OcrWeights _w;
    private readonly ConvLayer[] _convs;
    private readonly LstmLayer[] _lstm;
    private readonly Level _input = new();
    private readonly Sequences _sequences = new();
    private readonly Buffer<float> _pictures = new();
    private readonly Buffer<float> _logits = new();
    private readonly Buffer<float> _logitGradient = new();
    private readonly Buffer<float> _hiddenGradient = new();
    private readonly Buffer<float> _shared = new();           // the transformed tiles of the wide layers' weight gradients

    public OcrNetwork(OcrWeights weights)
    {
        _w = weights;
        var shape = weights.Shape;
        _convs = new ConvLayer[shape.Blocks.Length + 1];
        for (var i = 0; i < shape.Blocks.Length; i++)
        {
            _convs[i] = new ConvLayer(weights.Convs[i], pad: 1, shape.Blocks[i].PoolHeight, shape.Blocks[i].PoolWidth);
        }

        _convs[^1] = new ConvLayer(weights.Convs[^1], pad: 0, 1, 1);
        foreach (var conv in _convs)
        {
            conv.Shared = _shared;
        }

        _lstm = [.. weights.Lstm.Select(l => new LstmLayer(l[0], l[1]))];
    }

    public OcrWeights Weights => _w;

    /// <summary>Seconds spent in each part of the network since it was made (for measuring where the time goes).</summary>
    public Dictionary<string, double> Timings { get; } = new(StringComparer.Ordinal);

    /// <summary>Times also the parts inside the layers (products, normalization, pooling, recurrence).</summary>
    public bool Detailed
    {
        get => _convs[0].Timings is not null;
        set
        {
            for (var i = 0; i < _convs.Length; i++)
            {
                _convs[i].Timings = value ? Timings : null;
                _convs[i].Name = $"conv {i}";
            }

            for (var i = 0; i < _lstm.Length; i++)
            {
                _lstm[i].Timings = value ? Timings : null;
                _lstm[i].Name = $"lstm {i}";
            }
        }
    }

    /// <summary>Threads the network uses (the learning in a classroom gets half the cores).</summary>
    public int Threads { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// One learning pass: forward (normalization with the batch's statistics, running statistics moved), CTC, and
    /// backward; the gradients are added to g. Returns the loss as PyTorch's CTCLoss (mean over lines of
    /// −log p / text length).
    /// </summary>
    public double Learn(LineBatch batch, OcrWeights g)
    {
        Forward(batch, learning: true);
        var ctcStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var classes = _w.Shape.Classes;
        var frames = _sequences.Total;
        var gradient = _logitGradient.Ensure((long)frames * classes);
        var losses = new double[batch.Count];
        Work.For(batch.Count, (long)frames * classes * 8, Threads, (from, to) =>
        {
            for (var i = from; i < to; i++)
            {
                var labels = batch.Lines[i].Labels;
                var offset = (long)_sequences.Offsets[i] * classes;
                var length = Math.Max(1, labels.Length);
                losses[i] = Ctc.Loss(_logits.Data + offset, _sequences.Frames[i], classes, labels, gradient + offset, 1f / (length * batch.Count)) / length;
            }
        });

        Time("ctc", ref ctcStart);
        Backward(gradient, g);
        return losses.Sum() / Math.Max(1, batch.Count);
    }

    /// <summary>The scores of every frame of every line (normalization with the running statistics, as in use).</summary>
    public float[][] Read(LineBatch batch)
    {
        Forward(batch, learning: false);
        var classes = _w.Shape.Classes;
        var result = new float[batch.Count][];
        for (var i = 0; i < batch.Count; i++)
        {
            result[i] = new Span<float>(_logits.Data + (long)_sequences.Offsets[i] * classes, _sequences.Frames[i] * classes).ToArray();
        }

        return result;
    }

    /// <summary>The best path of the scores of one line: the most likely class of each frame, repeats joined, blanks dropped.</summary>
    public string Decode(float[] logits)
    {
        var classes = _w.Shape.Classes;
        var text = new System.Text.StringBuilder();
        var last = 0;
        for (var t = 0; t < logits.Length / classes; t++)
        {
            var best = Kernels.ArgMax(logits.AsSpan(t * classes, classes));
            if (best != last && best != 0)
            {
                text.Append(_w.Shape.Charset[best - 1]);
            }

            last = best;
        }

        return text.ToString();
    }

    public void Dispose()
    {
        foreach (var c in _convs)
        {
            c.Dispose();
        }

        foreach (var l in _lstm)
        {
            l.Dispose();
        }

        _input.Dispose();
        _pictures.Dispose();
        _logits.Dispose();
        _logitGradient.Dispose();
        _hiddenGradient.Dispose();
        _shared.Dispose();
    }

    private void Forward(LineBatch batch, bool learning)
    {
        var shape = _w.Shape;
        var widths = batch.Lines.Select(l => l.Width).ToArray();
        _input.Set(shape.Height, widths);
        var pictures = _pictures.Ensure(_input.Pixels);
        for (var i = 0; i < batch.Count; i++)
        {
            var line = batch.Lines[i];
            if (line.Ink.Length != shape.Height * line.Width)
            {
                throw new ArgumentException($"a line of the batch is not {shape.Height} × {line.Width}");
            }

            line.Ink.CopyTo(new Span<float>(pictures + _input.Offsets[i], line.Ink.Length));
        }

        var x = pictures;
        var level = _input;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < _convs.Length; i++)
        {
            x = _convs[i].Forward(x, level, learning, Threads);
            level = _convs[i].Out;
            Time($"conv {i} forward", ref start);
        }

        _sequences.Set(level);
        for (var l = 0; l < _lstm.Length; l++)
        {
            x = _lstm[l].Forward(x, _sequences, Threads);
            Time($"lstm {l} forward", ref start);
        }

        var frames = _sequences.Total;
        var classes = shape.Classes;
        var hidden2 = 2 * shape.Hidden;
        var logits = _logits.Ensure((long)frames * classes);
        fixed (float* w = _w.OutWeight)
        {
            Gemm.Multiply(new RowsA(x, hidden2), new ColumnsB(w, hidden2), frames, classes, hidden2, logits, classes, add: false, Threads);
        }

        var bias = _w.OutBias;
        Work.For(frames, (long)frames * classes, Threads, (from, to) =>
        {
            for (var f = from; f < to; f++)
            {
                Kernels.AddScaled(new Span<float>(logits + (long)f * classes, classes), bias, 1f);
            }
        });
        Time("output forward", ref start);
    }

    private void Time(string part, ref long start)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        Timings[part] = Timings.GetValueOrDefault(part) + (now - start) / (double)System.Diagnostics.Stopwatch.Frequency;
        start = now;
    }

    private void Backward(float* dLogits, OcrWeights g)
    {
        var shape = _w.Shape;
        var frames = _sequences.Total;
        var classes = shape.Classes;
        var hidden2 = 2 * shape.Hidden;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var top = _lstm[^1].Output;
        var dx = _hiddenGradient.Ensure((long)frames * hidden2);
        fixed (float* gw = g.OutWeight, w = _w.OutWeight)
        {
            Gemm.MultiplyLong(new ColumnsA(dLogits, classes), new RowsB(top, hidden2), classes, hidden2, frames, gw, hidden2, add: true, Threads);
            Gemm.Multiply(new RowsA(dLogits, classes), new RowsB(w, hidden2), frames, hidden2, classes, dx, hidden2, add: false, Threads);
        }

        Work.For(frames, (long)frames * classes, Threads, (from, to) =>
        {
            var part = new float[classes];
            for (var f = from; f < to; f++)
            {
                Kernels.AddScaled(part, new ReadOnlySpan<float>(dLogits + (long)f * classes, classes), 1f);
            }

            return part;
        }, part => Kernels.AddScaled(g.OutBias, part, 1f));

        Time("output backward", ref start);
        for (var l = _lstm.Length - 1; l >= 0; l--)
        {
            dx = _lstm[l].Backward(dx, g.Lstm[l], inputGradient: true, Threads);
            Time($"lstm {l} backward", ref start);
        }

        for (var i = _convs.Length - 1; i >= 0; i--)
        {
            dx = _convs[i].Backward(dx, g.Convs[i], inputGradient: i > 0, Threads);
            Time($"conv {i} backward", ref start);
        }
    }
}
