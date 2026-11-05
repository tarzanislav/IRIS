using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>A device found in the network.</summary>
public sealed record NetworkDevice(string Address, string? Mac, string? DnsName, string? NetBiosName, string? Workgroup, double? PingMs)
{
    /// <summary>A locally administered MAC (set by software, e.g. a phone's private address), not a maker's.</summary>
    public bool LocalMac => Mac is { Length: >= 2 } m && (Convert.ToByte(m[..2], 16) & 0x02) != 0;

    public JsonObject ToJson() => new()
    {
        ["address"] = Address,
        ["mac"] = Mac,
        ["dns"] = DnsName,
        ["netbios"] = NetBiosName,
        ["workgroup"] = Workgroup,
        ["ping"] = PingMs is { } p ? Math.Round(p, 1) : null,
        ["local_mac"] = LocalMac,
    };
}

/// <summary>
/// Finds what is in the own network (only the subnets of this computer, at most 1022 addresses each) the way an
/// administrator's inventory does: an echo (ping) to every address, a NetBIOS name query (what "nbtstat -A" asks
/// – Windows computers answer it even when they do not answer a ping), the MAC addresses the system learned on the
/// way (the ARP table), and the names in DNS. No ports are tried and nothing is attacked: it only asks for names.
/// </summary>
public static class NetworkScanner
{
    public static async Task<List<NetworkDevice>> ScanAsync(CancellationToken cancel, int parallel = 64)
    {
        var addresses = LocalHosts().ToList();
        var pings = new ConcurrentDictionary<string, double>();
        var names = new ConcurrentDictionary<string, (string Name, string? Group)>();
        using var gate = new SemaphoreSlim(parallel);
        await Task.WhenAll(addresses.Select(async address =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                var ping = Ping(address);
                var name = NetBios(address, cancel);
                if (await ping is { } ms)
                {
                    pings[address.ToString()] = ms;
                }

                if (await name is { } n)
                {
                    names[address.ToString()] = n;
                }
            }
            finally
            {
                gate.Release();
            }
        }));

        var arp = ArpTable();
        var alive = pings.Keys.Concat(names.Keys).Concat(arp.Keys.Where(a => addresses.Any(x => x.ToString() == a))).Distinct().ToList();
        var devices = await Task.WhenAll(alive.Select(async address =>
        {
            string? dns = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var entry = await Dns.GetHostEntryAsync(address, timeout.Token);
                dns = entry.HostName == address ? null : entry.HostName;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
            {
            }

            var n = names.TryGetValue(address, out var found) ? found : default;
            return new NetworkDevice(address, arp.GetValueOrDefault(address), dns, n.Name, n.Group, pings.TryGetValue(address, out var ms) ? ms : null);
        }));
        return [.. devices.OrderBy(d => IPAddress.Parse(d.Address).GetAddressBytes(), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))];
    }

    /// <summary>The addresses of the subnets this computer is in (IPv4, up, not loopback; at most a /22 each).</summary>
    public static IEnumerable<IPAddress> LocalHosts()
    {
        foreach (var face in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (face.OperationalStatus != OperationalStatus.Up || face.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in face.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.PrefixLength is < 22 or > 30)
                {
                    continue;
                }

                var own = BinaryPrimitives.ReadUInt32BigEndian(unicast.Address.GetAddressBytes());
                var mask = uint.MaxValue << (32 - unicast.PrefixLength);
                var network = own & mask;
                var broadcast = network | ~mask;
                for (var host = network + 1; host < broadcast; host++)
                {
                    if (host != own)
                    {
                        var bytes = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(bytes, host);
                        yield return new IPAddress(bytes);
                    }
                }
            }
        }
    }

    /// <summary>The broadcast addresses of the own subnets (for waking computers).</summary>
    public static IEnumerable<IPAddress> Broadcasts()
    {
        foreach (var face in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (face.OperationalStatus != OperationalStatus.Up || face.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in face.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.PrefixLength is > 0 and < 32)
                {
                    var own = BinaryPrimitives.ReadUInt32BigEndian(unicast.Address.GetAddressBytes());
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, own | ~(uint.MaxValue << (32 - unicast.PrefixLength)));
                    yield return new IPAddress(bytes);
                }
            }
        }
    }

    private static async Task<double?> Ping(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 700);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception e) when (e is PingException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A NetBIOS node status query (RFC 1002, 4.2.17): "which names do you have?" to UDP port 137; the answer lists
    /// the computer name (a unique name of type 0) and the workgroup (a group name of type 0).
    /// </summary>
    internal static async Task<(string Name, string? Group)?> NetBios(IPAddress address, CancellationToken cancel)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var query = new byte[50];
            BinaryPrimitives.WriteUInt16BigEndian(query, (ushort)Random.Shared.Next(1, 65535));
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4), 1); // one question
            query[12] = 32;
            // the name '*' padded with zeros, first-level encoded (each half byte + 'A'): '*' = 0x2A → "CK", zeros → "AA"
            query[13] = (byte)'C';
            query[14] = (byte)'K';
            for (var i = 15; i < 45; i++)
            {
                query[i] = (byte)'A';
            }

            query[45] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(46), 0x21); // NBSTAT
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(48), 1);    // IN
            await udp.SendAsync(query, new IPEndPoint(address, 137), cancel);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
            var answer = (await udp.ReceiveAsync(timeout.Token)).Buffer;
            return ParseNodeStatus(answer);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static (string Name, string? Group)? ParseNodeStatus(byte[] answer)
    {
        // header (12), the name (34), type and class (4), TTL (4), length (2), then the count of names
        var at = 12 + 34 + 4 + 4 + 2;
        if (answer.Length <= at)
        {
            return null;
        }

        int count = answer[at++];
        string? name = null, group = null;
        for (var i = 0; i < count && at + 18 <= answer.Length; i++, at += 18)
        {
            var text = Encoding.ASCII.GetString(answer, at, 15).TrimEnd(' ', '\0');
            var type = answer[at + 15];
            var isGroup = (answer[at + 16] & 0x80) != 0;
            if (type == 0 && !isGroup)
            {
                name ??= text;
            }
            else if (type == 0 && isGroup)
            {
                group ??= text;
            }
        }

        return name is null ? null : (name, group);
    }

    /// <summary>
    /// Whether a computer is on at this address: it answers a ping, or – when its firewall drops pings, as Windows does
    /// by default – its card answers the ARP question that the ping caused (a card of a computer that is off does not).
    /// </summary>
    public static async Task<bool> IsOnAsync(string address, CancellationToken cancel)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (await Ping(ip) is not null)
        {
            return true;
        }

        await Task.Delay(TimeSpan.FromSeconds(3), cancel); // the kernel asks a few times before it gives up
        return ArpTable().ContainsKey(address);
    }

    /// <summary>IPv4 address → MAC from the system's ARP table (Linux /proc/net/arp, Windows "arp -a").</summary>
    public static Dictionary<string, string> ArpTable()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists("/proc/net/arp"))
            {
                foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 && parts[2] == "0x2" && parts[3] != "00:00:00:00:00:00")
                    {
                        table[parts[0]] = parts[3].ToUpperInvariant().Replace(':', '-');
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var process = System.Diagnostics.Process.Start(start)!;
                foreach (var line in process.StandardOutput.ReadToEnd().Split('\n'))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out _) && parts[1].Length == 17 && parts[1] != "ff-ff-ff-ff-ff-ff")
                    {
                        table[parts[0]] = parts[1].ToUpperInvariant();
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }

        return table;
    }

    /// <summary>
    /// Wakes a computer (Wake-on-LAN): the "magic packet" – six bytes 0xFF and sixteen times the MAC – as a broadcast
    /// to port 9 of every own subnet and to 255.255.255.255. The computer must allow it (BIOS/UEFI and its network card).
    /// </summary>
    public static async Task<int> WakeAsync(string mac, CancellationToken cancel)
    {
        var bytes = ParseMac(mac) ?? throw new ArgumentException($"not a MAC address: {mac}");
        var packet = new byte[6 + 16 * 6];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            bytes.CopyTo(packet, 6 + 6 * i);
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var sent = 0;
        foreach (var target in Broadcasts().Append(IPAddress.Broadcast).Distinct())
        {
            try
            {
                await udp.SendAsync(packet, new IPEndPoint(target, 9), cancel);
                sent++;
            }
            catch (SocketException)
            {
            }
        }

        return sent;
    }

    public static byte[]? ParseMac(string mac)
    {
        var hex = new string([.. mac.Where(Uri.IsHexDigit)]);
        if (hex.Length != 12)
        {
            return null;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>The MAC of the network card that has the given local address (what the agent tells the hub, to be woken later).</summary>
    public static string? MacOf(IPAddress local)
    {
        if (local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4(); // a socket of both families gives ::ffff:a.b.c.d – the card has a.b.c.d
        }

        foreach (var face in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (face.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local)))
            {
                var bytes = face.GetPhysicalAddress().GetAddressBytes();
                return bytes.Length == 6 ? string.Join('-', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : null;
            }
        }

        return null;
    }
}
