using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Privacy;

/// <summary>Which part of the sentence pairs of a vault: to learn from, or kept aside only for measuring.</summary>
public enum VaultSection
{
    Learn,
    Measure,
}

/// <summary>What a vault says of itself without the password: its kind, its tag, when it was made and how its key is derived.</summary>
public sealed record VaultInfo(string Kind, string Tag, DateTime Created, int Iterations, JsonObject Header);

/// <summary>
/// The key of a vault, derived from the password; kept in a pinned array (the collector never copies it) and wiped
/// when disposed. Carries the parameters it was derived with, so outputs sealed with it open with the same password.
/// </summary>
public sealed class VaultKey : IDisposable
{
    private readonly byte[] _key = GC.AllocateUninitializedArray<byte>(32, pinned: true);

    internal VaultKey(ReadOnlySpan<char> password, byte[] salt, int iterations)
    {
        Rfc2898DeriveBytes.Pbkdf2(password, salt, _key, iterations, HashAlgorithmName.SHA256);
        Salt = salt;
        Iterations = iterations;
    }

    internal byte[] Salt { get; }

    internal int Iterations { get; }

    internal ReadOnlySpan<byte> Bytes => _key;

    /// <summary>A short check of the key (a keyed hash of a fixed text – nothing of the data): a wrong password is told apart from damage.</summary>
    internal string Check() => Convert.ToBase64String(HMACSHA256.HashData(_key, "IRIS vault key check"u8)[..16]);

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}

/// <summary>
/// The vault of IRIS: licensed data that must stay private (e.g. InterCorp – research use, never published), and
/// everything learned from them, kept encrypted on disk. The password gives the key (PBKDF2-SHA-256, 600 000 rounds,
/// a random salt); the data are AES-256-GCM in chunks of about 256 kB, each with its own nonce, bound to the header,
/// to its number and to whether it is the last one – a changed, reordered, removed or cut chunk is found. The data are
/// opened only in memory, a chunk at a time, and the chunk is wiped after use. Every vault has a tag
/// ("private:intercorp"); what is learned from it is sealed with the same tag and the same key. A small part of the
/// pairs (2 %, chosen by the pair itself, so always the same) is kept only for measuring and never given to learning.
/// Own format, only .NET's cryptography.
/// </summary>
public static class Vault
{
    /// <summary>The first bytes of every vault file.</summary>
    public static readonly byte[] Magic = "IRISVLT1"u8.ToArray();

    /// <summary>The rounds of PBKDF2-SHA-256 for a new vault.</summary>
    public const int Iterations = 600_000;

    /// <summary>The share of the pairs kept only for measuring (in thousandths).</summary>
    public const int MeasurePerMille = 20;

    private const int ChunkBytes = 256 * 1024;
    private const int TagBytes = 16;
    private const int NonceBytes = 12;

    /// <summary>Whether a file is a vault (by its first bytes).</summary>
    public static bool IsVault(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> start = stackalloc byte[8];
            return file.ReadAtLeast(start, 8, throwOnEndOfStream: false) == 8 && start.SequenceEqual(Magic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a file holds anything private: a vault or a sealed file, or a model file whose header says it was learned
    /// from private data. Public learning (the classroom), releases and catalogs refuse such files.
    /// </summary>
    public static bool HoldsPrivate(string path)
    {
        if (IsVault(path))
        {
            return true;
        }

        try
        {
            using var file = Model.ModelFile.Load(path);
            return file.Provenance["private"] is JsonValue value && value.TryGetValue(out string? tag) && IsPrivate(tag ?? "");
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a tag marks private data (anything that starts with "private:").</summary>
    public static bool IsPrivate(string tag) => tag.StartsWith("private:", StringComparison.Ordinal);

    /// <summary>The header of a vault, read without the password.</summary>
    public static VaultInfo Info(string path)
    {
        using var file = File.OpenRead(path);
        var (header, _) = ReadHeader(file);
        return new VaultInfo(
            header["kind"]?.GetValue<string>() ?? "",
            header["tag"]?.GetValue<string>() ?? "",
            DateTime.Parse(header["created"]?.GetValue<string>() ?? "2000-01-01", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            header["kdf"]?["iterations"]?.GetValue<int>() ?? 0,
            header);
    }

    /// <summary>The key of a vault from its password (slow on purpose); throws when the password is wrong.</summary>
    public static VaultKey Open(string path, ReadOnlySpan<char> password)
    {
        using var file = File.OpenRead(path);
        var (header, _) = ReadHeader(file);
        var salt = Convert.FromBase64String(header["kdf"]?["salt"]?.GetValue<string>() ?? throw new InvalidDataException("vault: no salt"));
        var iterations = header["kdf"]?["iterations"]?.GetValue<int>() ?? 0;
        if (iterations < 100_000 || salt.Length < 16)
        {
            throw new InvalidDataException("vault: the key derivation is too weak or damaged");
        }

        var key = new VaultKey(password, salt, iterations);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key.Check()), Encoding.ASCII.GetBytes(header["check"]?.GetValue<string>() ?? "")))
        {
            key.Dispose();
            throw new CryptographicException("the password of the vault is not right");
        }

        return key;
    }

    /// <summary>A new key for a new vault (a fresh random salt).</summary>
    public static VaultKey NewKey(ReadOnlySpan<char> password, int iterations = Iterations)
    {
        if (password.Length < 12)
        {
            throw new ArgumentException("the password of a vault needs at least 12 characters");
        }

        return new VaultKey(password, RandomNumberGenerator.GetBytes(16), iterations);
    }

    /// <summary>
    /// Writes sentence pairs into a new vault. Each pair goes to learning or, for 2 % of them, to measuring only –
    /// chosen by a hash of the pair, so a pair is always in the same part. Returns how many went to each part.
    /// </summary>
    public static (int Learn, int Measure) CreatePairs(string path, string tag, IEnumerable<(string Source, string Target)> pairs, VaultKey key)
    {
        var learn = 0;
        var measure = 0;
        using var writer = new ChunkWriter(path, "pairs", tag, key);
        var buffer = new StringBuilder();
        foreach (var (source, target) in pairs)
        {
            var s = Clean(source);
            var t = Clean(target);
            if (s.Length == 0 || t.Length == 0)
            {
                continue;
            }

            var section = ForMeasuring(s, t) ? VaultSection.Measure : VaultSection.Learn;
            if (section == VaultSection.Measure)
            {
                measure++;
            }
            else
            {
                learn++;
            }

            buffer.Append(section == VaultSection.Measure ? 'M' : 'L').Append('\t').Append(s).Append('\t').Append(t).Append('\n');
            if (buffer.Length >= ChunkBytes / 2)
            {
                writer.Write(buffer);
            }
        }

        writer.Write(buffer);
        writer.Finish(new JsonObject { ["learn"] = learn, ["measure"] = measure });
        return (learn, measure);
    }

    /// <summary>The pairs of one part of a vault, chunk by chunk (each chunk wiped once its pairs are read).</summary>
    public static IEnumerable<(string Source, string Target)> ReadPairs(string path, VaultKey key, VaultSection section)
    {
        var wanted = section == VaultSection.Measure ? 'M' : 'L';
        foreach (var text in ReadChunks(path, key, "pairs"))
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && parts[0].Length == 1 && parts[0][0] == wanted)
                {
                    yield return (parts[1], parts[2]);
                }
            }
        }
    }

    /// <summary>Seals bytes (e.g. an adapter learned from a vault) with the tag and the key of the vault.</summary>
    public static void Seal(string path, string tag, ReadOnlySpan<byte> data, VaultKey key, JsonObject? about = null)
    {
        using var writer = new ChunkWriter(path, "blob", tag, key, about);
        for (var at = 0; at < data.Length || at == 0; at += ChunkBytes)
        {
            writer.WriteBytes(data.Slice(at, Math.Min(ChunkBytes, data.Length - at)));
            if (data.Length == 0)
            {
                break;
            }
        }

        writer.Finish(new JsonObject { ["bytes"] = data.Length });
    }

    /// <summary>The bytes of a sealed file (the caller wipes them when done: CryptographicOperations.ZeroMemory).</summary>
    public static byte[] Unseal(string path, VaultKey key)
    {
        using var output = new MemoryStream();
        foreach (var (chunk, last) in ReadRaw(path, key, "blob"))
        {
            if (!last)
            {
                output.Write(chunk.Span); // the last chunk holds only the counts
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(chunk).Span);
        }

        var bytes = output.ToArray();
        CryptographicOperations.ZeroMemory(output.GetBuffer());
        return bytes;
    }

    // one pair to measuring only: a hash of the pair decides (always the same for the same pair)
    internal static bool ForMeasuring(string source, string target)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(source + "\t" + target), hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash) % 1000 < MeasurePerMille;
    }

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static IEnumerable<string> ReadChunks(string path, VaultKey key, string kind)
    {
        foreach (var (chunk, _) in ReadRaw(path, key, kind))
        {
            var text = Encoding.UTF8.GetString(chunk.Span);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(chunk).Span);
            yield return text;
        }
    }

    // the chunks of a vault, decrypted and checked one by one; the buffer is the caller's to wipe
    private static IEnumerable<(ReadOnlyMemory<byte> Data, bool Last)> ReadRaw(string path, VaultKey key, string kind)
    {
        using var file = File.OpenRead(path);
        var (header, headerBytes) = ReadHeader(file);
        if (header["kind"]?.GetValue<string>() != kind)
        {
            throw new InvalidDataException($"the vault holds {header["kind"]?.GetValue<string>()}, not {kind}");
        }

        if (header["kdf"]?["salt"]?.GetValue<string>() != Convert.ToBase64String(key.Salt) || header["check"]?.GetValue<string>() != key.Check())
        {
            throw new CryptographicException("this key does not open the vault");
        }

        var bound = SHA256.HashData(headerBytes);
        using var aes = new AesGcm(key.Bytes, TagBytes);
        var meta = new byte[4 + NonceBytes + 1];
        long index = 0;
        var last = false;
        while (!last)
        {
            if (file.ReadAtLeast(meta, meta.Length, throwOnEndOfStream: false) != meta.Length)
            {
                throw new InvalidDataException("the vault is cut short");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(meta);
            last = meta[^1] == 1;
            if (length < 0 || length > ChunkBytes * 4)
            {
                throw new InvalidDataException("the vault is damaged");
            }

            var cipher = new byte[length];
            var tag = new byte[TagBytes];
            if (file.ReadAtLeast(cipher, length, throwOnEndOfStream: false) != length || file.ReadAtLeast(tag, TagBytes, throwOnEndOfStream: false) != TagBytes)
            {
                throw new InvalidDataException("the vault is cut short");
            }

            var plain = GC.AllocateUninitializedArray<byte>(length, pinned: true);
            aes.Decrypt(meta.AsSpan(4, NonceBytes), cipher, tag, plain, Associated(bound, index, last));
            index++;
            yield return (plain, last);
        }

        if (file.Position != file.Length)
        {
            throw new InvalidDataException("the vault has data after its end");
        }
    }

    // what every chunk is bound to: the header, its number, and whether it is the last one
    private static byte[] Associated(byte[] header, long index, bool last)
    {
        var data = new byte[header.Length + 9];
        header.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(header.Length), index);
        data[^1] = last ? (byte)1 : (byte)0;
        return data;
    }

    private static (JsonObject Header, byte[] Bytes) ReadHeader(Stream file)
    {
        Span<byte> start = stackalloc byte[12];
        if (file.ReadAtLeast(start, 12, throwOnEndOfStream: false) != 12 || !start[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("not an IRIS vault");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(start[8..]);
        if (length is <= 0 or > 1 << 20)
        {
            throw new InvalidDataException("the header of the vault is damaged");
        }

        var bytes = new byte[length];
        if (file.ReadAtLeast(bytes, length, throwOnEndOfStream: false) != length || JsonNode.Parse(bytes) is not JsonObject header)
        {
            throw new InvalidDataException("the header of the vault is damaged");
        }

        return (header, bytes);
    }

    // writes the header, then the chunks; the header is fixed before the first chunk (every chunk is bound to it), and
    // the counts come in a last chunk of their own
    private sealed class ChunkWriter : IDisposable
    {
        private readonly FileStream _file;
        private readonly string _path;
        private readonly string _temporary;
        private readonly AesGcm _aes;
        private readonly byte[] _bound;
        private readonly byte[] _prefix = RandomNumberGenerator.GetBytes(4);
        private long _index;
        private bool _done;

        public ChunkWriter(string path, string kind, string tag, VaultKey key, JsonObject? about = null)
        {
            if (!IsPrivate(tag) && !tag.StartsWith("public:", StringComparison.Ordinal))
            {
                throw new ArgumentException("a vault's tag starts with private: or public:");
            }

            _path = path;
            _temporary = path + ".part";
            var header = new JsonObject
            {
                ["format"] = 1,
                ["kind"] = kind,
                ["tag"] = tag,
                ["created"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["cipher"] = "aes-256-gcm",
                ["kdf"] = new JsonObject { ["name"] = "pbkdf2-sha256", ["iterations"] = key.Iterations, ["salt"] = Convert.ToBase64String(key.Salt) },
                ["check"] = key.Check(),
                ["about"] = about?.DeepClone(),
            };
            var bytes = Encoding.UTF8.GetBytes(header.ToJsonString());
            _bound = SHA256.HashData(bytes);
            _aes = new AesGcm(key.Bytes, TagBytes);
            _file = new FileStream(_temporary, FileMode.Create, FileAccess.Write, FileShare.None);
            _file.Write(Magic);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            _file.Write(length);
            _file.Write(bytes);
        }

        public void Write(StringBuilder text)
        {
            if (text.Length == 0)
            {
                return;
            }

            var bytes = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(text.ToString()), pinned: true);
            Encoding.UTF8.GetBytes(text.ToString(), bytes);
            text.Clear();
            WriteBytes(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }

        public void WriteBytes(ReadOnlySpan<byte> plain) => Chunk(plain, last: false);

        public void Finish(JsonObject counts)
        {
            Chunk(Encoding.UTF8.GetBytes("#" + counts.ToJsonString()), last: true);
            _file.Flush(flushToDisk: true);
            _file.Dispose();
            File.Move(_temporary, _path, overwrite: true);
            _done = true;
        }

        private void Chunk(ReadOnlySpan<byte> plain, bool last)
        {
            var nonce = new byte[NonceBytes];
            _prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), _index);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagBytes];
            _aes.Encrypt(nonce, plain, cipher, tag, Associated(_bound, _index, last));
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, plain.Length);
            _file.Write(length);
            _file.Write(nonce);
            _file.WriteByte(last ? (byte)1 : (byte)0);
            _file.Write(cipher);
            _file.Write(tag);
            _index++;
        }

        public void Dispose()
        {
            _aes.Dispose();
            if (!_done)
            {
                _file.Dispose();
                File.Delete(_temporary); // an unfinished vault never stays
            }
        }
    }
}
