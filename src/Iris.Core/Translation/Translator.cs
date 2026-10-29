using Iris.Memory;
using Iris.Model;
using Iris.Tokenization;

namespace Iris.Translation;

/// <summary>A translation: the text, the ids the model saw and gave, and the score of the search.</summary>
public sealed record TranslationResult(string Text, int[] SourceIds, int[] OutputIds, float Score);

/// <summary>Where the translation of a sentence came from.</summary>
public enum TranslationOrigin
{
    /// <summary>The model.</summary>
    Model,

    /// <summary>The translation memory (a correction of the owner).</summary>
    Memory,

    /// <summary>The model, with glossary terms put in by the glossary.</summary>
    Glossary,
}

/// <summary>
/// One sentence of a translated text: the source, the translation and where it came from. Note says why the
/// glossary could not be used, if it could not.
/// </summary>
public sealed record SentenceTranslation(string Source, string Target, TranslationOrigin Origin, string? Note = null);

/// <summary>A translated text: its segments (sentences and what lies between them) and the translations of the sentences.</summary>
public sealed record TextTranslation(IReadOnlyList<Segment> Segments, IReadOnlyList<SentenceTranslation> Sentences)
{
    /// <summary>The whole translated text with the layout of the source.</summary>
    public string Text => Segmenter.Join(Segments, [.. Sentences.Select(s => s.Target)]);
}

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
    /// A sentence in the translation memory gets its remembered translation; glossary terms are hidden from the
    /// model behind placeholders and come back in their wanted form (when a placeholder is lost, the sentence is
    /// translated without the glossary and the note says so).
    /// </summary>
    public TextTranslation TranslateText(string text, int? beams = null, TranslationMemory? memory = null, Glossary? glossary = null)
    {
        var segments = Segmenter.Split(text);
        var sentences = segments.Where(s => s.IsSentence).Select(s => TranslateSentence(s.Text, beams, memory, glossary)).ToList();
        return new TextTranslation(segments, sentences);
    }

    /// <summary>Translates one sentence with the memory and the glossary (see <see cref="TranslateText"/>).</summary>
    public SentenceTranslation TranslateSentence(string sentence, int? beams = null, TranslationMemory? memory = null, Glossary? glossary = null)
    {
        if (memory is not null && memory.TryGet(sentence, out var remembered))
        {
            return new SentenceTranslation(sentence, remembered, TranslationOrigin.Memory);
        }

        var matches = glossary?.Find(sentence) ?? [];
        if (matches.Count > 0)
        {
            var (hidden, marks) = Placeholders.Protect(sentence, matches);
            if (marks.Count > 0 && Placeholders.TryRestore(Translate(hidden, beams).Text, marks, out var restored))
            {
                return new SentenceTranslation(sentence, restored, TranslationOrigin.Glossary);
            }

            return new SentenceTranslation(sentence, Translate(sentence, beams).Text, TranslationOrigin.Model,
                "the glossary terms did not survive the translation; translated without the glossary");
        }

        return new SentenceTranslation(sentence, Translate(sentence, beams).Text, TranslationOrigin.Model);
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
