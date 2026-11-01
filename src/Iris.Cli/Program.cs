using System.Diagnostics;
using System.Globalization;
using System.Text;
using Iris.Cli;
using Iris.Evaluation;
using Iris.Learning;
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
        ["translate", var model, .. var rest] => Translate(model,
            Options.Parse(rest, ["--greedy", "--ids", "--int8"], ["--beams", "--memory", "--glossary", "--adapter"])),
        ["learn", var model, var pairs, var adapter, .. var rest] => Learn(model, pairs, adapter,
            Options.Parse(rest, ["--int8"], ["--epochs", "--batch", "--rate", "--rank", "--smoothing", "--limit", "--layers"])),
        ["check-learning", var model, var adapter, var reference] => CheckLearning(model, adapter, reference),
        ["lines", var picture, .. var rest] => Lines(picture, Options.Parse(rest, [], ["--out"])),
        ["verify", var model, var reference, .. var rest] => Verify(model, reference,
            Options.Parse(rest, ["--no-greedy", "--no-beam", "--int8"], ["--min-greedy", "--min-beam", "--min-tokenizer", "--limit"])),
        ["bench", var model, .. var rest] => Bench(model, Options.Parse(rest, ["--int8"], ["--runs", "--max-seconds"])),
        ["quantize", var source, var target] => Quantize(source, target),
        ["score", var model, var source, var references, .. var rest] => Score(model, source, references,
            Options.Parse(rest, ["--greedy", "--int8"], ["--beams", "--limit", "--out", "--adapter"])),
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

static Translator Load(string modelPath, bool int8, TextWriter log, string? adapterPath = null)
{
    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath, int8);
    var learned = "";
    if (adapterPath is not null)
    {
        var adapter = Adapter.Load(adapterPath);
        translator = translator.WithAdapter(adapter, ModelFile.Sha256(modelPath));
        learned = $", adapter of {adapter.Parameters:N0} learned values";
    }

    log.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.00} s{(translator.Model.IsInt8 ? " (int8)" : "")}{learned}; " +
                  $"{Environment.ProcessorCount} processors");
    return translator;
}

// F3: learns an adapter from pairs (source TAB translation per line); the model file is not changed
static int Learn(string modelPath, string pairsPath, string adapterPath, Options options)
{
    options.NoWords();
    if (File.Exists(adapterPath) && Path.GetFullPath(adapterPath) == Path.GetFullPath(modelPath))
    {
        throw new UsageException("the adapter must be another file than the model");
    }

    var lines = File.ReadAllLines(pairsPath).Where(l => l.Trim().Length > 0).Take(options.Int("--limit", 1) ?? int.MaxValue).ToList();
    var texts = lines.Select((line, i) => line.Split('\t') is [var source, var target] && source.Trim().Length > 0 && target.Trim().Length > 0
        ? (Source: source.Trim(), Target: target.Trim())
        : throw new UsageException($"line {i + 1} of the pairs is not 'sentence TAB translation'")).ToList();
    var translator = Load(modelPath, options.Has("--int8"), Console.Out);
    var pairs = texts.Select(t => new TrainingPair(translator.Tokenizer.Encode(t.Source), translator.Tokenizer.EncodeTarget(t.Target))).ToList();
    var layers = options.Value("--layers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var adapter = Adapter.Create(translator.Model, ModelFile.Sha256(modelPath), options.Int("--rank", 1) ?? 8,
        targets: layers);
    var settings = new LearningSettings
    {
        Epochs = options.Int("--epochs", 1) ?? 3,
        BatchSize = options.Int("--batch", 1) ?? 8,
        LearningRate = (float)(options.Number("--rate") ?? 5e-4),
        LabelSmoothing = (float)(options.Number("--smoothing") ?? 0.1),
    };
    var trainer = new MarianTrainer(translator.Model, adapter);
    var usable = pairs.Where(trainer.Fits).ToList();
    var tokens = usable.Sum(p => p.Source.Length + p.Target.Length);
    Console.WriteLine($"{usable.Count} pairs ({pairs.Count - usable.Count} too long), {tokens:N0} tokens; adapter: rank {adapter.Rank}, " +
                      $"{adapter.Layers.Count} layers, {adapter.Parameters:N0} learned values; {settings.Epochs} epochs, batch {settings.BatchSize}, " +
                      $"rate {settings.LearningRate.ToString(CultureInfo.InvariantCulture)}");
    var start = trainer.Loss(usable);
    var watch = Stopwatch.StartNew();
    var last = TimeSpan.Zero;
    var end = trainer.Train(usable, settings, new Progress<LearningProgress>(p =>
    {
        if (watch.Elapsed - last >= TimeSpan.FromSeconds(10) || p.Step == p.Steps)
        {
            last = watch.Elapsed;
            Console.WriteLine($"  step {p.Step}/{p.Steps}: loss {p.Loss:0.000} ({watch.Elapsed.TotalSeconds:0} s)");
        }
    }));
    var seconds = watch.Elapsed.TotalSeconds;
    var after = trainer.Loss(usable);
    var perPair = seconds / (usable.Count * (double)settings.Epochs);
    adapter.With(new System.Text.Json.Nodes.JsonObject
    {
        // what was learned is described by counts and checksums only – never by the texts or names of files
        ["learned"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["pairs"] = usable.Count,
        ["pairs_sha256"] = ModelFile.Sha256(pairsPath),
        ["epochs"] = settings.Epochs,
        ["batch"] = settings.BatchSize,
        ["rate"] = settings.LearningRate,
        ["label_smoothing"] = settings.LabelSmoothing,
        ["loss_before"] = Math.Round(start, 4),
        ["loss_after"] = Math.Round(after, 4),
        ["int8"] = translator.Model.IsInt8,
    }).Save(adapterPath);
    Console.WriteLine($"loss per token {start:0.000} → {after:0.000} (smoothing 0; last epoch with smoothing {end:0.000}); " +
                      $"{seconds:0} s, {perPair:0.00} s per pair and epoch; adapter written ({new FileInfo(adapterPath).Length / 1e6:0.0} MB)");
    return 0;
}

// F3 against the reference: loss and gradient of an adapter on pairs, as PyTorch computed them (marian_reference.py learning)
static int CheckLearning(string modelPath, string adapterPath, string referencePath)
{
    var translator = Load(modelPath, false, Console.Out);
    var adapter = Adapter.Load(adapterPath);
    var reference = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(referencePath))!.AsObject();
    var smoothing = reference["label_smoothing"]!.GetValue<float>();
    var trainer = new MarianTrainer(translator.Model, adapter);
    var failures = new List<string>();
    var pairs = reference["pairs"]!.AsArray();
    var training = new List<TrainingPair>();
    foreach (var item in pairs)
    {
        var source = item!["source_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        var target = item["target_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        if (!translator.Tokenizer.Encode(item["source"]!.GetValue<string>()).SequenceEqual(source) ||
            !translator.Tokenizer.EncodeTarget(item["target"]!.GetValue<string>()).SequenceEqual(target))
        {
            failures.Add($"token ids differ: {item["source"]}");
        }

        training.Add(new TrainingPair(source, target));
    }

    var gradient = trainer.Gradient(training, smoothing);
    double worstLoss = 0;
    for (var i = 0; i < pairs.Count; i++)
    {
        var item = pairs[i]!;
        var loss = gradient.Losses[i];
        var expected = item["loss"]!.GetValue<double>();
        var error = Math.Abs(loss - expected) / Math.Max(1, Math.Abs(expected));
        worstLoss = Math.Max(worstLoss, error);
        if (error > 1e-4)
        {
            failures.Add($"loss {loss:0.0000} instead of {expected:0.0000}: {item["source"]}");
        }
    }

    double worstNorm = 0, worstValue = 0;
    var compared = 0;
    foreach (var (name, node) in reference["gradients"]!.AsObject())
    {
        foreach (var key in new[] { "a", "b" })
        {
            var own = key == "a" ? gradient.A[name] : gradient.B[name];
            var norm = Math.Sqrt(own.Sum(v => (double)v * v));
            var expectedNorm = node![key + "_norm"]!.GetValue<double>();
            var normError = Math.Abs(norm - expectedNorm) / Math.Max(expectedNorm, 1e-6);
            worstNorm = Math.Max(worstNorm, normError);
            if (normError > 1e-3)
            {
                failures.Add($"{name}.{key}: gradient norm {norm:g6} instead of {expectedNorm:g6}");
            }

            foreach (var pick in node[key + "_values"]!.AsArray())
            {
                var index = pick![0]!.GetValue<int>();
                var value = pick[1]!.GetValue<double>();
                var valueError = Math.Abs(own[index] - value) / (Math.Abs(value) + expectedNorm * 1e-3 + 1e-9);
                worstValue = Math.Max(worstValue, valueError);
                compared++;
                if (valueError > 1e-2)
                {
                    failures.Add($"{name}.{key}[{index}]: {own[index]:g6} instead of {value:g6}");
                }
            }
        }
    }

    Console.WriteLine($"{pairs.Count} pairs, {adapter.Layers.Count} layers, {compared} gradient values: worst loss difference " +
                      $"{worstLoss:e1}, gradient norm {worstNorm:e1}, values {worstValue:e1}");
    foreach (var failure in failures.Take(20))
    {
        Console.WriteLine($"  FAIL {failure}");
    }

    return failures.Count == 0 ? 0 : 1;
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

    var translator = Load(modelPath, options.Has("--int8"), Console.Error, options.Value("--adapter"));
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

    var translator = Load(modelPath, options.Has("--int8"), Console.Out, options.Value("--adapter"));
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

// OCR step O1: a picture of a page → straightened, its lines found (with --out: the page and each line as PNG)
static int Lines(string picturePath, Options options)
{
    options.NoWords();
    var watch = Stopwatch.StartNew();
    var picture = Iris.Vision.Imaging.ImageFile.Read(picturePath);
    var read = watch.Elapsed.TotalSeconds;
    var page = Iris.Vision.Layout.PageReader.Prepare(picture);
    Console.WriteLine($"{picture.Width} × {picture.Height}, read in {read:0.00} s; slant {page.Skew.ToString("0.00", CultureInfo.InvariantCulture)}°, " +
                      $"{page.Lines.Count} lines ({watch.Elapsed.TotalSeconds:0.00} s)");
    foreach (var (line, i) in page.Lines.Select((l, i) => (l, i)))
    {
        Console.WriteLine($"  line {i + 1}: x {line.X}, y {line.Y}, {line.Width} × {line.Height}");
    }

    if (options.Value("--out") is { } folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "page.png"), Iris.Vision.Imaging.Png.Write(page.Image));
        foreach (var (line, i) in page.Lines.Select((l, i) => (l, i)))
        {
            File.WriteAllBytes(Path.Combine(folder, $"line-{i + 1:000}.png"), Iris.Vision.Imaging.Png.Write(line.Image));
        }
    }

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
            --adapter <file>                           what IRIS learned (iris learn) for this model file
        iris learn <model.iris> <pairs.tsv> <adapter.iris> [options]
                                                       learns from pairs "sentence TAB translation" (F3); the model stays
            --epochs N (3), --batch N (8), --rate R (0.0005), --rank N (8), --smoothing R (0.1), --limit N
            --layers q,v                               which layers learn (q k v o of attention, 1 2 of feed-forward)
        iris check-learning <model.iris> <adapter.iris> <reference.json>
                                                       compares loss and gradients with the reference (tools/reference)
        iris remember <memory.jsonl> <sentence> <translation>
                                                       remembers a corrected translation of a sentence
        iris bench <model.iris> [--runs N] [--max-seconds S] [--int8]
                                                       time of the title and introduction of an article
        iris score <model.iris> <source.txt> <references.txt> [--beams N | --greedy] [--int8] [--limit N] [--out file] [--adapter file]
                                                       translates a test set line by line, chrF against the references
        iris chrf <translations.txt> <references.txt>  chrF (character 6-grams, β = 2, as sacreBLEU)
        iris quantize <model.iris> <model-int8.iris>   an int8 copy of a model file (about a quarter of the size)
        iris verify <model.iris> <reference.json> [options]
                                                       compares the core with reference data (tools/reference)
            --min-greedy R, --min-beam R, --min-tokenizer R   matches needed (0.99, 0.95, 1)
            --limit N, --no-greedy, --no-beam, --int8
        iris lines <picture.png|jpg> [--out folder]     OCR: straightens a page photo or scan and finds its lines
        iris info <model.iris>                         what a model file holds
        iris tokenize <model.spm> <text>               pieces and ids of a SentencePiece model
        """);
    return 2;
}
