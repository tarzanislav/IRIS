using System.Text.Json.Nodes;

namespace Iris.Translation;

/// <summary>When beam search may stop (as "early_stopping" in Hugging Face transformers).</summary>
public enum EarlyStopping
{
    /// <summary>false: stop when no running beam can beat the worst finished one at the current length.</summary>
    Heuristic,

    /// <summary>true: stop as soon as there are as many finished hypotheses as beams.</summary>
    Always,

    /// <summary>"never": stop only when no running beam can improve, even with the longest possible length.</summary>
    Never,
}

/// <summary>How a translation is searched: beams, length, forbidden and forced tokens.</summary>
public sealed record GenerationSettings
{
    /// <summary>Beams of beam search; 1 = greedy.</summary>
    public int NumBeams { get; init; } = 4;

    /// <summary>The longest output, the decoder start token included.</summary>
    public int MaxLength { get; init; } = 512;

    /// <summary>Scores of finished hypotheses are divided by length^LengthPenalty.</summary>
    public float LengthPenalty { get; init; } = 1f;

    public EarlyStopping EarlyStopping { get; init; } = EarlyStopping.Heuristic;

    /// <summary>Tokens that are never generated (the padding token for Marian).</summary>
    public int[] BadTokens { get; init; } = [];

    /// <summary>Tokens forced at the last possible position (the end of sentence for Marian); empty = none.</summary>
    public int[] ForcedEosTokens { get; init; } = [];

    /// <summary>Normalize the log-probabilities again after forbidding or forcing tokens.</summary>
    public bool Renormalize { get; init; }

    public int EosId { get; init; }

    public int DecoderStartId { get; init; }
}

/// <summary>The shape and the special tokens of a Marian translation model (MarianMT of OPUS-MT).</summary>
public sealed record MarianConfig
{
    public required int DModel { get; init; }

    public required int EncoderLayers { get; init; }

    public required int DecoderLayers { get; init; }

    public required int EncoderHeads { get; init; }

    public required int DecoderHeads { get; init; }

    public required int EncoderFfn { get; init; }

    public required int DecoderFfn { get; init; }

    public required int VocabSize { get; init; }

    public required int MaxPositions { get; init; }

    /// <summary>"swish" (= SiLU), "gelu" or "relu".</summary>
    public string Activation { get; init; } = "swish";

    /// <summary>Token vectors are multiplied by √d_model.</summary>
    public bool ScaleEmbedding { get; init; } = true;

    public float LayerNormEpsilon { get; init; } = 1e-5f;

    public required int PadId { get; init; }

    public required int EosId { get; init; }

    public required int UnknownId { get; init; }

    public required int DecoderStartId { get; init; }

    /// <summary>Special tokens: they stay whole in the source text and are left out of translations.</summary>
    public IReadOnlyDictionary<string, int> SpecialTokens { get; init; } = new Dictionary<string, int>();

    public GenerationSettings Generation { get; init; } = new();

    /// <summary>Reads the "config" of an IRIS model file of kind "marian"; a damaged config is a FormatException.</summary>
    public static MarianConfig FromJson(JsonObject config)
    {
        try
        {
            return Parse(config);
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException($"Marian model: damaged config ({ex.Message})", ex);
        }
    }

    private static MarianConfig Parse(JsonObject config)
    {
        int Int(string key) => config[key]?.GetValue<int>() ?? throw new FormatException($"Marian model: config.{key} is missing");
        var specials = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (text, id) in config["special_tokens"] as JsonObject ?? [])
        {
            specials[text] = id!.GetValue<int>();
        }

        var eos = Int("eos_id");
        var start = Int("decoder_start_id");
        var generation = config["generation"] as JsonObject ?? [];
        var settings = new GenerationSettings
        {
            NumBeams = generation["num_beams"]?.GetValue<int>() ?? 4,
            MaxLength = generation["max_length"]?.GetValue<int>() ?? 512,
            LengthPenalty = generation["length_penalty"]?.GetValue<float>() ?? 1f,
            EarlyStopping = generation["early_stopping"] switch
            {
                null => EarlyStopping.Heuristic,
                JsonValue v when v.TryGetValue(out bool b) => b ? EarlyStopping.Always : EarlyStopping.Heuristic,
                JsonValue v when v.TryGetValue(out string? s) && s == "never" => EarlyStopping.Never,
                var other => throw new FormatException($"Marian model: early_stopping {other.ToJsonString()} is not supported"),
            },
            BadTokens = Ints(generation["bad_tokens"]),
            ForcedEosTokens = Ints(generation["forced_eos"]),
            Renormalize = generation["renormalize"]?.GetValue<bool>() ?? false,
            EosId = eos,
            DecoderStartId = start,
        };
        var result = new MarianConfig
        {
            DModel = Int("d_model"),
            EncoderLayers = Int("encoder_layers"),
            DecoderLayers = Int("decoder_layers"),
            EncoderHeads = Int("encoder_heads"),
            DecoderHeads = Int("decoder_heads"),
            EncoderFfn = Int("encoder_ffn"),
            DecoderFfn = Int("decoder_ffn"),
            VocabSize = Int("vocab_size"),
            MaxPositions = Int("max_positions"),
            Activation = config["activation"]?.GetValue<string>() ?? "swish",
            ScaleEmbedding = config["scale_embedding"]?.GetValue<bool>() ?? true,
            LayerNormEpsilon = config["layer_norm_eps"]?.GetValue<float>() ?? 1e-5f,
            PadId = Int("pad_id"),
            EosId = eos,
            UnknownId = Int("unk_id"),
            DecoderStartId = start,
            SpecialTokens = specials,
            Generation = settings,
        };
        result.Validate();
        return result;
    }

    // a damaged or unsupported config is refused when the model is read, not halfway through a translation
    private void Validate()
    {
        void Require(bool condition, string what)
        {
            if (!condition)
            {
                throw new FormatException($"Marian model: {what}");
            }
        }

        Require(DModel > 0 && EncoderLayers > 0 && DecoderLayers > 0 && EncoderFfn > 0 && DecoderFfn > 0, "sizes must be positive");
        Require(EncoderHeads > 0 && DecoderHeads > 0 && DModel % EncoderHeads == 0 && DModel % DecoderHeads == 0,
            "d_model must be divisible by the number of heads");
        Require(VocabSize > 0 && MaxPositions > 0, "the vocabulary and positions must not be empty");
        Require(Activation is "swish" or "silu" or "relu", $"activation {Activation} is not supported (swish, silu, relu)");
        bool Token(int id) => (uint)id < (uint)VocabSize;
        Require(Token(PadId) && Token(EosId) && Token(UnknownId) && Token(DecoderStartId) && SpecialTokens.Values.All(Token),
            "special tokens must lie in the vocabulary");
        var g = Generation;
        Require(g.BadTokens.All(Token) && g.ForcedEosTokens.All(Token), "bad and forced tokens must lie in the vocabulary");
        Require(g.NumBeams >= 1, "num_beams must be at least 1");
        Require(g.MaxLength >= 2 && g.MaxLength - 1 <= MaxPositions, $"max_length must be 2 to {MaxPositions + 1}");
        Require(float.IsFinite(g.LengthPenalty), "length_penalty must be a number");
    }

    private static int[] Ints(JsonNode? node) => node is JsonArray array ? [.. array.Select(n => n!.GetValue<int>())] : [];
}
