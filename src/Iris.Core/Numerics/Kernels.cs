using System.Numerics;
using System.Runtime.InteropServices;

namespace Iris.Numerics;

/// <summary>The arithmetic of the model, written for the processor: dot products in SIMD registers, rows of a matrix in parallel.</summary>
public static class Kernels
{
    /// <summary>Rows below this many multiply-adds are done on one thread.</summary>
    private const long ParallelWork = 1 << 16;

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
        void Row(int o)
        {
            var weights = w.AsSpan(o * inputs, inputs);
            var bias = b is null ? 0f : b[o];
            for (var t = 0; t < n; t++)
            {
                y[t * outputs + o] = Dot(x.AsSpan(t * inputs, inputs), weights) + bias;
            }
        }

        if ((long)n * inputs * outputs < ParallelWork)
        {
            for (var o = 0; o < outputs; o++)
            {
                Row(o);
            }
        }
        else
        {
            Parallel.For(0, outputs, Row);
        }

        return y;
    }

    /// <summary>Normalizes each of the n rows (mean 0, variance 1) and scales and shifts it, in place.</summary>
    public static void LayerNorm(float[] x, int n, int width, float[] gain, float[] bias, float epsilon = 1e-5f)
    {
        for (var t = 0; t < n; t++)
        {
            var row = x.AsSpan(t * width, width);
            float mean = 0;
            foreach (var v in row)
            {
                mean += v;
            }

            mean /= width;
            float variance = 0;
            foreach (var v in row)
            {
                variance += (v - mean) * (v - mean);
            }

            variance /= width;
            var scale = 1f / MathF.Sqrt(variance + epsilon);
            for (var i = 0; i < width; i++)
            {
                row[i] = (row[i] - mean) * scale * gain[i] + bias[i];
            }
        }
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

    /// <summary>The log-softmax of a row (in double, so the sums of a long beam stay exact), in place.</summary>
    public static void LogSoftmax(Span<float> row)
    {
        var max = float.NegativeInfinity;
        foreach (var v in row)
        {
            max = MathF.Max(max, v);
        }

        double sum = 0;
        foreach (var v in row)
        {
            sum += Math.Exp(v - max);
        }

        var log = (float)Math.Log(sum);
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = row[i] - max - log;
        }
    }

    /// <summary>SiLU ("swish"): x · σ(x), in place.</summary>
    public static void Silu(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++)
        {
            x[i] /= 1f + MathF.Exp(-x[i]);
        }
    }

    /// <summary>GELU (exact, with erf), in place.</summary>
    public static void Gelu(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = 0.5f * x[i] * (1f + Erf(x[i] / MathF.Sqrt(2f)));
        }
    }

    public static void Relu(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = MathF.Max(0, x[i]);
        }
    }

    // the error function (Abramowitz–Stegun 7.1.26 refined by a Newton step is not needed at float precision)
    private static float Erf(float x)
    {
        var t = 1.0 / (1.0 + 0.5 * Math.Abs(x));
        var y = 1 - t * Math.Exp(-x * x - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
                    t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return (float)(x >= 0 ? y : -y);
    }
}
