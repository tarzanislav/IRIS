using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Iris.Numerics;

/// <summary>How a row of weights is read into float lanes: float32 as it is, int8 widened in the register.</summary>
internal unsafe interface IWeightRow
{
    static abstract Vector256<float> Load(void* row, int index);

    static abstract float Scalar(void* row, int index);
}

internal readonly unsafe struct FloatRow : IWeightRow
{
    public static Vector256<float> Load(void* row, int index) => Avx.LoadVector256((float*)row + index);

    public static float Scalar(void* row, int index) => ((float*)row)[index];
}

internal readonly unsafe struct Int8Row : IWeightRow
{
    // 8 bytes → 8 int32 (sign extended) → 8 floats, without touching memory in between
    public static Vector256<float> Load(void* row, int index) => Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32((sbyte*)row + index));

    public static float Scalar(void* row, int index) => ((sbyte*)row)[index];
}

/// <summary>
/// Dot products for dense layers (AVX2 + FMA). Every dot product is summed in the same order – eight lanes along the
/// row, then the lanes, then the rest one by one – whatever else is computed alongside, so a row gives the same
/// result alone or in a batch. Speed comes from computing four of them side by side: four outputs for one input row
/// (greedy), or one output for up to four input rows (beams, encoder), which also reads each row of weights once.
/// </summary>
internal static unsafe class RowKernels
{
    public static bool Supported => Avx2.IsSupported && Fma.IsSupported;

    /// <summary>result[r] = Σᵢ x[r·stride + i] · w[i] for r &lt; rows (1 to 4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Rows<TRow>(float* x, int stride, int rows, void* w, int length, float* result)
        where TRow : struct, IWeightRow
    {
        Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
        float* x1 = x + stride, x2 = x + 2 * stride, x3 = x + 3 * stride;
        var i = 0;
        switch (rows)
        {
            case 1:
                for (; i <= length - 8; i += 8)
                {
                    a0 = Fma.MultiplyAdd(Avx.LoadVector256(x + i), TRow.Load(w, i), a0);
                }

                break;
            case 2:
                for (; i <= length - 8; i += 8)
                {
                    var v = TRow.Load(w, i);
                    a0 = Fma.MultiplyAdd(Avx.LoadVector256(x + i), v, a0);
                    a1 = Fma.MultiplyAdd(Avx.LoadVector256(x1 + i), v, a1);
                }

                break;
            case 3:
                for (; i <= length - 8; i += 8)
                {
                    var v = TRow.Load(w, i);
                    a0 = Fma.MultiplyAdd(Avx.LoadVector256(x + i), v, a0);
                    a1 = Fma.MultiplyAdd(Avx.LoadVector256(x1 + i), v, a1);
                    a2 = Fma.MultiplyAdd(Avx.LoadVector256(x2 + i), v, a2);
                }

                break;
            default:
                for (; i <= length - 8; i += 8)
                {
                    var v = TRow.Load(w, i);
                    a0 = Fma.MultiplyAdd(Avx.LoadVector256(x + i), v, a0);
                    a1 = Fma.MultiplyAdd(Avx.LoadVector256(x1 + i), v, a1);
                    a2 = Fma.MultiplyAdd(Avx.LoadVector256(x2 + i), v, a2);
                    a3 = Fma.MultiplyAdd(Avx.LoadVector256(x3 + i), v, a3);
                }

                break;
        }

        var accumulators = stackalloc Vector256<float>[] { a0, a1, a2, a3 };
        for (var r = 0; r < rows; r++)
        {
            var sum = Vector256.Sum(accumulators[r]);
            for (var k = i; k < length; k++)
            {
                sum += x[r * stride + k] * TRow.Scalar(w, k);
            }

            result[r] = sum;
        }
    }

    /// <summary>result[k] = Σᵢ x[i] · w[k·rowSize + i] for four rows of weights.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Outputs<TRow>(float* x, void* w, long rowBytes, int length, float* result)
        where TRow : struct, IWeightRow
    {
        void* w1 = (byte*)w + rowBytes, w2 = (byte*)w + 2 * rowBytes, w3 = (byte*)w + 3 * rowBytes;
        Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
        var i = 0;
        for (; i <= length - 8; i += 8)
        {
            var v = Avx.LoadVector256(x + i);
            a0 = Fma.MultiplyAdd(v, TRow.Load(w, i), a0);
            a1 = Fma.MultiplyAdd(v, TRow.Load(w1, i), a1);
            a2 = Fma.MultiplyAdd(v, TRow.Load(w2, i), a2);
            a3 = Fma.MultiplyAdd(v, TRow.Load(w3, i), a3);
        }

        float s0 = Vector256.Sum(a0), s1 = Vector256.Sum(a1), s2 = Vector256.Sum(a2), s3 = Vector256.Sum(a3);
        for (; i < length; i++)
        {
            s0 += x[i] * TRow.Scalar(w, i);
            s1 += x[i] * TRow.Scalar(w1, i);
            s2 += x[i] * TRow.Scalar(w2, i);
            s3 += x[i] * TRow.Scalar(w3, i);
        }

        result[0] = s0;
        result[1] = s1;
        result[2] = s2;
        result[3] = s3;
    }

    /// <summary>
    /// y[t, o] = (x[t] · W[o]) × scale[o] + b[o] for the outputs from..to and all n rows of x; W is [outputs, inputs]
    /// of TRow elements, scales null = 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Linear<TRow>(float* x, int n, void* w, int elementSize, float* scales, float* b, int inputs, int outputs,
        float* y, int from, int to)
        where TRow : struct, IWeightRow
    {
        var result = stackalloc float[4];
        long rowBytes = (long)inputs * elementSize;
        var o = from;
        if (n == 1)
        {
            for (; o <= to - 4; o += 4)
            {
                Outputs<TRow>(x, (byte*)w + o * rowBytes, rowBytes, inputs, result);
                for (var k = 0; k < 4; k++)
                {
                    y[o + k] = result[k] * (scales is null ? 1f : scales[o + k]) + (b is null ? 0f : b[o + k]);
                }
            }
        }

        for (; o < to; o++)
        {
            var row = (byte*)w + o * rowBytes;
            var scale = scales is null ? 1f : scales[o];
            var bias = b is null ? 0f : b[o];
            for (var t = 0; t < n; t += 4)
            {
                var rows = Math.Min(4, n - t);
                Rows<TRow>(x + (long)t * inputs, inputs, rows, row, inputs, result);
                for (var r = 0; r < rows; r++)
                {
                    y[(long)(t + r) * outputs + o] = result[r] * scale + bias;
                }
            }
        }
    }
}
