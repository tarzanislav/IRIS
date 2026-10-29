using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Model;

/// <summary>Where a tensor or a blob lies in the data of a model file.</summary>
public sealed record TensorInfo(string Name, string DType, int[] Shape, long Offset, long Length)
{
    public long Count => Shape.Aggregate(1L, (a, b) => a * b);
}

/// <summary>A tensor to write: its name, element type ("f32" or "i8"), shape and bytes (little endian).</summary>
public sealed record TensorData(string Name, string DType, int[] Shape, byte[] Bytes)
{
    public static TensorData F32(string name, int[] shape, float[] values) =>
        new(name, "f32", shape, MemoryMarshal.AsBytes(values.AsSpan()).ToArray());

    public static TensorData I8(string name, int[] shape, sbyte[] values) =>
        new(name, "i8", shape, MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
}

/// <summary>
/// The IRIS model file – one file holds everything a model needs: settings, tensors (weights), blobs (tokenizer
/// models, vocabulary) and where it came from (source, licence). Layout: "IRISMDL1", the length of the header
/// (8 bytes, little endian), the header as JSON, then the data, every item on a 64-byte boundary.
/// </summary>
public sealed class ModelFile
{
    public static readonly byte[] Magic = "IRISMDL1"u8.ToArray();
    private const int Alignment = 64;

    // the whole file; the data begin at _dataStart (no second copy of a model of hundreds of megabytes)
    private readonly byte[] _file;
    private readonly int _dataStart;
    private readonly Dictionary<string, TensorInfo> _tensors;
    private readonly Dictionary<string, TensorInfo> _blobs;

    private ModelFile(JsonObject header, byte[] file, int dataStart, Dictionary<string, TensorInfo> tensors, Dictionary<string, TensorInfo> blobs)
    {
        Header = header;
        _file = file;
        _dataStart = dataStart;
        _tensors = tensors;
        _blobs = blobs;
    }

    /// <summary>Where the model came from (source, revision, licence, checksums).</summary>
    public JsonObject Provenance => Header["provenance"] as JsonObject ?? [];

    /// <summary>The whole header (kind, config, provenance …).</summary>
    public JsonObject Header { get; }

    /// <summary>The settings of the model ("config").</summary>
    public JsonObject Config => Header["config"] as JsonObject ?? [];

    public string Kind => Header["kind"]?.GetValue<string>() ?? "";

    public IReadOnlyCollection<TensorInfo> Tensors => _tensors.Values;

    public static ModelFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static ModelFile Parse(byte[] file)
    {
        if (file.Length < 16 || !file.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new FormatException("not an IRIS model file");
        }

        var headerLength = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(8));
        if (headerLength <= 0 || 16 + headerLength > file.Length)
        {
            throw new FormatException("IRIS model: damaged header");
        }

        var header = JsonNode.Parse(file.AsSpan(16, (int)headerLength)) as JsonObject ?? throw new FormatException("IRIS model: header is not an object");
        var dataStart = Align(16 + headerLength);
        var dataLength = file.Length - dataStart;
        Dictionary<string, TensorInfo> Read(string key)
        {
            var items = new Dictionary<string, TensorInfo>(StringComparer.Ordinal);
            foreach (var node in header[key] as JsonArray ?? [])
            {
                var info = FromJson(node) ?? throw new FormatException($"IRIS model: damaged {key}");
                if (info.Offset < 0 || info.Length < 0 || info.Offset + info.Length > dataLength)
                {
                    throw new FormatException($"IRIS model: {info.Name} lies outside the data");
                }

                items[info.Name] = info;
            }

            return items;
        }

        return new ModelFile(header, file, (int)dataStart, Read("tensors"), Read("blobs"));
    }

    public bool Has(string name) => _tensors.ContainsKey(name);

    /// <summary>The element type of a tensor ("f32", "i8"), or null when the model does not have it.</summary>
    public string? DTypeOf(string name) => _tensors.TryGetValue(name, out var t) ? t.DType : null;

    /// <summary>A tensor as it is stored (for copying it into another model file).</summary>
    public TensorData Raw(string name)
    {
        var info = _tensors.TryGetValue(name, out var t) ? t : throw new FormatException($"IRIS model: tensor {name} is missing");
        return new TensorData(info.Name, info.DType, info.Shape, Data(info).ToArray());
    }

    public IEnumerable<string> BlobNames => _blobs.Keys;

    /// <summary>An int8 tensor (a copy), checked against the expected shape when one is given.</summary>
    public sbyte[] Int8(string name, params int[] shape)
    {
        var info = Info(name, "i8", 1, shape);
        var values = new sbyte[info.Count];
        MemoryMarshal.Cast<byte, sbyte>(Data(info)).CopyTo(values);
        return values;
    }

    /// <summary>A float32 tensor (a copy), checked against the expected shape when one is given.</summary>
    public float[] Floats(string name, params int[] shape)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("IRIS models are little endian");
        }

        var info = Info(name, "f32", 4, shape);
        var values = new float[info.Count];
        MemoryMarshal.Cast<byte, float>(Data(info)).CopyTo(values);
        return values;
    }

    private TensorInfo Info(string name, string dtype, int size, int[] shape)
    {
        var info = _tensors.TryGetValue(name, out var t) ? t : throw new FormatException($"IRIS model: tensor {name} is missing");
        if (info.DType != dtype)
        {
            throw new FormatException($"IRIS model: {name} is {info.DType}, expected {dtype}");
        }

        if (shape.Length > 0 && !shape.SequenceEqual(info.Shape))
        {
            throw new FormatException($"IRIS model: {name} has shape [{string.Join(",", info.Shape)}], expected [{string.Join(",", shape)}]");
        }

        if (info.Length != info.Count * size)
        {
            throw new FormatException($"IRIS model: {name} has a wrong length");
        }

        return info;
    }

    public bool HasBlob(string name) => _blobs.ContainsKey(name);

    public byte[] Blob(string name) =>
        _blobs.TryGetValue(name, out var info) ? Data(info).ToArray() : throw new FormatException($"IRIS model: blob {name} is missing");

    private ReadOnlySpan<byte> Data(TensorInfo info) => _file.AsSpan(_dataStart + (int)info.Offset, (int)info.Length);

    /// <summary>Writes a model file: the header (kind, config, provenance), the float tensors and the blobs.</summary>
    public static void Write(string path, string kind, JsonObject config, JsonObject provenance,
        IEnumerable<(string Name, int[] Shape, float[] Values)> tensors, IEnumerable<(string Name, byte[] Bytes)> blobs) =>
        Write(path, kind, config, provenance, tensors.Select(t => TensorData.F32(t.Name, t.Shape, t.Values)), blobs);

    /// <summary>Writes a model file with tensors of any element type.</summary>
    public static void Write(string path, string kind, JsonObject config, JsonObject provenance,
        IEnumerable<TensorData> tensors, IEnumerable<(string Name, byte[] Bytes)> blobs)
    {
        var data = new MemoryStream();
        JsonArray Place(IEnumerable<(string Name, string DType, int[] Shape, byte[] Bytes)> items)
        {
            var list = new JsonArray();
            foreach (var (name, dtype, shape, bytes) in items)
            {
                data.Position = Align(data.Length);
                var offset = data.Position;
                data.Write(bytes);
                list.Add((JsonNode)ToJson(new TensorInfo(name, dtype, shape, offset, bytes.Length)));
            }

            return list;
        }

        var header = new JsonObject
        {
            ["format"] = 1,
            ["kind"] = kind,
            ["config"] = config.DeepClone(),
            ["provenance"] = provenance.DeepClone(),
            ["tensors"] = Place(tensors.Select(t => (t.Name, t.DType, t.Shape, t.Bytes))),
            ["blobs"] = Place(blobs.Select(b => (b.Name, "u8", new[] { b.Bytes.Length }, b.Bytes))),
        };
        var headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString());
        using var file = File.Create(path);
        file.Write(Magic);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, headerBytes.Length);
        file.Write(length);
        file.Write(headerBytes);
        file.Write(new byte[Align(16 + headerBytes.Length) - (16 + headerBytes.Length)]);
        data.Position = 0;
        data.CopyTo(file);
    }

    /// <summary>The SHA-256 of a file, as the registry of sources records it.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static long Align(long value) => (value + Alignment - 1) / Alignment * Alignment;

    private static JsonObject ToJson(TensorInfo info) => new()
    {
        ["name"] = info.Name,
        ["dtype"] = info.DType,
        ["shape"] = new JsonArray([.. info.Shape.Select(d => (JsonNode)d)]),
        ["offset"] = info.Offset,
        ["length"] = info.Length,
    };

    private static TensorInfo? FromJson(JsonNode? node)
    {
        try
        {
            return node is JsonObject o && o["name"] is JsonNode name && o["dtype"] is JsonNode dtype && o["shape"] is JsonArray shape
                ? new TensorInfo(name.GetValue<string>(), dtype.GetValue<string>(), [.. shape.Select(d => d!.GetValue<int>())],
                    o["offset"]!.GetValue<long>(), o["length"]!.GetValue<long>())
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }
}
