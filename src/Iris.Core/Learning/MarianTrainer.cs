using Iris.Numerics;
using Iris.Translation;

namespace Iris.Learning;

/// <summary>A sentence and its translation as token ids (source and target, both ending with the end of sentence).</summary>
public sealed record TrainingPair(int[] Source, int[] Target);

/// <summary>How an adapter learns.</summary>
public sealed record LearningSettings
{
    /// <summary>Passes over all pairs.</summary>
    public int Epochs { get; init; } = 3;

    /// <summary>Pairs per step (their gradients are summed, then the adapter moves once).</summary>
    public int BatchSize { get; init; } = 8;

    /// <summary>The step of Adam.</summary>
    public float LearningRate { get; init; } = 5e-4f;

    /// <summary>Part of the probability spread over the whole vocabulary (as Marian models were trained: 0.1).</summary>
    public float LabelSmoothing { get; init; } = 0.1f;

    /// <summary>Longest gradient (its norm); a longer one is shortened, so one odd pair cannot spoil the adapter.</summary>
    public float ClipNorm { get; init; } = 1f;

    /// <summary>Seed of the order of pairs, so learning can be repeated.</summary>
    public int Seed { get; init; } = 1;
}

/// <summary>Where learning is: steps done of all and the mean loss per token of the last step.</summary>
public readonly record struct LearningProgress(int Step, int Steps, double Loss);

/// <summary>The gradients of an adapter: one array for each A and B, in the shapes of the adapter.</summary>
internal sealed class Gradients
{
    public Gradients(Adapter adapter)
    {
        foreach (var (name, low) in adapter.Layers)
        {
            A[name] = new float[low.A.Length];
            B[name] = new float[low.B.Length];
        }
    }

    public Dictionary<string, float[]> A { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, float[]> B { get; } = new(StringComparer.Ordinal);

    public void Add(Gradients other)
    {
        foreach (var (name, values) in other.A)
        {
            Kernels.AddScaled(A[name], values, 1f);
            Kernels.AddScaled(B[name], other.B[name], 1f);
        }
    }

    public void Clear()
    {
        foreach (var values in A.Values.Concat(B.Values))
        {
            Array.Clear(values);
        }
    }

    public double Norm()
    {
        double sum = 0;
        foreach (var values in A.Values.Concat(B.Values))
        {
            foreach (var v in values)
            {
                sum += (double)v * v;
            }
        }

        return Math.Sqrt(sum);
    }
}

/// <summary>
/// Teaches an adapter of a Marian model from pairs of sentences: the whole translation is run at once (the decoder
/// sees the right previous tokens), the loss is the cross entropy of each right token, and its gradient is carried
/// back through every layer by hand – only the adapter learns, the model stays as it is. The backward pass is
/// written here from the formulas of each operation (dense, attention, LayerNorm, activation); a test compares it
/// with differences of the loss.
/// </summary>
public sealed class MarianTrainer
{
    private readonly MarianModel _model;
    private readonly Adapter _adapter;
    private readonly Dictionary<Dense, float[]> _transposed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Dense, string> _names = new(ReferenceEqualityComparer.Instance);
    private readonly float[] _embedTransposed;
    private readonly Func<float, float> _derivative;

    /// <summary>A trainer for an adapter of a model; learning changes the values of the adapter in place.</summary>
    public MarianTrainer(MarianModel model, Adapter adapter)
    {
        if (model.HasAdapter)
        {
            throw new ArgumentException("the model already has an adapter; learning starts from the model alone");
        }

        _adapter = adapter;
        _model = model.WithAdapter(adapter);
        foreach (var (name, dense) in _model.DenseLayers())
        {
            _names[dense] = name;
            _transposed[dense] = Transpose(dense.Weight);
        }

        _embedTransposed = Transpose(_model.Embedding);
        _derivative = model.Config.Activation == "relu" ? ReluSlope : SiluSlope;
    }

    // slopes of the activations: ReLU, and SiLU x·σ(x) → σ(x)·(1 + x·(1 − σ(x)))
    private static float ReluSlope(float x) => x > 0 ? 1f : 0f;

    private static float SiluSlope(float x)
    {
        var s = 1f / (1f + MathF.Exp(-x));
        return s * (1f + x * (1f - s));
    }

    /// <summary>The model with the adapter as it is now (for translating while learning).</summary>
    public MarianModel Model => _model;

    /// <summary>The mean loss per token of pairs (cross entropy of the right tokens; without label smoothing by default).</summary>
    public double Loss(IEnumerable<TrainingPair> pairs, float smoothing = 0)
    {
        double sum = 0;
        long tokens = 0;
        foreach (var pair in pairs)
        {
            var pass = Forward(pair);
            sum += pass.Loss(smoothing, null);
            tokens += pair.Target.Length;
        }

        return tokens == 0 ? 0 : sum / tokens;
    }

    /// <summary>
    /// Learns from pairs: in every epoch the pairs in a new (seeded) order, a step of Adam after each batch.
    /// Pairs longer than the model allows are left out. Returns the mean loss per token of the last epoch.
    /// </summary>
    public double Train(IReadOnlyList<TrainingPair> pairs, LearningSettings settings, IProgress<LearningProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var usable = pairs.Where(Fits).ToList();
        if (usable.Count == 0)
        {
            throw new ArgumentException("there is no pair to learn from");
        }

        var optimizer = new Adam(_adapter, settings.LearningRate);
        var random = new Random(settings.Seed);
        var batch = Math.Max(1, settings.BatchSize);
        var stepsPerEpoch = (usable.Count + batch - 1) / batch;
        var steps = stepsPerEpoch * settings.Epochs;
        var step = 0;
        double epochLoss = 0;
        var total = new Gradients(_adapter);
        for (var epoch = 0; epoch < settings.Epochs; epoch++)
        {
            var order = usable.ToArray();
            random.Shuffle(order);
            double lossSum = 0;
            long tokenSum = 0;
            for (var start = 0; start < order.Length; start += batch)
            {
                cancellation.ThrowIfCancellationRequested();
                var part = order.AsSpan(start, Math.Min(batch, order.Length - start)).ToArray();
                total.Clear();
                var results = new (double Loss, Gradients Gradients)[part.Length];
                Parallel.For(0, part.Length, i =>
                {
                    var gradients = new Gradients(_adapter);
                    results[i] = (Backward(part[i], gradients, settings.LabelSmoothing), gradients);
                });

                double loss = 0;
                var tokens = 0;
                for (var i = 0; i < part.Length; i++)
                {
                    total.Add(results[i].Gradients);
                    loss += results[i].Loss;
                    tokens += part[i].Target.Length;
                }

                optimizer.Step(total, 1f / tokens, settings.ClipNorm);
                lossSum += loss;
                tokenSum += tokens;
                progress?.Report(new LearningProgress(++step, steps, loss / tokens));
            }

            epochLoss = lossSum / tokenSum;
        }

        return epochLoss;
    }

    /// <summary>Whether a pair can be learned (lengths within the model).</summary>
    public bool Fits(TrainingPair pair) =>
        pair.Source.Length is > 0 && pair.Source.Length <= _model.Config.MaxPositions &&
        pair.Target.Length is > 0 && pair.Target.Length <= _model.Config.MaxPositions;

    /// <summary>
    /// The loss of each pair (summed over its tokens) and the gradient of their sum for every A and B of the adapter
    /// (to compare with a reference; learning itself uses <see cref="Train"/>).
    /// </summary>
    public (double[] Losses, IReadOnlyDictionary<string, float[]> A, IReadOnlyDictionary<string, float[]> B) Gradient(
        IReadOnlyList<TrainingPair> pairs, float smoothing)
    {
        var gradients = new Gradients(_adapter);
        var losses = pairs.Select(p => Backward(p, gradients, smoothing)).ToArray();
        return (losses, gradients.A, gradients.B);
    }

    /// <summary>The loss of one pair (summed over its tokens) and its gradient added to gradients.</summary>
    internal double Backward(TrainingPair pair, Gradients gradients, float smoothing)
    {
        var pass = Forward(pair);
        var loss = pass.Loss(smoothing, out var dLogits);
        BackwardFrom(pass, dLogits, gradients);
        return loss;
    }

    // ---------------------------------------------------------------- forward

    private sealed class AttentionPass
    {
        public required float[] Input;      // [n, d] the layer input (queries side)
        public required float[] Query;      // [n, d]
        public required float[] Keys;       // [m, d]
        public required float[] Values;     // [m, d]
        public required float[] Weights;    // [heads, n, m] softmax weights
        public required float[] Context;    // [n, d]
        public required float[] Normalized; // [n, d] x̂ of the LayerNorm after the residual
        public required float[] InverseStd; // [n]
    }

    private sealed class FeedForwardPass
    {
        public required float[] Input;      // [n, d]
        public required float[] Hidden;     // [n, ffn] before the activation
        public required float[] Activated;  // [n, ffn]
        public required float[] Normalized; // [n, d]
        public required float[] InverseStd; // [n]
    }

    private sealed class Pass
    {
        public required TrainingPair Pair;
        public required int[] DecoderInput;
        public required List<(AttentionPass Self, FeedForwardPass FeedForward)> Encoder;
        public required float[] Encoded;   // [n, d] output of the encoder
        public required List<(AttentionPass Self, AttentionPass Cross, FeedForwardPass FeedForward)> Decoder;
        public required float[] Output;    // [m, d] output of the decoder
        public required float[] Logits;    // [m, vocabulary]
        public required int Vocabulary;

        public double Loss(float smoothing, float[]? gradient)
        {
            var v = Vocabulary;
            double total = 0;
            for (var t = 0; t < Pair.Target.Length; t++)
            {
                var row = Logits.AsSpan(t * v, v);
                var logSum = Kernels.LogSumExp(row);
                var right = Pair.Target[t];
                double sumLog = 0;
                foreach (var z in row)
                {
                    sumLog += z - logSum;
                }

                total += -(1 - smoothing) * (row[right] - logSum) - smoothing * sumLog / v;
                if (gradient is not null)
                {
                    var g = gradient.AsSpan(t * v, v);
                    for (var i = 0; i < v; i++)
                    {
                        g[i] = (float)Math.Exp(row[i] - logSum) - smoothing / v;
                    }

                    g[right] -= 1 - smoothing;
                }
            }

            return total;
        }

        public double Loss(float smoothing, out float[] gradient)
        {
            gradient = new float[Logits.Length];
            return Loss(smoothing, gradient);
        }
    }

    private Pass Forward(TrainingPair pair)
    {
        var c = _model.Config;
        int d = c.DModel, n = pair.Source.Length, m = pair.Target.Length;
        if (!Fits(pair))
        {
            throw new ArgumentException($"a pair must have 1 to {c.MaxPositions} tokens on each side");
        }

        var x = Embed(pair.Source);
        var encoder = new List<(AttentionPass, FeedForwardPass)>();
        foreach (var layer in _model.Encoder)
        {
            var self = AttentionForward(layer.SelfAttention, x, n, x, n, c.EncoderHeads, causal: false);
            var feed = FeedForwardForward(layer.FeedForward, x, n);
            encoder.Add((self, feed));
        }

        var encoded = x;
        var decoderInput = new int[m];
        decoderInput[0] = c.DecoderStartId;
        Array.Copy(pair.Target, 0, decoderInput, 1, m - 1);
        var y = Embed(decoderInput);
        var decoder = new List<(AttentionPass, AttentionPass, FeedForwardPass)>();
        foreach (var layer in _model.Decoder)
        {
            var self = AttentionForward(layer.SelfAttention, y, m, y, m, c.DecoderHeads, causal: true);
            var cross = AttentionForward(layer.CrossAttention, y, m, encoded, n, c.DecoderHeads, causal: false);
            var feed = FeedForwardForward(layer.FeedForward, y, m);
            decoder.Add((self, cross, feed));
        }

        var logits = _model.Embedding.Linear(y, m, _model.OutputBias);
        return new Pass
        {
            Pair = pair,
            DecoderInput = decoderInput,
            Encoder = encoder,
            Encoded = encoded,
            Decoder = decoder,
            Output = y,
            Logits = logits,
            Vocabulary = c.VocabSize,
        };
    }

    private float[] Embed(int[] ids)
    {
        var d = _model.Config.DModel;
        var x = new float[ids.Length * d];
        for (var t = 0; t < ids.Length; t++)
        {
            if ((uint)ids[t] >= (uint)_model.Config.VocabSize)
            {
                throw new ArgumentException($"token id {ids[t]} is outside the vocabulary");
            }

            var row = x.AsSpan(t * d, d);
            _model.Embedding.Row(ids[t], row);
            var p = _model.PositionRow(t);
            for (var i = 0; i < d; i++)
            {
                row[i] = row[i] * _model.EmbedScale + p[i];
            }
        }

        return x;
    }

    // x ← LayerNorm(x + attention(x, memory)); x is changed in place, what backward needs is kept
    private AttentionPass AttentionForward(Attention a, float[] x, int n, float[] memory, int count, int heads, bool causal)
    {
        var d = _model.Config.DModel;
        var input = (float[])x.Clone();
        var q = a.Query.Apply(input, n);
        var k = a.Key.Apply(memory, count);
        var v = a.Value.Apply(memory, count);
        var weights = new float[heads * n * count];
        var context = new float[n * d];
        var size = d / heads;
        var scale = (float)(1.0 / Math.Sqrt(size));
        Parallel.For(0, heads, h =>
        {
            for (var t = 0; t < n; t++)
            {
                var keys = causal ? t + 1 : count;
                var w = weights.AsSpan((h * n + t) * count, keys);
                var query = q.AsSpan(t * d + h * size, size);
                for (var j = 0; j < keys; j++)
                {
                    w[j] = Kernels.Dot(query, k.AsSpan(j * d + h * size, size)) * scale;
                }

                Kernels.Softmax(w);
                var o = context.AsSpan(t * d + h * size, size);
                for (var j = 0; j < keys; j++)
                {
                    Kernels.AddScaled(o, v.AsSpan(j * d + h * size, size), w[j]);
                }
            }
        });

        var output = a.Output.Apply(context, n);
        Kernels.Add(x, output);
        var (normalized, inverse) = Normalize(x, n, a.Norm);
        return new AttentionPass
        {
            Input = input,
            Query = q,
            Keys = k,
            Values = v,
            Weights = weights,
            Context = context,
            Normalized = normalized,
            InverseStd = inverse,
        };
    }

    private FeedForwardPass FeedForwardForward(FeedForward f, float[] x, int n)
    {
        var input = (float[])x.Clone();
        var hidden = f.Up.Apply(input, n);
        var activated = (float[])hidden.Clone();
        _model.Activate(activated);
        Kernels.Add(x, f.Down.Apply(activated, n));
        var (normalized, inverse) = Normalize(x, n, f.Norm);
        return new FeedForwardPass { Input = input, Hidden = hidden, Activated = activated, Normalized = normalized, InverseStd = inverse };
    }

    // LayerNorm in place (as the model computes it), keeping x̂ and 1/σ of every row
    private (float[] Normalized, float[] InverseStd) Normalize(float[] x, int n, Norm norm)
    {
        var d = _model.Config.DModel;
        var normalized = new float[n * d];
        var inverse = new float[n];
        for (var t = 0; t < n; t++)
        {
            var row = x.AsSpan(t * d, d);
            double sum = 0;
            foreach (var value in row)
            {
                sum += value;
            }

            var mean = sum / d;
            double squares = 0;
            foreach (var value in row)
            {
                squares += (value - mean) * (value - mean);
            }

            var m = (float)mean;
            var s = (float)(1.0 / Math.Sqrt(squares / d + _model.Config.LayerNormEpsilon));
            inverse[t] = s;
            var hat = normalized.AsSpan(t * d, d);
            for (var i = 0; i < d; i++)
            {
                hat[i] = (row[i] - m) * s;
                row[i] = hat[i] * norm.Gain[i] + norm.Bias[i];
            }
        }

        return (normalized, inverse);
    }

    // ---------------------------------------------------------------- backward

    private void BackwardFrom(Pass pass, float[] dLogits, Gradients gradients)
    {
        var c = _model.Config;
        int d = c.DModel, n = pass.Pair.Source.Length, m = pass.Pair.Target.Length;

        // logits = y · Eᵀ + b  →  dy = dlogits · E
        var dy = Kernels.Linear(dLogits, m, _embedTransposed, null, c.VocabSize, d);
        var dEncoded = new float[n * d];
        for (var l = _model.Decoder.Count - 1; l >= 0; l--)
        {
            var layer = _model.Decoder[l];
            var (self, cross, feed) = pass.Decoder[l];
            FeedForwardBackward(layer.FeedForward, feed, dy, m, gradients);
            AttentionBackward(layer.CrossAttention, cross, dy, m, pass.Encoded, n, dEncoded, c.DecoderHeads, causal: false, gradients);
            AttentionBackward(layer.SelfAttention, self, dy, m, null, m, null, c.DecoderHeads, causal: true, gradients);
        }

        var dx = dEncoded;
        for (var l = _model.Encoder.Count - 1; l >= 0; l--)
        {
            var layer = _model.Encoder[l];
            var (self, feed) = pass.Encoder[l];
            FeedForwardBackward(layer.FeedForward, feed, dx, n, gradients);
            AttentionBackward(layer.SelfAttention, self, dx, n, null, n, null, c.EncoderHeads, causal: false, gradients);
        }

        // the token vectors and positions do not learn: the gradient stops here
    }

    // dx (the gradient of the block output) becomes the gradient of the block input, in place
    private void FeedForwardBackward(FeedForward f, FeedForwardPass pass, float[] dx, int n, Gradients gradients)
    {
        var dSum = NormalizeBackward(dx, pass.Normalized, pass.InverseStd, n, f.Norm);
        var dActivated = DenseBackward(f.Down, pass.Activated, dSum, n, gradients);
        for (var i = 0; i < dActivated.Length; i++)
        {
            dActivated[i] *= _derivative(pass.Hidden[i]);
        }

        var dInput = DenseBackward(f.Up, pass.Input, dActivated, n, gradients);
        Array.Copy(dSum, dx, dx.Length);
        Kernels.Add(dx, dInput);
    }

    // the same for attention; with memory (cross attention) the gradient of keys and values goes to dMemory
    private void AttentionBackward(Attention a, AttentionPass pass, float[] dx, int n, float[]? memory, int count, float[]? dMemory,
        int heads, bool causal, Gradients gradients)
    {
        var d = _model.Config.DModel;
        var size = d / heads;
        var scale = (float)(1.0 / Math.Sqrt(size));
        var dSum = NormalizeBackward(dx, pass.Normalized, pass.InverseStd, n, a.Norm);
        var dContext = DenseBackward(a.Output, pass.Context, dSum, n, gradients);
        var dq = new float[n * d];
        var dk = new float[count * d];
        var dv = new float[count * d];
        Parallel.For(0, heads, h =>
        {
            var dWeights = new float[count];
            for (var t = 0; t < n; t++)
            {
                var keys = causal ? t + 1 : count;
                var w = pass.Weights.AsSpan((h * n + t) * count, keys);
                var dOut = dContext.AsSpan(t * d + h * size, size);
                double weighted = 0;
                for (var j = 0; j < keys; j++)
                {
                    Kernels.AddScaled(dv.AsSpan(j * d + h * size, size), dOut, w[j]);
                    dWeights[j] = Kernels.Dot(dOut, pass.Values.AsSpan(j * d + h * size, size));
                    weighted += (double)w[j] * dWeights[j];
                }

                var query = pass.Query.AsSpan(t * d + h * size, size);
                var dQuery = dq.AsSpan(t * d + h * size, size);
                for (var j = 0; j < keys; j++)
                {
                    var dScore = w[j] * (float)(dWeights[j] - weighted) * scale;
                    Kernels.AddScaled(dQuery, pass.Keys.AsSpan(j * d + h * size, size), dScore);
                    Kernels.AddScaled(dk.AsSpan(j * d + h * size, size), query, dScore);
                }
            }
        });

        var source = memory ?? pass.Input;
        var dInput = DenseBackward(a.Query, pass.Input, dq, n, gradients);
        var dKeys = DenseBackward(a.Key, source, dk, count, gradients);
        var dValues = DenseBackward(a.Value, source, dv, count, gradients);
        Array.Copy(dSum, dx, dx.Length);
        Kernels.Add(dx, dInput);
        if (dMemory is null)
        {
            Kernels.Add(dx, dKeys);
            Kernels.Add(dx, dValues);
        }
        else
        {
            Kernels.Add(dMemory, dKeys);
            Kernels.Add(dMemory, dValues);
        }
    }

    // y = LayerNorm(s): the gradient of s from the gradient of y (gain and bias do not learn)
    private float[] NormalizeBackward(float[] dy, float[] normalized, float[] inverse, int n, Norm norm)
    {
        var d = _model.Config.DModel;
        var ds = new float[n * d];
        for (var t = 0; t < n; t++)
        {
            var g = dy.AsSpan(t * d, d);
            var hat = normalized.AsSpan(t * d, d);
            double mean = 0, meanHat = 0;
            for (var i = 0; i < d; i++)
            {
                var gh = g[i] * norm.Gain[i];
                mean += gh;
                meanHat += gh * hat[i];
            }

            mean /= d;
            meanHat /= d;
            var o = ds.AsSpan(t * d, d);
            for (var i = 0; i < d; i++)
            {
                o[i] = inverse[t] * (float)(g[i] * norm.Gain[i] - mean - hat[i] * meanHat);
            }
        }

        return ds;
    }

    // y = x·Wᵀ + b + s·(x·Aᵀ)·Bᵀ: returns dx; adds dA and dB of the adapter to gradients
    private float[] DenseBackward(Dense dense, float[] x, float[] dy, int n, Gradients gradients)
    {
        int inputs = dense.Weight.Columns, outputs = dense.Weight.Rows;
        var dx = Kernels.Linear(dy, n, _transposed[dense], null, outputs, inputs);
        if (dense.Low is not { } low)
        {
            return dx;
        }

        var name = _names[dense];
        var rank = low.Rank;
        var s = dense.LowScale;
        var h = Kernels.Linear(x, n, low.A, null, inputs, rank);             // [n, r]
        var g = Kernels.Linear(dy, n, Transpose(low.B, outputs, rank), null, outputs, rank); // dy·B [n, r]
        Kernels.AddScaled(dx, Kernels.Linear(g, n, Transpose(low.A, rank, inputs), null, rank, inputs), s);
        var dA = gradients.A[name];
        var dB = gradients.B[name];
        for (var t = 0; t < n; t++)
        {
            var dyRow = dy.AsSpan(t * outputs, outputs);
            var hRow = h.AsSpan(t * rank, rank);
            var gRow = g.AsSpan(t * rank, rank);
            var xRow = x.AsSpan(t * inputs, inputs);
            for (var o = 0; o < outputs; o++)
            {
                if (dyRow[o] != 0)
                {
                    Kernels.AddScaled(dB.AsSpan(o * rank, rank), hRow, s * dyRow[o]);
                }
            }

            for (var k = 0; k < rank; k++)
            {
                Kernels.AddScaled(dA.AsSpan(k * inputs, inputs), xRow, s * gRow[k]);
            }
        }

        return dx;
    }

    private static float[] Transpose(float[] values, int rows, int columns)
    {
        var result = new float[values.Length];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                result[c * rows + r] = values[r * columns + c];
            }
        }

        return result;
    }

    // the weights as floats, transposed: [columns, rows] (int8 weights are widened with their scales)
    private static float[] Transpose(Matrix matrix)
    {
        int rows = matrix.Rows, columns = matrix.Columns;
        var result = new float[(long)rows * columns];
        const int block = 64;
        Parallel.For(0, (rows + block - 1) / block, b =>
        {
            var row = new float[columns];
            for (var r = b * block; r < Math.Min(rows, (b + 1) * block); r++)
            {
                matrix.Row(r, row);
                for (var c = 0; c < columns; c++)
                {
                    result[(long)c * rows + r] = row[c];
                }
            }
        });
        return result;
    }

    /// <summary>Adam (with bias correction), one moment pair for every learned value.</summary>
    private sealed class Adam
    {
        private const float Beta1 = 0.9f, Beta2 = 0.999f, Epsilon = 1e-8f;
        private readonly Adapter _adapter;
        private readonly float _rate;
        private readonly Dictionary<float[], (float[] M, float[] V)> _moments = new(ReferenceEqualityComparer.Instance);
        private int _step;

        public Adam(Adapter adapter, float rate)
        {
            _adapter = adapter;
            _rate = rate;
            foreach (var low in adapter.Layers.Values)
            {
                _moments[low.A] = (new float[low.A.Length], new float[low.A.Length]);
                _moments[low.B] = (new float[low.B.Length], new float[low.B.Length]);
            }
        }

        public void Step(Gradients gradients, float factor, float clip)
        {
            var norm = gradients.Norm() * factor;
            if (clip > 0 && norm > clip)
            {
                factor *= (float)(clip / norm);
            }

            _step++;
            var correction1 = 1 - Math.Pow(Beta1, _step);
            var correction2 = 1 - Math.Pow(Beta2, _step);
            var rate = (float)(_rate * Math.Sqrt(correction2) / correction1);
            foreach (var (name, low) in _adapter.Layers)
            {
                Update(low.A, gradients.A[name]);
                Update(low.B, gradients.B[name]);
            }

            void Update(float[] values, float[] gradient)
            {
                var (m, v) = _moments[values];
                for (var i = 0; i < values.Length; i++)
                {
                    var g = gradient[i] * factor;
                    m[i] = Beta1 * m[i] + (1 - Beta1) * g;
                    v[i] = Beta2 * v[i] + (1 - Beta2) * g * g;
                    values[i] -= rate * m[i] / (MathF.Sqrt(v[i]) + Epsilon);
                }
            }
        }
    }
}
