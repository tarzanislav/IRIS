using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Numerics;
using Microsoft.Win32.SafeHandles;

namespace Iris.Model;

/// <summary>Where a tensor or a blob lies in the data of a model file.</summary>
public sealed record TensorInfo(string Name, string DType, int[] Shape, long Offset, long Length)
{
    public long Count => Shape.Aggregate(1L, (a, b) => checked(a * b));
}

/// <summary>A tensor to write: its name, element type ("f32", "i8" or "u8"), shape and bytes (little endian).</summary>
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
/// (8 bytes, little endian), the header as JSON (format 1), then the data, every item on a 64-byte boundary.
/// A file is not read whole: the header is read when it is opened and each tensor straight into its array when it is
/// asked for, so loading a model needs about the memory of the model itself. Dispose closes the file.
/// </summary>
public sealed class ModelFile : IDisposable
{
    public const int Format = 1;
    private const int Alignment = 64;
    private static readonly byte[] Magic = "IRISMDL1"u8.ToArray();
    private static readonly Dictionary<string, int> ElementSizes = new(StringComparer.Ordinal) { ["f32"] = 4, ["i8"] = 1, ["u8"] = 1 };

    private readonly SafeFileHandle? _handle;
    private readonly byte[]? _bytes;
    private readonly long _dataStart;
    private readonly Dictionary<string, TensorInfo> _tensors;
    private readonly Dictionary<string, TensorInfo> _blobs;

    private ModelFile(JsonObject header, SafeFileHandle? handle, byte[]? bytes, long dataStart, long dataLength)
    {
        Header = header;
        _handle = handle;
        _bytes = bytes;
        _dataStart = dataStart;
        _tensors = Items(header, "tensors", dataLength);
        _blobs = Items(header, "blobs", dataLength);
    }

    /// <summary>The whole header (kind, config, provenance …).</summary>
    public JsonObject Header { get; }

    /// <summary>The settings of the model ("config").</summary>
    public JsonObject Config => Header["config"] as JsonObject ?? [];

    /// <summary>Where the model came from (source, revision, licence, checksums).</summary>
    public JsonObject Provenance => Header["provenance"] as JsonObject ?? [];

    public string Kind => Header["kind"] is JsonValue kind && kind.TryGetValue(out string? text) ? text : "";

    public IReadOnlyCollection<TensorInfo> Tensors => _tensors.Values;

    public IEnumerable<string> BlobNames => _blobs.Keys;

    /// <summary>Opens a model file and reads its header.</summary>
    public static ModelFile Load(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            var length = RandomAccess.GetLength(handle);
            Span<byte> start = stackalloc byte[16];
            if (length < start.Length)
            {
                throw new FormatException("not an IRIS model file");
            }

            ReadExactly(handle, start, 0);
            var headerLength = HeaderLength(start, length);
            var header = new byte[headerLength];
            ReadExactly(handle, header, 16);
            var dataStart = Align(16 + headerLength);
            return new ModelFile(ParseHeader(header), handle, null, dataStart, Math.Max(0, length - dataStart));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Reads a model file that is already in memory.</summary>
    public static ModelFile Parse(byte[] file)
    {
        if (file.Length < 16)
        {
            throw new FormatException("not an IRIS model file");
        }

        var headerLength = HeaderLength(file.AsSpan(0, 16), file.Length);
        var dataStart = Align(16 + headerLength);
        return new ModelFile(ParseHeader(file.AsSpan(16, headerLength)), null, file, dataStart, Math.Max(0, file.Length - dataStart));
    }

    public void Dispose() => _handle?.Dispose();

    /// <summary>The element type of a tensor ("f32", "i8"), or null when the model does not have it.</summary>
    public string? DTypeOf(string name) => _tensors.TryGetValue(name, out var t) ? t.DType : null;

    /// <summary>A float32 tensor, checked against the expected shape when one is given.</summary>
    public float[] Floats(string name, params int[] shape)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("IRIS models are little endian");
        }

        var info = Info(name, "f32", shape);
        var values = new float[info.Count];
        Read(info, MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    /// <summary>An int8 tensor, checked against the expected shape when one is given.</summary>
    public sbyte[] Int8(string name, params int[] shape)
    {
        var info = Info(name, "i8", shape);
        var values = new sbyte[info.Count];
        Read(info, MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    /// <summary>A tensor as it is stored (for copying it into another model file).</summary>
    public TensorData Raw(string name)
    {
        var info = _tensors.TryGetValue(name, out var t) ? t : throw new FormatException($"IRIS model: tensor {name} is missing");
        var bytes = new byte[info.Length];
        Read(info, bytes);
        return new TensorData(info.Name, info.DType, info.Shape, bytes);
    }

    public byte[] Blob(string name)
    {
        var info = _blobs.TryGetValue(name, out var t) ? t : throw new FormatException($"IRIS model: blob {name} is missing");
        var bytes = new byte[info.Length];
        Read(info, bytes);
        return bytes;
    }

    /// <summary>A weight matrix: float32, or int8 with its scales in "name.scale"; with quantize, float32 becomes int8.</summary>
    internal Matrix Matrix(string name, int rows, int columns, bool quantize = false)
    {
        if (DTypeOf(name) == "i8")
        {
            return Numerics.Matrix.FromInt8(Int8(name, rows, columns), Floats(name + ".scale", rows), rows, columns);
        }

        var matrix = Numerics.Matrix.FromFloats(Floats(name, rows, columns), rows, columns);
        return quantize ? matrix.ToInt8() : matrix;
    }

    /// <summary>The tensors that store a matrix under a name (int8 values and their scales, or floats).</summary>
    internal static IEnumerable<TensorData> TensorsOf(string name, Matrix matrix) => matrix.Int8Values is { } values
        ? [TensorData.I8(name, [matrix.Rows, matrix.Columns], values), TensorData.F32(name + ".scale", [matrix.Rows], matrix.Scales!)]
        : [TensorData.F32(name, [matrix.Rows, matrix.Columns], matrix.FloatValues!)];

    /// <summary>Writes a model file: the header (kind, config, provenance), the float tensors and the blobs.</summary>
    public static void Write(string path, string kind, JsonObject config, JsonObject provenance,
        IEnumerable<(string Name, int[] Shape, float[] Values)> tensors, IEnumerable<(string Name, byte[] Bytes)> blobs) =>
        Write(path, kind, config, provenance, tensors.Select(t => TensorData.F32(t.Name, t.Shape, t.Values)), blobs);

    /// <summary>Writes a model file with tensors of any element type; the data go straight to the file.</summary>
    public static void Write(string path, string kind, JsonObject config, JsonObject provenance,
        IEnumerable<TensorData> tensors, IEnumerable<(string Name, byte[] Bytes)> blobs)
    {
        var items = tensors.ToList();
        var blobItems = blobs.Select(b => new TensorData(b.Name, "u8", [b.Bytes.Length], b.Bytes)).ToList();
        long position = 0;
        JsonArray Place(List<TensorData> list)
        {
            var array = new JsonArray();
            foreach (var item in list)
            {
                position = Align(position);
                array.Add((JsonNode)ToJson(new TensorInfo(item.Name, item.DType, item.Shape, position, item.Bytes.Length)));
                position += item.Bytes.Length;
            }

            return array;
        }

        var header = new JsonObject
        {
            ["format"] = Format,
            ["kind"] = kind,
            ["config"] = config.DeepClone(),
            ["provenance"] = provenance.DeepClone(),
            ["tensors"] = Place(items),
            ["blobs"] = Place(blobItems),
        };
        var headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString());
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> start = stackalloc byte[16];
        Magic.CopyTo(start);
        BinaryPrimitives.WriteInt64LittleEndian(start[8..], headerBytes.Length);
        file.Write(start);
        file.Write(headerBytes);
        Pad(file, Align(16 + headerBytes.Length));
        var dataStart = file.Position;
        foreach (var item in items.Concat(blobItems))
        {
            Pad(file, dataStart + Align(file.Position - dataStart));
            file.Write(item.Bytes);
        }
    }

    /// <summary>The SHA-256 of a file, as the registry of sources records it.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int HeaderLength(ReadOnlySpan<byte> start, long fileLength)
    {
        if (!start[..8].SequenceEqual(Magic))
        {
            throw new FormatException("not an IRIS model file");
        }

        var length = BinaryPrimitives.ReadInt64LittleEndian(start[8..]);
        if (length <= 0 || length > int.MaxValue || length > fileLength - 16)
        {
            throw new FormatException("IRIS model: damaged header");
        }

        return (int)length;
    }

    private static JsonObject ParseHeader(ReadOnlySpan<byte> json)
    {
        JsonObject header;
        try
        {
            header = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("IRIS model: the header is not an object");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"IRIS model: damaged header ({ex.Message})", ex);
        }

        if (header["format"] is not JsonValue format || !format.TryGetValue(out int version) || version != Format)
        {
            throw new FormatException($"IRIS model: format {header["format"]?.ToJsonString() ?? "?"} is not supported (IRIS reads format {Format})");
        }

        return header;
    }

    // the tensors or blobs of the header, each checked to lie inside the data
    private static Dictionary<string, TensorInfo> Items(JsonObject header, string key, long dataLength)
    {
        var items = new Dictionary<string, TensorInfo>(StringComparer.Ordinal);
        foreach (var node in header[key] as JsonArray ?? [])
        {
            var info = FromJson(node) ?? throw new FormatException($"IRIS model: damaged {key}");
            if (info.Offset > dataLength - info.Length)
            {
                throw new FormatException($"IRIS model: {info.Name} lies outside the data");
            }

            items[info.Name] = info;
        }

        return items;
    }

    private TensorInfo Info(string name, string dtype, int[] shape)
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

        return info;
    }

    private void Read(TensorInfo info, Span<byte> target)
    {
        if (_bytes is not null)
        {
            _bytes.AsSpan((int)(_dataStart + info.Offset), target.Length).CopyTo(target);
        }
        else
        {
            ReadExactly(_handle!, target, _dataStart + info.Offset);
        }
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> target, long offset)
    {
        while (target.Length > 0)
        {
            var read = RandomAccess.Read(handle, target, offset);
            if (read == 0)
            {
                throw new FormatException("IRIS model: the file ends too early");
            }

            target = target[read..];
            offset += read;
        }
    }

    private static void Pad(FileStream file, long position)
    {
        Span<byte> zeros = stackalloc byte[Alignment];
        while (file.Position < position)
        {
            file.Write(zeros[..(int)Math.Min(zeros.Length, position - file.Position)]);
        }
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

    // a tensor or blob of the header, or null when it is not complete and consistent
    private static TensorInfo? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || !Text(o, "name", out var name) || !Text(o, "dtype", out var dtype) ||
            !ElementSizes.TryGetValue(dtype, out var size) || o["shape"] is not JsonArray shapeArray ||
            !Number(o, "offset", out var offset) || !Number(o, "length", out var length) || offset < 0 || length < 0)
        {
            return null;
        }

        var shape = new int[shapeArray.Count];
        for (var i = 0; i < shape.Length; i++)
        {
            if (shapeArray[i] is not JsonValue v || !v.TryGetValue(out shape[i]) || shape[i] < 0)
            {
                return null;
            }
        }

        try
        {
            var info = new TensorInfo(name, dtype, shape, offset, length);
            return checked(info.Count * size) == length ? info : null;
        }
        catch (OverflowException)
        {
            return null;
        }

        static bool Text(JsonObject o, string key, out string value)
        {
            value = "";
            return o[key] is JsonValue v && v.TryGetValue(out string? s) && (value = s ?? "").Length > 0;
        }

        static bool Number(JsonObject o, string key, out long value)
        {
            value = 0;
            return o[key] is JsonValue v && v.TryGetValue(out value);
        }
    }
}
