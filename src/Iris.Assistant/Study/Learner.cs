using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Iris.Assistant.Packs;
using Iris.Evaluation;
using Iris.Learning;
using Iris.Model;
using Iris.Translation;

namespace Iris.Assistant.Study;

/// <summary>
/// How a learning went: how many pairs it learned from, how many were kept aside to check it, chrF of IRIS on those
/// before and after, and whether the result is used (only when it is not worse).
/// </summary>
public sealed record LearningReport(string Pair, DateTimeOffset Learned, int Pairs, int Checked, double ChrfBefore, double ChrfAfter,
    bool Accepted, double Seconds, string ModelSha256);

/// <summary>
/// Learns an adapter for one direction from the owner's verified pairs and corrections. Every tenth pair (chosen by
/// its text, so always the same ones) is kept aside: IRIS translates them before and after, and the adapter is used
/// only when chrF on them did not fall. A rejected adapter never replaces the one in use.
/// </summary>
public static class Learner
{
    /// <summary>Fewer pairs than this teach nothing that could be checked.</summary>
    public const int MinimumPairs = 20;

    public static LearningReport Learn(LanguagePair pair, string modelPath, IReadOnlyList<(string Source, string Target)> pairs,
        string adapterPath, LearningSettings? settings = null, IProgress<LearningProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var distinct = pairs.Select(p => (Source: p.Source.Trim(), Target: p.Target.Trim()))
            .Where(p => p.Source.Length > 0 && p.Target.Length > 0).Distinct().ToList();
        if (distinct.Count < MinimumPairs)
        {
            throw new InvalidOperationException($"at least {MinimumPairs} pairs are needed, there are {distinct.Count}");
        }

        var watch = Stopwatch.StartNew();
        var translator = Translator.Load(modelPath);
        var sha = ModelFile.Sha256(modelPath);
        var check = distinct.Where(p => Stable(p.Source) % 10 == 0).ToList();
        if (check.Count < 2)
        {
            check = [.. distinct.OrderBy(p => Stable(p.Source)).Take(2)];
        }

        var learn = distinct.Except(check).ToList();
        var training = learn.Select(p => new TrainingPair(translator.Tokenizer.Encode(p.Source), translator.Tokenizer.EncodeTarget(p.Target))).ToList();
        var adapter = Adapter.Create(translator.Model, sha);
        var trainer = new MarianTrainer(translator.Model, adapter);
        settings ??= new LearningSettings { Epochs = learn.Count < 200 ? 5 : 3 };
        var before = Score(translator, check);
        trainer.Train(training, settings, progress, cancellation);
        var after = Score(new Translator(trainer.Model, translator.Tokenizer), check);
        var accepted = after >= before;
        var report = new LearningReport(pair.ToString(), DateTimeOffset.Now, learn.Count, check.Count, Math.Round(before, 2), Math.Round(after, 2),
            accepted, Math.Round(watch.Elapsed.TotalSeconds, 1), sha);
        if (accepted)
        {
            adapter.With(new System.Text.Json.Nodes.JsonObject
            {
                ["learned"] = report.Learned.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["pairs"] = report.Pairs,
                ["checked"] = report.Checked,
                ["chrf_before"] = report.ChrfBefore,
                ["chrf_after"] = report.ChrfAfter,
            }).Save(adapterPath);
        }

        WriteReport(ReportPath(adapterPath), report);
        return report;
    }

    /// <summary>The report of the last learning of an adapter, or null.</summary>
    public static LearningReport? LastReport(string adapterPath)
    {
        try
        {
            var path = ReportPath(adapterPath);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), HistoryJson.Default.LearningReport) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ReportPath(string adapterPath) => Path.ChangeExtension(adapterPath, ".json");

    private static void WriteReport(string path, LearningReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, HistoryJson.Default.LearningReport), new UTF8Encoding(false));
    }

    private static double Score(Translator translator, List<(string Source, string Target)> pairs) =>
        Chrf.Corpus(pairs.Select(p => (translator.TranslateSentence(p.Source, 1).Target, p.Target)));

    // the same number for the same text on every computer (string.GetHashCode differs between runs)
    private static uint Stable(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
