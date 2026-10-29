using System.Diagnostics;
using System.Globalization;
using System.Text;
using Iris.Evaluation;
using Iris.Model;
using Iris.Tokenization;
using Iris.Translation;

// iris – the command line of the IRIS translator
Console.OutputEncoding = Encoding.UTF8;
try
{
    return args switch
    {
        ["tokenize", var model, .. var words] when words.Length > 0 => Tokenize(model, string.Join(' ', words)),
        ["info", var model] => Info(model),
        ["translate", var model, .. var rest] => Translate(model, rest),
        ["verify", var model, var reference, .. var rest] => Verify(model, reference, rest),
        ["bench", var model, .. var rest] => Bench(model, rest),
        ["quantize", var source, var target] => Quantize(source, target),
        ["chrf", var hypotheses, var references] => ChrfOfFiles(hypotheses, references),
        ["score", var model, var source, var references, .. var rest] => Score(model, source, references, rest),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is FormatException or IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
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
    var model = ModelFile.Load(path);
    Console.WriteLine($"kind: {model.Kind}");
    Console.WriteLine($"config: {model.Config.ToJsonString()}");
    Console.WriteLine($"provenance: {model.Provenance.ToJsonString()}");
    Console.WriteLine($"tensors: {model.Tensors.Count}, parameters: {model.Tensors.Sum(t => t.Count):N0}");
    return 0;
}

// iris translate <model.iris> [--beams N | --greedy] [--ids] [text …]; without text every line of the input is translated
static int Translate(string modelPath, string[] rest)
{
    int? beams = null;
    var showIds = false;
    var int8 = false;
    var words = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--greedy":
                beams = 1;
                break;
            case "--beams" when i + 1 < rest.Length && int.TryParse(rest[i + 1], CultureInfo.InvariantCulture, out var n) && n >= 1:
                beams = n;
                i++;
                break;
            case "--ids":
                showIds = true;
                break;
            case "--int8":
                int8 = true;
                break;
            default:
                words.Add(rest[i]);
                break;
        }
    }

    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath, int8);
    Console.Error.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.0} s{(translator.Model.IsInt8 ? " (int8)" : "")}");
    void One(string text)
    {
        if (!showIds)
        {
            Console.WriteLine(translator.TranslateText(text, beams));
            return;
        }

        foreach (var sentence in Segmenter.Sentences(text))
        {
            var result = translator.Translate(sentence, beams);
            Console.WriteLine(result.Text);
            Console.Error.WriteLine($"  source: {string.Join(' ', result.SourceIds)}");
            Console.Error.WriteLine($"  output: {string.Join(' ', result.OutputIds)} (score {result.Score.ToString("0.####", CultureInfo.InvariantCulture)})");
        }
    }

    if (words.Count > 0)
    {
        One(string.Join(' ', words));
    }
    else
    {
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
    }

    return 0;
}

// iris verify <model.iris> <reference.json> [--min-greedy R] [--min-beam R] [--min-tokenizer R] [--limit N] [--no-greedy] [--no-beam]
static int Verify(string modelPath, string referencePath, string[] rest)
{
    double minGreedy = 0.99, minBeam = 0.95, minTokenizer = 1.0;
    int? limit = null;
    bool greedy = true, beam = true, int8 = false;
    for (var i = 0; i < rest.Length; i++)
    {
        double Next() => i + 1 < rest.Length && double.TryParse(rest[++i], CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new ArgumentException($"{rest[i - 1]} needs a number");
        switch (rest[i])
        {
            case "--min-greedy": minGreedy = Next(); break;
            case "--min-beam": minBeam = Next(); break;
            case "--min-tokenizer": minTokenizer = Next(); break;
            case "--limit": limit = (int)Next(); break;
            case "--no-greedy": greedy = false; break;
            case "--no-beam": beam = false; break;
            case "--int8": int8 = true; break;
            default: throw new ArgumentException($"unknown option {rest[i]}");
        }
    }

    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath, int8);
    var reference = ReferenceSet.Load(referencePath);
    Console.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.0} s{(translator.Model.IsInt8 ? " (int8)" : "")}; " +
                      $"reference: {reference.Sentences.Count} sentences, {reference.Beams} beams");
    Console.WriteLine($"reference model: {reference.Description}");
    var report = ReferenceCheck.Run(translator, reference, greedy, beam, limit, (done, all) =>
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

// iris bench <model.iris> [--runs N] [--max-seconds S]: the gate of F2 – the title and introduction of an article
// (invented text) translated greedy and with the beams of the model; the median of the runs after one warm-up run
static int Bench(string modelPath, string[] rest)
{
    var runs = 3;
    double? maxSeconds = null;
    var int8 = false;
    for (var i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--runs" when i + 1 < rest.Length: runs = int.Parse(rest[++i], CultureInfo.InvariantCulture); break;
            case "--max-seconds" when i + 1 < rest.Length: maxSeconds = double.Parse(rest[++i], CultureInfo.InvariantCulture); break;
            case "--int8": int8 = true; break;
            default: throw new ArgumentException($"unknown option {rest[i]}");
        }
    }

    const string title = "Harbor Lights returns for a third season with a new detective";
    const string intro = "The streaming service confirmed on Tuesday that the crime drama Harbor Lights will return next spring. " +
                         "The new season follows a young detective who moves to a quiet coastal town, where an old case suddenly opens again. " +
                         "Filming began in September, and the first trailer is expected before Christmas.";
    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath, int8);
    Console.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.00} s{(translator.Model.IsInt8 ? " (int8)" : "")}; " +
                      $"{Environment.ProcessorCount} processors");
    var sentences = new[] { title }.Concat(Segmenter.Sentences(intro)).ToList();
    var passed = true;
    foreach (var beams in new[] { 1, translator.Settings.NumBeams }.Distinct())
    {
        var times = new List<double>();
        var tokens = 0;
        List<string> output = [];
        for (var run = 0; run <= runs; run++)
        {
            watch.Restart();
            var results = sentences.Select(s => translator.Translate(s, beams)).ToList();
            var seconds = watch.Elapsed.TotalSeconds;
            if (run > 0)
            {
                times.Add(seconds);
            }

            tokens = results.Sum(r => r.OutputIds.Length);
            output = [.. results.Select(r => r.Text)];
        }

        times.Sort();
        var median = times[times.Count / 2];
        var name = beams == 1 ? "greedy" : $"beam {beams}";
        Console.WriteLine($"{name}: {median:0.000} s for title + introduction ({sentences.Count} sentences, {tokens} tokens, " +
                          $"{tokens / median:0} tokens/s; runs {string.Join(", ", times.Select(t => t.ToString("0.000", CultureInfo.InvariantCulture)))})");
        foreach (var line in output)
        {
            Console.WriteLine($"  {line}");
        }

        if (maxSeconds is { } limit && median > limit)
        {
            passed = false;
        }
    }

    if (maxSeconds is { } max)
    {
        Console.WriteLine(passed ? $"result: PASSED (≤ {max} s)" : $"result: FAILED (> {max} s)");
    }

    return passed ? 0 : 1;
}

// iris quantize <model.iris> <model-int8.iris>: an int8 copy of a model file
static int Quantize(string sourcePath, string targetPath)
{
    var watch = Stopwatch.StartNew();
    Quantizer.Write(ModelFile.Load(sourcePath), targetPath);
    Console.WriteLine($"{targetPath}: {new FileInfo(targetPath).Length / 1e6:0.0} MB (from {new FileInfo(sourcePath).Length / 1e6:0.0} MB) " +
                      $"in {watch.Elapsed.TotalSeconds:0.0} s, SHA-256 {ModelFile.Sha256(targetPath)}");
    return 0;
}

// iris chrf <hypotheses.txt> <references.txt>: chrF of translations against references, line by line
static int ChrfOfFiles(string hypothesesPath, string referencesPath)
{
    var hypotheses = File.ReadAllLines(hypothesesPath);
    var references = File.ReadAllLines(referencesPath);
    if (hypotheses.Length != references.Length)
    {
        throw new ArgumentException($"{hypotheses.Length} translations, {references.Length} references");
    }

    Console.WriteLine($"chrF2 {Chrf.Corpus(hypotheses.Zip(references)).ToString("0.00", CultureInfo.InvariantCulture)} ({hypotheses.Length} sentences)");
    return 0;
}

// iris score <model.iris> <source.txt> <references.txt> [--beams N | --greedy] [--int8] [--limit N] [--out file]:
// translates a test set line by line and measures chrF against the references
static int Score(string modelPath, string sourcePath, string referencesPath, string[] rest)
{
    int? beams = null;
    int? limit = null;
    string? outPath = null;
    var int8 = false;
    for (var i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--greedy": beams = 1; break;
            case "--beams" when i + 1 < rest.Length: beams = int.Parse(rest[++i], CultureInfo.InvariantCulture); break;
            case "--limit" when i + 1 < rest.Length: limit = int.Parse(rest[++i], CultureInfo.InvariantCulture); break;
            case "--out" when i + 1 < rest.Length: outPath = rest[++i]; break;
            case "--int8": int8 = true; break;
            default: throw new ArgumentException($"unknown option {rest[i]}");
        }
    }

    var sources = File.ReadAllLines(sourcePath).Take(limit ?? int.MaxValue).ToList();
    var references = File.ReadAllLines(referencesPath).Take(sources.Count).ToList();
    if (references.Count != sources.Count)
    {
        throw new ArgumentException($"{sources.Count} source lines, {references.Count} references");
    }

    var translator = Translator.Load(modelPath, int8);
    var watch = Stopwatch.StartNew();
    var translations = new List<string>();
    foreach (var line in sources)
    {
        translations.Add(translator.Translate(line, beams).Text);
        if (translations.Count % 100 == 0)
        {
            Console.WriteLine($"  {translations.Count}/{sources.Count}");
        }
    }

    if (outPath is not null)
    {
        File.WriteAllLines(outPath, translations);
    }

    var score = Chrf.Corpus(translations.Zip(references));
    var name = (beams ?? translator.Settings.NumBeams) == 1 ? "greedy" : $"beam {beams ?? translator.Settings.NumBeams}";
    Console.WriteLine($"chrF2 {score.ToString("0.00", CultureInfo.InvariantCulture)} ({sources.Count} sentences, {name}" +
                      $"{(translator.Model.IsInt8 ? ", int8" : "")}, {watch.Elapsed.TotalSeconds:0} s)");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        iris tokenize <model.spm> <text>           pieces and ids of a SentencePiece model
        iris info <model.iris>                     what an IRIS model file holds
        iris translate <model.iris> [options] [text]
                                                   translates the text, or every line of the input
            --beams N                              beam search with N beams (default: the model's setting)
            --greedy                               greedy search (1 beam)
            --ids                                  also shows the token ids
            --int8                                 int8 weights (float32 models are turned into int8 when read)
        iris verify <model.iris> <reference.json> [options]
                                                   compares the core with reference data (tools/reference)
            --min-greedy R                         greedy matches needed (default 0.99)
            --min-beam R                           beam matches needed (default 0.95)
            --min-tokenizer R                      tokenizer matches needed (default 1)
            --limit N                              only the first N sentences
            --no-greedy, --no-beam                 skip a search
            --int8                                 int8 weights
        iris bench <model.iris> [--runs N] [--max-seconds S] [--int8]
                                                   time of the title and introduction of an article
        iris quantize <model.iris> <model-int8.iris>
                                                   an int8 copy of a model file (about a quarter of the size)
        iris score <model.iris> <source.txt> <references.txt> [--beams N | --greedy] [--int8] [--limit N] [--out file]
                                                   translates a test set line by line, chrF against the references
        iris chrf <translations.txt> <references.txt>
                                                   chrF (character 6-grams, β = 2, as sacreBLEU) of two files
        """);
    return 2;
}
