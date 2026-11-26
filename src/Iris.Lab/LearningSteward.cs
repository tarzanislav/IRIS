using System.Globalization;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>A test of the classroom's model by the server: its version and CER of the model and of its average (%).</summary>
public sealed record PlanTest(int Version, double Cer, double? CerUnseen, double? AverageCer, double? AverageUnseen)
{
    /// <summary>How well it reads: the mean of the fonts it learns from and the unseen font (lower is better).</summary>
    public static double Score(double cer, double? unseen) => unseen is { } u ? (cer + u) / 2 : cer;

    public double ModelScore => Score(Cer, CerUnseen);

    public double? AverageScore => AverageCer is { } a ? Score(a, AverageUnseen) : null;

    /// <summary>The better of the model and its average.</summary>
    public double BestScore => AverageScore is { } a ? Math.Min(a, ModelScore) : ModelScore;

    public static PlanTest? From(JsonObject entry)
    {
        static double? D(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : null;
        return entry["version"] is JsonValue v && v.TryGetValue(out int version) && D(entry["cer"]) is { } cer
            ? new PlanTest(version, cer, D(entry["cer_unseen"]), D(entry["average_cer"]), D(entry["average_cer_unseen"]))
            : null;
    }
}

/// <summary>
/// What the steward decided at the end of a plan: the next plan's highest rate and rounds, whether the next plan
/// starts from the average of the model, the kind of the decision and why.
/// </summary>
public sealed record PlanDecision(double Rate, int Rounds, bool FromAverage, string Kind, string Reason, double? Gain, int Plateaus)
{
    public JsonObject ToJson(DateTime time, int version) => new()
    {
        ["time"] = time.ToString("o", CultureInfo.InvariantCulture),
        ["version"] = version,
        ["rate"] = Rate,
        ["rounds"] = Rounds,
        ["from_average"] = FromAverage,
        ["kind"] = Kind,
        ["reason"] = Reason,
        ["gain"] = Gain is { } g ? Math.Round(g * 100, 2) : null,
        ["plateaus"] = Plateaus,
    };
}

/// <summary>
/// The steward of the learning: when a plan of the rate ends, it judges the plan by the server's tests and starts
/// the next one by itself – nobody has to wait for the administrator. Cosine cycles with warm restarts, each set by
/// how much the last one brought:
/// <list type="bullet">
/// <item>it read clearly better (≥ 3 % lower CER than the best before it) – the same rate again;</item>
/// <item>somewhat better (≥ 0.5 %) – a lower rate (×0.6): finer steps where the gains get small;</item>
/// <item>about the same – a plateau: half the rate on a longer plan; every third plateau one exploring plan at twice
/// the rate (a warm restart can leave a valley the small steps cannot); at the lowest rate the learning is saturated –
/// it goes on gently and says so (more gain needs new data or a bigger model, not more rounds);</item>
/// <item>clearly worse (the rate was too high, or the exploration did not pay) – half the rate, and from the average of
/// the model when that reads better.</item>
/// </list>
/// A plan with fewer than three tests is repeated as it was. The next plan starts from the average of the model when
/// the last test read it at least 1 % better than the model itself.
/// </summary>
public static class LearningSteward
{
    /// <summary>A plan at least this much better is a good one (relative to the best CER before it).</summary>
    public const double Good = 0.03;

    /// <summary>Less than this is no progress.</summary>
    public const double Some = 0.005;

    public const double MinRate = 2e-5;

    public const double MaxRate = 3e-4;

    public const int MinRounds = 300;

    public const int MaxRounds = 1500;

    /// <param name="rate">The highest rate of the plan that ended.</param>
    /// <param name="rounds">Its rounds.</param>
    /// <param name="start">The version it started at.</param>
    /// <param name="end">The version it ended at.</param>
    /// <param name="tests">The server's tests (any order; also from before the plan).</param>
    /// <param name="plateaus">How many plans in a row brought no progress before this one.</param>
    public static PlanDecision Next(double rate, int rounds, int start, int end, IReadOnlyList<PlanTest> tests, int plateaus)
    {
        rate = Math.Clamp(rate, MinRate, MaxRate);
        rounds = Math.Clamp(rounds, MinRounds, MaxRounds);
        var before = tests.Where(t => t.Version <= start).Select(t => t.BestScore).DefaultIfEmpty(double.NaN).Min();
        var during = tests.Where(t => t.Version > start && t.Version <= end).OrderBy(t => t.Version).ToList();
        var last = during.LastOrDefault();
        var fromAverage = last?.AverageScore is { } a && a < last.ModelScore * 0.99;
        if (during.Count < 3 || !double.IsFinite(before) || before <= 0)
        {
            return new PlanDecision(rate, rounds, fromAverage, "repeat", "málo meraní – plán sa opakuje", null, plateaus);
        }

        var gain = (before - during.Min(t => t.BestScore)) / before;
        if (gain >= Good)
        {
            return new PlanDecision(rate, rounds, fromAverage, "keep", $"model sa zlepšil o {gain:P1} – rovnaká rýchlosť", gain, 0);
        }

        if (gain >= Some)
        {
            return new PlanDecision(Lower(rate, 0.6), rounds, fromAverage, "lower", $"zlepšenie {gain:P1} – jemnejšie kroky", gain, 0);
        }

        if (gain > -Good)
        {
            var count = plateaus + 1;
            if (count % 3 == 0 && rate * 2 <= MaxRate)
            {
                return new PlanDecision(rate * 2, rounds, fromAverage, "explore", "tretí plán bez zlepšenia – skúšobný plán s vyššou rýchlosťou", gain, count);
            }

            if (rate <= MinRate * 1.0001)
            {
                return new PlanDecision(MinRate, MaxRounds, fromAverage, "saturated",
                    "učenie je nasýtené – ďalšie zlepšenie prinesú nové dáta alebo väčší model, nie ďalšie kolá", gain, count);
            }

            return new PlanDecision(Lower(rate, 0.5), Math.Min(MaxRounds, (int)(rounds * 1.5)), fromAverage, "plateau", "bez zlepšenia – polovičná rýchlosť na dlhší plán", gain, count);
        }

        // worse than before the plan: back to smaller steps, from the average when it reads better
        return new PlanDecision(Lower(rate, 0.5), rounds, fromAverage, "repair", $"model sa zhoršil o {-gain:P1} – polovičná rýchlosť", gain, plateaus);
    }

    private static double Lower(double rate, double factor) => Math.Max(MinRate, Math.Round(rate * factor, 7));
}

/// <summary>The steward's settings and memory, kept by the hub in plan-auto.json: on or off, plateaus in a row, the last decision.</summary>
public sealed class StewardState
{
    public bool On { get; set; } = true;

    public int Plateaus { get; set; }

    public JsonObject? Last { get; set; }

    public static StewardState Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject json)
            {
                return new StewardState
                {
                    On = json["on"] is not JsonValue on || !on.TryGetValue(out bool value) || value,
                    Plateaus = json["plateaus"] is JsonValue p && p.TryGetValue(out int n) ? Math.Max(0, n) : 0,
                    Last = json["last"] as JsonObject,
                };
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
        }

        return new StewardState();
    }

    public JsonObject ToJson() => new() { ["on"] = On, ["plateaus"] = Plateaus, ["last"] = Last?.DeepClone() };

    public void Save(string path)
    {
        File.WriteAllText(path + ".new", ToJson().ToJsonString());
        File.Move(path + ".new", path, overwrite: true);
    }
}
