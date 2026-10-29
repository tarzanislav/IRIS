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

/// <summary>How far a text is translated: sentences done of all.</summary>
public readonly record struct TranslationProgress(int Done, int Total);

/// <summary>A translated text: its segments (sentences and what lies between them) and the translations of the sentences.</summary>
public sealed record TextTranslation(IReadOnlyList<Segment> Segments, IReadOnlyList<SentenceTranslation> Sentences)
{
    /// <summary>The whole translated text with the layout of the source.</summary>
    public string Text => Segmenter.Join(Segments, [.. Sentences.Select(s => s.Target)]);
}

/// <summary>
/// Translates text with a Marian model file: tokenizer, model and search in one place. The main entry is
/// <see cref="TranslateText"/>; <see cref="RunModel"/> is the bare model for one sentence. A translator does not
/// change after it is loaded, so it can translate on several threads at once (a memory and a glossary passed to it
/// must not be changed meanwhile).
/// </summary>
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
    public static Translator Load(string path, bool int8 = false)
    {
        using var file = ModelFile.Load(path);
        return Load(file, int8);
    }

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

    /// <summary>
    /// Translates a text sentence by sentence; lines, paragraphs and spaces between sentences stay as they are.
    /// A sentence in the translation memory gets its remembered translation; glossary terms are hidden from the model
    /// behind placeholders and come back in their wanted form; a sentence too long for the model is translated in
    /// parts. Each sentence says where its translation came from and, in Note, what could not be done.
    /// Cancellation is checked before each sentence; progress counts translated sentences.
    /// </summary>
    public TextTranslation TranslateText(string text, int? beams = null, TranslationMemory? memory = null, Glossary? glossary = null,
        CancellationToken cancellation = default, IProgress<TranslationProgress>? progress = null)
    {
        var segments = Segmenter.Split(text);
        var sources = segments.Where(s => s.IsSentence).ToList();
        var sentences = new List<SentenceTranslation>(sources.Count);
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            sentences.Add(TranslateSentence(source.Text, beams, memory, glossary));
            progress?.Report(new TranslationProgress(sentences.Count, sources.Count));
        }

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
            if (marks.Count == 0)
            {
                var (plain, longNote) = ModelText(sentence, beams);
                return new SentenceTranslation(sentence, plain, TranslationOrigin.Model,
                    Join("the sentence has no free placeholder numbers; translated without the glossary", longNote));
            }

            var (protectedText, note) = ModelText(hidden, beams);
            if (Placeholders.TryRestore(protectedText, marks, out var restored))
            {
                return new SentenceTranslation(sentence, restored, TranslationOrigin.Glossary, note);
            }

            var (fallback, fallbackNote) = ModelText(sentence, beams);
            return new SentenceTranslation(sentence, fallback, TranslationOrigin.Model,
                Join("the glossary terms did not survive the translation; translated without the glossary", fallbackNote));
        }

        var (text, sentenceNote) = ModelText(sentence, beams);
        return new SentenceTranslation(sentence, text, TranslationOrigin.Model, sentenceNote);
    }

    /// <summary>
    /// The bare model: translates one sentence as it is (no memory, glossary or splitting); beams = 1 is greedy
    /// search, null = the beams of the model. A sentence longer than the positions of the model is refused.
    /// </summary>
    public TranslationResult RunModel(string sentence, int? beams = null)
    {
        var source = Tokenizer.Encode(sentence);
        var best = Generate(source, beams ?? Settings.NumBeams);
        return new TranslationResult(Tokenizer.Decode(best.Tokens), source, best.Tokens, best.Score);
    }

    /// <summary>Searches the translation of source ids with the given number of beams (1 = greedy).</summary>
    public Hypothesis Generate(IReadOnlyList<int> sourceIds, int beams)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(beams, 1);
        var encoded = Model.Encode(sourceIds);
        var session = Model.StartDecoding(encoded, beams);
        return beams == 1 ? Search.Greedy(session, Settings) : Search.Beam(session, Settings);
    }

    // the model's translation of a sentence; one too long for the model is cut into parts that fit – at ", ; :" or
    // between words, as near the middle as possible – whose translations are joined
    private (string Text, string? Note) ModelText(string sentence, int? beams)
    {
        if (Fits(sentence))
        {
            return (RunModel(sentence, beams).Text, null);
        }

        var parts = new List<string>();
        Cut(sentence, parts);
        return (string.Join(" ", parts.Select(p => RunModel(p, beams).Text)),
            $"the sentence was too long for the model and was translated in {parts.Count} parts");
    }

    private bool Fits(string text) => Tokenizer.Encode(text).Length <= Model.Config.MaxPositions;

    private void Cut(string text, List<string> parts)
    {
        if (Fits(text) || text.Length < 2)
        {
            parts.Add(text);
            return;
        }

        var middle = text.Length / 2;
        var at = Boundary(text, middle, c => c is ',' or ';' or ':');
        if (at < 0)
        {
            at = Boundary(text, middle, _ => true);
        }

        if (at < 0)
        {
            at = middle;
        }

        Cut(text[..at].Trim(), parts);
        Cut(text[at..].Trim(), parts);
    }

    // the place after a separator followed by a space nearest to the middle, or -1
    private static int Boundary(string text, int middle, Func<char, bool> separator)
    {
        var best = -1;
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (char.IsWhiteSpace(text[i]) && separator(text[i - 1]) && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle)))
            {
                best = i;
            }
        }

        return best;
    }

    private static string Join(string first, string? second) => second is null ? first : $"{first}; {second}";
}
