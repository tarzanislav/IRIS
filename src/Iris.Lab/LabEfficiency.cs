using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// The log of efficiency: one line per computer and round – what it learned, in what time, with what load, how hard
/// it worked and whether it had to step aside – kept for 60 days in the hub's folder (efficiency.jsonl). From it the
/// console shows the share of every computer and group, and saves a table to send for analysis. No addresses, no
/// card numbers: names, the kind of hardware and numbers only.
/// </summary>
public sealed class EfficiencyLog
{
    public const int Days = 60;
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Func<DateTime> _now;
    private DateTime _pruned = DateTime.MinValue;

    public EfficiencyLog(string folder, Func<DateTime> now)
    {
        _path = Path.Combine(folder, "efficiency.jsonl");
        _now = now;
    }

    /// <summary>The columns of the table, in their order (the names of the fields of a line).</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "time", "round", "computer", "name", "group", "model", "cpu", "logical", "lines", "seconds", "asked", "stopped", "used",
        "threads", "duty", "eco", "moving", "per_thread", "cpu_own", "cpu_total", "cpu_others", "strain", "reasons", "loss",
    ];

    /// <summary>Writes the line of one computer for one round (the time is added); once a day the old lines go.</summary>
    public void Add(JsonObject line)
    {
        var now = _now();
        line["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            try
            {
                if (now - _pruned > TimeSpan.FromDays(1))
                {
                    Prune(now);
                }

                File.AppendAllText(_path, line.ToJsonString() + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the log is a help; the learning goes on without it
            }
        }
    }

    /// <summary>The lines since a time, the oldest first (a broken line is skipped).</summary>
    public List<JsonObject> Since(DateTime since)
    {
        var result = new List<JsonObject>();
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return result;
            }

            foreach (var text in File.ReadLines(_path))
            {
                if (Parse(text) is { } line && Time(line) >= since)
                {
                    result.Add(line);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// What every computer and every group did in these lines: lines and their share of all, hours of learning,
    /// rounds (used, stopped early), lines per second and thread, the load it had, how often it struggled or was
    /// busy and why. The busiest first.
    /// </summary>
    public static JsonObject Summary(IReadOnlyList<JsonObject> lines, Func<string, string?>? alias = null)
    {
        var all = lines.Sum(l => Number(l, "lines"));
        JsonObject Of(IEnumerable<JsonObject> part, string key, string? label)
        {
            var list = part.ToList();
            var learned = list.Sum(l => Number(l, "lines"));
            var threadSeconds = list.Sum(l => Number(l, "seconds") * Math.Max(0.05, Number(l, "duty")) * Math.Max(1, Number(l, "threads")));
            var reasons = list.SelectMany(l => (l["reasons"]?.GetValue<string>() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                .GroupBy(r => r, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => (JsonNode?)new JsonObject { ["code"] = g.Key, ["rounds"] = g.Count() });
            return new JsonObject
            {
                ["key"] = key,
                ["name"] = label,
                ["model"] = list.LastOrDefault()?["model"]?.GetValue<string>(),
                ["cpu"] = list.LastOrDefault()?["cpu"]?.GetValue<string>(),
                ["logical"] = list.LastOrDefault()?["logical"]?.GetValue<int>(),
                ["computers"] = list.Select(l => l["computer"]?.GetValue<string>()).Distinct().Count(),
                ["lines"] = learned,
                ["share"] = all > 0 ? Math.Round(100 * learned / all, 2) : 0,
                ["hours"] = Math.Round(list.Sum(l => Number(l, "seconds")) / 3600, 3),
                ["rounds"] = list.Count,
                ["used"] = list.Count(l => l["used"]?.GetValue<bool>() == true),
                ["stopped"] = list.Count(l => l["stopped"]?.GetValue<bool>() == true),
                ["per_thread"] = threadSeconds > 0 ? Math.Round(learned / threadSeconds, 3) : null,
                ["per_second"] = list.Sum(l => Number(l, "seconds")) is > 0 and var s ? Math.Round(learned / s, 2) : null,
                ["threads"] = Mean(list, "threads", 1),
                ["duty"] = Mean(list, "duty", 2),
                ["cpu_own"] = Mean(list, "cpu_own", 1),
                ["cpu_others"] = Mean(list, "cpu_others", 1),
                ["struggled"] = list.Count(l => Number(l, "strain") >= 2),
                ["busy"] = list.Count(l => Number(l, "strain") is >= 1 and < 2),
                ["reasons"] = new JsonArray([.. reasons]),
            };
        }

        var computers = lines.GroupBy(l => l["computer"]?.GetValue<string>() ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => Of(g, g.Key, alias?.Invoke(g.Key) ?? g.Last()["name"]?.GetValue<string>() ?? g.Key))
            .OrderByDescending(c => c["lines"]!.GetValue<double>());
        var groups = lines.GroupBy(l => l["group"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => Of(g, g.Key, g.Key.Length == 0 ? null : g.Key))
            .OrderByDescending(c => c["lines"]!.GetValue<double>());
        return new JsonObject
        {
            ["lines"] = all,
            ["rounds"] = lines.Select(l => Number(l, "round")).Distinct().Count(),
            ["from"] = lines.Count > 0 ? lines[0]["time"]?.GetValue<string>() : null,
            ["until"] = lines.Count > 0 ? lines[^1]["time"]?.GetValue<string>() : null,
            ["computers"] = new JsonArray([.. computers.Select(c => (JsonNode?)c)]),
            ["groups"] = new JsonArray([.. groups.Select(c => (JsonNode?)c)]),
        };
    }

    /// <summary>
    /// The lines as a table to open in Excel and to send for analysis: semicolons, decimal commas, UTF-8 with its mark,
    /// the columns of <see cref="Columns"/>.
    /// </summary>
    public static byte[] Table(IEnumerable<JsonObject> lines)
    {
        var text = new StringBuilder();
        text.Append(string.Join(';', Columns)).Append("\r\n");
        foreach (var line in lines)
        {
            text.Append(string.Join(';', Columns.Select(c => Cell(line[c])))).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    // the decimal comma is written by hand: the server runs without the data of cultures (no ICU on a bare Debian)
    private static string Cell(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return "";
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag ? "1" : "0";
        }

        if (value.GetValueKind() == JsonValueKind.Number)
        {
            return value.GetValue<double>().ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        var text = value.ToString();
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    private void Prune(DateTime now)
    {
        _pruned = now;
        if (!File.Exists(_path))
        {
            return;
        }

        var keep = File.ReadLines(_path).Where(t => Parse(t) is { } line && Time(line) >= now.AddDays(-Days)).ToList();
        File.WriteAllLines(_path + ".new", keep);
        File.Move(_path + ".new", _path, overwrite: true);
    }

    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime Time(JsonObject line) =>
        DateTime.TryParse(line["time"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : DateTime.MinValue;

    private static double Number(JsonObject line, string key) =>
        line[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : line[key] is JsonValue b && b.TryGetValue<bool>(out var f) ? (f ? 1 : 0) : 0;

    private static double? Mean(List<JsonObject> lines, string key, int digits)
    {
        var values = lines.Where(l => l[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number).Select(l => Number(l, key)).ToList();
        return values.Count > 0 ? Math.Round(values.Average(), digits) : null;
    }
}
