using System.Diagnostics;
using System.Text.Json.Nodes;
using Iris.Numerics;

namespace Iris.Lab;

/// <summary>
/// A diagnosis of a computer, taken when the console asks for it: the memory of the agent (what Windows counts for
/// it, its managed heap, the native memory of the learning, its threads and handles), the memory of the whole
/// computer, and the programs that hold most memory (names and sizes only – nothing of what they do or show). It is
/// what one would read from the Task Manager, without going to the computer.
/// </summary>
public static class Diagnosis
{
    private const double Mb = 1024.0 * 1024;

    /// <summary>How many programs by memory it names.</summary>
    public const int Programs = 12;

    public static JsonObject Take(DateTime now)
    {
        var result = new JsonObject
        {
            ["time"] = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["agent"] = Self(now),
            ["system"] = Machine.MemoryNow()?.ToJson(),
            ["system_up_hours"] = Math.Round(Environment.TickCount64 / 3_600_000.0, 1),
            ["programs"] = Biggest(Programs),
        };
        return result;
    }

    // the agent's own process: what the system counts, and what .NET and the numerics know of it
    private static JsonObject Self(DateTime now)
    {
        using var self = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        double? started = null;
        try
        {
            started = Math.Round((now - self.StartTime).TotalHours, 1);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }

        return new JsonObject
        {
            ["private_mb"] = Math.Round(self.PrivateMemorySize64 / Mb),
            ["working_set_mb"] = Math.Round(self.WorkingSet64 / Mb),
            ["peak_working_set_mb"] = Math.Round(self.PeakWorkingSet64 / Mb),
            ["heap_mb"] = Math.Round(GC.GetTotalMemory(false) / Mb),
            ["heap_committed_mb"] = Math.Round(gc.TotalCommittedBytes / Mb),
            ["native_mb"] = Math.Round(NativeUse.Bytes / Mb),
            ["threads"] = self.Threads.Count,
            ["pool_threads"] = ThreadPool.ThreadCount,
            ["handles"] = self.HandleCount,
            ["collections"] = new JsonArray(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
            ["up_hours"] = started,
        };
    }

    // the programs holding most memory, one line per name (all its processes together), the biggest first
    private static JsonArray Biggest(int count)
    {
        var sizes = new Dictionary<string, (double Private, double Working, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0)
                    {
                        continue;
                    }

                    var name = process.ProcessName;
                    var (privateBytes, working, n) = sizes.GetValueOrDefault(name);
                    sizes[name] = (privateBytes + process.PrivateMemorySize64, working + process.WorkingSet64, n + 1);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                {
                    // a process that ended meanwhile, or one even the service may not look at
                }
            }
        }

        return new JsonArray([.. sizes.OrderByDescending(p => p.Value.Private).Take(count).Select(p => (JsonNode?)new JsonObject
        {
            ["name"] = p.Key,
            ["count"] = p.Value.Count,
            ["private_mb"] = Math.Round(p.Value.Private / Mb),
            ["working_set_mb"] = Math.Round(p.Value.Working / Mb),
        })]);
    }
}
