using System.Globalization;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// The history of the classroom: for every computer one row a minute (the mean of its reports in that minute –
/// processor, learning, memory, temperature, lines per second, state), kept in a file per day; files older than the
/// days to keep are removed. The console asks for any span and gets it averaged into buckets.
/// </summary>
public sealed class LabHistory
{
    private readonly string _folder;
    private readonly int _days;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Minute> _open = new(StringComparer.OrdinalIgnoreCase);

    public LabHistory(string folder, int days = 60)
    {
        _folder = folder;
        _days = days;
        Directory.CreateDirectory(folder);
        Prune(DateTime.Now);
    }

    /// <summary>A report of a computer (the status it sent) at a moment.</summary>
    public void Record(string agent, JsonObject status, DateTime now)
    {
        var machine = status["machine"] as JsonObject;
        var last = status["last"] as JsonObject;
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        lock (_gate)
        {
            if (_open.TryGetValue(agent, out var open) && open.Time != minute)
            {
                Write(agent, open);
                _open.Remove(agent);
            }

            if (!_open.TryGetValue(agent, out open))
            {
                open = new Minute(minute);
                _open[agent] = open;
            }

            open.Add("cpu", machine?["cpu"]);
            open.Add("own", machine?["cpu_own"]);
            open.Add("memory", machine?["memory"]);
            open.Add("temperature", machine?["temperature"]);
            open.Add("lps", status["state"]?.GetValue<string>() == "learning" ? last?["lines_per_second"] : JsonValue.Create(0));
            open.State = status["state"]?.GetValue<string>() ?? open.State;
        }
    }

    /// <summary>Writes the minutes still open (at the end, or before a query reads the files).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            foreach (var (agent, minute) in _open)
            {
                Write(agent, minute);
            }

            _open.Clear();
        }
    }

    /// <summary>
    /// The rows between two moments (one computer, or all when agent is null), averaged into buckets of the given
    /// minutes: {"series": {"PC-01": [{"t", "cpu", "own", "memory", "temperature", "lps"}, …], …}}.
    /// </summary>
    public JsonObject Query(DateTime from, DateTime to, string? agent, int bucketMinutes)
    {
        Flush();
        bucketMinutes = Math.Max(1, bucketMinutes);
        var buckets = new SortedDictionary<string, SortedDictionary<DateTime, Minute>>(StringComparer.OrdinalIgnoreCase);
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var path = DayFile(day);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (JsonNode.Parse(line) is not JsonObject row)
                {
                    continue;
                }

                var name = row["pc"]!.GetValue<string>();
                var time = DateTime.ParseExact(row["t"]!.GetValue<string>(), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                if (time < from || time > to || (agent is not null && !string.Equals(name, agent, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var start = from.AddMinutes(Math.Floor((time - from).TotalMinutes / bucketMinutes) * bucketMinutes);
                if (!buckets.TryGetValue(name, out var series))
                {
                    buckets[name] = series = [];
                }

                if (!series.TryGetValue(start, out var bucket))
                {
                    series[start] = bucket = new Minute(start);
                }

                foreach (var key in Minute.Keys)
                {
                    bucket.Add(key, row[key]);
                }
            }
        }

        var result = new JsonObject();
        foreach (var (name, series) in buckets)
        {
            result[name] = new JsonArray([.. series.Values.Select(b => (JsonNode?)b.ToJson(name))]);
        }

        return new JsonObject { ["series"] = result, ["bucket"] = bucketMinutes };
    }

    private void Write(string agent, Minute minute)
    {
        try
        {
            File.AppendAllText(DayFile(minute.Time), minute.ToJson(agent).ToJsonString() + "\n");
        }
        catch (IOException)
        {
            // the history is a help; the learning goes on without it
        }
    }

    private string DayFile(DateTime day) => Path.Combine(_folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    private void Prune(DateTime now)
    {
        foreach (var file in Directory.EnumerateFiles(_folder, "*.jsonl"))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                day < now.Date.AddDays(-_days))
            {
                File.Delete(file);
            }
        }
    }

    // the sums of one minute (or one bucket) of one computer
    private sealed class Minute(DateTime time)
    {
        public static readonly string[] Keys = ["cpu", "own", "memory", "temperature", "lps"];

        private readonly Dictionary<string, (double Sum, int Count)> _values = new(StringComparer.Ordinal);

        public DateTime Time { get; } = time;

        public string State { get; set; } = "";

        public void Add(string key, JsonNode? value)
        {
            if (value is JsonValue v && v.TryGetValue(out double number) && double.IsFinite(number))
            {
                var (sum, count) = _values.GetValueOrDefault(key);
                _values[key] = (sum + number, count + 1);
            }
        }

        public JsonObject ToJson(string agent)
        {
            var row = new JsonObject { ["t"] = Time.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), ["pc"] = agent };
            foreach (var key in Keys)
            {
                row[key] = _values.TryGetValue(key, out var v) && v.Count > 0 ? Math.Round(v.Sum / v.Count, 1) : null;
            }

            if (State.Length > 0)
            {
                row["state"] = State;
            }

            return row;
        }
    }
}
