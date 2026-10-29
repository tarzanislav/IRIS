namespace Iris.Numerics;

/// <summary>
/// A matrix of weights [rows, columns], kept as float32 or as int8 with one scale per row (symmetric, per output).
/// Rows are outputs of a dense layer or tokens of a vocabulary.
/// </summary>
internal sealed class Matrix
{
    private Matrix(int rows, int columns, float[]? floats, sbyte[]? bytes, float[]? scales)
    {
        Rows = rows;
        Columns = columns;
        FloatValues = floats;
        Int8Values = bytes;
        Scales = scales;
    }

    public int Rows { get; }

    public int Columns { get; }

    /// <summary>The weights as float32, or null when they are int8.</summary>
    public float[]? FloatValues { get; }

    /// <summary>The weights as int8, or null when they are float32.</summary>
    public sbyte[]? Int8Values { get; }

    /// <summary>The scale of each row of int8 weights (value = int8 × scale).</summary>
    public float[]? Scales { get; }

    public bool IsInt8 => Int8Values is not null;

    public static Matrix FromFloats(float[] values, int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.LongLength, (long)rows * columns);
        return new Matrix(rows, columns, values, null, null);
    }

    public static Matrix FromInt8(sbyte[] values, float[] scales, int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.LongLength, (long)rows * columns);
        ArgumentOutOfRangeException.ThrowIfNotEqual(scales.Length, rows);
        return new Matrix(rows, columns, null, values, scales);
    }

    /// <summary>This matrix with int8 weights (itself when it already has them).</summary>
    public Matrix ToInt8()
    {
        if (Int8Values is not null)
        {
            return this;
        }

        var (values, scales) = Kernels.QuantizeRows(FloatValues!, Rows, Columns);
        return FromInt8(values, scales, Rows, Columns);
    }

    /// <summary>y[t] = x[t] · Mᵀ + b for each of the n rows of x (Columns values per row of x, Rows per row of y).</summary>
    public void Linear(float[] x, int n, float[]? bias, float[] y)
    {
        if (Int8Values is null)
        {
            Kernels.Linear(x, n, FloatValues!, bias, Columns, Rows, y);
        }
        else
        {
            Kernels.LinearInt8(x, n, Int8Values, Scales!, bias, Columns, Rows, y);
        }
    }

    /// <summary>The same into a new array.</summary>
    public float[] Linear(float[] x, int n, float[]? bias)
    {
        var y = new float[n * Rows];
        Linear(x, n, bias, y);
        return y;
    }

    /// <summary>One row as floats (for the vector of a token).</summary>
    public void Row(int row, Span<float> output)
    {
        if (Int8Values is null)
        {
            FloatValues.AsSpan(row * Columns, Columns).CopyTo(output);
            return;
        }

        Kernels.Widen(Int8Values.AsSpan(row * Columns, Columns), output);
        var scale = Scales![row];
        for (var i = 0; i < Columns; i++)
        {
            output[i] *= scale;
        }
    }
}
