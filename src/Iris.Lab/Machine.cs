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
    private readonly List<(double Temperature, double Cpu)> _temperatures = [];
    private bool _fixedTemperature;

    /// <summary>
    /// Whether the firmware gives one number that never moves (some desktop boards fill their ACPI zone with e.g.
    /// 30 °C): then there is no temperature, rather than a wrong one.
    /// </summary>
    public bool FixedTemperature => _fixedTemperature;

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
        return new MachineSample(total, Math.Min(own, total > 0 ? total : own), used, totalGb, Checked(Temperature(), total), IdleSeconds(), cores);
    }

    // a temperature that stays exactly the same over many samples while the load goes up and down is not measured
    private double? Checked(double? temperature, double cpu)
    {
        if (temperature is not { } t || _fixedTemperature)
        {
            return _fixedTemperature ? null : temperature;
        }

        _temperatures.Add((t, cpu));
        if (_temperatures.Count > 60)
        {
            _temperatures.RemoveAt(0);
        }

        if (_temperatures.Count >= 30 && _temperatures.All(x => x.Temperature == t) && _temperatures.Max(x => x.Cpu) - _temperatures.Min(x => x.Cpu) >= 30)
        {
            _fixedTemperature = true;
            return null;
        }

        return temperature;
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

    /// <summary>
    /// Asks the system to run this process for efficiency, or not. Windows (EcoQoS, Windows 10 1709 and newer): on
    /// processors with efficient cores the work goes there, and the cores run at their most economical clock – less
    /// power and heat, quiet fans, somewhat slower. Every system: the process runs below normal priority, so whatever
    /// the computer is asked to do comes first. Returns whether the efficiency mode was set.
    /// </summary>
    public static bool SetEfficiency(bool on)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return false;
        }

        // PROCESS_POWER_THROTTLING_EXECUTION_SPEED: on = throttled (EcoQoS), off = the normal way, set explicitly
        var state = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
        return SetProcessInformation(-1, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottling>());
    }

    private const int ProcessPowerThrottling = 4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int informationClass, ref PowerThrottling information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
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
