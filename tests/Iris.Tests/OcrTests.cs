using System.Text.Json.Nodes;
using Iris.Model;
using Iris.Vision.Imaging;
using Iris.Vision.Ocr;

namespace Iris.Tests;

/// <summary>The OCR line reader: its fast convolutions and LSTM against a plain, slow computation of the same model.</summary>
public static class OcrTests
{
    private static readonly (int Channels, (int H, int W)? Pool)[] Blocks =
        [(4, (2, 2)), (6, (2, 2)), (6, null), (8, (2, 1)), (8, null), (8, (2, 1))];

    private const int Features = 10, Hidden = 5, Layers = 2;
    private const string Charset = "abc ";

    [Test]
    public static void TheReaderComputesWhatThePlainFormulasGive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iris-ocr-{Guid.NewGuid():N}.iris");
        try
        {
            var tensors = WriteModel(path);
            var reader = LineReader.Load(path);
            var random = new Random(4);
            const int width = 37;
            var ink = Enumerable.Range(0, 32 * width).Select(_ => (float)random.NextDouble()).ToArray();
            var reading = reader.Read(ink, width);
            var expected = Plain(tensors, ink, width, out var frames);
            Assert.Equal(width / 4, reading.Frames, "frames: width / 4");
            Assert.Equal(frames, reading.Frames, "frames as the plain computation");
            var worst = expected.Select((v, i) => Math.Abs(v - reading.Logits[i])).Max();
            Assert.True(worst < 1e-4, $"logits differ by up to {worst:e2}");
            Assert.True(reading.Text.All(c => Charset.Contains(c)), "the text is made of the charset");

            var picture = new GrayImage(74, 64, Enumerable.Repeat((byte)255, 74 * 64).ToArray());
            Assert.Equal(37 / 4, reader.Read(picture).Frames, "a picture is scaled to height 32 in proportion");
            Assert.Throws<FormatException>(() => LineReader.Load(WriteOther(path)), "only iris-ocr files");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".other");
        }
    }

    private static Dictionary<string, (int[] Shape, float[] Values)> WriteModel(string path)
    {
        var random = new Random(11);
        var tensors = new Dictionary<string, (int[] Shape, float[] Values)>(StringComparer.Ordinal);
        void Add(string name, params int[] shape)
        {
            var values = new float[shape.Aggregate(1, (a, b) => a * b)];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)(random.NextDouble() - 0.5);
            }

            tensors[name] = (shape, values);
        }

        var channels = 1;
        for (var i = 0; i < Blocks.Length; i++)
        {
            Add($"conv.{i}.w", Blocks[i].Channels, channels, 3, 3);
            Add($"conv.{i}.b", Blocks[i].Channels);
            channels = Blocks[i].Channels;
        }

        Add($"conv.{Blocks.Length}.w", Features, channels, 2, 1);
        Add($"conv.{Blocks.Length}.b", Features);
        var input = Features;
        for (var l = 0; l < Layers; l++)
        {
            foreach (var d in new[] { "fw", "bw" })
            {
                Add($"lstm.{l}.{d}.ih", 4 * Hidden, input);
                Add($"lstm.{l}.{d}.hh", 4 * Hidden, Hidden);
                Add($"lstm.{l}.{d}.b", 4 * Hidden);
            }

            input = 2 * Hidden;
        }

        Add("out.w", Charset.Length + 1, 2 * Hidden);
        Add("out.b", Charset.Length + 1);
        var config = new JsonObject
        {
            ["height"] = 32,
            ["blocks"] = new JsonArray([.. Blocks.Select(b => (JsonNode)new JsonObject
            {
                ["channels"] = b.Channels,
                ["pool"] = b.Pool is var (h, w) ? new JsonArray(h, w) : null,
            })]),
            ["features"] = Features,
            ["hidden"] = Hidden,
            ["layers"] = Layers,
            ["charset"] = Charset,
        };
        ModelFile.Write(path, "iris-ocr", config, [], tensors.Select(t => (t.Key, t.Value.Shape, t.Value.Values)), []);
        return tensors;
    }

    private static string WriteOther(string path)
    {
        ModelFile.Write(path + ".other", "marian", [], [], [("x", new[] { 1 }, new float[1])], []);
        return path + ".other";
    }

    // the same model, written as plainly as possible: channels first, loops over everything
    private static float[] Plain(Dictionary<string, (int[] Shape, float[] Values)> t, float[] ink, int width, out int frames)
    {
        int c = 1, h = 32, w = width;
        var map = ink.ToArray(); // [c, h, w]
        for (var i = 0; i < Blocks.Length; i++)
        {
            map = Convolve(map, c, h, w, t[$"conv.{i}.w"].Values, t[$"conv.{i}.b"].Values, Blocks[i].Channels, 3, 3, 1, out h, out w);
            c = Blocks[i].Channels;
            for (var k = 0; k < map.Length; k++)
            {
                map[k] = Math.Max(0, map[k]);
            }

            if (Blocks[i].Pool is var (ph, pw))
            {
                int oh = h / ph, ow = w / pw;
                var pooled = new float[c * oh * ow];
                for (var ch = 0; ch < c; ch++)
                {
                    for (var y = 0; y < oh; y++)
                    {
                        for (var x = 0; x < ow; x++)
                        {
                            var m = float.NegativeInfinity;
                            for (var dy = 0; dy < ph; dy++)
                            {
                                for (var dx = 0; dx < pw; dx++)
                                {
                                    m = Math.Max(m, map[(ch * h + y * ph + dy) * w + x * pw + dx]);
                                }
                            }

                            pooled[(ch * oh + y) * ow + x] = m;
                        }
                    }
                }

                (map, h, w) = (pooled, oh, ow);
            }
        }

        map = Convolve(map, c, h, w, t[$"conv.{Blocks.Length}.w"].Values, t[$"conv.{Blocks.Length}.b"].Values, Features, 2, 1, 0, out _, out w);
        frames = w;
        var sequence = new float[w][];
        for (var x = 0; x < w; x++)
        {
            sequence[x] = Enumerable.Range(0, Features).Select(f => Math.Max(0, map[f * w + x])).ToArray();
        }

        for (var l = 0; l < Layers; l++)
        {
            var fw = Lstm(t, $"lstm.{l}.fw", sequence, reverse: false);
            var bw = Lstm(t, $"lstm.{l}.bw", sequence, reverse: true);
            sequence = [.. fw.Zip(bw, (a, b) => a.Concat(b).ToArray())];
        }

        var classes = Charset.Length + 1;
        var logits = new float[w * classes];
        for (var x = 0; x < w; x++)
        {
            for (var k = 0; k < classes; k++)
            {
                double sum = t["out.b"].Values[k];
                for (var j = 0; j < 2 * Hidden; j++)
                {
                    sum += t["out.w"].Values[k * 2 * Hidden + j] * sequence[x][j];
                }

                logits[x * classes + k] = (float)sum;
            }
        }

        return logits;
    }

    private static float[] Convolve(float[] map, int c, int h, int w, float[] weight, float[] bias, int outputs, int kh, int kw, int pad, out int oh, out int ow)
    {
        oh = h + 2 * pad - kh + 1;
        ow = w + 2 * pad - kw + 1;
        var result = new float[outputs * oh * ow];
        for (var o = 0; o < outputs; o++)
        {
            for (var y = 0; y < oh; y++)
            {
                for (var x = 0; x < ow; x++)
                {
                    double sum = bias[o];
                    for (var i = 0; i < c; i++)
                    {
                        for (var ky = 0; ky < kh; ky++)
                        {
                            for (var kx = 0; kx < kw; kx++)
                            {
                                int sy = y + ky - pad, sx = x + kx - pad;
                                if (sy >= 0 && sy < h && sx >= 0 && sx < w)
                                {
                                    sum += weight[((o * c + i) * kh + ky) * kw + kx] * map[(i * h + sy) * w + sx];
                                }
                            }
                        }
                    }

                    result[(o * oh + y) * ow + x] = (float)sum;
                }
            }
        }

        return result;
    }

    private static float[][] Lstm(Dictionary<string, (int[] Shape, float[] Values)> t, string name, float[][] sequence, bool reverse)
    {
        var (ih, hh, b) = (t[name + ".ih"].Values, t[name + ".hh"].Values, t[name + ".b"].Values);
        var inputs = sequence[0].Length;
        var output = new float[sequence.Length][];
        var h = new double[Hidden];
        var c = new double[Hidden];
        for (var step = 0; step < sequence.Length; step++)
        {
            var x = sequence[reverse ? sequence.Length - 1 - step : step];
            var g = new double[4 * Hidden];
            for (var r = 0; r < 4 * Hidden; r++)
            {
                g[r] = b[r];
                for (var j = 0; j < inputs; j++)
                {
                    g[r] += ih[r * inputs + j] * x[j];
                }

                for (var j = 0; j < Hidden; j++)
                {
                    g[r] += hh[r * Hidden + j] * h[j];
                }
            }

            static double S(double v) => 1 / (1 + Math.Exp(-v));
            for (var k = 0; k < Hidden; k++)
            {
                c[k] = S(g[Hidden + k]) * c[k] + S(g[k]) * Math.Tanh(g[2 * Hidden + k]);
                h[k] = S(g[3 * Hidden + k]) * Math.Tanh(c[k]);
            }

            output[reverse ? sequence.Length - 1 - step : step] = [.. h.Select(v => (float)v)];
        }

        return output;
    }
}
