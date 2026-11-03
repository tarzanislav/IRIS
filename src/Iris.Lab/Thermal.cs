using System.Runtime.InteropServices;

namespace Iris.Lab;

/// <summary>
/// The temperature on Windows from the performance counters of the ACPI thermal zones ("Thermal Zone Information",
/// in kelvin) – the same values as WMI gives, but through the plain PDH functions, without COM and without
/// administrator rights. Many desktop boards have no zone; then there is no temperature (null), and nothing is
/// installed to get one (a driver in the kernel would be a risk).
/// </summary>
internal static partial class Thermal
{
    private const uint Double = 0x00000200;
    private static readonly Lock Gate = new();
    private static nint _query;
    private static nint _counter;
    private static bool _failed;

    public static double? Windows()
    {
        lock (Gate)
        {
            if (_failed)
            {
                return null;
            }

            try
            {
                if (_query == 0)
                {
                    if (PdhOpenQueryW(null, 0, out _query) != 0 ||
                        PdhAddEnglishCounterW(_query, @"\Thermal Zone Information(*)\Temperature", 0, out _counter) != 0)
                    {
                        _failed = true;
                        return null;
                    }
                }

                if (PdhCollectQueryData(_query) != 0)
                {
                    return null;
                }

                uint size = 0;
                PdhGetFormattedCounterArrayW(_counter, Double, ref size, out var count, 0);
                if (size == 0 || count == 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArrayW(_counter, Double, ref size, out count, buffer) != 0)
                    {
                        return null;
                    }

                    double? best = null;
                    var item = Marshal.SizeOf<CounterItem>();
                    for (var i = 0; i < count; i++)
                    {
                        var value = Marshal.PtrToStructure<CounterItem>(buffer + i * item);
                        var celsius = value.Value - 273.15;
                        if (value.Status == 0 && celsius is > 0 and < 150)
                        {
                            best = Math.Max(best ?? 0, celsius);
                        }
                    }

                    return best;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _failed = true;
                return null;
            }
        }
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nint user, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

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
}
