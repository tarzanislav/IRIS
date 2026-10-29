using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Iris.Translation;

namespace Iris.Cli;

/// <summary>One sentence of reference data: what the reference implementation gave for it.</summary>
internal sealed record ReferenceSentence(
    string Text,
    int[] Source,
    int[]? Greedy,
    string? GreedyText,
    int[]? Beam,
    string? BeamText,
    float? BeamScore,
    (int Id, float Logit)[] FirstStep);

/// <summary>Reference data made in CI by tools/reference (Hugging Face transformers + PyTorch).</summary>
internal sealed record ReferenceSet(int Beams, string Description, IReadOnlyList<ReferenceSentence> Sentences)
{
    public static ReferenceSet Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        static int[]? Ids(JsonElement item, string name) =>
            item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? [.. p.EnumerateArray().Select(e => e.GetInt32())] : null;
        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var sentences = new List<ReferenceSentence>();
        foreach (var item in root.GetProperty("sentences").EnumerateArray())
        {
            var first = item.TryGetProperty("first_step", out var f)
                ? f.EnumerateArray().Select(pair => (pair[0].GetInt32(), pair[1].GetSingle())).ToArray()
                : [];
            sentences.Add(new ReferenceSentence(
                item.GetProperty("text").GetString() ?? "",
                Ids(item, "source") ?? [],
                Ids(item, "greedy"),
                Text(item, "greedy_text"),
                Ids(item, "beam"),
                Text(item, "beam_text"),
                item.TryGetProperty("beam_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetSingle() : null,
                first));
        }

        var description = root.TryGetProperty("model", out var model) ? model.GetRawText() : "";
        return new ReferenceSet(root.TryGetProperty("beams", out var b) ? b.GetInt32() : 4, description, sentences);
    }
}

/// <summary>How well the own core repeats the reference: matches of ids and texts, differences of logits, time.</summary>
internal sealed class CheckReport
{
    private const int ShownMismatches = 20;

    public int Sentences { get; internal set; }

    public int TokenizerMatches { get; internal set; }

    public int GreedyChecked { get; internal set; }

    public int GreedyMatches { get; internal set; }

    public int GreedyTextMatches { get; internal set; }

    public int BeamChecked { get; internal set; }

    public int BeamMatches { get; internal set; }

    public int BeamTextMatches { get; internal set; }

    /// <summary>The largest |own − reference| over the best logits of the first decoder step.</summary>
    public double FirstStepDifference { get; internal set; }

    /// <summary>The largest |own − reference| of the score of the best beam (sentences whose beams match).</summary>
    public double BeamScoreDifference { get; internal set; }

    public double GreedySeconds { get; internal set; }

    public double BeamSeconds { get; internal set; }

    public int GeneratedTokens { get; internal set; }

    public List<string> Mismatches { get; } = [];

    public double TokenizerRate => Rate(TokenizerMatches, Sentences);

    public double GreedyRate => Rate(GreedyMatches, GreedyChecked);

    public double BeamRate => Rate(BeamMatches, BeamChecked);

    internal void Note(string mismatch)
    {
        if (Mismatches.Count < ShownMismatches)
        {
            Mismatches.Add(mismatch);
        }
    }

    public string Summary()
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"sentences {Sentences} · tokenizer {TokenizerMatches}/{Sentences} ({TokenizerRate:P1})");
        if (GreedyChecked > 0)
        {
            text.Append(c, $" · greedy {GreedyMatches}/{GreedyChecked} ({GreedyRate:P1}), texts {GreedyTextMatches}");
        }

        if (BeamChecked > 0)
        {
            text.Append(c, $" · beam {BeamMatches}/{BeamChecked} ({BeamRate:P1}), texts {BeamTextMatches}, score max |Δ| {BeamScoreDifference:G3}");
        }

        text.Append(c, $" · first-step logits max |Δ| {FirstStepDifference:G3}");
        var seconds = GreedySeconds + BeamSeconds;
        text.Append(c, $" · {GreedySeconds:0.0} s greedy, {BeamSeconds:0.0} s beam, {GeneratedTokens / Math.Max(seconds, 1e-9):0} tokens/s");
        return text.ToString();
    }

    private static double Rate(int part, int whole) => whole == 0 ? 1 : (double)part / whole;
}

/// <summary>Checks the own core against reference data, sentence by sentence.</summary>
internal static class ReferenceCheck
{
    public static CheckReport Run(Translator translator, ReferenceSet reference, bool greedy = true, bool beam = true, int? limit = null,
        Action<int, int>? progress = null)
    {
        var report = new CheckReport();
        var sentences = reference.Sentences.Take(limit ?? int.MaxValue).ToList();
        var greedyWatch = new Stopwatch();
        var beamWatch = new Stopwatch();
        for (var i = 0; i < sentences.Count; i++)
        {
            var s = sentences[i];
            report.Sentences++;

            // the tokenizer; the search below starts from the reference ids, so the model is checked on its own
            var source = translator.Tokenizer.Encode(s.Text);
            if (source.SequenceEqual(s.Source))
            {
                report.TokenizerMatches++;
            }
            else
            {
                report.Note($"#{i} tokenizer: expected [{Show(s.Source)}], actual [{Show(source)}]");
            }

            if (s.FirstStep.Length > 0)
            {
                var session = translator.Model.StartDecoding(translator.Model.Encode(s.Source), 1);
                var logits = session.Step([translator.Settings.DecoderStartId]);
                foreach (var (id, logit) in s.FirstStep)
                {
                    report.FirstStepDifference = Math.Max(report.FirstStepDifference, Math.Abs(logits[id] - logit));
                }
            }

            if (greedy && s.Greedy is { } expectedGreedy)
            {
                greedyWatch.Start();
                var found = translator.Generate(s.Source, 1);
                greedyWatch.Stop();
                report.GreedyChecked++;
                report.GeneratedTokens += found.Tokens.Length;
                if (Same(report, i, "greedy", expectedGreedy, found.Tokens))
                {
                    report.GreedyMatches++;
                }

                if (s.GreedyText is { } text && translator.Tokenizer.Decode(found.Tokens) == text)
                {
                    report.GreedyTextMatches++;
                }
            }

            if (beam && s.Beam is { } expectedBeam)
            {
                beamWatch.Start();
                var found = translator.Generate(s.Source, reference.Beams);
                beamWatch.Stop();
                report.BeamChecked++;
                report.GeneratedTokens += found.Tokens.Length;
                if (Same(report, i, "beam", expectedBeam, found.Tokens))
                {
                    report.BeamMatches++;
                    if (s.BeamScore is { } score)
                    {
                        report.BeamScoreDifference = Math.Max(report.BeamScoreDifference, Math.Abs(found.Score - score));
                    }
                }

                if (s.BeamText is { } text && translator.Tokenizer.Decode(found.Tokens) == text)
                {
                    report.BeamTextMatches++;
                }
            }

            progress?.Invoke(i + 1, sentences.Count);
        }

        report.GreedySeconds = greedyWatch.Elapsed.TotalSeconds;
        report.BeamSeconds = beamWatch.Elapsed.TotalSeconds;
        return report;
    }

    // the ids are the same; when not, the first difference is noted
    private static bool Same(CheckReport report, int index, string what, int[] expected, int[] actual)
    {
        if (expected.SequenceEqual(actual))
        {
            return true;
        }

        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        report.Note($"#{index} {what}: first difference at token {at} of {expected.Length}: expected [{Show(expected.Skip(at).Take(8))}], " +
                    $"actual [{Show(actual.Skip(at).Take(8))}]");
        return false;
    }

    private static string Show(IEnumerable<int> ids) => string.Join(" ", ids);
}
