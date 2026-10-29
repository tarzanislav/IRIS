using System.Text.Json;
using Iris.Tokenization;

namespace Iris.Tests;

/// <summary>The own SentencePiece tokenizer gives exactly the pieces of SentencePiece itself (test data made by tools/reference).</summary>
public sealed class TokenizerTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "tokenizer", name);

    [Test]
    public void PiecesAreTheSameAsSentencePiece()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Data("expected.json")));
        var failures = new List<string>();
        var checkedTexts = 0;
        foreach (var model in expected.RootElement.EnumerateObject())
        {
            var tokenizer = new SentencePieceTokenizer(SentencePieceModel.Load(Data(model.Name + ".model")));
            foreach (var item in model.Value.EnumerateArray())
            {
                var text = item.GetProperty("text").GetString()!;
                var pieces = item.GetProperty("pieces").EnumerateArray().Select(p => p.GetString()!).ToList();
                var ids = item.GetProperty("ids").EnumerateArray().Select(p => p.GetInt32()).ToList();
                var decoded = item.GetProperty("decoded").GetString()!;
                var actual = tokenizer.Encode(text);
                checkedTexts++;
                if (!actual.Select(p => p.Text).SequenceEqual(pieces) || !actual.Select(p => p.Id).SequenceEqual(ids))
                {
                    failures.Add($"{model.Name} „{text}“: expected [{string.Join(" ", pieces)}], actual [{string.Join(" ", actual.Select(p => p.Text))}] " +
                                 $"ids [{string.Join(",", ids)}] / [{string.Join(",", actual.Select(p => p.Id))}]");
                }

                var back = tokenizer.Decode(ids);
                if (back != decoded)
                {
                    failures.Add($"{model.Name} decode „{text}“: expected „{decoded}“, actual „{back}“");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedTexts} differ:\n" + string.Join("\n", failures));
    }

    [Test]
    public void ModelFileIsRead()
    {
        var model = SentencePieceModel.Load(Data("nfkc.model"));
        Assert.True(model.Pieces.Count == 200 && model.ModelType == 1 && model.NormalizerName == "nmt_nfkc" && model.PrecompiledCharsmap.Length > 1000,
            $"pieces {model.Pieces.Count}, type {model.ModelType}, normalizer {model.NormalizerName}");
        Assert.True(model.Pieces[model.UnknownId].Type == PieceType.Unknown, "the unknown piece");
        Assert.True(SentencePieceModel.Load(Data("bytes.model")).ByteFallback, "byte fallback");
        Assert.Throws<FormatException>(() => SentencePieceModel.Parse([0x0A, 0xFF]), "a damaged file");
    }
}
