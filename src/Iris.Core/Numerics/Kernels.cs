using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Iris.Numerics;

/// <summary>The arithmetic of the model, written for the processor: dot products in SIMD registers, rows of a matrix in parallel.</summary>
internal static class Kernels
{
    /// <summary>Rows below this many multiply-adds are done on one thread.</summary>
    private const long ParallelWork = 1 << 16;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length;
        var i = 0;
        var sum = Vector<float>.Zero;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..a.Length]);
            for (var k = 0; k < va.Length; k++)
            {
                sum += va[k] * vb[k];
            }

            i = va.Length * Vector<float>.Count;
        }

        var result = Vector.Sum(sum);
        for (; i < n; i++)
        {
            result += a[i] * b[i];
        }

        return result;
    }

    /// <summary>y[t] = x[t] · Wᵀ + b for each of the n rows of x; W is [outputs, inputs] (as PyTorch keeps it).</summary>
    public static float[] Linear(float[] x, int n, float[] w, float[]? b, int inputs, int outputs)
    {
        var y = new float[n * outputs];
        Linear(x, n, w, b, inputs, outputs, y);
        return y;
    }

    /// <summary>The same into y (n × outputs values), so that a caller can reuse its buffer.</summary>
    public static unsafe void Linear(float[] x, int n, float[] w, float[]? b, int inputs, int outputs, float[] y)
    {
        CheckSizes(x.LongLength, n, w.LongLength, b, inputs, outputs, y.LongLength);
        if (RowKernels.Supported)
        {
            ForRanges(outputs, (long)n * inputs * outputs, (from, to) =>
            {
                fixed (float* px = x, pw = w, pb = b, py = y)
                {
                    RowKernels.Linear<FloatRow>(px, n, pw, null, pb, inputs, outputs, py, from, to);
                }
            });
            return;
        }

        ForRanges(outputs, (long)n * inputs * outputs, (from, to) =>
        {
            for (var o = from; o < to; o++)
            {
                var weights = w.AsSpan(o * inputs, inputs);
                var bias = b is null ? 0f : b[o];
                for (var t = 0; t < n; t++)
                {
                    y[t * outputs + o] = Dot(x.AsSpan(t * inputs, inputs), weights) + bias;
                }
            }
        });
    }

    /// <summary>
    /// The same with int8 weights, one scale per output row: y[t,o] = (x[t] · W8[o]) × scale[o] + b[o]. The weights
    /// are widened to float in the registers (AVX2), so a quarter of the bytes is read; without AVX2 each row is
    /// widened once into a buffer and used for all n rows of x.
    /// </summary>
    public static unsafe void LinearInt8(float[] x, int n, sbyte[] w, float[] scales, float[]? b, int inputs, int outputs, float[] y)
    {
        CheckSizes(x.LongLength, n, w.LongLength, b, inputs, outputs, y.LongLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(scales.Length, outputs);
        if (RowKernels.Supported)
        {
            ForRanges(outputs, (long)n * inputs * outputs, (from, to) =>
            {
                fixed (float* px = x, ps = scales, pb = b, py = y)
                fixed (sbyte* pw = w)
                {
                    RowKernels.Linear<Int8Row>(px, n, pw, ps, pb, inputs, outputs, py, from, to);
                }
            });
            return;
        }

        ForRanges(outputs, (long)n * inputs * outputs, (from, to) =>
        {
            var row = new float[inputs];
            for (var o = from; o < to; o++)
            {
                Widen(w.AsSpan(o * inputs, inputs), row);
                var scale = scales[o];
                var bias = b is null ? 0f : b[o];
                for (var t = 0; t < n; t++)
                {
                    y[t * outputs + o] = Dot(x.AsSpan(t * inputs, inputs), row) * scale + bias;
                }
            }
        });
    }

    // the pointer kernels trust these sizes, so they are checked once here
    private static void CheckSizes(long x, int n, long w, float[]? b, int inputs, int outputs, long y)
    {
        if (n < 0 || inputs < 0 || outputs < 0 || x < (long)n * inputs || w < (long)inputs * outputs || y < (long)n * outputs ||
            (b is not null && b.Length < outputs))
        {
            throw new ArgumentException($"linear layer: sizes do not fit (n {n}, inputs {inputs}, outputs {outputs})");
        }
    }

    /// <summary>int8 → float, element by element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Widen(ReadOnlySpan<sbyte> source, Span<float> target)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<sbyte>.Count)
        {
            var count = Vector<float>.Count;
            for (; i <= source.Length - Vector<sbyte>.Count; i += Vector<sbyte>.Count)
            {
                Vector.Widen(new Vector<sbyte>(source[i..]), out var low16, out var high16);
                Vector.Widen(low16, out var a, out var b);
                Vector.Widen(high16, out var c, out var d);
                Vector.ConvertToSingle(a).CopyTo(target[i..]);
                Vector.ConvertToSingle(b).CopyTo(target[(i + count)..]);
                Vector.ConvertToSingle(c).CopyTo(target[(i + 2 * count)..]);
                Vector.ConvertToSingle(d).CopyTo(target[(i + 3 * count)..]);
            }
        }

        for (; i < source.Length; i++)
        {
            target[i] = source[i];
        }
    }

    /// <summary>Symmetric int8 quantization of each row: scale = max|w| / 127, q = round(w / scale).</summary>
    public static (sbyte[] Values, float[] Scales) QuantizeRows(ReadOnlySpan<float> w, int rows, int columns)
    {
        var values = new sbyte[rows * columns];
        var scales = new float[rows];
        for (var r = 0; r < rows; r++)
        {
            var row = w.Slice(r * columns, columns);
            var max = 0f;
            foreach (var v in row)
            {
                max = MathF.Max(max, MathF.Abs(v));
            }

            var scale = max > 0 ? max / 127f : 1f;
            scales[r] = scale;
            for (var c = 0; c < columns; c++)
            {
                values[r * columns + c] = (sbyte)Math.Clamp(MathF.Round(row[c] / scale), -127, 127);
            }
        }

        return (values, scales);
    }

    // runs body over [0, count) in contiguous ranges, in parallel when there is enough work; a few ranges per
    // processor keep the threads busy without the cost of scheduling every row
    private static void ForRanges(int count, long work, Action<int, int> body)
    {
        var threads = Environment.ProcessorCount;
        if (work < ParallelWork || threads == 1 || count < 2)
        {
            body(0, count);
            return;
        }

        var ranges = Math.Min(count, threads * 4);
        Parallel.For(0, ranges, r => body((int)((long)count * r / ranges), (int)((long)count * (r + 1) / ranges)));
    }

    /// <summary>
    /// Normalizes each of the n rows (mean 0, variance 1) and scales and shifts it, in place. Mean and variance are
    /// summed in double, so they are as exact as float allows whatever the width.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void LayerNorm(float[] x, int n, int width, float[] gain, float[] bias, float epsilon = 1e-5f)
    {
        for (var t = 0; t < n; t++)
        {
            var row = x.AsSpan(t * width, width);
            double sum = 0;
            foreach (var v in row)
            {
                sum += v;
            }

            var mean = sum / width;
            double squares = 0;
            foreach (var v in row)
            {
                squares += (v - mean) * (v - mean);
            }

            var m = (float)mean;
            var scale = (float)(1.0 / Math.Sqrt(squares / width + epsilon));
            for (var i = 0; i < width; i++)
            {
                row[i] = (row[i] - m) * scale * gain[i] + bias[i];
            }
        }
    }

    /// <summary>y += a · x, element by element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AddScaled(Span<float> y, ReadOnlySpan<float> x, float a)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && y.Length >= Vector<float>.Count)
        {
            var vy = MemoryMarshal.Cast<float, Vector<float>>(y);
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..y.Length]);
            var va = new Vector<float>(a);
            for (var k = 0; k < vy.Length; k++)
            {
                vy[k] += va * vx[k];
            }

            i = vy.Length * Vector<float>.Count;
        }

        for (; i < y.Length; i++)
        {
            y[i] += a * x[i];
        }
    }

    /// <summary>The index of the largest value; the first one when several are equal (as argmax in PyTorch).</summary>
    public static int ArgMax(ReadOnlySpan<float> x)
    {
        var best = 0;
        for (var i = 1; i < x.Length; i++)
        {
            if (x[i] > x[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>a += b, element by element.</summary>
    public static void Add(float[] a, float[] b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a.AsSpan());
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b.AsSpan(0, a.Length));
        for (var k = 0; k < va.Length; k++)
        {
            va[k] += vb[k];
        }

        for (var i = va.Length * Vector<float>.Count; i < a.Length; i++)
        {
            a[i] += b[i];
        }
    }

    /// <summary>The softmax of a row, in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Softmax(Span<float> row)
    {
        var max = float.NegativeInfinity;
        foreach (var v in row)
        {
            max = MathF.Max(max, v);
        }

        float sum = 0;
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = MathF.Exp(row[i] - max);
            sum += row[i];
        }

        for (var i = 0; i < row.Length; i++)
        {
            row[i] /= sum;
        }
    }

    /// <summary>The log-softmax of a row, in place; the sum of the exponentials is kept in double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void LogSoftmax(Span<float> row)
    {
        var max = Max(row);
        var log = (float)Math.Log(SumExp(row, max));
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vmax = Vector256.Create(max);
            var vlog = Vector256.Create(log);
            for (; i <= row.Length - 8; i += 8)
            {
                var v = Vector256.Create<float>(row[i..]);
                (v - vmax - vlog).CopyTo(row[i..]);
            }
        }

        for (; i < row.Length; i++)
        {
            row[i] = row[i] - max - log;
        }
    }

    /// <summary>log Σ exp(x), the sum in double.</summary>
    public static double LogSumExp(ReadOnlySpan<float> row)
    {
        var max = Max(row);
        return float.IsNegativeInfinity(max) ? double.NegativeInfinity : max + Math.Log(SumExp(row, max));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Max(ReadOnlySpan<float> row)
    {
        var max = float.NegativeInfinity;
        var i = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= 8)
        {
            var m = Vector256.Create(float.NegativeInfinity);
            for (; i <= row.Length - 8; i += 8)
            {
                m = Vector256.Max(m, Vector256.Create<float>(row[i..]));
            }

            for (var k = 0; k < 8; k++)
            {
                max = MathF.Max(max, m[k]);
            }
        }

        for (; i < row.Length; i++)
        {
            max = MathF.Max(max, row[i]);
        }

        return max;
    }

    // Σ exp(x − max): eight exponentials at a time in float (Vector256.Exp, within a unit or two in the last place),
    // summed in double; the few values left over at the end with MathF.Exp
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double SumExp(ReadOnlySpan<float> row, float max)
    {
        double sum = 0;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vmax = Vector256.Create(max);
            var s0 = Vector256<double>.Zero;
            var s1 = Vector256<double>.Zero;
            for (; i <= row.Length - 8; i += 8)
            {
                var (low, high) = Vector256.Widen(Vector256.Exp(Vector256.Create<float>(row[i..]) - vmax));
                s0 += low;
                s1 += high;
            }

            sum = Vector256.Sum(s0 + s1);
        }

        for (; i < row.Length; i++)
        {
            sum += MathF.Exp(row[i] - max);
        }

        return sum;
    }

    /// <summary>SiLU ("swish"): x · σ(x), in place.</summary>
    public static void Silu(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++)
        {
            x[i] /= 1f + MathF.Exp(-x[i]);
        }
    }

    /// <summary>ReLU: max(0, x), in place.</summary>
    public static void Relu(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = MathF.Max(0, x[i]);
        }
    }
}
