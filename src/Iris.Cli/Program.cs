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
        ["ocr", var model, var picture] => Ocr(model, picture),
        ["check-ocr", var model, var folder, .. var rest] => CheckOcr(model, folder, Options.Parse(rest, [], ["--min-text"])),
        ["train-ocr", var folder, .. var rest] => TrainOcr(folder, Options.Parse(rest, [],
            ["--start", "--minutes", "--plan", "--rate", "--fonts", "--families", "--exclude", "--unseen", "--sentences", "--threads",
             "--drawing-threads", "--duty", "--batch", "--test-lines", "--test-every", "--seed"])),
        ["check-ocr-training", var folder, .. var rest] => CheckOcrTraining(folder, Options.Parse(rest, [], ["--threads"])),
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

// OCR: the text of a picture of a page, line by line
static int Ocr(string modelPath, string picturePath)
{
    var watch = Stopwatch.StartNew();
    var reader = Iris.Vision.Ocr.LineReader.Load(modelPath);
    var page = Iris.Vision.Layout.PageReader.Prepare(Iris.Vision.Imaging.ImageFile.Read(picturePath));
    var lines = new string[page.Lines.Count];
    Parallel.For(0, lines.Length, i => lines[i] = reader.Read(page.Lines[i].Image).Text);
    foreach (var line in lines)
    {
        Console.WriteLine(line);
    }

    Console.Error.WriteLine($"{lines.Length} lines, slant {page.Skew.ToString("0.00", CultureInfo.InvariantCulture)}°, {watch.Elapsed.TotalSeconds:0.00} s");
    return 0;
}

// the C# reader against what PyTorch gave for the same line pictures (tools/ocr/train.py writes the reference)
static int CheckOcr(string modelPath, string folder, Options options)
{
    options.NoWords();
    var reader = Iris.Vision.Ocr.LineReader.Load(modelPath);
    var reference = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "reference.json")))!["lines"]!.AsArray();
    int sameText = 0, sameFrames = 0, frames = 0;
    double worst = 0;
    foreach (var item in reference)
    {
        var picture = Iris.Vision.Imaging.ImageFile.Read(Path.Combine(folder, item!["file"]!.GetValue<string>()));
        var reading = reader.Read(picture);
        var expected = item["best"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        if (reading.Frames != expected.Length)
        {
            Console.WriteLine($"  FAIL {item["file"]}: {reading.Frames} frames instead of {expected.Length}");
            continue;
        }

        sameText += reading.Text == item["read"]!.GetValue<string>() ? 1 : 0;
        for (var t = 0; t < reading.Frames; t++)
        {
            sameFrames += Kernels(reading, t) == expected[t] ? 1 : 0;
        }

        frames += reading.Frames;
        foreach (var (key, t) in new[] { ("logits_first", 0), ("logits_middle", reading.Frames / 2) })
        {
            var values = item[key]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
            for (var k = 0; k < values.Length; k++)
            {
                worst = Math.Max(worst, Math.Abs(values[k] - reading.Logits[t * reading.Classes + k]) / (1 + Math.Abs(values[k])));
            }
        }
    }

    var textRate = (double)sameText / reference.Count;
    Console.WriteLine($"{reference.Count} lines: the same text in {sameText}, the same best class in {sameFrames} of {frames} frames, " +
                      $"largest logit difference {worst:e1}");
    var needed = options.Number("--min-text") ?? 0.95;
    return textRate >= needed && worst < 1e-3 ? 0 : 1;

    static int Kernels(Iris.Vision.Ocr.LineReading r, int t)
    {
        var best = 0;
        for (var k = 1; k < r.Classes; k++)
        {
            if (r.Logits[t * r.Classes + k] > r.Logits[t * r.Classes + best])
            {
                best = k;
            }
        }

        return best;
    }
}

// OCR learning in C#: from a learning file (or from scratch) on lines drawn with own font code, for the given minutes
static int TrainOcr(string folder, Options options)
{
    options.NoWords();
    var shape = Iris.Vision.Training.OcrShape.Default;
    Iris.Vision.Training.OcrWeights weights;
    Iris.Vision.Training.AdamW adam;
    Iris.Vision.Training.OcrLearningState state;
    if (options.Value("--start") is { } start)
    {
        (weights, adam, state) = Iris.Vision.Training.OcrFiles.LoadTraining(start);
        shape = weights.Shape;
    }
    else
    {
        weights = Iris.Vision.Training.OcrWeights.Create(shape, options.Int("--seed", 0) ?? 1);
        adam = new Iris.Vision.Training.AdamW();
        state = new Iris.Vision.Training.OcrLearningState();
    }

    var folders = options.Value("--fonts")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? DefaultFontFolders();
    var families = options.Value("--families")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var exclude = options.Value("--exclude")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? ["Math", "Display", "Unifont"];
    var unseenName = options.Value("--unseen") ?? "Caladea";
    var faces = Iris.Vision.Training.LineArtist.FindFonts(folders, shape.Charset, exclude)
        .Where(f => families is null || families.Any(n => f.Name.Contains(n, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f.File).Contains(n, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    var unseen = faces.Where(f => unseenName.Length > 0 && f.Name.Contains(unseenName, StringComparison.OrdinalIgnoreCase)).ToList();
    var learn = faces.Except(unseen).ToList();
    if (learn.Count == 0)
    {
        throw new UsageException("no font with all the characters was found (--fonts folders)");
    }

    Console.WriteLine($"{learn.Count} fonts to learn from, {unseen.Count} unseen ({unseenName}); {weights.Learnable().Sum(t => t.Values.Length):N0} parameters, step {state.Step}");
    var training = new Iris.Vision.Training.OcrTrainingOptions
    {
        Minutes = options.Number("--minutes") ?? 60,
        Plan = options.Number("--plan"),
        Rate = options.Number("--rate") ?? 1e-3,
        Threads = options.Int("--threads", 1) ?? Environment.ProcessorCount,
        DrawingThreads = options.Int("--drawing-threads", 1) ?? 1,
        Duty = Math.Clamp(options.Number("--duty") ?? 1, 0.05, 1),
        BatchSize = options.Int("--batch", 1) ?? 32,
        TestLines = options.Int("--test-lines", 1) ?? 600,
        TestEveryMinutes = options.Number("--test-every") ?? 20,
        Seed = options.Int("--seed", 0) ?? 1,
    };
    var sentences = Iris.Vision.Training.LineTexts.ReadSentences(options.Value("--sentences"));
    var trainer = new Iris.Vision.Training.OcrTrainer(weights, adam, state, new Iris.Vision.Training.LineArtist(learn),
        unseen.Count > 0 ? new Iris.Vision.Training.LineArtist(unseen) : null, sentences, training);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    var last = trainer.Run(folder, new SyncProgress<Iris.Vision.Training.OcrProgress>(p => Console.WriteLine(
        p.CerKnown is { } k
            ? $"step {p.Step}, {p.Minutes:F0} min: CER {k:F2} % (learned fonts), {p.CerUnseen?.ToString("F2", CultureInfo.InvariantCulture) ?? "–"} % (unseen font)"
            : $"step {p.Step}, {p.Minutes:F0} min, loss {p.Loss:F3}, rate {p.Rate:E2}, {p.LinesPerSecond:F1} lines/s")), cancel.Token);

    var result = new System.Text.Json.Nodes.JsonObject
    {
        ["step"] = last.Step,
        ["minutes"] = Math.Round(last.Minutes),
        ["cer_known"] = last.CerKnown,
        ["cer_unseen"] = last.CerUnseen,
        ["lines_per_second"] = Math.Round(last.LinesPerSecond, 2),
        ["parameters"] = weights.Learnable().Sum(t => t.Values.Length),
        ["fonts"] = new System.Text.Json.Nodes.JsonArray([.. learn.Select(f => (System.Text.Json.Nodes.JsonNode?)f.Name)]),
        ["unseen_fonts"] = new System.Text.Json.Nodes.JsonArray([.. unseen.Select(f => (System.Text.Json.Nodes.JsonNode?)f.Name)]),
        ["history"] = new System.Text.Json.Nodes.JsonArray([.. trainer.History.Select(h => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
        {
            ["step"] = h.Step,
            ["minutes"] = Math.Round(h.Minutes),
            ["cer_known"] = h.CerKnown,
            ["cer_unseen"] = h.CerUnseen,
        })]),
    };
    File.WriteAllText(Path.Combine(folder, "result.json"), result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"done: step {last.Step}, {last.Minutes:F0} min, CER {last.CerKnown:F2} % (learned fonts), {last.CerUnseen?.ToString("F2", CultureInfo.InvariantCulture) ?? "–"} % (unseen font), {last.LinesPerSecond:F1} lines/s");
    return 0;

    static string[] DefaultFontFolders() => OperatingSystem.IsWindows()
        ? [Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts")]
        : ["/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts")];
}

// the C# OCR learning against PyTorch (tools/ocr/reference_training.py): loss, every gradient, one AdamW step, running
// statistics – and the time of a learning step of both on the same batch
static int CheckOcrTraining(string folder, Options options)
{
    options.NoWords();
    var (weights, adam, _) = Iris.Vision.Training.OcrFiles.LoadTraining(Path.Combine(folder, "start.iris"));
    using var batchFile = ModelFile.Load(Path.Combine(folder, "batch.iris"));
    using var expected = ModelFile.Load(Path.Combine(folder, "expected.iris"));
    var shape = weights.Shape;
    if (shape.Charset != Iris.Vision.Training.OcrShape.DefaultCharset)
    {
        Console.WriteLine("FAIL the charset of C# (OcrShape.DefaultCharset) differs from tools/ocr/charset.py");
        return 1;
    }

    var texts = batchFile.Config["texts"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
    var lines = texts.Select((text, i) =>
    {
        var info = batchFile.Tensors.First(t => t.Name == $"line.{i}");
        var ink = batchFile.Floats($"line.{i}");
        return new Iris.Vision.Training.TrainingLine(ink, info.Shape[1], text, shape.Encode(text));
    }).ToList();
    var batch = new Iris.Vision.Training.LineBatch(lines);
    var threads = options.Int("--threads", 1) ?? Environment.ProcessorCount;
    var start = weights.Copy();
    using var network = new Iris.Vision.Training.OcrNetwork(weights) { Threads = threads };
    var gradients = new Iris.Vision.Training.OcrWeights(shape);
    gradients.Clear();
    var loss = network.Learn(batch, gradients);
    var expectedLoss = expected.Config["loss"]!.GetValue<double>();
    var failures = 0;
    void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}: {detail}");
        failures += ok ? 0 : 1;
    }

    Check("loss", Math.Abs(loss - expectedLoss) <= 1e-4 * Math.Abs(expectedLoss) + 1e-5, $"C# {loss:G7}, PyTorch {expectedLoss:G7}");
    double worstGradient = 0, worstAfter = 0, worstStatistic = 0;
    // a gradient that is nearly zero everywhere (a shift that the next normalization takes away) is compared with the
    // largest gradient of the model instead of with itself; where two values of a pooling window are equal up to the
    // last bit, the order of summation may pick the other one and send that bit of gradient elsewhere – so the whole
    // tensor (relative length of the difference) must agree closely, single values a little less
    var largest = gradients.Learnable().Max(t => expected.Floats("grad." + t.Name).Max(MathF.Abs));
    double worstLength = 0;
    foreach (var t in gradients.Learnable())
    {
        var mine = t.ForFile();
        var theirs = expected.Floats("grad." + t.Name);
        var scale = Math.Max(theirs.Max(MathF.Abs), 1e-4f * largest) + 1e-12f;
        var difference = mine.Zip(theirs, (a, b) => Math.Abs(a - b)).Max() / scale;
        var length = Math.Sqrt(mine.Zip(theirs, (a, b) => (double)(a - b) * (a - b)).Sum()) /
                     Math.Max(Math.Sqrt(theirs.Sum(b => (double)b * b)), 1e-4 * largest * Math.Sqrt(theirs.Length));
        worstGradient = Math.Max(worstGradient, difference);
        worstLength = Math.Max(worstLength, length);
        if (length > 2e-3 || difference > 3e-2)
        {
            Check($"gradient of {t.Name}", false, $"difference {length:E1} of its length, largest single {difference:E1} of its largest value");
        }
    }

    Check("gradients", worstLength <= 2e-3 && worstGradient <= 3e-2, $"difference at most {worstLength:E1} of a tensor's length, largest single {worstGradient:E1}");
    foreach (var t in weights.Statistics())
    {
        var theirs = expected.Floats("stat." + t.Name);
        worstStatistic = Math.Max(worstStatistic, t.Values.Zip(theirs, (a, b) => Math.Abs(a - b) / (1 + Math.Abs(b))).Max());
    }

    Check("running statistics", worstStatistic <= 1e-4, $"largest difference {worstStatistic:E1}");
    // the optimizer on its own: PyTorch's gradients, clipped and stepped by C#, must give PyTorch's weights
    var pytorchGradients = new Iris.Vision.Training.OcrWeights(shape);
    foreach (var t in pytorchGradients.Learnable())
    {
        t.FromFile(expected.Floats("grad." + t.Name)).CopyTo(t.Values, 0);
    }

    var norm = Iris.Vision.Training.AdamW.Clip(pytorchGradients, 5);
    var expectedNorm = expected.Config["norm"]!.GetValue<double>();
    Check("gradient norm", Math.Abs(norm - expectedNorm) <= 1e-4 * expectedNorm, $"C# {norm:G6}, PyTorch {expectedNorm:G6}");
    var stepped = start.Copy();
    new Iris.Vision.Training.AdamW().Step(stepped, pytorchGradients, 1e-3f);
    foreach (var t in stepped.Learnable())
    {
        worstAfter = Math.Max(worstAfter, t.ForFile().Zip(expected.Floats("after." + t.Name), (a, b) => (double)Math.Abs(a - b)).Max());
    }

    Check("one AdamW step", worstAfter <= 2e-6, $"largest difference {worstAfter:E1}");

    // speed: whole learning steps (forward, backward, clipping, AdamW) on this batch
    var watch = Stopwatch.StartNew();
    const int Steps = 3;
    for (var s = 0; s < Steps; s++)
    {
        gradients.Clear();
        network.Learn(batch, gradients);
        Iris.Vision.Training.AdamW.Clip(gradients, 5);
        adam.Step(weights, gradients, 1e-4f);
    }

    var seconds = watch.Elapsed.TotalSeconds / Steps;
    var packed = expected.Config["seconds_packed"]!.GetValue<double>();
    var padded = expected.Config["seconds_padded"]!.GetValue<double>();
    Console.WriteLine($"speed of a step of {lines.Count} lines: C# {seconds:F2} s ({threads} threads), PyTorch {padded:F2} s as train.py (padded), " +
                      $"{packed:F2} s each line at its own width ({expected.Config["threads"]} threads, torch {expected.Config["torch"]})");
    foreach (var (part, time) in network.Timings.OrderByDescending(p => p.Value).Take(6))
    {
        Console.WriteLine($"    {part}: {time / (Steps + 1):F3} s");
    }

    Console.WriteLine(failures == 0 ? "the C# learning matches PyTorch" : $"{failures} differences");
    return failures == 0 ? 0 : 1;
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
        iris ocr <iris-ocr.iris> <picture.png|jpg>      OCR: the text of a page photo or scan
        iris check-ocr <iris-ocr.iris> <reference folder> [--min-text R]
                                                       compares the OCR reader with the PyTorch reference (tools/ocr)
        iris train-ocr <folder> [options]              OCR learning in C# on lines drawn with own font code
            --start <iris-ocr-train.iris>              go on from a learning file (else a new reader)
            --minutes M (60), --plan M, --rate R (0.001), --batch N (32), --seed N
            --fonts <dir;dir>, --families a,b, --exclude a,b, --unseen <name> (Caladea), --sentences <dir>
            --threads N, --drawing-threads N (1), --duty D (1; 0.5 = rest as long as it works)
            --test-lines N (600), --test-every M (20)
        iris check-ocr-training <reference folder> [--threads N]
                                                       the C# OCR learning against PyTorch (tools/ocr/reference_training.py)
        iris info <model.iris>                         what a model file holds
        iris tokenize <model.spm> <text>               pieces and ids of a SentencePiece model
        """);
    return 2;
}

/// <summary>Reports progress on the thread that does the work (Progress&lt;T&gt; would post it elsewhere).</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
