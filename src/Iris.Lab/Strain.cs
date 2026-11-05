using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// What tells whether a computer struggles, read without any driver: how fast the processor runs against its base
/// clock (it falls when the processor gets hot or meets its power limit), the ceiling the firmware sets (100 % =
/// none), the threads waiting for a processor, the free memory, the pages read back from the disk because memory
/// is short, and how busy the disk is. A value the system does not give is null.
/// </summary>
public sealed record StrainSignals(double? Performance, double? Limit, double? Queue, double? FreeMb, double? PagesIn, double? DiskBusy)
{
    public static StrainSignals None { get; } = new(null, null, null, null, null, null);
}

/// <summary>How a computer is doing: 0 fine, 1 busy, 2 struggling – with the reasons (a code and its value).</summary>
public sealed record Strain(int Level, IReadOnlyList<(string Code, double Value)> Reasons)
{
    public JsonObject ToJson() => new()
    {
        ["level"] = Level,
        ["reasons"] = new JsonArray([.. Reasons.Select(r => (JsonNode?)new JsonObject { ["code"] = r.Code, ["value"] = Math.Round(r.Value, 1) })]),
    };
}

/// <summary>
/// Judges the strain of one computer from what it measures every few seconds and from its rounds. Every computer is
/// its own measure: the speed of its processor, the ceiling of its clock and the speed of its learning are compared
/// with what this computer usually does in the same mode (efficient or not; for the learning also with the same
/// threads and share of time) – so an office computer with a low power limit and a fast desktop are both judged
/// fairly, and a lower load is never taken for a slower computer. A level counts only when it held in at least
/// three of the last six samples: a spike, or the minute after a start, is no struggle.
/// </summary>
public sealed class StrainJudge
{
    private const int Window = 6;
    private const int Need = 3;
    private readonly Queue<(int Level, List<(string, double)> Reasons)> _recent = new();
    private readonly Dictionary<bool, List<double>> _performance = new() { [true] = [], [false] = [] };
    private readonly List<double> _limits = [];
    private readonly Dictionary<string, List<double>> _speed = new(StringComparer.Ordinal);
    private (string Key, double Share)? _lastSpeed;

    /// <summary>
    /// The usual speed of the busy cores of this computer (90th percentile over the last hours), or null while
    /// unknown – the speed of the processor is judged only against it.
    /// </summary>
    public double? UsualPerformance(bool eco) => Percentile(_performance[eco], 0.9, 12);

    /// <summary>What a round learned per second and thread (lines), remembered per mode, threads and share of time.</summary>
    public void Round(double linesPerThreadSecond, bool eco, int threads, double duty)
    {
        if (!(linesPerThreadSecond > 0) || !double.IsFinite(linesPerThreadSecond))
        {
            return;
        }

        var key = Key(eco, threads, duty);
        if (!_speed.TryGetValue(key, out var list))
        {
            _speed[key] = list = [];
        }

        _lastSpeed = Percentile(list, 0.8, 3) is { } usual ? (key, linesPerThreadSecond / usual) : null;
        Add(list, linesPerThreadSecond, 30);
    }

    public Strain Judge(MachineSample sample, StrainSignals signals, bool eco, bool learning, int threads = 0, double duty = 0)
    {
        var reasons = new List<(string, double)>();
        var level = 0;
        void Reason(int at, string code, double value)
        {
            level = Math.Max(level, at);
            reasons.Add((code, value));
        }

        // the clock matters only while the processor works (an idle one runs slow on purpose)
        var busy = sample.CpuTotal >= 25;
        if (signals.Limit is { } limit)
        {
            // the ceiling of the clock: a fixed one (the power plan) is the usual one; it counts when it falls below
            // what this computer had – heat or the power supply
            var usual = _limits.Count >= 12 ? _limits.Max() : (double?)null;
            Add(_limits, limit, 1440);
            if (busy && limit < 95 && (usual is { } most ? limit < most - 10 : limit < 80))
            {
                Reason(limit < (usual ?? 100) - 25 ? 2 : 1, "limit", limit);
            }
        }

        if (signals.Performance is { } performance && busy)
        {
            if (UsualPerformance(eco) is { } usual && usual > 0)
            {
                var share = 100 * performance / usual;
                if (share < 80)
                {
                    Reason(2, "slow", share);
                }
                else if (share < 90)
                {
                    Reason(1, "slow", share);
                }
            }

            if (signals.Limit is not < 95)
            {
                Add(_performance[eco], performance, 2160);
            }
        }

        if (signals.Queue is { } queue && sample.Cores > 0)
        {
            if (queue > 2 * sample.Cores)
            {
                Reason(2, "queue", queue);
            }
            else if (queue > sample.Cores)
            {
                Reason(1, "queue", queue);
            }
        }

        if (sample.CpuTotal >= 95)
        {
            Reason(1, "full", sample.CpuTotal);
        }

        if (sample.CpuOthers >= 30)
        {
            // busy with others (someone works, an update, the antivirus): busy, not a struggle by itself
            Reason(1, "others", sample.CpuOthers);
        }

        var free = signals.FreeMb ?? (sample.MemoryTotalGb > 0 ? sample.MemoryTotalGb * 1024 * (100 - sample.MemoryUsed) / 100 : (double?)null);
        if (free is { } mb && sample.MemoryTotalGb > 0)
        {
            var share = mb / (sample.MemoryTotalGb * 1024);
            if (mb < 300 || share < 0.05)
            {
                Reason(2, "memory", mb);
            }
            else if (share < 0.10)
            {
                Reason(1, "memory", mb);
            }
        }

        // pages read from the disk are a struggle only when memory is short: with free memory they are files being
        // opened (a program starting, an update, the antivirus) – Windows counts those reads among them too
        var tight = free is { } left && sample.MemoryTotalGb > 0 && (left < 1024 || left / (sample.MemoryTotalGb * 1024) < 0.15);
        if (signals.PagesIn is { } pages && pages > 20 && tight)
        {
            Reason(pages > 100 ? 2 : 1, "paging", pages);
        }

        if (signals.DiskBusy is { } disk && disk > 90)
        {
            Reason(1, "disk", disk);
        }

        if (learning && _lastSpeed is { } speed && speed.Key == Key(eco, threads, duty) && speed.Share < 0.8)
        {
            Reason(speed.Share < 0.6 ? 2 : 1, "learning", 100 * speed.Share);
        }

        _recent.Enqueue((level, reasons));
        while (_recent.Count > Window)
        {
            _recent.Dequeue();
        }

        // the highest level that held in at least three of the recent samples (a short spike is not a struggle)
        var held = Enumerable.Range(0, 3).Reverse().First(l => l == 0 || _recent.Count(r => r.Level >= l) >= Need);
        var why = _recent.Reverse().FirstOrDefault(r => r.Level >= held && held > 0).Reasons ?? [];
        return new Strain(held, held == 0 ? [] : why);
    }

    private static string Key(bool eco, int threads, double duty) => string.Create(CultureInfo.InvariantCulture, $"{eco}|{threads}|{Math.Round(duty * 20) / 20:0.00}");

    private static void Add(List<double> list, double value, int keep)
    {
        list.Add(value);
        if (list.Count > keep)
        {
            list.RemoveAt(0);
        }
    }

    private static double? Percentile(List<double> values, double p, int least)
    {
        if (values.Count < least)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * (sorted.Count - 1) + 0.5))];
    }
}

/// <summary>
/// Reads the signals of strain. Windows: the plain performance counters (PDH, English names, no administrator
/// rights, no driver) – "% Processor Performance" of every logical processor weighted by how busy it is (idle cores
/// run slow on purpose, so the mean of all would fall with a lower load), "% Performance Limit", "Processor Queue
/// Length", "Available MBytes", "Pages Input/sec" and the idle time of the disks. Linux: /proc and cpufreq.
/// </summary>
internal sealed partial class StrainCounters
{
    private const uint Double = 0x00000200;
    private static readonly string[] Paths =
    [
        @"\Processor Information(_Total)\% Processor Performance",
        @"\Processor Information(_Total)\% Performance Limit",
        @"\System\Processor Queue Length",
        @"\Memory\Available MBytes",
        @"\Memory\Pages Input/sec",
        @"\PhysicalDisk(_Total)\% Idle Time",
        @"\Processor Information(*)\% Processor Performance",
        @"\Processor Information(*)\% Processor Time",
    ];

    private nint _query;
    private readonly nint[] _counters = new nint[Paths.Length];
    private bool _failed;
    private long? _lastMajorFaults;
    private long _lastTicks;

    public StrainSignals Read()
    {
        try
        {
            return OperatingSystem.IsWindows() ? Windows() : OperatingSystem.IsLinux() ? Linux() : StrainSignals.None;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or IndexOutOfRangeException)
        {
            return StrainSignals.None;
        }
    }

    private StrainSignals Windows()
    {
        if (_failed)
        {
            return StrainSignals.None;
        }

        if (_query == 0)
        {
            if (PdhOpenQueryW(null, 0, out _query) != 0)
            {
                _failed = true;
                return StrainSignals.None;
            }

            for (var i = 0; i < Paths.Length; i++)
            {
                // a counter this Windows does not have stays 0 and gives null
                _ = PdhAddEnglishCounterW(_query, Paths[i], 0, out _counters[i]);
            }
        }

        if (PdhCollectQueryData(_query) != 0)
        {
            return StrainSignals.None;
        }

        double? Value(int i)
        {
            if (_counters[i] == 0 || PdhGetFormattedCounterValue(_counters[i], Double, out _, out var value) != 0 || value.Status != 0)
            {
                return null; // also the first reading of a rate, which needs two
            }

            return double.IsFinite(value.Value) ? value.Value : null;
        }

        // the speed of the cores that work: each core's speed weighted by its busy time
        var performance = Value(0);
        var speeds = Instances(6);
        var times = Instances(7);
        if (speeds.Count > 0 && times.Count > 0)
        {
            double weight = 0, sum = 0;
            foreach (var (name, speed) in speeds)
            {
                if (!name.Contains("_Total", StringComparison.Ordinal) && times.TryGetValue(name, out var time) && time > 0)
                {
                    sum += speed * time;
                    weight += time;
                }
            }

            if (weight >= 25)
            {
                performance = sum / weight;
            }
        }

        var idle = Value(5);
        return new StrainSignals(performance, Value(1), Value(2), Value(3), Value(4), idle is { } rest ? Math.Clamp(100 - rest, 0, 100) : null);
    }

    // the values of a counter of many instances (name → value)
    private Dictionary<string, double> Instances(int i)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (_counters[i] == 0)
        {
            return result;
        }

        uint size = 0;
        PdhGetFormattedCounterArrayW(_counters[i], Double, ref size, out var count, 0);
        if (size == 0 || count == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counters[i], Double, ref size, out count, buffer) != 0)
            {
                return result;
            }

            var item = Marshal.SizeOf<CounterItem>();
            for (var k = 0; k < count; k++)
            {
                var value = Marshal.PtrToStructure<CounterItem>(buffer + k * item);
                if (value.Status == 0 && double.IsFinite(value.Value) && Marshal.PtrToStringUni(value.Name) is { } name)
                {
                    result[name] = value.Value;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private StrainSignals Linux()
    {
        double? queue = null;
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (line.StartsWith("procs_running ", StringComparison.Ordinal))
            {
                queue = Math.Max(0, double.Parse(line[14..], CultureInfo.InvariantCulture) - Environment.ProcessorCount);
            }
        }

        double? free = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                free = double.Parse(line[13..].Trim().Split(' ')[0], CultureInfo.InvariantCulture) / 1024;
            }
        }

        double? pages = null;
        var now = Environment.TickCount64;
        foreach (var line in File.ReadLines("/proc/vmstat"))
        {
            if (line.StartsWith("pgmajfault ", StringComparison.Ordinal))
            {
                var faults = long.Parse(line[11..], CultureInfo.InvariantCulture);
                if (_lastMajorFaults is { } last && now > _lastTicks)
                {
                    pages = (faults - last) * 1000.0 / (now - _lastTicks);
                }

                _lastMajorFaults = faults;
                _lastTicks = now;
            }
        }

        // the clock against the highest one, and the ceiling against it (cpufreq, where the system has it)
        double? performance = null, limit = null;
        const string Cpu = "/sys/devices/system/cpu/cpu0/cpufreq";
        if (File.Exists(Path.Combine(Cpu, "cpuinfo_max_freq")))
        {
            var max = double.Parse(File.ReadAllText(Path.Combine(Cpu, "cpuinfo_max_freq")).Trim(), CultureInfo.InvariantCulture);
            if (max > 0)
            {
                var current = Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*")
                    .Select(d => Path.Combine(d, "cpufreq", "scaling_cur_freq")).Where(File.Exists)
                    .Select(f => double.Parse(File.ReadAllText(f).Trim(), CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Average();
                performance = current > 0 ? 100 * current / max : null;
                var ceiling = Path.Combine(Cpu, "scaling_max_freq");
                limit = File.Exists(ceiling) ? 100 * double.Parse(File.ReadAllText(ceiling).Trim(), CultureInfo.InvariantCulture) / max : null;
            }
        }

        return new StrainSignals(performance, limit, queue, free, pages, null);
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nint user, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, nint items);

    // PDH_FMT_COUNTERVALUE_ITEM_W with a double: name pointer, status, padding, value
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public uint Status;
        public uint Padding;
        public double Value;
    }

    // PDH_FMT_COUNTERVALUE with a double: status, padding, value
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public uint Padding;
        public double Value;
    }
}
