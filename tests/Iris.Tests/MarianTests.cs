using System.Text;
using System.Text.Json.Nodes;
using Iris.Model;
using Iris.Tokenization;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>
/// The Marian core on its own: positions, the decoder cache, search and tokenizer. Whether it computes exactly what
/// the reference computes is checked in CI against Hugging Face transformers (tools/reference, iris verify).
/// </summary>
public static class MarianTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "tokenizer", name);

    [Test]
    public static void PositionsAreSinesThenCosines()
    {
        var table = MarianModel.Positions(3, 8);
        Assert.Sequence([0f, 0, 0, 0, 1, 1, 1, 1], table[..8], "position 0");
        Assert.Near(Math.Sin(1), table[8], 1e-7, "sin of position 1, k 0");
        Assert.Near(Math.Sin(2 / Math.Pow(10000, 2.0 / 8)), table[17], 1e-7, "sin of position 2, k 1");
        Assert.Near(Math.Cos(2 / Math.Pow(10000, 6.0 / 8)), table[16 + 7], 1e-7, "cos of position 2, k 3");
        Assert.Equal(15, MarianModel.Positions(1, 15).Length, "odd width");
    }

    [Test]
    public static void ReorderedRowsContinueTheirParents()
    {
        using var tiny = TinyModel.Create();
        var model = MarianModel.Load(ModelFile.Load(tiny.Path));
        var source = model.Encode([5, 9, 7, 0]);
        var start = model.Config.DecoderStartId;

        // two rows with different histories, then swapped
        var both = model.StartDecoding(source, 2);
        both.Step([start, start]);
        both.Step([11, 12]);
        both.Reorder([1, 1]);
        var after = both.Step([13, 14]);

        var single = model.StartDecoding(source, 1);
        single.Step([start]);
        single.Step([12]);
        var expected0 = single.Step([13]);
        single = model.StartDecoding(source, 1);
        single.Step([start]);
        single.Step([12]);
        var expected1 = single.Step([14]);

        var vocab = model.Config.VocabSize;
        Assert.Sequence(expected0, after[..vocab], "row 0 continues the history of row 1");
        Assert.Sequence(expected1, after[vocab..], "row 1 continues its own history");
    }

    [Test]
    public static void BeamFindsWhatGreedyMisses()
    {
        // tokens: 0 end, 1 A, 2 B, 3 start; A is likelier at first, but only B leads to a likely end
        var settings = new GenerationSettings { NumBeams = 2, MaxLength = 20, EosId = 0, DecoderStartId = 3, BadTokens = [3], ForcedEosTokens = [0] };
        var greedy = Search.Greedy(new ChainSession(1), settings);
        Assert.Sequence([.. Enumerable.Repeat(1, 18), 0], greedy.Tokens, "greedy follows A until the forced end");

        var beam = Search.Beam(new ChainSession(2), settings);
        Assert.Sequence([2, 0], beam.Tokens, "beam search finds B and the end");
        Assert.Near((Math.Log(0.399) + Math.Log(0.9)) / 2, beam.Score, 1e-5, "score = log-probability / length");
    }

    [Test]
    public static void TopKeepsTheOrderOfEqualValues()
    {
        Assert.Sequence([3, 1, 4, 0], Search.Top([1f, 2, 0, 5, 2], 4), "largest first, equal values in order");
        Assert.Sequence([1, 0], Search.Top([1f, 2], 3), "fewer values than asked");
    }

    [Test]
    public static void MarianTokenizerFollowsTheReferenceRules()
    {
        var spm = SentencePieceModel.Load(Data("nfkc.model"));
        var vocabulary = new Dictionary<string, int> { ["</s>"] = 0, ["<unk>"] = 1 };
        foreach (var piece in spm.Pieces.Where(p => p.Type == PieceType.Normal))
        {
            vocabulary.TryAdd(piece.Text, vocabulary.Count);
        }

        vocabulary[">>slk<<"] = vocabulary.Count;
        var pad = vocabulary["<pad>"] = vocabulary.Count;
        var specials = new Dictionary<string, int> { ["</s>"] = 0, ["<unk>"] = 1, ["<pad>"] = pad };
        var tokenizer = new MarianTokenizer(spm, spm, vocabulary, specials, 1, 0);
        var plain = new SentencePieceTokenizer(spm);

        const string text = "the film premiere is tonight.";
        var ids = tokenizer.Encode(text);
        Assert.Equal(0, ids[^1], "the end of sentence");
        Assert.True(!ids.Contains(1), "the test text has no unknown pieces");
        Assert.Sequence(plain.Encode(text).Select(p => vocabulary.GetValueOrDefault(p.Text, 1)), ids[..^1], "pieces → vocabulary ids");
        Assert.Equal(text, tokenizer.Decode(ids), "back to text without the end of sentence");
        Assert.Equal("he film", tokenizer.Decode(tokenizer.Encode("The film")), "an unknown piece is left out of the text, as the reference does");

        var withSpecial = tokenizer.Encode("film</s>night <pad>");
        Assert.True(withSpecial.Count(id => id == 0) == 2 && withSpecial[^2] == pad, $"special tokens stay whole: {string.Join(' ', withSpecial)}");
        Assert.Equal(vocabulary[">>slk<<"], tokenizer.Encode(">>slk<< film")[0], "language code");
        Assert.Equal(1, tokenizer.Encode(">>xx<< film")[0], "unknown language code");
        Assert.Sequence([0], tokenizer.Encode(""), "empty text");
        Assert.Equal("film", tokenizer.Decode([1, .. tokenizer.Encode("film"), pad]), "special tokens are left out of text");
    }

    [Test]
    public static void TranslatorRunsOnAModelFile()
    {
        using var tiny = TinyModel.Create();
        var translator = Translator.Load(tiny.Path);
        var greedy = translator.Translate("The film premiere is tonight.", beams: 1);
        var again = translator.Translate("The film premiere is tonight.", beams: 1);
        Assert.Sequence(greedy.OutputIds, again.OutputIds, "the same text gives the same translation");
        Assert.True(greedy.OutputIds.Length is > 0 and <= 7, $"at most max_length − 1 tokens: {greedy.OutputIds.Length}");
        Assert.True(!greedy.OutputIds.Contains(translator.Model.Config.PadId), "padding is never generated");
        var beam = translator.Translate("The film premiere is tonight.");
        Assert.True(beam.OutputIds.Length is > 0 and <= 7 && !beam.OutputIds.Contains(translator.Model.Config.PadId), "beam search gives a result");
        Assert.Throws<FormatException>(() => MarianModel.Load(ModelFile.Load(tiny.WriteOther())), "a file of another kind");
    }

    // a Markov chain over 4 tokens: the next token depends only on the last one
    private sealed class ChainSession(int rows) : IDecoderSession
    {
        private static readonly double[][] Next =
        [
            [0.97, 0.01, 0.01, 0.01], // after the end
            [0.45, 0.55, 1e-9, 1e-9], // after A
            [0.9, 0.1, 1e-9, 1e-9], // after B
            [0.001, 0.6, 0.399, 1e-9], // after the start
        ];

        public int Rows => rows;

        public int VocabSize => 4;

        public float[] Step(ReadOnlySpan<int> tokens)
        {
            var logits = new float[rows * 4];
            for (var r = 0; r < rows; r++)
            {
                for (var v = 0; v < 4; v++)
                {
                    logits[r * 4 + v] = (float)Math.Log(Next[tokens[r]][v]);
                }
            }

            return logits;
        }

        public void Reorder(ReadOnlySpan<int> parents)
        {
        }
    }
}

/// <summary>A small random Marian model file (with the SentencePiece test model as tokenizer), deleted after the test.</summary>
internal sealed class TinyModel : IDisposable
{
    private TinyModel(string path) => Path = path;

    public string Path { get; }

    public static TinyModel Create(int d = 16, int layers = 2, int heads = 4, int ffn = 32)
    {
        var spmPath = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "tokenizer", "nfkc.model");
        var spm = SentencePieceModel.Load(spmPath);
        var vocabulary = new JsonObject { ["</s>"] = 0, ["<unk>"] = 1 };
        foreach (var piece in spm.Pieces.Where(p => p.Type == PieceType.Normal))
        {
            if (!vocabulary.ContainsKey(piece.Text))
            {
                vocabulary[piece.Text] = vocabulary.Count;
            }
        }

        var pad = vocabulary.Count;
        vocabulary["<pad>"] = pad;
        var v = pad + 1;
        var random = new Random(42);
        var tensors = new List<(string, int[], float[])>();
        void Add(string name, float scale, params int[] shape)
        {
            var values = new float[shape.Aggregate(1, (a, b) => a * b)];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)((random.NextDouble() * 2 - 1) * scale);
            }

            tensors.Add((name, shape, values));
        }

        Add("embed", 0.5f, v, d);
        Add("out.bias", 1f, v);
        foreach (var (side, blocks) in new[] { ("enc", new[] { "attn" }), ("dec", new[] { "self", "cross" }) })
        {
            for (var l = 0; l < layers; l++)
            {
                foreach (var b in blocks)
                {
                    foreach (var p in "qkvo")
                    {
                        Add($"{side}.{l}.{b}.{p}.w", 0.3f, d, d);
                        Add($"{side}.{l}.{b}.{p}.b", 0.1f, d);
                    }

                    Add($"{side}.{l}.{b}.ln.g", 1f, d);
                    Add($"{side}.{l}.{b}.ln.b", 0.1f, d);
                }

                Add($"{side}.{l}.ffn.1.w", 0.3f, ffn, d);
                Add($"{side}.{l}.ffn.1.b", 0.1f, ffn);
                Add($"{side}.{l}.ffn.2.w", 0.3f, d, ffn);
                Add($"{side}.{l}.ffn.2.b", 0.1f, d);
                Add($"{side}.{l}.ffn.ln.g", 1f, d);
                Add($"{side}.{l}.ffn.ln.b", 0.1f, d);
            }
        }

        var config = new JsonObject
        {
            ["d_model"] = d, ["encoder_layers"] = layers, ["decoder_layers"] = layers, ["encoder_heads"] = heads,
            ["decoder_heads"] = heads, ["encoder_ffn"] = ffn, ["decoder_ffn"] = ffn, ["vocab_size"] = v, ["max_positions"] = 32,
            ["activation"] = "swish", ["scale_embedding"] = true, ["pad_id"] = pad, ["eos_id"] = 0, ["unk_id"] = 1,
            ["decoder_start_id"] = pad, ["special_tokens"] = new JsonObject { ["</s>"] = 0, ["<unk>"] = 1, ["<pad>"] = pad },
            ["generation"] = new JsonObject
            {
                ["num_beams"] = 3, ["max_length"] = 8, ["bad_tokens"] = new JsonArray(pad), ["forced_eos"] = new JsonArray(0),
            },
        };
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"iris-tiny-{Guid.NewGuid():N}.iris");
        var spmBytes = File.ReadAllBytes(spmPath);
        ModelFile.Write(path, "marian", config, new JsonObject { ["source"] = "random test model" }, tensors,
            [("source.spm", spmBytes), ("target.spm", spmBytes), ("vocab.json", Encoding.UTF8.GetBytes(vocabulary.ToJsonString()))]);
        return new TinyModel(path);
    }

    public string WriteOther()
    {
        var other = Path + ".other";
        ModelFile.Write(other, "other", [], [], [], []);
        return other;
    }

    public void Dispose()
    {
        File.Delete(Path);
        File.Delete(Path + ".other");
    }
}
