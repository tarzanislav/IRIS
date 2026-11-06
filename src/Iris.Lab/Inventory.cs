using System.Buffers.Binary;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// The tables of the firmware (SMBIOS/DMI) that every PC carries: maker and model, the service tag, BIOS, the board,
/// the case, the processor and every memory module – read from the raw table, no WMI, no driver. Each structure is a
/// type, a length, its fixed fields and then its strings (1-based indexes into a list ending with two zeros).
/// </summary>
public static class SmBios
{
    /// <summary>What the table says, as JSON (fields the firmware leaves empty are missing).</summary>
    public static JsonObject Parse(ReadOnlySpan<byte> table)
    {
        var result = new JsonObject();
        var modules = new JsonArray();
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            int type = table[offset], length = table[offset + 1];
            if (length < 4 || offset + length > table.Length)
            {
                break;
            }

            var fields = table.Slice(offset, length).ToArray();
            var strings = Strings(table, offset + length, out var next);
            string? S(int at) => at < length && fields[at] is var i and > 0 && i <= strings.Count ? Clean(strings[i - 1]) : null;
            int Byte(int at) => at < length ? fields[at] : -1;
            int Word(int at) => at + 1 < length ? BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(at)) : -1;
            long Dword(int at) => at + 3 < length ? BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(at)) : -1;
            switch (type)
            {
                case 0:
                    result["bios"] = Object(("vendor", S(4)), ("version", S(5)), ("date", S(8)));
                    break;
                case 1:
                    Put(result, "maker", S(4));
                    Put(result, "model", S(5));
                    Put(result, "version", S(6));
                    Put(result, "serial", S(7));
                    Put(result, "sku", S(0x19));
                    Put(result, "family", S(0x1A));
                    if (length >= 0x18)
                    {
                        Put(result, "uuid", Uuid(fields.AsSpan(8, 16)));
                    }

                    break;
                case 2:
                    result["board"] = Object(("maker", S(4)), ("model", S(5)), ("version", S(6)));
                    break;
                case 3:
                    var kind = Byte(5) & 0x7F;
                    result["chassis"] = Object(("maker", S(4)), ("type", ChassisName(kind)), ("serial", S(7)), ("asset", S(8)));
                    if (result["serial"] is null)
                    {
                        Put(result, "serial", S(7));
                    }

                    break;
                case 4 when (Byte(0x18) & 0x40) != 0 && result["cpu"] is null:
                    var cores = Byte(0x23);
                    var threads = Byte(0x25);
                    cores = cores == 0xFF && Word(0x2A) > 0 ? Word(0x2A) : cores;
                    threads = threads == 0xFF && Word(0x2E) > 0 ? Word(0x2E) : threads;
                    result["cpu"] = Object(("name", S(0x10)), ("maker", S(7)), ("socket", S(4)));
                    PutNumber((JsonObject)result["cpu"]!, "cores", cores > 0 ? cores : null);
                    PutNumber((JsonObject)result["cpu"]!, "threads", threads > 0 ? threads : null);
                    PutNumber((JsonObject)result["cpu"]!, "max_mhz", Word(0x14) is > 0 and < 20000 and var mhz ? mhz : null);
                    break;
                case 17:
                    var size = Word(0x0C);
                    if (size is 0 or 0xFFFF or -1)
                    {
                        break; // an empty slot
                    }

                    long megabytes = size == 0x7FFF ? Dword(0x1C) & 0x7FFFFFFF : (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;
                    var module = Object(("slot", S(0x10)), ("bank", S(0x11)), ("type", MemoryName(Byte(0x12))), ("maker", S(0x17)), ("part", S(0x1A)));
                    module["size_gb"] = Math.Round(megabytes / 1024.0, 1);
                    PutNumber(module, "speed", Word(0x20) is > 0 and < 0xFFFF and var configured ? configured : Word(0x15) is > 0 and < 0xFFFF and var speed ? speed : null);
                    modules.Add((JsonNode)module);
                    break;
                case 127:
                    offset = table.Length;
                    continue;
            }

            offset = next;
        }

        if (modules.Count > 0)
        {
            result["memory_modules"] = modules;
        }

        return result;
    }

    // the strings after a structure, and where the next structure begins
    private static List<string> Strings(ReadOnlySpan<byte> table, int start, out int next)
    {
        var list = new List<string>();
        var at = start;
        if (at + 1 < table.Length && table[at] == 0 && table[at + 1] == 0)
        {
            next = at + 2;
            return list;
        }

        while (at < table.Length)
        {
            var end = table[at..].IndexOf((byte)0);
            if (end < 0)
            {
                next = table.Length;
                return list;
            }

            list.Add(Encoding.Latin1.GetString(table.Slice(at, end)));
            at += end + 1;
            if (at < table.Length && table[at] == 0)
            {
                next = at + 1;
                return list;
            }
        }

        next = table.Length;
        return list;
    }

    // firmware strings are often padded or placeholders: those count as missing
    private static string? Clean(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Not Specified", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) || trimmed.Trim('0', ' ', '.').Length == 0
            ? null
            : trimmed;
    }

    private static string? Uuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOfAnyExcept((byte)0) < 0 || bytes.IndexOfAnyExcept((byte)0xFF) < 0)
        {
            return null;
        }

        // the first three fields are little-endian (SMBIOS 2.6 and newer)
        return new Guid(bytes).ToString().ToUpperInvariant();
    }

    public static string? ChassisName(int type) => type switch
    {
        3 => "desktop",
        4 => "low profile desktop",
        5 => "pizza box",
        6 => "mini tower",
        7 => "tower",
        8 => "portable",
        9 => "laptop",
        10 => "notebook",
        13 => "all in one",
        14 => "sub notebook",
        15 => "space-saving",
        16 => "lunch box",
        17 or 23 => "server",
        24 => "sealed-case",
        30 => "tablet",
        31 => "convertible",
        32 => "detachable",
        34 => "embedded",
        35 => "mini PC",
        36 => "stick PC",
        _ => null,
    };

    public static string? MemoryName(int type) => type switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => null,
    };

    private static JsonObject Object(params (string Key, string? Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            Put(json, key, value);
        }

        return json;
    }

    private static void Put(JsonObject json, string key, string? value)
    {
        if (value is not null)
        {
            json[key] = value;
        }
    }

    private static void PutNumber(JsonObject json, string key, int? value)
    {
        if (value is { } v)
        {
            json[key] = v;
        }
    }
}

/// <summary>
/// What a computer of the classroom is, for the console: the firmware's tables (maker, model, service tag, BIOS,
/// processor, memory modules), the cores (performance and efficient ones on hybrid processors), disks, graphics, the
/// system and the network cards. Read once when the agent connects; nothing is installed and nothing leaves the
/// classroom's own server.
/// </summary>
public static partial class Inventory
{
    public static JsonObject Collect()
    {
        JsonObject json;
        try
        {
            json = OperatingSystem.IsWindows() ? Windows() : OperatingSystem.IsLinux() ? Linux() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ExternalException or ArgumentException)
        {
            json = [];
        }

        var cpu = json["cpu"] as JsonObject ?? [];
        json["cpu"] = cpu;
        cpu["logical"] = Environment.ProcessorCount;
        cpu["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        cpu["avx2"] = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        cpu["avx512"] = System.Runtime.Intrinsics.X86.Avx512F.IsSupported;
        json["network"] = new JsonArray([.. RealCards(Cards()).Select(c => (JsonNode?)c.DeepClone())]);
        json["time"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
        return json;
    }

    /// <summary>
    /// The real network cards of a list: Windows also lists the filters bound to a card (WFP, QoS, LightWeight Filter)
    /// and its WAN miniports, with the card's own address – those say nothing; one entry per address stays.
    /// </summary>
    public static IEnumerable<JsonObject> RealCards(JsonArray? cards)
    {
        string[] noise = ["-WFP", "QoS Packet Scheduler", "LightWeight Filter", "WAN Miniport", "Kernel Debug", "Virtual Adapter", "-0000"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards?.OfType<JsonObject>() ?? [])
        {
            var name = card["name"]?.GetValue<string>() ?? "";
            var mac = card["mac"]?.GetValue<string>();
            if (noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)) || (mac is not null && !seen.Add(mac)))
            {
                continue;
            }

            yield return card;
        }
    }

    private static JsonArray Cards()
    {
        var cards = new JsonArray();
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                         n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet))
            {
                var mac = card.GetPhysicalAddress().GetAddressBytes();
                cards.Add((JsonNode)new JsonObject
                {
                    ["name"] = card.Description,
                    ["wireless"] = card.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    ["mbps"] = card.Speed > 0 ? card.Speed / 1_000_000 : null,
                    ["mac"] = mac.Length == 6 ? string.Join('-', mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : null,
                });
            }
        }
        catch (NetworkInformationException)
        {
        }

        return cards;
    }

    private static JsonObject Windows()
    {
        var json = new JsonObject();
        if (Firmware() is { } table)
        {
            json = SmBios.Parse(table);
        }

        var cpu = json["cpu"] as JsonObject ?? [];
        json["cpu"] = cpu;
        if (Cores() is { } cores)
        {
            cpu["cores"] = cores.Performance + cores.Efficient;
            if (cores.Efficient > 0)
            {
                cpu["performance_cores"] = cores.Performance;
                cpu["efficient_cores"] = cores.Efficient;
            }
        }

        json["memory_gb"] = MemoryGb();
        json["disks"] = Disks();
        if (OperatingSystem.IsWindows())
        {
            json["gpu"] = new JsonArray([.. Graphics().Select(g => (JsonNode?)g)]);
            json["os"] = WindowsVersion();
        }

        return json;
    }

    private static unsafe byte[]? Firmware()
    {
        const uint Rsmb = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size < 8)
        {
            return null;
        }

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (GetSystemFirmwareTable(Rsmb, 0, p, size) != size)
            {
                return null;
            }
        }

        // RawSMBIOSData: calling method, major, minor, revision, length (4 bytes), then the table
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)), size - 8);
        return buffer.AsSpan(8, length).ToArray();
    }

    // the cores by their efficiency class (hybrid processors: the highest class are the performance cores)
    private static unsafe (int Performance, int Efficient)? Cores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0, null, ref length);
        if (length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!GetLogicalProcessorInformationEx(0, p, ref length))
            {
                return null;
            }
        }

        var classes = new List<int>();
        for (var at = 0; at + 10 <= length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 4));
            if (size <= 0)
            {
                break;
            }

            classes.Add(buffer[at + 9]); // PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass
            at += size;
        }

        if (classes.Count == 0)
        {
            return null;
        }

        var top = classes.Max();
        return top == 0 ? (classes.Count, 0) : (classes.Count(c => c == top), classes.Count(c => c != top));
    }

    private static double MemoryGb()
    {
        var info = GC.GetGCMemoryInfo();
        return Math.Round(info.TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024), 1);
    }

    // the physical disks: model, bus and size (\\.\PhysicalDriveN, asked for its description, not opened for reading)
    private static unsafe JsonArray Disks()
    {
        var disks = new JsonArray();
        var query = stackalloc uint[3];
        var description = stackalloc byte[1024];
        for (var i = 0; i < 16; i++)
        {
            var handle = CreateFileW($@"\\.\PhysicalDrive{i}", 0x80000000, 3, 0, 3, 0, 0);
            if (handle == -1)
            {
                handle = CreateFileW($@"\\.\PhysicalDrive{i}", 0, 3, 0, 3, 0, 0);
            }

            if (handle == -1)
            {
                continue;
            }

            try
            {
                query[0] = query[1] = query[2] = 0; // the standard description of the device
                string? model = null, bus = null;
                if (DeviceIoControl(handle, 0x2D1400, query, 12, description, 1024, out var got, 0) && got >= 40)
                {
                    var bytes = new ReadOnlySpan<byte>(description, (int)got).ToArray();
                    string? Text(int at)
                    {
                        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                        if (offset <= 0 || offset >= bytes.Length)
                        {
                            return null;
                        }

                        var end = Array.IndexOf(bytes, (byte)0, offset);
                        var text = Encoding.ASCII.GetString(bytes, offset, (end < 0 ? bytes.Length : end) - offset).Trim();
                        return text.Length == 0 ? null : text;
                    }

                    model = string.Join(' ', new[] { Text(12), Text(16) }.Where(t => t is not null));
                    bus = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)) switch
                    {
                        1 => "SCSI",
                        3 => "ATA",
                        7 => "USB",
                        8 => "RAID",
                        10 => "SAS",
                        11 => "SATA",
                        12 => "SD",
                        17 => "NVMe",
                        _ => null,
                    };
                }

                long length = 0;
                DeviceIoControl(handle, 0x7405C, null, 0, &length, 8, out _, 0);
                disks.Add((JsonNode)new JsonObject
                {
                    ["model"] = string.IsNullOrEmpty(model) ? null : model,
                    ["bus"] = bus,
                    ["size_gb"] = length > 0 ? Math.Round(length / 1e9) : null,
                });
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return disks;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> Graphics()
    {
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (root is null)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
        {
            using var key = root.OpenSubKey(sub);
            if (key?.GetValue("DriverDesc") is string name && !names.Contains(name) && !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static JsonObject WindowsVersion()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        var name = key?.GetValue("ProductName") as string ?? "Windows";
        if (int.TryParse(build, out var number) && number >= 22000)
        {
            name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal); // the registry keeps the old name
        }

        return new JsonObject
        {
            ["name"] = name,
            ["version"] = key?.GetValue("DisplayVersion") as string,
            ["build"] = key?.GetValue("UBR") is int ubr ? $"{build}.{ubr}" : build,
        };
    }

    private static JsonObject Linux()
    {
        JsonObject json;
        try
        {
            json = SmBios.Parse(File.ReadAllBytes("/sys/firmware/dmi/tables/DMI")); // root only
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            json = [];
            string? Dmi(string name)
            {
                try
                {
                    var text = File.ReadAllText($"/sys/class/dmi/id/{name}").Trim();
                    return text.Length == 0 ? null : text;
                }
                catch (Exception f) when (f is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            json["maker"] = Dmi("sys_vendor");
            json["model"] = Dmi("product_name");
            json["bios"] = new JsonObject { ["vendor"] = Dmi("bios_vendor"), ["version"] = Dmi("bios_version"), ["date"] = Dmi("bios_date") };
            json["board"] = new JsonObject { ["maker"] = Dmi("board_vendor"), ["model"] = Dmi("board_name") };
            json["chassis"] = new JsonObject { ["type"] = int.TryParse(Dmi("chassis_type"), out var t) ? SmBios.ChassisName(t) : null };
        }

        var cpu = json["cpu"] as JsonObject ?? [];
        json["cpu"] = cpu;
        if (cpu["name"] is null && File.Exists("/proc/cpuinfo"))
        {
            cpu["name"] = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
        }

        json["memory_gb"] = MemoryGb();
        var disks = new JsonArray();
        if (Directory.Exists("/sys/block"))
        {
            foreach (var block in Directory.EnumerateDirectories("/sys/block").Where(d => !Path.GetFileName(d).StartsWith("loop", StringComparison.Ordinal) && !Path.GetFileName(d).StartsWith("ram", StringComparison.Ordinal)))
            {
                var model = File.Exists(Path.Combine(block, "device", "model")) ? File.ReadAllText(Path.Combine(block, "device", "model")).Trim() : null;
                var sectors = File.Exists(Path.Combine(block, "size")) && long.TryParse(File.ReadAllText(Path.Combine(block, "size")).Trim(), out var s) ? s : 0;
                disks.Add((JsonNode)new JsonObject
                {
                    ["model"] = model,
                    ["bus"] = Path.GetFileName(block).StartsWith("nvme", StringComparison.Ordinal) ? "NVMe" : null,
                    ["size_gb"] = sectors > 0 ? Math.Round(sectors * 512 / 1e9) : null,
                });
            }
        }

        json["disks"] = disks;
        if (File.Exists("/etc/os-release"))
        {
            json["os"] = new JsonObject
            {
                ["name"] = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?[12..].Trim('"'),
            };
        }

        return json;
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint id, byte* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
