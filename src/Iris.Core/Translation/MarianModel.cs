using Iris.Model;
using Iris.Numerics;

namespace Iris.Translation;

/// <summary>A dense layer: y = x · Wᵀ + b, W kept as [outputs, inputs] in float32 or int8.</summary>
internal sealed record Dense(Matrix Weight, float[] Bias)
{
    public float[] Apply(float[] x, int rows) => Weight.Linear(x, rows, Bias);
}

internal sealed record Norm(float[] Gain, float[] Bias);

internal sealed record Attention(Dense Query, Dense Key, Dense Value, Dense Output, Norm Norm);

internal sealed record FeedForward(Dense Up, Dense Down, Norm Norm);

internal sealed record EncoderLayer(Attention SelfAttention, FeedForward FeedForward);

internal sealed record DecoderLayer(Attention SelfAttention, Attention CrossAttention, FeedForward FeedForward);

/// <summary>A source sentence after the encoder: for every decoder layer, the keys and values of cross attention.</summary>
public sealed class EncodedSource
{
    internal EncodedSource(int length, float[][] keys, float[][] values)
    {
        Length = length;
        Keys = keys;
        Values = values;
    }

    /// <summary>Tokens of the source sentence (the end of sentence included).</summary>
    public int Length { get; }

    internal float[][] Keys { get; }

    internal float[][] Values { get; }
}

/// <summary>
/// The Marian transformer (MarianMT, the models of OPUS-MT): encoder and decoder with post-LayerNorm, sinusoidal
/// positions, token vectors scaled by √d_model and one shared vocabulary whose vectors are also the output layer.
/// Computes exactly what the reference (Hugging Face transformers) computes; the decoder keeps the keys and values of
/// earlier tokens, so each new token costs only its own step.
/// </summary>
public sealed class MarianModel
{
    private readonly Matrix _embed;
    private readonly float[] _outputBias;
    private readonly float[] _positions;
    private readonly EncoderLayer[] _encoder;
    private readonly DecoderLayer[] _decoder;
    private readonly float _embedScale;
    private readonly Action<Span<float>> _activation;

    private MarianModel(MarianConfig config, Matrix embed, float[] outputBias, EncoderLayer[] encoder, DecoderLayer[] decoder)
    {
        Config = config;
        _embed = embed;
        _outputBias = outputBias;
        _encoder = encoder;
        _decoder = decoder;
        _positions = Positions(config.MaxPositions, config.DModel);
        _embedScale = config.ScaleEmbedding ? (float)Math.Sqrt(config.DModel) : 1f;
        _activation = config.Activation switch
        {
            "swish" or "silu" => Kernels.Silu,
            "relu" => Kernels.Relu,
            var other => throw new FormatException($"Marian model: activation {other} is not supported"),
        };
    }

    public MarianConfig Config { get; }

    /// <summary>The weights are kept as int8 (one scale per row).</summary>
    public bool IsInt8 => _embed.IsInt8;

    /// <summary>
    /// Reads a model file of kind "marian" (tensor names: embed, out.bias, enc.N.*, dec.N.*). Weights may be stored
    /// as float32 or int8; with quantize, float32 weights become int8 as they are read.
    /// </summary>
    public static MarianModel Load(ModelFile file, bool quantize = false)
    {
        if (file.Kind != "marian")
        {
            throw new FormatException($"not a Marian model (kind \"{file.Kind}\")");
        }

        var c = MarianConfig.FromJson(file.Config);
        int d = c.DModel, v = c.VocabSize;

        Dense Dense(string name, int inputs, int outputs) =>
            new(file.Matrix(name + ".w", outputs, inputs, quantize), file.Floats(name + ".b", outputs));
        Norm Norm(string name) => new(file.Floats(name + ".g", d), file.Floats(name + ".b", d));
        Attention Attention(string name) =>
            new(Dense(name + ".q", d, d), Dense(name + ".k", d, d), Dense(name + ".v", d, d), Dense(name + ".o", d, d), Norm(name + ".ln"));
        FeedForward FeedForward(string name, int width) => new(Dense(name + ".1", d, width), Dense(name + ".2", width, d), Norm(name + ".ln"));

        var encoder = Enumerable.Range(0, c.EncoderLayers)
            .Select(i => new EncoderLayer(Attention($"enc.{i}.attn"), FeedForward($"enc.{i}.ffn", c.EncoderFfn))).ToArray();
        var decoder = Enumerable.Range(0, c.DecoderLayers)
            .Select(i => new DecoderLayer(Attention($"dec.{i}.self"), Attention($"dec.{i}.cross"), FeedForward($"dec.{i}.ffn", c.DecoderFfn))).ToArray();
        return new MarianModel(c, file.Matrix("embed", v, d, quantize), file.Floats("out.bias", v), encoder, decoder);
    }

    /// <summary>
    /// The sinusoidal positions of Marian: sin(pos / 10000^(2k/dim)) in the first half of the vector, cos of the
    /// same angles in the second half; computed in double and rounded to float, as the reference does.
    /// </summary>
    public static float[] Positions(int count, int dim)
    {
        var table = new float[count * dim];
        var sines = (dim + 1) / 2;
        for (var pos = 0; pos < count; pos++)
        {
            var row = table.AsSpan(pos * dim, dim);
            for (var k = 0; k < sines; k++)
            {
                row[k] = (float)Math.Sin(pos / Math.Pow(10000, 2.0 * k / dim));
            }

            for (var k = 0; k < dim / 2; k++)
            {
                row[sines + k] = (float)Math.Cos(pos / Math.Pow(10000, 2.0 * k / dim));
            }
        }

        return table;
    }

    /// <summary>Runs the encoder over the ids of a source sentence (as the tokenizer gives them, end of sentence included).</summary>
    public EncodedSource Encode(IReadOnlyList<int> ids)
    {
        int n = ids.Count, d = Config.DModel;
        if (n == 0 || n > Config.MaxPositions)
        {
            throw new ArgumentException($"a source sentence must have 1 to {Config.MaxPositions} tokens, it has {n}");
        }

        var x = new float[n * d];
        for (var t = 0; t < n; t++)
        {
            Embed(ids[t], t, x.AsSpan(t * d, d));
        }

        var scores = new float[n];
        var context = new float[n * d];
        foreach (var layer in _encoder)
        {
            var a = layer.SelfAttention;
            var q = a.Query.Apply(x, n);
            var k = a.Key.Apply(x, n);
            var v = a.Value.Apply(x, n);
            for (var t = 0; t < n; t++)
            {
                Attend(q.AsSpan(t * d, d), k, v, n, Config.EncoderHeads, context.AsSpan(t * d, d), scores);
            }

            Residual(x, a.Output.Apply(context, n), n, a.Norm);
            Residual(x, FeedForwardOf(layer.FeedForward, x, n), n, layer.FeedForward.Norm);
        }

        var keys = new float[_decoder.Length][];
        var values = new float[_decoder.Length][];
        for (var l = 0; l < _decoder.Length; l++)
        {
            keys[l] = _decoder[l].CrossAttention.Key.Apply(x, n);
            values[l] = _decoder[l].CrossAttention.Value.Apply(x, n);
        }

        return new EncodedSource(n, keys, values);
    }

    /// <summary>Starts decoding a source sentence with a number of hypotheses (rows) side by side.</summary>
    public IDecoderSession StartDecoding(EncodedSource source, int rows) => new MarianSession(this, source, rows);

    // one decoder step for every row: the logits of the next token, [rows, vocabulary], in the buffer of the session
    internal float[] Step(MarianSession s, ReadOnlySpan<int> tokens)
    {
        int rows = s.Rows, d = Config.DModel, t = s.Length;
        if (tokens.Length != rows)
        {
            throw new ArgumentException($"expected {rows} tokens, got {tokens.Length}");
        }

        if (t >= Config.MaxPositions)
        {
            throw new InvalidOperationException($"the translation is longer than {Config.MaxPositions} tokens");
        }

        s.Reserve(t + 1);
        var x = new float[rows * d];
        for (var r = 0; r < rows; r++)
        {
            Embed(tokens[r], t, x.AsSpan(r * d, d));
        }

        var scores = new float[Math.Max(t + 1, s.Source.Length)];
        var context = new float[rows * d];
        for (var l = 0; l < _decoder.Length; l++)
        {
            var layer = _decoder[l];

            var a = layer.SelfAttention;
            var q = a.Query.Apply(x, rows);
            var k = a.Key.Apply(x, rows);
            var v = a.Value.Apply(x, rows);
            for (var r = 0; r < rows; r++)
            {
                k.AsSpan(r * d, d).CopyTo(s.Keys[l][r].AsSpan(t * d));
                v.AsSpan(r * d, d).CopyTo(s.Values[l][r].AsSpan(t * d));
                Attend(q.AsSpan(r * d, d), s.Keys[l][r], s.Values[l][r], t + 1, Config.DecoderHeads, context.AsSpan(r * d, d), scores);
            }

            Residual(x, a.Output.Apply(context, rows), rows, a.Norm);

            var c = layer.CrossAttention;
            q = c.Query.Apply(x, rows);
            for (var r = 0; r < rows; r++)
            {
                Attend(q.AsSpan(r * d, d), s.Source.Keys[l], s.Source.Values[l], s.Source.Length, Config.DecoderHeads, context.AsSpan(r * d, d), scores);
            }

            Residual(x, c.Output.Apply(context, rows), rows, c.Norm);
            Residual(x, FeedForwardOf(layer.FeedForward, x, rows), rows, layer.FeedForward.Norm);
        }

        s.Length = t + 1;
        _embed.Linear(x, rows, _outputBias, s.Logits);
        return s.Logits;
    }

    // token vector × scale + position
    private void Embed(int id, int position, Span<float> output)
    {
        if ((uint)id >= (uint)Config.VocabSize)
        {
            throw new ArgumentException($"token id {id} is outside the vocabulary");
        }

        var d = Config.DModel;
        _embed.Row(id, output);
        var p = _positions.AsSpan(position * d, d);
        for (var i = 0; i < d; i++)
        {
            output[i] = output[i] * _embedScale + p[i];
        }
    }

    // x = LayerNorm(x + y), post-LayerNorm as in Marian
    private void Residual(float[] x, float[] y, int rows, Norm norm)
    {
        Kernels.Add(x, y);
        Kernels.LayerNorm(x, rows, Config.DModel, norm.Gain, norm.Bias, Config.LayerNormEpsilon);
    }

    private float[] FeedForwardOf(FeedForward f, float[] x, int rows)
    {
        var hidden = f.Up.Apply(x, rows);
        _activation(hidden);
        return f.Down.Apply(hidden, rows);
    }

    // attention of one query over `count` keys and values (rows of d_model), head by head:
    // softmax(q·k × head_dim^-½) · v
    private void Attend(ReadOnlySpan<float> query, ReadOnlySpan<float> keys, ReadOnlySpan<float> values, int count, int heads,
        Span<float> output, Span<float> scores)
    {
        var d = Config.DModel;
        var size = d / heads;
        var scale = (float)(1.0 / Math.Sqrt(size));
        var s = scores[..count];
        output.Clear();
        for (var h = 0; h < heads; h++)
        {
            var q = query.Slice(h * size, size);
            for (var j = 0; j < count; j++)
            {
                s[j] = Kernels.Dot(q, keys.Slice(j * d + h * size, size)) * scale;
            }

            Kernels.Softmax(s);
            var o = output.Slice(h * size, size);
            for (var j = 0; j < count; j++)
            {
                Kernels.AddScaled(o, values.Slice(j * d + h * size, size), s[j]);
            }
        }
    }
}

/// <summary>
/// Decoding of one source sentence: the hypotheses (rows) and the keys and values of their tokens so far. The
/// logits of a step stay valid until the next step (one buffer per session, so decoding does not allocate them anew).
/// </summary>
internal sealed class MarianSession : IDecoderSession
{
    private readonly MarianModel _model;
    private int _capacity;

    internal MarianSession(MarianModel model, EncodedSource source, int rows)
    {
        if (rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        _model = model;
        Source = source;
        Rows = rows;
        Logits = new float[rows * model.Config.VocabSize];
        var layers = model.Config.DecoderLayers;
        Keys = new float[layers][][];
        Values = new float[layers][][];
        for (var l = 0; l < layers; l++)
        {
            Keys[l] = new float[rows][];
            Values[l] = new float[rows][];
            for (var r = 0; r < rows; r++)
            {
                Keys[l][r] = [];
                Values[l][r] = [];
            }
        }
    }

    public int Rows { get; }

    public int VocabSize => _model.Config.VocabSize;

    /// <summary>Tokens decoded so far (the decoder start token included).</summary>
    public int Length { get; internal set; }

    internal EncodedSource Source { get; }

    internal float[] Logits { get; }

    // [layer][row] → [capacity, d_model]
    internal float[][][] Keys { get; }

    internal float[][][] Values { get; }

    public float[] Step(ReadOnlySpan<int> tokens) => _model.Step(this, tokens);

    /// <summary>Row r continues the hypothesis of row parents[r] (beam search): the cached keys and values follow.</summary>
    public void Reorder(ReadOnlySpan<int> parents)
    {
        if (parents.Length != Rows)
        {
            throw new ArgumentException($"expected {Rows} parents, got {parents.Length}");
        }

        var identity = true;
        for (var r = 0; r < Rows; r++)
        {
            identity &= parents[r] == r;
        }

        if (identity)
        {
            return;
        }

        // a parent continued by several rows lends its buffer to the first and is copied into buffers of rows
        // that no row continues; nothing new is allocated
        var used = Length * _model.Config.DModel;
        var continued = new bool[Rows];
        foreach (var p in parents)
        {
            continued[p] = true;
        }

        foreach (var cache in new[] { Keys, Values })
        {
            foreach (var layer in cache)
            {
                var old = (float[][])layer.Clone();
                var free = new Stack<float[]>(Enumerable.Range(0, Rows).Where(r => !continued[r]).Select(r => old[r]));
                var taken = new bool[Rows];
                for (var r = 0; r < Rows; r++)
                {
                    var p = parents[r];
                    if (!taken[p])
                    {
                        layer[r] = old[p];
                        taken[p] = true;
                    }
                    else
                    {
                        var copy = free.Pop();
                        old[p].AsSpan(0, used).CopyTo(copy);
                        layer[r] = copy;
                    }
                }
            }
        }
    }

    // room for `length` tokens in every cache
    internal void Reserve(int length)
    {
        if (length <= _capacity)
        {
            return;
        }

        var d = _model.Config.DModel;
        var capacity = Math.Min(Math.Max(Math.Max(16, _capacity * 2), length), _model.Config.MaxPositions);
        var used = Length * d;
        foreach (var cache in new[] { Keys, Values })
        {
            foreach (var layer in cache)
            {
                for (var r = 0; r < Rows; r++)
                {
                    var grown = new float[capacity * d];
                    layer[r].AsSpan(0, used).CopyTo(grown);
                    layer[r] = grown;
                }
            }
        }

        _capacity = capacity;
    }
}
