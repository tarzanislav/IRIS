using Iris.Numerics;
using Iris.Vision.Ocr;
using Iris.Vision.Training;

namespace Iris.Tests;

/// <summary>The own C# trainer of the OCR: the product of matrices, the backward pass, CTC, fonts and learning.</summary>
public static unsafe class OcrTrainingTests
{
    [Test]
    public static void ProductOfMatricesIsExactInEveryForm()
    {
        var random = new Random(3);
        foreach (var (m, n, k) in new[] { (1, 1, 1), (5, 17, 3), (6, 16, 256), (37, 33, 300), (200, 48, 700), (13, 160, 1100), (97, 10, 2000) })
        {
            var a = Values(random, m * k);
            var b = Values(random, k * n);
            var at = Transpose(a, m, k);
            var bt = Transpose(b, k, n);
            var expected = new double[m * n];
            for (var i = 0; i < m; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    double sum = 0;
                    for (var x = 0; x < k; x++)
                    {
                        sum += (double)a[i * k + x] * b[x * n + j];
                    }

                    expected[i * n + j] = sum;
                }
            }

            foreach (var threads in new[] { 1, 3 })
            {
                foreach (var add in new[] { false, true })
                {
                    foreach (var form in new[] { "rows·rows", "columns·columns", "rows·columns·long", "columns·rows·long" })
                    {
                        var c = Values(random, m * n);
                        var before = (float[])c.Clone();
                        fixed (float* pa = a, pb = b, pat = at, pbt = bt, pc = c)
                        {
                            switch (form)
                            {
                                case "rows·rows":
                                    Gemm.Multiply(new RowsA(pa, k), new RowsB(pb, n), m, n, k, pc, n, add, threads);
                                    break;
                                case "columns·columns":
                                    Gemm.Multiply(new ColumnsA(pat, m), new ColumnsB(pbt, k), m, n, k, pc, n, add, threads);
                                    break;
                                case "rows·columns·long":
                                    Gemm.MultiplyLong(new RowsA(pa, k), new ColumnsB(pbt, k), m, n, k, pc, n, add, threads);
                                    break;
                                default:
                                    Gemm.MultiplyLong(new ColumnsA(pat, m), new RowsB(pb, n), m, n, k, pc, n, add, threads);
                                    break;
                            }
                        }

                        for (var i = 0; i < m * n; i++)
                        {
                            var want = expected[i] + (add ? before[i] : 0);
                            Assert.Near(want, c[i], 1e-4 * Math.Sqrt(k) + 1e-5, $"{form} {m}×{n}×{k} threads {threads} add {add} [{i}]");
                        }
                    }
                }
            }
        }
    }

    // a small reader: 8 rows, blocks with and without pooling, LSTM; widths of 8 and 16 test the vector paths too
    internal static OcrShape Tiny(int channels = 4, int hidden = 5) =>
        new(8, [new(channels, 2, 2), new(channels + 2, 1, 1), new(channels * 2, 2, 1)], 6, hidden, 2, "abc");

    internal static LineBatch TinyBatch(OcrShape shape, int seed, params (int Width, string Text)[] lines)
    {
        var random = new Random(seed);
        return new LineBatch(lines.Select(l => new TrainingLine(Values(random, shape.Height * l.Width).Select(v => (v + 1) / 2).ToArray(), l.Width, l.Text, shape.Encode(l.Text))));
    }

    [Test]
    public static void CtcGradientIsTheSlopeOfItsLoss()
    {
        var random = new Random(5);
        const int Frames = 7, Classes = 4;
        var logits = Values(random, Frames * Classes).Select(v => v * 3).ToArray();
        int[] labels = [1, 2, 2, 3];
        var gradient = new float[logits.Length];
        double Loss()
        {
            fixed (float* x = logits, g = new float[logits.Length])
            {
                return Ctc.Loss(x, Frames, Classes, labels, g, 1f);
            }
        }

        fixed (float* x = logits, g = gradient)
        {
            Ctc.Loss(x, Frames, Classes, labels, g, 1f);
        }

        for (var i = 0; i < logits.Length; i++)
        {
            var keep = logits[i];
            logits[i] = keep + 1e-3f;
            var up = Loss();
            logits[i] = keep - 1e-3f;
            var down = Loss();
            logits[i] = keep;
            Assert.Near((up - down) / 2e-3, gradient[i], 2e-3, $"CTC gradient [{i}]");
        }

        // too few frames for the text: no loss, no gradient (zero_infinity)
        fixed (float* x = logits, g = gradient)
        {
            Assert.Equal(0.0, Ctc.Loss(x, 2, Classes, labels, g, 1f), "impossible line");
        }

        Assert.True(gradient.Take(2 * Classes).All(v => v == 0), "no gradient for an impossible line");
    }

    [Test]
    public static void NetworkGradientsAreTheSlopesOfTheLoss()
    {
        var failures = "";
        foreach (var (channels, hidden, threads, smooth) in new[] { (4, 5, 1, true), (8, 8, 3, true), (4, 5, 1, false), (8, 8, 3, false) })
        {
            // smooth: no pooling and every ReLU open (β large), so the loss has no kinks and the slope along a random
            // direction must match exactly; with pooling, a window whose largest value changes is a kink, so single
            // weights are checked there and most of them must match (the PyTorch comparison in CI is exact)
            var shape = smooth ? new OcrShape(4, [new(channels, 1, 1), new(channels + 2, 1, 1)], 6, hidden, 2, "abc") : Tiny(channels, hidden);
            var weights = OcrWeights.Create(shape, seed: 11);
            var random = new Random(2);
            foreach (var c in weights.Convs)
            {
                for (var o = 0; o < c.Outputs; o++)
                {
                    c.Gamma[o] = 0.5f + (float)random.NextDouble();
                    c.Beta[o] = 6;
                }
            }

            var batch = TinyBatch(shape, 9, (40, "abca"), (33, "cab"), (24, "b"), (17, ""));
            using var network = new OcrNetwork(weights) { Threads = threads };
            var gradients = new OcrWeights(shape);
            gradients.Clear();
            network.Learn(batch, gradients);
            double Loss() => network.Learn(batch, new OcrWeights(shape));
            double Slope(float[] values, float[] direction, float step)
            {
                var keep = (float[])values.Clone();
                for (var i = 0; i < keep.Length; i++)
                {
                    values[i] = keep[i] + step * direction[i];
                }

                var up = Loss();
                for (var i = 0; i < keep.Length; i++)
                {
                    values[i] = keep[i] - step * direction[i];
                }

                var down = Loss();
                keep.CopyTo(values, 0);
                return (up - down) / (2 * step);
            }

            foreach (var (tensor, gradient) in weights.Learnable().Zip(gradients.Learnable()))
            {
                if (smooth)
                {
                    var direction = Values(random, tensor.Values.Length);
                    var numeric = Slope(tensor.Values, direction, 2e-3f);
                    var analytic = gradient.Values.Zip(direction, (g, v) => (double)g * v).Sum();
                    failures += Math.Abs(numeric - analytic) <= 0.02 * Math.Abs(numeric) + 2e-4 ? "" : $" {tensor.Name} (smooth, {numeric:G4} ≠ {analytic:G4})";
                    continue;
                }

                var checks = Math.Min(8, tensor.Values.Length);
                var good = 0;
                for (var c = 0; c < checks; c++)
                {
                    var index = random.Next(tensor.Values.Length);
                    var unit = new float[tensor.Values.Length];
                    unit[index] = 1;
                    var numeric = Slope(tensor.Values, unit, 1e-3f);
                    good += Math.Abs(numeric - gradient.Values[index]) <= 0.03 * Math.Abs(numeric) + 5e-4 ? 1 : 0;
                }

                failures += good >= checks * 3 / 4 ? "" : $" {tensor.Name} ({good} of {checks})";
            }
        }

        Assert.True(failures.Length == 0, "gradients differ from the slopes:" + failures);
    }

    [Test]
    public static void LearnedReaderReadsLikeTheReaderOfIris()
    {
        var shape = Tiny(8, 8);
        var weights = OcrWeights.Create(shape, seed: 4);
        var random = new Random(8);
        foreach (var c in weights.Convs)
        {
            for (var o = 0; o < c.Outputs; o++)
            {
                c.Mean[o] = (float)(random.NextDouble() - 0.5);
                c.Variance[o] = 0.5f + (float)random.NextDouble();
                c.Gamma[o] = 0.5f + (float)random.NextDouble();
                c.Beta[o] = (float)(random.NextDouble() - 0.5) * 0.3f;
            }
        }

        var batch = TinyBatch(shape, 3, (37, "ab"), (21, "c"));
        using var network = new OcrNetwork(weights) { Threads = 2 };
        var logits = network.Read(batch);
        var path = Path.Combine(Path.GetTempPath(), $"iris-ocr-test-{Environment.ProcessId}.iris");
        try
        {
            OcrFiles.SaveReader(path, weights);
            var reader = LineReader.Load(path);
            for (var i = 0; i < batch.Count; i++)
            {
                var line = batch.Lines[i];
                var reading = reader.Read(line.Ink, line.Width);
                Assert.Equal(logits[i].Length, reading.Logits.Length, "frames × classes");
                for (var k = 0; k < logits[i].Length; k++)
                {
                    Assert.Near(reading.Logits[k], logits[i][k], 1e-4, $"logit {k} of line {i}");
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void LearningFileKeepsEverything()
    {
        var shape = Tiny();
        var weights = OcrWeights.Create(shape, seed: 1);
        var adam = new AdamW();
        var gradients = new OcrWeights(shape);
        using (var network = new OcrNetwork(weights))
        {
            gradients.Clear();
            network.Learn(TinyBatch(shape, 1, (30, "abc"), (20, "a")), gradients);
        }

        adam.Step(weights, gradients, 1e-3f);
        var state = new OcrLearningState { Step = 7, Minutes = 12.5, PlanStart = 3, PlanEnd = 100, PlanRate = 5e-4 };
        var path = Path.Combine(Path.GetTempPath(), $"iris-ocr-train-test-{Environment.ProcessId}.iris");
        try
        {
            OcrFiles.SaveTraining(path, weights, adam, state);
            var (loaded, loadedAdam, loadedState) = OcrFiles.LoadTraining(path);
            Assert.Equal(state, loadedState, "state");
            Assert.Equal(1, loadedAdam.Steps, "steps of Adam");
            foreach (var (a, b) in weights.Learnable().Zip(loaded.Learnable()).Concat(weights.Statistics().Zip(loaded.Statistics())))
            {
                Assert.Sequence(a.Values, b.Values, a.Name);
            }

            foreach (var t in weights.Learnable())
            {
                Assert.Sequence(adam.Moments(t.Name, 0).V, loadedAdam.Moments(t.Name, 0).V, "v of " + t.Name);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static float[] Values(Random random, int count)
    {
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return values;
    }

    private static float[] Transpose(float[] x, int rows, int columns)
    {
        var t = new float[x.Length];
        for (var i = 0; i < rows; i++)
        {
            for (var j = 0; j < columns; j++)
            {
                t[j * rows + i] = x[i * columns + j];
            }
        }

        return t;
    }
}
