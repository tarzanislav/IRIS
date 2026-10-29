using System.Diagnostics;
using System.Globalization;
using System.Text;
using Iris.Cli;
using Iris.Evaluation;
using Iris.Memory;
using Iris.Model;
using Iris.Tokenization;
using Iris.Translation;

// iris – the command line of the IRIS translator. Exit codes: 0 done, 1 error or a failed gate, 2 wrong use.
Console.OutputEncoding = Encoding.UTF8;
try
{
    return args switch
    {
        ["tokenize", var model, .. var words] when words.Length > 0 => Tokenize(model, string.Join(' ', words)),
        ["info", var model] => Info(model),
        ["translate", var model, .. var rest] => Translate(model, Options.Parse(rest, ["--greedy", "--ids", "--int8"], ["--beams", "--memory", "--glossary"])),
        ["verify", var model, var reference, .. var rest] => Verify(model, reference,
            Options.Parse(rest, ["--no-greedy", "--no-beam", "--int8"], ["--min-greedy", "--min-beam", "--min-tokenizer", "--limit"])),
        ["bench", var model, .. var rest] => Bench(model, Options.Parse(rest, ["--int8"], ["--runs", "--max-seconds"])),
        ["quantize", var source, var target] => Quantize(source, target),
        ["score", var model, var source, var references, .. var rest] => Score(model, source, references,
            Options.Parse(rest, ["--greedy", "--int8"], ["--beams", "--limit", "--out"])),
        ["chrf", var hypotheses, var references] => ChrfOfFiles(hypotheses, references),
        ["remember", var memoryPath, var source, var target] => Remember(memoryPath, source, target),
        _ => Usage(),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"iris: {ex.Message} (iris without arguments shows how to use it)");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"iris: {ex.Message}");
    return 1;
}

static int Tokenize(string modelPath, string text)
{
    var tokenizer = new SentencePieceTokenizer(SentencePieceModel.Load(modelPath));
    var pieces = tokenizer.Encode(text);
    Console.WriteLine(string.Join(' ', pieces.Select(p => p.Text)));
    Console.WriteLine(string.Join(' ', pieces.Select(p => p.Id)));
    return 0;
}

static int Info(string path)
{
    using var model = ModelFile.Load(path);
    Console.WriteLine($"kind: {model.Kind}");
    Console.WriteLine($"config: {model.Config.ToJsonString()}");
    Console.WriteLine($"provenance: {model.Provenance.ToJsonString()}");
    Console.WriteLine($"tensors: {model.Tensors.Count}, parameters: {model.Tensors.Sum(t => t.Count):N0}");
    return 0;
}

static Translator Load(string modelPath, bool int8, TextWriter log)
{
    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath, int8);
    log.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.00} s{(translator.Model.IsInt8 ? " (int8)" : "")}; " +
                  $"{Environment.ProcessorCount} processors");
    return translator;
}

// the text of the words, or every line of the input when there are none
static int Translate(string modelPath, Options options)
{
    int? beams = options.Has("--greedy") ? 1 : options.Int("--beams", 1);
    var memory = options.Value("--memory") is { } memoryPath ? TranslationMemory.Open(memoryPath) : null;
    var glossary = options.Value("--glossary") is { } glossaryPath ? Glossary.Load(glossaryPath) : null;
    var showIds = options.Has("--ids");
    if (showIds && (memory is not null || glossary is not null))
    {
        throw new UsageException("--ids shows the bare model and cannot be used with --memory or --glossary");
    }

    var translator = Load(modelPath, options.Has("--int8"), Console.Error);
    void One(string text)
    {
        if (showIds)
        {
            foreach (var sentence in Segmenter.Sentences(text))
            {
                var result = translator.RunModel(sentence, beams);
                Console.WriteLine(result.Text);
                Console.Error.WriteLine($"  source: {string.Join(' ', result.SourceIds)}");
                Console.Error.WriteLine($"  output: {string.Join(' ', result.OutputIds)} (score {result.Score.ToString("0.####", CultureInfo.InvariantCulture)})");
            }

            return;
        }

        var translation = translator.TranslateText(text, beams, memory, glossary);
        Console.WriteLine(translation.Text);
        foreach (var sentence in translation.Sentences.Where(s => s.Origin != TranslationOrigin.Model || s.Note is not null))
        {
            Console.Error.WriteLine($"  {sentence.Origin.ToString().ToLowerInvariant()}: {sentence.Source}{(sentence.Note is null ? "" : $" ({sentence.Note})")}");
        }
    }

    if (options.Words.Count > 0)
    {
        One(string.Join(' ', options.Words));
        return 0;
    }

    Console.InputEncoding = Encoding.UTF8;
    while (Console.ReadLine() is { } line)
    {
        if (line.Length == 0)
        {
            Console.WriteLine();
            continue;
        }

        One(line);
    }

    return 0;
}

static int Verify(string modelPath, string referencePath, Options options)
{
    options.NoWords();
    var minGreedy = options.Number("--min-greedy") ?? 0.99;
    var minBeam = options.Number("--min-beam") ?? 0.95;
    var minTokenizer = options.Number("--min-tokenizer") ?? 1.0;
    bool greedy = !options.Has("--no-greedy"), beam = !options.Has("--no-beam");
    var translator = Load(modelPath, options.Has("--int8"), Console.Out);
    var reference = ReferenceSet.Load(referencePath);
    Console.WriteLine($"reference: {reference.Sentences.Count} sentences, {reference.Beams} beams; model {reference.Description}");
    var report = ReferenceCheck.Run(translator, reference, greedy, beam, options.Int("--limit", 1), (done, all) =>
    {
        if (done % 50 == 0 || done == all)
        {
            Console.WriteLine($"  {done}/{all}");
        }
    });
    foreach (var mismatch in report.Mismatches)
    {
        Console.WriteLine($"mismatch: {mismatch}");
    }

    var passed = report.TokenizerRate >= minTokenizer && (!greedy || report.GreedyRate >= minGreedy) && (!beam || report.BeamRate >= minBeam);
    Console.WriteLine($"summary: {report.Summary()}");
    Console.WriteLine(passed
        ? "result: PASSED"
        : $"result: FAILED (needed tokenizer ≥ {minTokenizer:P1}, greedy ≥ {minGreedy:P1}, beam ≥ {minBeam:P1})");
    return passed ? 0 : 1;
}

// the gate of F2: the title and introduction of an article (invented text) translated greedy and with the beams of
// the model; the median of the runs after one warm-up run
static int Bench(string modelPath, Options options)
{
    options.NoWords();
    var runs = options.Int("--runs", 1) ?? 3;
    var maxSeconds = options.Number("--max-seconds");
    const string title = "Harbor Lights returns for a third season with a new detective";
    const string intro = "The streaming service confirmed on Tuesday that the crime drama Harbor Lights will return next spring. " +
                         "The new season follows a young detective who moves to a quiet coastal town, where an old case suddenly opens again. " +
                         "Filming began in September, and the first trailer is expected before Christmas.";
    var translator = Load(modelPath, options.Has("--int8"), Console.Out);
    var sentences = new[] { title }.Concat(Segmenter.Sentences(intro)).ToList();
    var passed = true;
    foreach (var beams in new[] { 1, translator.Settings.NumBeams }.Distinct())
    {
        var times = new List<double>();
        List<TranslationResult> results = [];
        for (var run = 0; run <= runs; run++)
        {
            var watch = Stopwatch.StartNew();
            results = [.. sentences.Select(s => translator.RunModel(s, beams))];
            if (run > 0)
            {
                times.Add(watch.Elapsed.TotalSeconds);
            }
        }

        times.Sort();
        var median = times[times.Count / 2];
        var tokens = results.Sum(r => r.OutputIds.Length);
        var name = beams == 1 ? "greedy" : $"beam {beams}";
        Console.WriteLine($"{name}: {median:0.000} s for title + introduction ({sentences.Count} sentences, {tokens} tokens, " +
                          $"{tokens / median:0} tokens/s; runs {string.Join(", ", times.Select(t => t.ToString("0.000", CultureInfo.InvariantCulture)))})");
        foreach (var result in results)
        {
            Console.WriteLine($"  {result.Text}");
        }

        passed &= maxSeconds is null || median <= maxSeconds;
    }

    if (maxSeconds is { } max)
    {
        Console.WriteLine(passed ? $"result: PASSED (≤ {max} s)" : $"result: FAILED (> {max} s)");
    }

    return passed ? 0 : 1;
}

static int Quantize(string sourcePath, string targetPath)
{
    if (Path.GetFullPath(sourcePath) == Path.GetFullPath(targetPath))
    {
        throw new UsageException("the int8 copy must be a new file");
    }

    var watch = Stopwatch.StartNew();
    using (var source = ModelFile.Load(sourcePath))
    {
        Quantizer.Write(source, targetPath);
    }

    Console.WriteLine($"{targetPath}: {new FileInfo(targetPath).Length / 1e6:0.0} MB (from {new FileInfo(sourcePath).Length / 1e6:0.0} MB) " +
                      $"in {watch.Elapsed.TotalSeconds:0.0} s, SHA-256 {ModelFile.Sha256(targetPath)}");
    return 0;
}

static int ChrfOfFiles(string hypothesesPath, string referencesPath)
{
    var hypotheses = File.ReadAllLines(hypothesesPath);
    var references = File.ReadAllLines(referencesPath);
    if (hypotheses.Length != references.Length)
    {
        throw new UsageException($"{hypotheses.Length} translations, {references.Length} references");
    }

    Console.WriteLine($"chrF2 {Chrf.Corpus(hypotheses.Zip(references)).ToString("0.00", CultureInfo.InvariantCulture)} ({hypotheses.Length} sentences)");
    return 0;
}

// translates a test set line by line (each line as one sentence) and measures chrF against the references
static int Score(string modelPath, string sourcePath, string referencesPath, Options options)
{
    options.NoWords();
    int? beams = options.Has("--greedy") ? 1 : options.Int("--beams", 1);
    var sources = File.ReadAllLines(sourcePath).Take(options.Int("--limit", 1) ?? int.MaxValue).ToList();
    var references = File.ReadAllLines(referencesPath).Take(sources.Count).ToList();
    if (references.Count != sources.Count)
    {
        throw new UsageException($"{sources.Count} source lines, {references.Count} references");
    }

    var translator = Load(modelPath, options.Has("--int8"), Console.Out);
    var watch = Stopwatch.StartNew();
    var translations = new List<string>();
    foreach (var line in sources)
    {
        translations.Add(translator.TranslateSentence(line, beams).Target);
        if (translations.Count % 100 == 0)
        {
            Console.WriteLine($"  {translations.Count}/{sources.Count}");
        }
    }

    if (options.Value("--out") is { } outPath)
    {
        File.WriteAllLines(outPath, translations);
    }

    var used = beams ?? translator.Settings.NumBeams;
    Console.WriteLine($"chrF2 {Chrf.Corpus(translations.Zip(references)).ToString("0.00", CultureInfo.InvariantCulture)} " +
                      $"({sources.Count} sentences, {(used == 1 ? "greedy" : $"beam {used}")}{(translator.Model.IsInt8 ? ", int8" : "")}, " +
                      $"{watch.Elapsed.TotalSeconds:0} s)");
    return 0;
}

static int Remember(string memoryPath, string source, string target)
{
    var memory = TranslationMemory.Open(memoryPath);
    memory.Remember(source, target);
    Console.WriteLine($"remembered ({memory.Count} sentences in the memory)");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        iris translate <model.iris> [options] [text]   translates the text, or every line of the input
            --beams N | --greedy                       beam search with N beams (default: the model's) or greedy
            --int8                                     int8 weights (float32 models are turned into int8 when read)
            --memory <file>                            translation memory (corrected sentences, JSON lines)
            --glossary <file>                          names and titles: "term" or "term TAB translation" per line
            --ids                                      the bare model, with token ids (not with --memory, --glossary)
        iris remember <memory.jsonl> <sentence> <translation>
                                                       remembers a corrected translation of a sentence
        iris bench <model.iris> [--runs N] [--max-seconds S] [--int8]
                                                       time of the title and introduction of an article
        iris score <model.iris> <source.txt> <references.txt> [--beams N | --greedy] [--int8] [--limit N] [--out file]
                                                       translates a test set line by line, chrF against the references
        iris chrf <translations.txt> <references.txt>  chrF (character 6-grams, β = 2, as sacreBLEU)
        iris quantize <model.iris> <model-int8.iris>   an int8 copy of a model file (about a quarter of the size)
        iris verify <model.iris> <reference.json> [options]
                                                       compares the core with reference data (tools/reference)
            --min-greedy R, --min-beam R, --min-tokenizer R   matches needed (0.99, 0.95, 1)
            --limit N, --no-greedy, --no-beam, --int8
        iris info <model.iris>                         what a model file holds
        iris tokenize <model.spm> <text>               pieces and ids of a SentencePiece model
        """);
    return 2;
}
