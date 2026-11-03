using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>One look at a computer: how busy it is (all of it, and the learning alone), memory, temperature, idleness.</summary>
public sealed record MachineSample(
    double CpuTotal, double CpuOwn, double MemoryUsed, double MemoryTotalGb, double? Temperature, double? IdleSeconds, int Cores)
{
    /// <summary>What the rest of the computer uses (someone working, an update, an antivirus scan).</summary>
    public double CpuOthers => Math.Max(0, CpuTotal - CpuOwn);

    public JsonObject ToJson() => new()
    {
        ["cpu"] = Math.Round(CpuTotal, 1),
        ["cpu_own"] = Math.Round(CpuOwn, 1),
        ["memory"] = Math.Round(MemoryUsed, 1),
        ["memory_gb"] = Math.Round(MemoryTotalGb, 1),
        ["temperature"] = Temperature is { } t ? Math.Round(t, 1) : null,
        ["idle"] = IdleSeconds is { } i ? Math.Round(i) : null,
        ["cores"] = Cores,
    };
}

/// <summary>
/// Measures the computer it runs on, lightly: a few system counters read every few seconds and the difference to
/// the previous reading (no polling loops, no drivers). Windows: system times, memory status, the last input of the
/// user, the ACPI thermal zone where the firmware offers it. Linux: /proc and /sys.
/// </summary>
public sealed partial class Machine
{
    private readonly Process _self = Process.GetCurrentProcess();
    private (long Idle, long Total)? _lastSystem;
    private (TimeSpan Cpu, long Ticks)? _lastOwn;

    public MachineSample Sample()
    {
        var cores = Environment.ProcessorCount;
        var total = SystemBusy();
        _self.Refresh();
        var own = 0.0;
        var now = Stopwatch.GetTimestamp();
        var cpu = _self.TotalProcessorTime;
        if (_lastOwn is var (lastCpu, lastTicks))
        {
            var seconds = (now - lastTicks) / (double)Stopwatch.Frequency;
            own = seconds > 0 ? 100 * (cpu - lastCpu).TotalSeconds / (seconds * cores) : 0;
        }

        _lastOwn = (cpu, now);
        var (used, totalGb) = Memory();
        return new MachineSample(total, Math.Min(own, total > 0 ? total : own), used, totalGb, Temperature(), IdleSeconds(), cores);
    }

    // share of all cores busy since the last sample (%)
    private double SystemBusy()
    {
        (long Idle, long Total)? now = null;
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                now = (idle, kernel + user); // kernel time includes idle time
            }
        }
        else if (File.Exists("/proc/stat"))
        {
            var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            now = (parts[3] + (parts.Length > 4 ? parts[4] : 0), parts.Sum());
        }

        if (now is not { } current)
        {
            return 0;
        }

        var result = 0.0;
        if (_lastSystem is var (lastIdle, lastTotal) && current.Total > lastTotal)
        {
            result = 100.0 * (1 - (current.Idle - lastIdle) / (double)(current.Total - lastTotal));
        }

        _lastSystem = current;
        return Math.Clamp(result, 0, 100);
    }

    private static (double Used, double TotalGb) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? (status.Load, status.Total / (1024.0 * 1024 * 1024)) : (0, 0);
        }

        if (File.Exists("/proc/meminfo"))
        {
            var values = File.ReadLines("/proc/meminfo").Select(l => l.Split(':')).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => double.Parse(p[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
            var total = values.GetValueOrDefault("MemTotal");
            var available = values.GetValueOrDefault("MemAvailable");
            return total > 0 ? (100 * (1 - available / total), total / (1024.0 * 1024)) : (0, 0);
        }

        return (0, 0);
    }

    // the warmest sensor the system shows (°C), or null: Linux thermal zones; on Windows the ACPI zone needs WMI –
    // see Thermal.Windows (null where the firmware has no zone, as on many desktop boards)
    private static double? Temperature()
    {
        if (OperatingSystem.IsWindows())
        {
            return Thermal.Windows();
        }

        const string Zones = "/sys/class/thermal";
        if (!Directory.Exists(Zones))
        {
            return null;
        }

        double? best = null;
        foreach (var zone in Directory.EnumerateDirectories(Zones, "thermal_zone*"))
        {
            try
            {
                var value = double.Parse(File.ReadAllText(Path.Combine(zone, "temp")).Trim(), CultureInfo.InvariantCulture) / 1000;
                if (value is > 0 and < 150)
                {
                    best = Math.Max(best ?? 0, value);
                }
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
            {
            }
        }

        return best;
    }

    // seconds since the last key press or mouse move of the user of this session (Windows; a service sees no input)
    private static double? IdleSeconds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info) || info.Time == 0)
        {
            return null;
        }

        return (uint)(Environment.TickCount - (int)info.Time) / 1000.0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInput info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong Total;
        public ulong Available;
        public ulong TotalPage;
        public ulong AvailablePage;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtended;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }
}
