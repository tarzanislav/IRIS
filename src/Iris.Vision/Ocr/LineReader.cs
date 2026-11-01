using Iris.Model;
using Iris.Numerics;
using Iris.Vision.Imaging;

namespace Iris.Vision.Ocr;

/// <summary>What the reader made of a line: the text and, for each column step, the scores of every character.</summary>
public sealed record LineReading(string Text, float[] Logits, int Frames, int Classes);

/// <summary>
/// The IRIS line reader (model file of kind "iris-ocr", learned by tools/ocr): convolutions turn a line picture of
/// height 32 into a row of column features, two bidirectional LSTM layers read the row both ways, and a last layer
/// scores every character (and "nothing", the CTC blank) for each column; the best path, with repeats and blanks
/// removed, is the text. Feature maps are kept row by row with the channels together ([height · width, channels]),
/// so a 3 × 3 convolution is one matrix product over the gathered neighbourhoods.
/// </summary>
public sealed class LineReader
{
    private readonly int _height;
    private readonly string _charset;
    private readonly Conv[] _convs;
    private readonly (int H, int W)?[] _pools;
    private readonly Conv _collapse;
    private readonly Lstm[][] _lstm;
    private readonly float[] _outWeight;
    private readonly float[] _outBias;
    private readonly int _hidden;

    private LineReader(int height, string charset, Conv[] convs, (int H, int W)?[] pools, Conv collapse, Lstm[][] lstm, float[] outWeight, float[] outBias, int hidden)
    {
        (_height, _charset, _convs, _pools, _collapse, _lstm, _outWeight, _outBias, _hidden) =
            (height, charset, convs, pools, collapse, lstm, outWeight, outBias, hidden);
    }

    /// <summary>The characters the model knows (class k + 1 is _charset[k]; class 0 is the blank).</summary>
    public string Charset => _charset;

    public int Height => _height;

    public static LineReader Load(string path)
    {
        using var file = ModelFile.Load(path);
        return Load(file);
    }

    public static LineReader Load(ModelFile file)
    {
        if (file.Kind != "iris-ocr")
        {
            throw new FormatException($"not an IRIS OCR model (kind \"{file.Kind}\")");
        }

        var config = file.Config;
        var height = config["height"]!.GetValue<int>();
        var charset = config["charset"]!.GetValue<string>();
        var hidden = config["hidden"]!.GetValue<int>();
        var features = config["features"]!.GetValue<int>();
        var layers = config["layers"]!.GetValue<int>();
        var blocks = config["blocks"]!.AsArray();
        var convs = new Conv[blocks.Count];
        var pools = new (int, int)?[blocks.Count];
        var channels = 1;
        var rows = height;
        for (var i = 0; i < blocks.Count; i++)
        {
            var output = blocks[i]!["channels"]!.GetValue<int>();
            convs[i] = Conv.Read(file, $"conv.{i}", channels, output, 3, 3);
            if (blocks[i]!["pool"] is System.Text.Json.Nodes.JsonArray pool)
            {
                pools[i] = (pool[0]!.GetValue<int>(), pool[1]!.GetValue<int>());
                rows /= pools[i]!.Value.Item1;
            }

            channels = output;
        }

        var collapse = Conv.Read(file, $"conv.{blocks.Count}", channels, features, rows, 1);
        var lstm = new Lstm[layers][];
        var input = features;
        for (var l = 0; l < layers; l++)
        {
            lstm[l] = [Lstm.Read(file, $"lstm.{l}.fw", input, hidden), Lstm.Read(file, $"lstm.{l}.bw", input, hidden)];
            input = 2 * hidden;
        }

        var classes = charset.Length + 1;
        return new LineReader(height, charset, convs, pools, collapse, lstm, file.Floats("out.w", classes, 2 * hidden), file.Floats("out.b", classes), hidden);
    }

    /// <summary>Reads a line picture (any size; scaled to the model's height, width in proportion).</summary>
    public LineReading Read(GrayImage line)
    {
        var width = Math.Max(8, (int)Math.Round(line.Width * (double)_height / line.Height));
        var scaled = line.Width == width && line.Height == _height ? line : line.Resized(width, _height);
        var ink = new float[_height * width];
        for (var i = 0; i < ink.Length; i++)
        {
            ink[i] = 1f - scaled.Pixels[i] / 255f;
        }

        return Read(ink, width);
    }

    /// <summary>Reads ink values (1 = ink, 0 = paper) of height 32 and the given width, row after row.</summary>
    public LineReading Read(float[] ink, int width)
    {
        int h = _height, w = width, c = 1;
        var map = ink;
        for (var i = 0; i < _convs.Length; i++)
        {
            map = _convs[i].Apply(map, h, w, padding: 1);
            Kernels.Relu(map);
            c = _convs[i].Outputs;
            if (_pools[i] is var (ph, pw))
            {
                (map, h, w) = MaxPool(map, h, w, c, ph, pw);
            }
        }

        // the collapsing convolution: all remaining rows at once, no padding → one row of features
        var sequence = _collapse.Apply(map, h, w, padding: 0);
        Kernels.Relu(sequence);
        var frames = w;
        var input = _collapse.Outputs;
        foreach (var layer in _lstm)
        {
            var forward = layer[0].Run(sequence, frames, reverse: false);
            var backward = layer[1].Run(sequence, frames, reverse: true);
            var joined = new float[frames * 2 * _hidden];
            for (var t = 0; t < frames; t++)
            {
                forward.AsSpan(t * _hidden, _hidden).CopyTo(joined.AsSpan(t * 2 * _hidden));
                backward.AsSpan(t * _hidden, _hidden).CopyTo(joined.AsSpan(t * 2 * _hidden + _hidden));
            }

            sequence = joined;
            input = 2 * _hidden;
        }

        var classes = _charset.Length + 1;
        var logits = Kernels.Linear(sequence, frames, _outWeight, _outBias, input, classes);
        return new LineReading(BestPath(logits, frames, classes), logits, frames, classes);
    }

    // the most likely class of each frame; repeats joined, blanks dropped
    private string BestPath(float[] logits, int frames, int classes)
    {
        var text = new System.Text.StringBuilder();
        var last = 0;
        for (var t = 0; t < frames; t++)
        {
            var best = Kernels.ArgMax(logits.AsSpan(t * classes, classes));
            if (best != last && best != 0)
            {
                text.Append(_charset[best - 1]);
            }

            last = best;
        }

        return text.ToString();
    }

    private static (float[] Map, int H, int W) MaxPool(float[] map, int h, int w, int c, int ph, int pw)
    {
        int oh = h / ph, ow = w / pw;
        var result = new float[oh * ow * c];
        for (var y = 0; y < oh; y++)
        {
            for (var x = 0; x < ow; x++)
            {
                var target = result.AsSpan((y * ow + x) * c, c);
                target.Fill(float.NegativeInfinity);
                for (var dy = 0; dy < ph; dy++)
                {
                    for (var dx = 0; dx < pw; dx++)
                    {
                        var source = map.AsSpan(((y * ph + dy) * w + x * pw + dx) * c, c);
                        for (var k = 0; k < c; k++)
                        {
                            target[k] = MathF.Max(target[k], source[k]);
                        }
                    }
                }
            }
        }

        return (result, oh, ow);
    }

    /// <summary>A convolution with weights rearranged to [outputs, kh, kw, inputs] (the order of the gathered neighbourhoods).</summary>
    private sealed class Conv
    {
        private Conv(int inputs, int outputs, int kh, int kw, float[] weight, float[] bias)
        {
            (Inputs, Outputs, Kh, Kw, Weight, Bias) = (inputs, outputs, kh, kw, weight, bias);
        }

        public int Inputs { get; }

        public int Outputs { get; }

        public int Kh { get; }

        public int Kw { get; }

        public float[] Weight { get; }

        public float[] Bias { get; }

        public static Conv Read(ModelFile file, string name, int inputs, int outputs, int kh, int kw)
        {
            var torch = file.Floats(name + ".w", outputs, inputs, kh, kw);
            var weight = new float[torch.Length];
            for (var o = 0; o < outputs; o++)
            {
                for (var i = 0; i < inputs; i++)
                {
                    for (var y = 0; y < kh; y++)
                    {
                        for (var x = 0; x < kw; x++)
                        {
                            weight[((o * kh + y) * kw + x) * inputs + i] = torch[((o * inputs + i) * kh + y) * kw + x];
                        }
                    }
                }
            }

            return new Conv(inputs, outputs, kh, kw, weight, file.Floats(name + ".b", outputs));
        }

        // map [h · w, inputs] → [oh · ow, outputs]; with padding 1 the size stays, with 0 it shrinks by kernel − 1
        public float[] Apply(float[] map, int h, int w, int padding)
        {
            int oh = h + 2 * padding - Kh + 1, ow = w + 2 * padding - Kw + 1;
            var size = Kh * Kw * Inputs;
            var columns = new float[oh * ow * size];
            Parallel.For(0, oh, y =>
            {
                for (var x = 0; x < ow; x++)
                {
                    var target = columns.AsSpan((y * ow + x) * size, size);
                    for (var ky = 0; ky < Kh; ky++)
                    {
                        var sy = y + ky - padding;
                        for (var kx = 0; kx < Kw; kx++)
                        {
                            var sx = x + kx - padding;
                            var part = target.Slice((ky * Kw + kx) * Inputs, Inputs);
                            if (sy < 0 || sy >= h || sx < 0 || sx >= w)
                            {
                                part.Clear();
                            }
                            else
                            {
                                map.AsSpan((sy * w + sx) * Inputs, Inputs).CopyTo(part);
                            }
                        }
                    }
                }
            });
            return Kernels.Linear(columns, oh * ow, Weight, Bias, size, Outputs);
        }
    }

    /// <summary>One direction of an LSTM layer: gates i, f, g, o (the order of PyTorch).</summary>
    private sealed class Lstm(float[] inputWeight, float[] hiddenWeight, float[] bias, int inputs, int hidden)
    {
        public static Lstm Read(ModelFile file, string name, int inputs, int hidden) =>
            new(file.Floats(name + ".ih", 4 * hidden, inputs), file.Floats(name + ".hh", 4 * hidden, hidden), file.Floats(name + ".b", 4 * hidden), inputs, hidden);

        public float[] Run(float[] sequence, int frames, bool reverse)
        {
            // the input part of every gate for all frames at once, then the recurrence frame by frame
            var pre = Kernels.Linear(sequence, frames, inputWeight, bias, inputs, 4 * hidden);
            var output = new float[frames * hidden];
            var h = new float[hidden];
            var c = new float[hidden];
            var gates = new float[4 * hidden];
            for (var step = 0; step < frames; step++)
            {
                var t = reverse ? frames - 1 - step : step;
                Kernels.Linear(h, 1, hiddenWeight, null, hidden, 4 * hidden, gates);
                var p = pre.AsSpan(t * 4 * hidden, 4 * hidden);
                for (var k = 0; k < hidden; k++)
                {
                    var i = Sigmoid(gates[k] + p[k]);
                    var f = Sigmoid(gates[hidden + k] + p[hidden + k]);
                    var g = MathF.Tanh(gates[2 * hidden + k] + p[2 * hidden + k]);
                    var o = Sigmoid(gates[3 * hidden + k] + p[3 * hidden + k]);
                    c[k] = f * c[k] + i * g;
                    h[k] = o * MathF.Tanh(c[k]);
                }

                h.CopyTo(output.AsSpan(t * hidden, hidden));
            }

            return output;
        }

        private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
    }
}
