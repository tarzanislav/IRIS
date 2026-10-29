using System.Diagnostics;
using System.Globalization;
using System.Text;
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
            default:
                words.Add(rest[i]);
                break;
        }
    }

    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath);
    Console.Error.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.0} s");
    void One(string text)
    {
        var result = translator.Translate(text, beams);
        Console.WriteLine(result.Text);
        if (showIds)
        {
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
    bool greedy = true, beam = true;
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
            default: throw new ArgumentException($"unknown option {rest[i]}");
        }
    }

    var watch = Stopwatch.StartNew();
    var translator = Translator.Load(modelPath);
    var reference = ReferenceSet.Load(referencePath);
    Console.WriteLine($"model loaded in {watch.Elapsed.TotalSeconds:0.0} s; reference: {reference.Sentences.Count} sentences, {reference.Beams} beams");
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
        iris verify <model.iris> <reference.json> [options]
                                                   compares the core with reference data (tools/reference)
            --min-greedy R                         greedy matches needed (default 0.99)
            --min-beam R                           beam matches needed (default 0.95)
            --min-tokenizer R                      tokenizer matches needed (default 1)
            --limit N                              only the first N sentences
            --no-greedy, --no-beam                 skip a search
        """);
    return 2;
}
