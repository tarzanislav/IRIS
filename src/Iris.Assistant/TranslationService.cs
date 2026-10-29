using System.Collections.Concurrent;
using Iris.Assistant.Packs;
using Iris.Memory;
using Iris.Translation;

namespace Iris.Assistant;

/// <summary>A translated text of the application: the translation, its sentences and the packs it went through.</summary>
public sealed record AppTranslation(string Text, IReadOnlyList<SentenceTranslation> Sentences, IReadOnlyList<LanguagePair> Route)
{
    /// <summary>True when the text went through English because no direct pack is installed.</summary>
    public bool ThroughPivot => Route.Count > 1;
}

/// <summary>
/// Translates texts with the installed language packs, the translation memory and the glossary of each direction.
/// A model is loaded the first time its pair is needed and stays loaded; one service serves the whole application and
/// may be used from several threads. A sentence the owner corrected for this direction is taken from the memory first;
/// without a direct pack the sentence goes through English, each half with its own glossary.
/// </summary>
public sealed class TranslationService(PackStore packs, Func<LanguagePair, TranslationMemory> memoryOf, Func<LanguagePair, Glossary> glossaryOf)
{
    private readonly ConcurrentDictionary<LanguagePair, Lazy<Translator>> _translators = new();
    private readonly ConcurrentDictionary<LanguagePair, TranslationMemory> _memories = new();
    private readonly ConcurrentDictionary<LanguagePair, Glossary> _glossaries = new();

    /// <summary>The service with the files of this computer (IrisPaths): models, memories and glossaries.</summary>
    public static TranslationService ForThisComputer() => new(
        new PackStore(IrisPaths.Models),
        pair => TranslationMemory.Open(IrisPaths.Memory(pair.ToString())),
        pair => File.Exists(IrisPaths.Glossary(pair.ToString())) ? Glossary.Load(IrisPaths.Glossary(pair.ToString())) : new Glossary());

    public PackStore Packs { get; } = packs;

    /// <summary>The translation memory of a direction (the corrections of the owner).</summary>
    public TranslationMemory MemoryFor(LanguagePair pair) => _memories.GetOrAdd(pair, memoryOf);

    /// <summary>The glossary of names and titles of a direction.</summary>
    public Glossary GlossaryFor(LanguagePair pair) => _glossaries.GetOrAdd(pair, glossaryOf);

    /// <summary>Whether a text can be translated from one language to another with the installed packs.</summary>
    public bool CanTranslate(string source, string target) => Packs.Route(source, target) is not null;

    /// <summary>
    /// Translates a text; beams = 1 is greedy (quick), null = the beams of the model (best). Sentences, lines and
    /// paragraphs keep their places. Cancellation is checked before each sentence.
    /// </summary>
    public AppTranslation Translate(string text, string source, string target, int? beams = null,
        CancellationToken cancellation = default, IProgress<TranslationProgress>? progress = null)
    {
        var route = Packs.Route(source, target)
            ?? throw new InvalidOperationException($"no language pack translates {source} → {target}");
        var direct = new LanguagePair(source, target);
        var memory = MemoryFor(direct);
        var segments = Segmenter.Split(text);
        var sentences = segments.Where(s => s.IsSentence).ToList();
        var results = new List<SentenceTranslation>(sentences.Count);
        foreach (var sentence in sentences)
        {
            cancellation.ThrowIfCancellationRequested();
            results.Add(TranslateSentence(sentence.Text, route, memory, beams));
            progress?.Report(new TranslationProgress(results.Count, sentences.Count));
        }

        return new AppTranslation(Segmenter.Join(segments, [.. results.Select(r => r.Target)]), results, route);
    }

    /// <summary>Loads the model of a pair now (e.g. while the window opens) instead of at the first translation.</summary>
    public void Preload(LanguagePair pair) => _ = TranslatorOf(pair);

    /// <summary>Forgets the memories and glossaries read so far (after the user deleted their data).</summary>
    public void ForgetUserData()
    {
        _memories.Clear();
        _glossaries.Clear();
    }

    /// <summary>Forgets a loaded model, e.g. after its pack was removed or replaced.</summary>
    public void Unload(LanguagePair pair) => _translators.TryRemove(pair, out _);

    private SentenceTranslation TranslateSentence(string sentence, IReadOnlyList<LanguagePair> route, TranslationMemory memory, int? beams)
    {
        if (memory.TryGet(sentence, out var remembered))
        {
            return new SentenceTranslation(sentence, remembered, TranslationOrigin.Memory);
        }

        if (route.Count == 1)
        {
            return TranslatorOf(route[0]).TranslateSentence(sentence, beams, null, GlossaryFor(route[0]));
        }

        var middle = TranslatorOf(route[0]).TranslateSentence(sentence, beams, null, GlossaryFor(route[0]));
        var last = TranslatorOf(route[1]).TranslateSentence(middle.Target, beams, null, GlossaryFor(route[1]));
        var notes = string.Join("; ", new[] { "translated through English", middle.Note, last.Note }.OfType<string>());
        var origin = middle.Origin == TranslationOrigin.Glossary || last.Origin == TranslationOrigin.Glossary
            ? TranslationOrigin.Glossary
            : TranslationOrigin.Model;
        return new SentenceTranslation(sentence, last.Target, origin, notes);
    }

    private Translator TranslatorOf(LanguagePair pair) =>
        _translators.GetOrAdd(pair, p => new Lazy<Translator>(() => Translator.Load(Packs.PathOf(p)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
