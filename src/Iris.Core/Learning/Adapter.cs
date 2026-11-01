using System.Text.Json.Nodes;
using Iris.Model;

namespace Iris.Learning;

/// <summary>
/// The learned part of one dense layer: two small matrices whose product is added to the frozen weights,
/// W' = W + scale · B·A, with A [rank, inputs] and B [outputs, rank] (a low-rank adapter).
/// </summary>
public sealed class LowRank
{
    internal LowRank(int inputs, int outputs, int rank, float[] a, float[] b)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(a.Length, rank * inputs);
        ArgumentOutOfRangeException.ThrowIfNotEqual(b.Length, outputs * rank);
        Inputs = inputs;
        Outputs = outputs;
        Rank = rank;
        A = a;
        B = b;
    }

    public int Inputs { get; }

    public int Outputs { get; }

    public int Rank { get; }

    /// <summary>[rank, inputs]: what the layer looks at.</summary>
    internal float[] A { get; }

    /// <summary>[outputs, rank]: how it changes the output; zero at the start, so a new adapter changes nothing.</summary>
    internal float[] B { get; }
}

/// <summary>
/// What IRIS learned for one model: a low-rank part for chosen dense layers (by default the queries and values of
/// every attention). The base model is never changed; the adapter is a small file of its own (kind "iris-adapter")
/// that records which model it belongs to, so it is used only with that model.
/// </summary>
public sealed class Adapter
{
    public const string Kind = "iris-adapter";

    /// <summary>The layers an adapter learns by default: queries and values of every attention.</summary>
    public static readonly IReadOnlyList<string> DefaultTargets = ["q", "v"];

    private Adapter(int rank, float alpha, string baseSha256, IReadOnlyDictionary<string, LowRank> layers, JsonObject provenance)
    {
        Rank = rank;
        Alpha = alpha;
        BaseSha256 = baseSha256;
        Layers = layers;
        Provenance = provenance;
    }

    public int Rank { get; }

    public float Alpha { get; }

    /// <summary>The factor of B·A: alpha / rank.</summary>
    public float Scale => Alpha / Rank;

    /// <summary>The SHA-256 of the model file the adapter was learned on.</summary>
    public string BaseSha256 { get; }

    /// <summary>Dense layer name (e.g. "dec.0.self.q") → its learned part.</summary>
    public IReadOnlyDictionary<string, LowRank> Layers { get; }

    /// <summary>How the adapter came to be (data, steps, quality) – free JSON.</summary>
    public JsonObject Provenance { get; }

    /// <summary>The number of learned values.</summary>
    public long Parameters => Layers.Values.Sum(l => (long)l.A.Length + l.B.Length);

    /// <summary>
    /// A new adapter for the dense layers of a model whose last part of the name (q, k, v, o, 1, 2) is in targets.
    /// A starts small and random (seeded, so learning can be repeated), B at zero.
    /// </summary>
    public static Adapter Create(Translation.MarianModel model, string baseSha256, int rank = 8, float alpha = 16,
        IEnumerable<string>? targets = null, int seed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        var wanted = (targets ?? DefaultTargets).ToHashSet(StringComparer.Ordinal);
        var random = new Random(seed);
        var layers = new SortedDictionary<string, LowRank>(StringComparer.Ordinal);
        foreach (var (name, dense) in model.DenseLayers())
        {
            if (!wanted.Contains(name[(name.LastIndexOf('.') + 1)..]))
            {
                continue;
            }

            int inputs = dense.Weight.Columns, outputs = dense.Weight.Rows;
            var a = new float[rank * inputs];
            var bound = 1.0 / Math.Sqrt(inputs);
            for (var i = 0; i < a.Length; i++)
            {
                a[i] = (float)((random.NextDouble() * 2 - 1) * bound);
            }

            layers[name] = new LowRank(inputs, outputs, rank, a, new float[outputs * rank]);
        }

        if (layers.Count == 0)
        {
            throw new ArgumentException("the adapter would learn no layer");
        }

        return new Adapter(rank, alpha, baseSha256, layers, []);
    }

    /// <summary>Writes the adapter to a file (kind "iris-adapter").</summary>
    public void Save(string path)
    {
        var config = new JsonObject
        {
            ["rank"] = Rank,
            ["alpha"] = Alpha,
            ["base_sha256"] = BaseSha256,
            ["layers"] = new JsonArray([.. Layers.Keys.Select(k => (JsonNode)k)]),
        };
        var tensors = new List<TensorData>();
        foreach (var (name, layer) in Layers)
        {
            tensors.Add(TensorData.F32(name + ".lora_a", [layer.Rank, layer.Inputs], layer.A));
            tensors.Add(TensorData.F32(name + ".lora_b", [layer.Outputs, layer.Rank], layer.B));
        }

        var temporary = path + ".part";
        ModelFile.Write(temporary, Kind, config, Provenance, tensors, []);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads an adapter file.</summary>
    public static Adapter Load(string path)
    {
        using var file = ModelFile.Load(path);
        if (file.Kind != Kind)
        {
            throw new FormatException($"not an IRIS adapter (kind \"{file.Kind}\")");
        }

        var config = file.Config;
        var rank = config["rank"]?.GetValue<int>() ?? throw new FormatException("adapter: rank is missing");
        var alpha = config["alpha"]?.GetValue<float>() ?? throw new FormatException("adapter: alpha is missing");
        var sha = config["base_sha256"]?.GetValue<string>() ?? throw new FormatException("adapter: base_sha256 is missing");
        if (rank < 1 || !float.IsFinite(alpha))
        {
            throw new FormatException("adapter: rank or alpha is wrong");
        }

        var layers = new SortedDictionary<string, LowRank>(StringComparer.Ordinal);
        foreach (var node in config["layers"]?.AsArray() ?? throw new FormatException("adapter: layers are missing"))
        {
            var name = node?.GetValue<string>() ?? throw new FormatException("adapter: a layer has no name");
            var a = file.Tensors.FirstOrDefault(t => t.Name == name + ".lora_a") ?? throw new FormatException($"adapter: {name}.lora_a is missing");
            if (a.Shape.Length != 2 || a.Shape[0] != rank)
            {
                throw new FormatException($"adapter: {name}.lora_a has a wrong shape");
            }

            var inputs = a.Shape[1];
            var b = file.Tensors.FirstOrDefault(t => t.Name == name + ".lora_b") ?? throw new FormatException($"adapter: {name}.lora_b is missing");
            if (b.Shape.Length != 2 || b.Shape[1] != rank)
            {
                throw new FormatException($"adapter: {name}.lora_b has a wrong shape");
            }

            var outputs = b.Shape[0];
            layers[name] = new LowRank(inputs, outputs, rank, file.Floats(name + ".lora_a", rank, inputs), file.Floats(name + ".lora_b", outputs, rank));
        }

        return new Adapter(rank, alpha, sha, layers, (JsonObject)file.Provenance.DeepClone());
    }

    /// <summary>The same adapter with other provenance (after learning).</summary>
    public Adapter With(JsonObject provenance) => new(Rank, Alpha, BaseSha256, Layers, provenance);

    /// <summary>A copy whose values can be changed without changing this adapter.</summary>
    public Adapter Copy() => new(Rank, Alpha, BaseSha256,
        Layers.ToDictionary(p => p.Key, p => new LowRank(p.Value.Inputs, p.Value.Outputs, p.Value.Rank, [.. p.Value.A], [.. p.Value.B]), StringComparer.Ordinal),
        (JsonObject)Provenance.DeepClone());
}
