using Iris.Model;

namespace Iris.Numerics;

/// <summary>
/// A matrix of weights [rows, columns], kept as float32 or as int8 with one scale per row (symmetric, per output).
/// Rows are outputs of a dense layer or tokens of a vocabulary.
/// </summary>
public sealed class Matrix
{
    private readonly float[]? _floats;
    private readonly sbyte[]? _bytes;
    private readonly float[]? _scales;

    private Matrix(int rows, int columns, float[]? floats, sbyte[]? bytes, float[]? scales)
    {
        Rows = rows;
        Columns = columns;
        _floats = floats;
        _bytes = bytes;
        _scales = scales;
    }

    public int Rows { get; }

    public int Columns { get; }

    public bool IsInt8 => _bytes is not null;

    /// <summary>Bytes the weights take in memory.</summary>
    public long Size => _bytes is null ? (long)Rows * Columns * 4 : (long)Rows * Columns + Rows * 4L;

    public static Matrix FromFloats(float[] values, int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, rows * columns);
        return new Matrix(rows, columns, values, null, null);
    }

    public static Matrix FromInt8(sbyte[] values, float[] scales, int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, rows * columns);
        ArgumentOutOfRangeException.ThrowIfNotEqual(scales.Length, rows);
        return new Matrix(rows, columns, null, values, scales);
    }

    /// <summary>
    /// Reads a matrix from a model file: a float32 tensor, or an int8 tensor with its scales in "name.scale".
    /// With quantize, float32 weights are turned into int8 as they are read.
    /// </summary>
    public static Matrix Load(ModelFile file, string name, int rows, int columns, bool quantize = false)
    {
        if (file.DTypeOf(name) == "i8")
        {
            return FromInt8(file.Int8(name, rows, columns), file.Floats(name + ".scale", rows), rows, columns);
        }

        var floats = file.Floats(name, rows, columns);
        return quantize ? FromFloats(floats, rows, columns).ToInt8() : FromFloats(floats, rows, columns);
    }

    /// <summary>This matrix with int8 weights (itself when it already has them).</summary>
    public Matrix ToInt8()
    {
        if (_bytes is not null)
        {
            return this;
        }

        var (values, scales) = Kernels.QuantizeRows(_floats!, Rows, Columns);
        return FromInt8(values, scales, Rows, Columns);
    }

    /// <summary>The tensors that store this matrix under a name (the int8 values and their scales, or the floats).</summary>
    public IEnumerable<TensorData> ToTensors(string name)
    {
        if (_bytes is null)
        {
            yield return TensorData.F32(name, [Rows, Columns], _floats!);
            yield break;
        }

        yield return TensorData.I8(name, [Rows, Columns], _bytes);
        yield return TensorData.F32(name + ".scale", [Rows], _scales!);
    }

    /// <summary>y[t] = x[t] · Mᵀ + b for each of the n rows of x (x has Columns values per row, y has Rows).</summary>
    public float[] Linear(float[] x, int n, float[]? bias) => _bytes is null
        ? Kernels.Linear(x, n, _floats!, bias, Columns, Rows)
        : Kernels.LinearInt8(x, n, _bytes, _scales!, bias, Columns, Rows);

    /// <summary>One row as floats (for the vector of a token).</summary>
    public void Row(int row, Span<float> output)
    {
        if (_bytes is null)
        {
            _floats.AsSpan(row * Columns, Columns).CopyTo(output);
            return;
        }

        Kernels.Widen(_bytes.AsSpan(row * Columns, Columns), output);
        var scale = _scales![row];
        for (var i = 0; i < Columns; i++)
        {
            output[i] *= scale;
        }
    }
}
