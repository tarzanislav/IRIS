namespace Iris.Vision.Training;

/// <summary>
/// A convolution with batch normalization: weights [outputs, kh, kw, inputs] (the order of the gathered
/// neighbourhoods, as the C# reader keeps them), the scale and shift of the normalization and its running mean and
/// variance (learned statistics, not trained by the optimizer).
/// </summary>
public sealed class ConvParameters
{
    public ConvParameters(int inputs, int outputs, int kh, int kw)
    {
        (Inputs, Outputs, Kh, Kw) = (inputs, outputs, kh, kw);
        Weight = new float[outputs * kh * kw * inputs];
        Gamma = new float[outputs];
        Beta = new float[outputs];
        Mean = new float[outputs];
        Variance = new float[outputs];
    }

    public int Inputs { get; }

    public int Outputs { get; }

    public int Kh { get; }

    public int Kw { get; }

    /// <summary>Values of one gathered neighbourhood (kh · kw · inputs).</summary>
    public int Features => Kh * Kw * Inputs;

    public float[] Weight { get; }

    public float[] Gamma { get; }

    public float[] Beta { get; }

    public float[] Mean { get; }

    public float[] Variance { get; }

    /// <summary>[outputs, kh, kw, inputs] → [outputs, inputs, kh, kw] (PyTorch and the model files).</summary>
    public float[] WeightForFile() => Permute(Weight, toFile: true);

    public void WeightFromFile(ReadOnlySpan<float> file) => Permute(file.ToArray(), toFile: false).CopyTo(Weight, 0);

    /// <summary>Any tensor of the weight's shape (e.g. a moment of Adam) between the two orders.</summary>
    public float[] Permute(float[] values, bool toFile)
    {
        var result = new float[values.Length];
        for (var o = 0; o < Outputs; o++)
        {
            for (var i = 0; i < Inputs; i++)
            {
                for (var y = 0; y < Kh; y++)
                {
                    for (var x = 0; x < Kw; x++)
                    {
                        var mine = ((o * Kh + y) * Kw + x) * Inputs + i;
                        var file = ((o * Inputs + i) * Kh + y) * Kw + x;
                        if (toFile)
                        {
                            result[file] = values[mine];
                        }
                        else
                        {
                            result[mine] = values[file];
                        }
                    }
                }
            }
        }

        return result;
    }
}

/// <summary>One direction of an LSTM layer (gates i, f, g, o as in PyTorch, with its two biases).</summary>
public sealed class LstmParameters
{
    public LstmParameters(int inputs, int hidden)
    {
        (Inputs, Hidden) = (inputs, hidden);
        InputWeight = new float[4 * hidden * inputs];
        HiddenWeight = new float[4 * hidden * hidden];
        InputBias = new float[4 * hidden];
        HiddenBias = new float[4 * hidden];
    }

    public int Inputs { get; }

    public int Hidden { get; }

    /// <summary>[4 · hidden, inputs]</summary>
    public float[] InputWeight { get; }

    /// <summary>[4 · hidden, hidden]</summary>
    public float[] HiddenWeight { get; }

    public float[] InputBias { get; }

    public float[] HiddenBias { get; }
}

/// <summary>A named tensor of the line reader as the training files keep it (shape in the order of PyTorch).</summary>
public sealed record OcrTensor(string Name, float[] Values, int[] Shape, ConvParameters? Conv = null)
{
    /// <summary>The values in the order of the files (conv weights permuted, everything else as it is).</summary>
    public float[] ForFile(float[]? values = null) => Conv is null ? (values ?? Values) : Conv.Permute(values ?? Values, toFile: true);

    public float[] FromFile(float[] file) => Conv is null ? file : Conv.Permute(file, toFile: false);
}

/// <summary>
/// All parameters of the line reader for learning: the convolutions with their (not yet folded) batch normalization,
/// the LSTM layers and the output layer. The same object holds gradients (one per learnable tensor).
/// </summary>
public sealed class OcrWeights
{
    public OcrWeights(OcrShape shape)
    {
        Shape = shape;
        var convs = new List<ConvParameters>();
        var channels = 1;
        foreach (var block in shape.Blocks)
        {
            convs.Add(new ConvParameters(channels, block.Channels, 3, 3));
            channels = block.Channels;
        }

        convs.Add(new ConvParameters(channels, shape.Features, shape.Rows, 1));
        Convs = [.. convs];
        Lstm = new LstmParameters[shape.Layers][];
        var inputs = shape.Features;
        for (var l = 0; l < shape.Layers; l++)
        {
            Lstm[l] = [new LstmParameters(inputs, shape.Hidden), new LstmParameters(inputs, shape.Hidden)];
            inputs = 2 * shape.Hidden;
        }

        OutWeight = new float[shape.Classes * 2 * shape.Hidden];
        OutBias = new float[shape.Classes];
    }

    public OcrShape Shape { get; }

    /// <summary>The blocks' convolutions, then the collapsing one (kh = rows left, kw = 1, no padding).</summary>
    public ConvParameters[] Convs { get; }

    /// <summary>[layer][direction: 0 forward, 1 backward]</summary>
    public LstmParameters[][] Lstm { get; }

    /// <summary>[classes, 2 · hidden]</summary>
    public float[] OutWeight { get; }

    public float[] OutBias { get; }

    /// <summary>The tensors the optimizer learns, with the names of the training files.</summary>
    public IEnumerable<OcrTensor> Learnable()
    {
        for (var i = 0; i < Convs.Length; i++)
        {
            var c = Convs[i];
            yield return new($"conv.{i}.w", c.Weight, [c.Outputs, c.Inputs, c.Kh, c.Kw], c);
            yield return new($"bn.{i}.gamma", c.Gamma, [c.Outputs]);
            yield return new($"bn.{i}.beta", c.Beta, [c.Outputs]);
        }

        for (var l = 0; l < Lstm.Length; l++)
        {
            for (var d = 0; d < 2; d++)
            {
                var p = Lstm[l][d];
                var name = $"lstm.{l}.{(d == 0 ? "fw" : "bw")}";
                yield return new($"{name}.ih", p.InputWeight, [4 * p.Hidden, p.Inputs]);
                yield return new($"{name}.hh", p.HiddenWeight, [4 * p.Hidden, p.Hidden]);
                yield return new($"{name}.bih", p.InputBias, [4 * p.Hidden]);
                yield return new($"{name}.bhh", p.HiddenBias, [4 * p.Hidden]);
            }
        }

        yield return new("out.w", OutWeight, [Shape.Classes, 2 * Shape.Hidden]);
        yield return new("out.b", OutBias, [Shape.Classes]);
    }

    /// <summary>The running statistics of the batch normalizations.</summary>
    public IEnumerable<OcrTensor> Statistics()
    {
        for (var i = 0; i < Convs.Length; i++)
        {
            yield return new($"bn.{i}.mean", Convs[i].Mean, [Convs[i].Outputs]);
            yield return new($"bn.{i}.var", Convs[i].Variance, [Convs[i].Outputs]);
        }
    }

    /// <summary>
    /// New weights as PyTorch starts them (so learning from scratch behaves alike): convolutions and the output layer
    /// uniform in ±1/√fan-in, LSTM uniform in ±1/√hidden, normalization scale 1, shift 0, running variance 1.
    /// </summary>
    public static OcrWeights Create(OcrShape shape, int seed)
    {
        var weights = new OcrWeights(shape);
        var random = new Random(seed);
        void Uniform(float[] values, double bound)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)((random.NextDouble() * 2 - 1) * bound);
            }
        }

        foreach (var c in weights.Convs)
        {
            Uniform(c.Weight, 1 / Math.Sqrt(c.Features));
            c.Gamma.AsSpan().Fill(1);
            c.Variance.AsSpan().Fill(1);
        }

        foreach (var direction in weights.Lstm.SelectMany(l => l))
        {
            var bound = 1 / Math.Sqrt(direction.Hidden);
            Uniform(direction.InputWeight, bound);
            Uniform(direction.HiddenWeight, bound);
            Uniform(direction.InputBias, bound);
            Uniform(direction.HiddenBias, bound);
        }

        Uniform(weights.OutWeight, 1 / Math.Sqrt(2 * shape.Hidden));
        Uniform(weights.OutBias, 1 / Math.Sqrt(2 * shape.Hidden));
        return weights;
    }

    /// <summary>A copy (the same shape and values).</summary>
    public OcrWeights Copy()
    {
        var copy = new OcrWeights(Shape);
        foreach (var (mine, theirs) in Learnable().Zip(copy.Learnable()).Concat(Statistics().Zip(copy.Statistics())))
        {
            mine.Values.CopyTo(theirs.Values, 0);
        }

        return copy;
    }

    /// <summary>Sets every learnable tensor to zero (for gradients).</summary>
    public void Clear()
    {
        foreach (var t in Learnable())
        {
            Array.Clear(t.Values);
        }
    }
}
