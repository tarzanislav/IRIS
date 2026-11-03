using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>Who is on the other end: a computer that learns, or the console of the owner.</summary>
public enum LabRole : byte
{
    Agent = 1,
    Console = 2,
}

/// <summary>The kinds of messages (the protocol has no other).</summary>
public enum MessageKind : byte
{
    /// <summary>Agent → hub: measurements and what the agent does (JSON).</summary>
    Status = 1,

    /// <summary>Hub → agent: the model of a round, the rate and the deadline (JSON + tensors).</summary>
    Model = 2,

    /// <summary>Agent → hub: what it learned in a round (JSON + tensors of the change).</summary>
    Delta = 3,

    /// <summary>Hub → agent: settings of learning (JSON).</summary>
    Settings = 4,

    /// <summary>Hub → agent: one of the fixed commands (pause, resume, shutdown, restart); console → hub: the same for an agent.</summary>
    Command = 5,

    /// <summary>Console → hub: what the hub knows now; hub → console: the answer (JSON).</summary>
    Snapshot = 6,

    /// <summary>Either way: an error or a note (JSON).</summary>
    Note = 7,

    /// <summary>Hub → agent: no model now (outside the time of learning or waiting for the round), with the seconds to wait.</summary>
    Wait = 8,

    /// <summary>Console → hub: the history of a span (from, to, computer, bucket minutes); hub → console: the series (JSON).</summary>
    History = 9,
}

/// <summary>A message: its kind, a small JSON header and binary data (tensors), possibly empty.</summary>
public sealed record LabMessage(MessageKind Kind, JsonObject Header, byte[] Data)
{
    public LabMessage(MessageKind kind, JsonObject header)
        : this(kind, header, [])
    {
    }
}

/// <summary>
/// A connection between the hub and an agent or the console, with every message signed. Both sides prove that they
/// know the key of the role without sending it (HMAC-SHA256 over two fresh random numbers), derive a key for this
/// connection, and sign every message with it and with its number – a changed, repeated, dropped or reordered message
/// ends the connection. An agent never listens on a port: it connects to the hub, so a computer in the classroom has
/// nothing open to the network, and only a holder of the key can give it a command. The contents are not secret
/// (a model of reading, measurements), so they are not encrypted.
/// </summary>
public sealed class LabChannel : IDisposable
{
    public const int Version = 1;

    /// <summary>The largest message (a model of 2.5 million weights is 10 MB).</summary>
    public const int MaxMessage = 96 << 20;

    private static readonly byte[] Magic = "IRISLAB1"u8.ToArray();
    private readonly Stream _stream;
    private readonly byte[] _session;
    private readonly byte _sendDirection;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly SemaphoreSlim _receiving = new(1, 1);
    private ulong _sent;
    private ulong _received;

    private LabChannel(Stream stream, byte[] session, bool server, LabRole role, string name)
    {
        (_stream, _session, PeerRole, PeerName) = (stream, session, role, name);
        _sendDirection = server ? (byte)2 : (byte)1;
    }

    public LabRole PeerRole { get; }

    /// <summary>The name the agent gave (its computer), or "hub" on the agent's side.</summary>
    public string PeerName { get; }

    /// <summary>The hub's side: checks that the other end knows the key of its role.</summary>
    public static async Task<LabChannel> AcceptAsync(Stream stream, Func<LabRole, byte[]?> keyOf, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var serverNonce = RandomNumberGenerator.GetBytes(32);
        var hello = new byte[Magic.Length + 2 + 32];
        Magic.CopyTo(hello, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(Magic.Length), Version);
        serverNonce.CopyTo(hello, Magic.Length + 2);
        await stream.WriteAsync(hello, token);
        await stream.FlushAsync(token);

        var head = await ReadExactly(stream, Magic.Length + 2 + 1 + 2, token);
        if (!head.AsSpan(0, Magic.Length).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(Magic.Length)) != Version)
        {
            throw new LabProtocolException("not an IRIS lab client of this version");
        }

        var role = (LabRole)head[Magic.Length + 2];
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(Magic.Length + 3));
        if (nameLength > 200)
        {
            throw new LabProtocolException("the name is too long");
        }

        var rest = await ReadExactly(stream, nameLength + 64, token);
        var name = Encoding.UTF8.GetString(rest, 0, nameLength);
        var clientNonce = rest.AsSpan(nameLength, 32).ToArray();
        var proof = rest.AsSpan(nameLength + 32, 32).ToArray();
        var key = (role is LabRole.Agent or LabRole.Console ? keyOf(role) : null) ?? throw new LabProtocolException("unknown role");
        var expected = Mac(key, "hello"u8, [(byte)role], Encoding.UTF8.GetBytes(name), serverNonce, clientNonce);
        if (!CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            throw new LabProtocolException("the key does not fit");
        }

        await stream.WriteAsync(Mac(key, "welcome"u8, clientNonce, serverNonce), token);
        await stream.FlushAsync(token);
        return new LabChannel(stream, Mac(key, "session"u8, serverNonce, clientNonce), server: true, role, name);
    }

    /// <summary>The agent's or console's side: connects with the key of its role and checks that the hub knows it too.</summary>
    public static async Task<LabChannel> ConnectAsync(Stream stream, LabRole role, string name, byte[] key, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var hello = await ReadExactly(stream, Magic.Length + 2 + 32, token);
        if (!hello.AsSpan(0, Magic.Length).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(Magic.Length)) != Version)
        {
            throw new LabProtocolException("not an IRIS hub of this version");
        }

        var serverNonce = hello.AsSpan(Magic.Length + 2, 32).ToArray();
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        var nameBytes = Encoding.UTF8.GetBytes(name.Length > 60 ? name[..60] : name);
        var message = new byte[Magic.Length + 2 + 1 + 2 + nameBytes.Length + 64];
        Magic.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(Magic.Length), Version);
        message[Magic.Length + 2] = (byte)role;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(Magic.Length + 3), (ushort)nameBytes.Length);
        nameBytes.CopyTo(message, Magic.Length + 5);
        clientNonce.CopyTo(message, Magic.Length + 5 + nameBytes.Length);
        Mac(key, "hello"u8, [(byte)role], nameBytes, serverNonce, clientNonce).CopyTo(message, Magic.Length + 5 + nameBytes.Length + 32);
        await stream.WriteAsync(message, token);
        await stream.FlushAsync(token);

        var welcome = await ReadExactly(stream, 32, token);
        if (!CryptographicOperations.FixedTimeEquals(welcome, Mac(key, "welcome"u8, clientNonce, serverNonce)))
        {
            throw new LabProtocolException("the hub does not know the key");
        }

        return new LabChannel(stream, Mac(key, "session"u8, serverNonce, clientNonce), server: false, role, "hub");
    }

    public async Task SendAsync(LabMessage message, CancellationToken cancel)
    {
        var header = Encoding.UTF8.GetBytes(message.Header.ToJsonString());
        var bodyLength = 1 + 8 + 4 + header.Length + message.Data.Length;
        if (bodyLength > MaxMessage)
        {
            throw new LabProtocolException("the message is too large");
        }

        await _sending.WaitAsync(cancel);
        try
        {
            var frame = new byte[4 + bodyLength + 32];
            BinaryPrimitives.WriteInt32LittleEndian(frame, bodyLength);
            var body = frame.AsSpan(4, bodyLength);
            body[0] = (byte)message.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(body[1..], _sent++);
            BinaryPrimitives.WriteInt32LittleEndian(body[9..], header.Length);
            header.CopyTo(body[13..]);
            message.Data.CopyTo(body[(13 + header.Length)..]);
            Sign(_sendDirection, body, frame.AsSpan(4 + bodyLength, 32));
            await _stream.WriteAsync(frame, cancel);
            await _stream.FlushAsync(cancel);
        }
        finally
        {
            _sending.Release();
        }
    }

    public async Task<LabMessage> ReceiveAsync(CancellationToken cancel)
    {
        await _receiving.WaitAsync(cancel);
        try
        {
            var lengthBytes = await ReadExactly(_stream, 4, cancel);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length < 13 || length > MaxMessage)
            {
                throw new LabProtocolException("a message of a wrong size");
            }

            var frame = await ReadExactly(_stream, length + 32, cancel);
            var body = frame.AsSpan(0, length);
            Span<byte> mac = stackalloc byte[32];
            Sign(_sendDirection == 1 ? (byte)2 : (byte)1, body, mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, frame.AsSpan(length, 32)))
            {
                throw new LabProtocolException("a message with a wrong signature");
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(body[1..]) != _received++)
            {
                throw new LabProtocolException("a message out of order (repeated or dropped)");
            }

            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(body[9..]);
            if (headerLength < 0 || 13 + headerLength > length)
            {
                throw new LabProtocolException("a broken message");
            }

            var header = JsonNode.Parse(body.Slice(13, headerLength)) as JsonObject ?? [];
            if (!Enum.IsDefined((MessageKind)body[0]))
            {
                throw new LabProtocolException("an unknown kind of message");
            }

            return new LabMessage((MessageKind)body[0], header, body[(13 + headerLength)..].ToArray());
        }
        finally
        {
            _receiving.Release();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _sending.Dispose();
        _receiving.Dispose();
    }

    private void Sign(byte direction, ReadOnlySpan<byte> body, Span<byte> mac)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _session);
        hmac.AppendData([direction]);
        hmac.AppendData(body);
        hmac.GetHashAndReset(mac);
    }

    private static byte[] Mac(byte[] key, ReadOnlySpan<byte> label, params byte[][] parts)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(label);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, part.Length);
            hmac.AppendData(length);
            hmac.AppendData(part);
        }

        return hmac.GetHashAndReset();
    }

    private static async Task<byte[]> ReadExactly(Stream stream, int count, CancellationToken cancel)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancel);
        return buffer;
    }
}

/// <summary>Something on the other end does not follow the protocol (or does not know the key): the connection ends.</summary>
public sealed class LabProtocolException(string message) : Exception(message);

/// <summary>The keys of the lab: 32 random bytes per role, kept in files only the system and administrators may read.</summary>
public static class LabKeys
{
    /// <summary>Reads a key file (base64), or creates one with a new random key when it does not exist.</summary>
    public static byte[] ReadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return Read(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }

    public static byte[] Read(string path)
    {
        var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        return key.Length >= 32 ? key : throw new FormatException("a lab key has 32 bytes");
    }
}
