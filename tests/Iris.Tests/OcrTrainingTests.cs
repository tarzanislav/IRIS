using Iris.Numerics;
using Iris.Vision.Fonts;
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
        foreach (var (m, n, k) in new[] { (1, 1, 1), (5, 17, 3), (6, 16, 256), (37, 33, 300), (200, 48, 700), (13, 160, 1100), (97, 10, 2000), (2, 50, 300), (3, 70, 260), (8, 65, 513), (1, 200, 192) })
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

    [Test]
    public static void FewRowsGiveTheSameBitsAsAFullBlock()
    {
        // one to three rows go through the narrow kernels, six through the full one: the same sums in the same order
        var random = new Random(5);
        const int n = 70, k = 300;
        var a = Values(random, 6 * k);
        var b = Values(random, k * n);
        var start = Values(random, 6 * n);
        foreach (var add in new[] { false, true })
        {
            var full = (float[])start.Clone();
            fixed (float* pa = a, pb = b, pc = full)
            {
                Gemm.Multiply(new RowsA(pa, k), new RowsB(pb, n), 6, n, k, pc, n, add, threads: 1);
            }

            for (var rows = 1; rows <= 3; rows++)
            {
                var few = (float[])start.Clone();
                fixed (float* pa = a, pb = b, pc = few)
                {
                    Gemm.Multiply(new RowsA(pa, k), new RowsB(pb, n), rows, n, k, pc, n, add, threads: 1);
                }

                for (var i = 0; i < 6 * n; i++)
                {
                    var want = i < rows * n ? full[i] : start[i];
                    Assert.True(BitConverter.SingleToInt32Bits(want) == BitConverter.SingleToInt32Bits(few[i]), $"{rows} rows, add {add} [{i}]: {few[i]} ≠ {want}");
                }
            }
        }
    }

    // a small reader: 8 rows, blocks with and without pooling, LSTM; widths of 8 and 16 test the vector paths too
    internal static OcrShape Tiny(int channels = 4, int hidden = 5, int height = 8) =>
        new(height, [new(channels, 2, 2), new(channels + 2, 1, 1), new(channels * 2, 2, 1)], 6, hidden, 2, "abc");

    internal static LineBatch TinyBatch(OcrShape shape, int seed, params (int Width, string Text)[] lines)
    {
        var random = new Random(seed);
        return new LineBatch(lines.Select(l => new TrainingLine(Values(random, shape.Height * l.Width).Select(v => (v + 1) / 2).ToArray(), l.Width, l.Text, shape.Encode(l.Text))));
    }

    [Test]
    public static void VectorExponentialIsExact()
    {
        // across the whole range the gates see, and at the ends where it is held
        var worst = 0.0;
        for (var i = -870000; i <= 880000; i += 7)
        {
            var x = i / 10000f;
            var fast = Kernels.Exp(System.Runtime.Intrinsics.Vector256.Create(x))[3];
            var exact = Math.Exp(x);
            worst = Math.Max(worst, Math.Abs(fast - exact) / exact);
        }

        Assert.True(worst < 3e-7, $"relative error {worst:E2}");
        Assert.True(float.IsFinite(Kernels.Exp(System.Runtime.Intrinsics.Vector256.Create(1000f))[0]), "large inputs stay finite");
        Assert.True(Kernels.Exp(System.Runtime.Intrinsics.Vector256.Create(-1000f))[0] > 0, "small inputs stay above zero");
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
        foreach (var (channels, hidden, threads, smooth, height) in new[] { (4, 5, 1, true, 4), (8, 8, 3, true, 4), (4, 5, 1, false, 8), (8, 8, 3, false, 8), (8, 8, 3, false, 6) })
        {
            // smooth: no pooling and every ReLU open (β large), so the loss has no kinks and the slope along a random
            // direction must match exactly; with pooling, a window whose largest value changes is a kink, so single
            // weights are checked there and most of them must match (the PyTorch comparison in CI is exact); a height of
            // 6 leaves a row over at the second pooling
            var shape = smooth ? new OcrShape(height, [new(channels, 1, 1), new(channels + 2, 1, 1)], 6, hidden, 2, "abc") : Tiny(channels, hidden, height);
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
    public static void FastConvolutionsGiveTheSameGradients()
    {
        // channels in whole vectors, so the 3 × 3 layers after the first take the Winograd way (odd widths and the
        // pooling make tiles that stick out of the picture) and the first one the direct way (40 = 32 + 8 channels)
        var shape = new OcrShape(8, [new(40, 2, 2), new(24, 1, 1), new(32, 2, 1)], 16, 8, 1, "abc");
        var weights = OcrWeights.Create(shape, seed: 21);
        var batch = TinyBatch(shape, 5, (41, "abca"), (35, "cab"), (22, "b"));
        (double Loss, OcrWeights Gradients, float[][] Logits) Run(bool fast)
        {
            var previous = ConvLayer.UseFastPaths;
            ConvLayer.UseFastPaths = fast;
            try
            {
                using var network = new OcrNetwork(weights.Copy()) { Threads = 3 };
                var g = new OcrWeights(shape);
                g.Clear();
                var loss = network.Learn(batch, g);
                return (loss, g, network.Read(batch));
            }
            finally
            {
                ConvLayer.UseFastPaths = previous;
            }
        }

        var direct = Run(false);
        var fast = Run(true);
        Assert.Near(direct.Loss, fast.Loss, 1e-5 * Math.Abs(direct.Loss), "loss");
        foreach (var (a, b) in direct.Gradients.Learnable().Zip(fast.Gradients.Learnable()))
        {
            var scale = a.Values.Max(MathF.Abs) + 1e-9f;
            var worst = a.Values.Zip(b.Values, (x, y) => MathF.Abs(x - y)).Max() / scale;
            Assert.True(worst < 1e-4, $"gradient of {a.Name}: the fast way differs by {worst:E1} of its largest value");
        }

        for (var i = 0; i < direct.Logits.Length; i++)
        {
            for (var k = 0; k < direct.Logits[i].Length; k++)
            {
                Assert.Near(direct.Logits[i][k], fast.Logits[i][k], 1e-4, $"logit {k} of line {i}");
            }
        }
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

    [Test]
    public static void RasterizerCoversExactAreas()
    {
        // a square at a fractional place: every pixel gets its covered share, the sum is the area
        var square = new Rasterizer(20, 20);
        square.MoveTo(3.25f, 4.5f);
        square.LineTo(13.25f, 4.5f);
        square.LineTo(13.25f, 14.5f);
        square.LineTo(3.25f, 14.5f);
        square.Close();
        var coverage = square.Coverage();
        Assert.Near(100, coverage.Sum(), 1e-3, "area of a 10 × 10 square");
        Assert.Near(0.75 * 0.5, coverage[4 * 20 + 3], 1e-5, "a corner pixel");
        Assert.Near(1, coverage[8 * 20 + 8], 1e-6, "an inner pixel");

        // a triangle, drawn the other way round (the winding does not matter), and a circle of cubic curves
        var triangle = new Rasterizer(30, 30);
        triangle.MoveTo(2, 2);
        triangle.LineTo(2.5f, 27);
        triangle.LineTo(26, 13.3f);
        triangle.Close();
        Assert.Near(0.5 * Math.Abs((0.5 * 11.3) - (24 * 25)), triangle.Coverage().Sum(), 1e-2, "area of a triangle");

        var circle = new Rasterizer(40, 40);
        const float K = 0.5523f * 15, C = 20;
        circle.MoveTo(C + 15, C);
        circle.CubicTo(C + 15, C + K, C + K, C + 15, C, C + 15);
        circle.CubicTo(C - K, C + 15, C - 15, C + K, C - 15, C);
        circle.CubicTo(C - 15, C - K, C - K, C - 15, C, C - 15);
        circle.CubicTo(C + K, C - 15, C + 15, C - K, C + 15, C);
        circle.Close();
        Assert.Near(Math.PI * 225, circle.Coverage().Sum(), 4, "area of a circle");
    }

    [Test]
    public static void FontsAreReadAndDrawn()
    {
        var file = new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", @"C:\Windows\Fonts\arial.ttf" }.FirstOrDefault(File.Exists)
            ?? throw new SkipException("no DejaVu Sans or Arial on this computer");
        var face = FontFace.Load(file)[0];
        Assert.True(face.Covers(OcrShape.DefaultCharset), $"{face.Name} has every character of the OCR");
        Assert.True(face.Kerning(face.GlyphOf('A'), face.GlyphOf('V')) < 0, "A and V are kerned closer");
        var line = LineArtist.Typeset("Žluťoučký kůň", face, 32, 255, 0);
        var ink = line.Values.Count(v => v < 128);
        Assert.True(line.Height > 32 && line.Width > 200 && ink > 500, $"the line is drawn ({line.Width} × {line.Height}, {ink} dark pixels)");

        var artist = new LineArtist([face]);
        var random = new Random(1);
        var texts = new LineTexts(OcrShape.Default, new Dictionary<string, string[]>(), 1);
        var drawn = Enumerable.Range(0, 20).Select(_ => artist.Line(texts.Next(), OcrShape.Default, random)).Where(l => l is not null).ToList();
        Assert.True(drawn.Count >= 15, "most lines fit");
        Assert.True(drawn.All(l => l!.Ink.Length == 32 * l.Width && l.Ink.All(v => v >= 0 && v <= 1)), "lines are 32 rows of ink values");
    }

    [Test]
    public static void SmallReaderLearns()
    {
        var shape = Tiny(8, 8);
        var weights = OcrWeights.Create(shape, seed: 3);
        var adam = new AdamW();
        // two "letters": a dark stroke and a light gap repeated in different rhythms
        var random = new Random(4);
        TrainingLine Line(string text)
        {
            var width = 8 * text.Length + 8;
            var ink = new float[shape.Height * width];
            for (var i = 0; i < text.Length; i++)
            {
                var stripes = text[i] == 'a' ? 1 : text[i] == 'b' ? 2 : 3;
                for (var y = 0; y < shape.Height; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        ink[y * width + 4 + 8 * i + x] = x % (8 / stripes) < 2 ? 0.9f : 0.05f + 0.05f * (float)random.NextDouble();
                    }
                }
            }

            return new TrainingLine(ink, width, text, shape.Encode(text));
        }

        var batch = new LineBatch(new[] { "abc", "cab", "bca", "aab", "cc", "bac" }.Select(Line));
        using var network = new OcrNetwork(weights) { Threads = 2 };
        var gradients = new OcrWeights(shape);
        double first = 0, last = 0;
        for (var step = 0; step < 300; step++)
        {
            gradients.Clear();
            last = network.Learn(batch, gradients);
            first = step == 0 ? last : first;
            AdamW.Clip(gradients, 5);
            adam.Step(weights, gradients, 1e-2f);
        }

        Assert.True(last < first * 0.3, $"the loss falls ({first:F3} → {last:F3})");
        Assert.Equal(0.0, OcrTrainer.Cer(network, batch.Lines), "it reads the lines it learned");
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
