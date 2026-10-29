using Iris.Model;
using Iris.Tokenization;

namespace Iris.Translation;

/// <summary>A translation: the text, the ids the model saw and gave, and the score of the search.</summary>
public sealed record TranslationResult(string Text, int[] SourceIds, int[] OutputIds, float Score);

/// <summary>Translates text with a Marian model file: tokenizer, model and search in one place.</summary>
public sealed class Translator
{
    public Translator(MarianModel model, MarianTokenizer tokenizer)
    {
        Model = model;
        Tokenizer = tokenizer;
    }

    public MarianModel Model { get; }

    public MarianTokenizer Tokenizer { get; }

    public GenerationSettings Settings => Model.Config.Generation;

    /// <summary>Loads a Marian model file; with int8, float32 weights are turned into int8 as they are read.</summary>
    public static Translator Load(string path, bool int8 = false) => Load(ModelFile.Load(path), int8);

    public static Translator Load(ModelFile file, bool int8 = false)
    {
        var model = MarianModel.Load(file, int8);
        var c = model.Config;
        var tokenizer = new MarianTokenizer(SentencePieceModel.Parse(file.Blob("source.spm")), SentencePieceModel.Parse(file.Blob("target.spm")),
            MarianTokenizer.ReadVocabulary(file.Blob("vocab.json")), c.SpecialTokens, c.UnknownId, c.EosId);
        if (tokenizer.VocabularySize > c.VocabSize)
        {
            throw new FormatException($"Marian model: the vocabulary has {tokenizer.VocabularySize} ids, the model {c.VocabSize}");
        }

        return new Translator(model, tokenizer);
    }

    /// <summary>Translates a text; beams = 1 is greedy search, null = the beams of the model.</summary>
    public TranslationResult Translate(string text, int? beams = null)
    {
        var source = Tokenizer.Encode(text);
        var best = Generate(source, beams ?? Settings.NumBeams);
        return new TranslationResult(Tokenizer.Decode(best.Tokens), source, best.Tokens, best.Score);
    }

    /// <summary>
    /// Translates a longer text sentence by sentence; lines, paragraphs and spaces between sentences stay as they are.
    /// </summary>
    public string TranslateText(string text, int? beams = null)
    {
        var segments = Segmenter.Split(text);
        var translations = segments.Where(s => s.IsSentence).Select(s => Translate(s.Text, beams).Text).ToList();
        return Segmenter.Join(segments, translations);
    }

    /// <summary>Searches the translation of source ids with the given number of beams (1 = greedy).</summary>
    public Hypothesis Generate(IReadOnlyList<int> sourceIds, int beams)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(beams, 1);
        var encoded = Model.Encode(sourceIds);
        var session = Model.StartDecoding(encoded, beams);
        return beams == 1 ? Search.Greedy(session, Settings) : Search.Beam(session, Settings);
    }
}
