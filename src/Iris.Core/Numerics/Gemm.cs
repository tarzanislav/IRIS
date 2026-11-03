using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Iris.Numerics;

/// <summary>
/// A left matrix A (m × k) handed to the product in panels: <see cref="Pack"/> writes the rows [m0, m0 + rows) and
/// the columns [k0, k0 + depth) column after column, <see cref="Gemm.MR"/> values each (rows past <c>rows</c> as
/// zeros). A panel may be read from anywhere – a plain matrix, a transposed one, or the neighbourhoods of a picture
/// (a convolution without ever building the large matrix of neighbourhoods).
/// </summary>
internal unsafe interface IMatrixA
{
    void Pack(float* target, int m0, int rows, int k0, int depth);
}

/// <summary>A right matrix B (k × n) in panels: the columns [n0, n0 + columns), row after row, <see cref="Gemm.NR"/> values each.</summary>
internal unsafe interface IMatrixB
{
    void Pack(float* target, int n0, int columns, int k0, int depth);
}

/// <summary>A[m, k] = data[m · stride + k].</summary>
internal readonly unsafe struct RowsA : IMatrixA
{
    private readonly float* _data;
    private readonly int _stride;

    public RowsA(float* data, int stride)
    {
        _data = data;
        _stride = stride;
    }

    public void Pack(float* target, int m0, int rows, int k0, int depth)
    {
        var sources = stackalloc float*[Gemm.MR];
        for (var i = 0; i < Gemm.MR; i++)
        {
            sources[i] = i < rows ? _data + (long)(m0 + i) * _stride + k0 : null;
        }

        Gemm.Interleave(sources, depth, target);
    }
}

/// <summary>A[m, k] = data[k · stride + m] (the transpose of a row-major matrix, e.g. Xᵀ for the weight gradient).</summary>
internal readonly unsafe struct ColumnsA : IMatrixA
{
    private readonly float* _data;
    private readonly int _stride;

    public ColumnsA(float* data, int stride)
    {
        _data = data;
        _stride = stride;
    }

    public void Pack(float* target, int m0, int rows, int k0, int depth)
    {
        if (rows == Gemm.MR && Sse2.IsSupported)
        {
            // six neighbouring values per k: four and two at once
            for (var k = 0; k < depth; k++)
            {
                var source = _data + (long)(k0 + k) * _stride + m0;
                var row = target + k * Gemm.MR;
                Sse.Store(row, Sse.LoadVector128(source));
                Sse2.StoreScalar((double*)(row + 4), Sse2.LoadScalarVector128((double*)(source + 4)));
            }

            return;
        }

        for (var k = 0; k < depth; k++)
        {
            var source = _data + (long)(k0 + k) * _stride + m0;
            var row = target + k * Gemm.MR;
            var i = 0;
            for (; i < rows; i++)
            {
                row[i] = source[i];
            }

            for (; i < Gemm.MR; i++)
            {
                row[i] = 0;
            }
        }
    }
}

/// <summary>B[k, n] = data[k · stride + n].</summary>
internal readonly unsafe struct RowsB : IMatrixB
{
    private readonly float* _data;
    private readonly int _stride;

    public RowsB(float* data, int stride)
    {
        _data = data;
        _stride = stride;
    }

    public void Pack(float* target, int n0, int columns, int k0, int depth)
    {
        for (var k = 0; k < depth; k++)
        {
            var source = _data + (long)(k0 + k) * _stride + n0;
            var row = target + k * Gemm.NR;
            if (columns == Gemm.NR && Avx.IsSupported)
            {
                Avx.Store(row, Avx.LoadVector256(source));
                Avx.Store(row + 8, Avx.LoadVector256(source + 8));
                continue;
            }

            var j = 0;
            for (; j < columns; j++)
            {
                row[j] = source[j];
            }

            for (; j < Gemm.NR; j++)
            {
                row[j] = 0;
            }
        }
    }
}

/// <summary>B[k, n] = data[n · stride + k] (a weight matrix kept as [outputs, inputs], used as Wᵀ).</summary>
internal readonly unsafe struct ColumnsB : IMatrixB
{
    private readonly float* _data;
    private readonly int _stride;

    public ColumnsB(float* data, int stride)
    {
        _data = data;
        _stride = stride;
    }

    public void Pack(float* target, int n0, int columns, int k0, int depth)
    {
        for (var j = 0; j < columns; j++)
        {
            var source = _data + (long)(n0 + j) * _stride + k0;
            for (var k = 0; k < depth; k++)
            {
                target[k * Gemm.NR + j] = source[k];
            }
        }

        for (var j = columns; j < Gemm.NR; j++)
        {
            for (var k = 0; k < depth; k++)
            {
                target[k * Gemm.NR + j] = 0;
            }
        }
    }
}

/// <summary>
/// The product of matrices, C = A · B (or C += A · B), the heart of learning: in the convolutions, the LSTM and the
/// output layer, forwards and backwards. Written for the processor the way fast libraries do it, but as own code:
/// B is cut into panels of <see cref="NR"/> columns and blocks of <see cref="KC"/> rows that stay in the cache, A
/// into panels of <see cref="MR"/> rows, and a small kernel keeps a 6 × 16 block of C in twelve AVX registers while
/// it runs along k with fused multiply-adds – every value of A and B loaded once feeds 6 or 16 multiply-adds.
/// The rows of C are shared among threads (or, for a weight gradient with few rows and a long k, the k is shared and
/// the parts summed at the end), and no thread waits for another until all are done.
/// </summary>
internal static unsafe class Gemm
{
    /// <summary>Rows of a panel of A (and of the block of C held in registers).</summary>
    public const int MR = 6;

    /// <summary>Columns of a panel of B (two AVX registers).</summary>
    public const int NR = 16;

    /// <summary>Rows of B (columns of A) in one block: a panel of B (KC × NR floats, 16 kB) stays in the L1 cache.</summary>
    public const int KC = 256;

    /// <summary>Rows of A packed at a time (MC × KC floats, 96 kB, stay in the L2 cache).</summary>
    public const int MC = 96;

    // multiply-adds below which threads cost more than they bring
    private const long ParallelWork = 1 << 18;

    // a block of zeros to read missing rows from (never written)
    private static readonly float* Zeros = AllocateZeros();

    [ThreadStatic]
    private static float* t_a;

    [ThreadStatic]
    private static long t_aSize;

    [ThreadStatic]
    private static float* t_b;

    [ThreadStatic]
    private static long t_bSize;

    public static bool Fast => Avx2.IsSupported && Fma.IsSupported;

    /// <summary>C = A · B (add: C += A · B) for A m × k, B k × n, C row-major with the given stride.</summary>
    public static void Multiply<TA, TB>(TA a, TB b, int m, int n, int k, float* c, int ldc, bool add, int threads)
        where TA : struct, IMatrixA
        where TB : struct, IMatrixB
    {
        if (m <= 0 || n <= 0)
        {
            return;
        }

        if (k <= 0)
        {
            Clear(c, m, n, ldc, add);
            return;
        }

        using var packed = PackedB.Create(b, n, k, threads);
        Multiply(a, packed, m, c, ldc, add, threads);
    }

    /// <summary>C = A · B with B packed beforehand (a weight matrix used many times, e.g. in every LSTM step).</summary>
    public static void Multiply<TA>(TA a, PackedB b, int m, float* c, int ldc, bool add, int threads)
        where TA : struct, IMatrixA
    {
        if (m <= 0 || b.N <= 0)
        {
            return;
        }

        if (b.K <= 0)
        {
            Clear(c, m, b.N, ldc, add);
            return;
        }

        var chunks = (m + MC - 1) / MC;
        if (threads <= 1 || chunks == 1 || (long)m * b.N * b.K < ParallelWork)
        {
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                Block(a, b, chunk * MC, Math.Min(MC, m - chunk * MC), c, ldc, add);
            }

            return;
        }

        var bb = b;
        var cc = (nint)c;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads }, chunk =>
            Block(a, bb, chunk * MC, Math.Min(MC, m - chunk * MC), (float*)cc, ldc, add));
    }

    /// <summary>
    /// C = A · B (add: C += A · B) for a long k and a small C (a weight gradient: k = all pixels or frames of a
    /// batch): the k is split among the threads, each sums its part into its own C, and the parts are added up.
    /// </summary>
    public static void MultiplyLong<TA, TB>(TA a, TB b, int m, int n, int k, float* c, int ldc, bool add, int threads)
        where TA : struct, IMatrixA
        where TB : struct, IMatrixB
    {
        if (m <= 0 || n <= 0)
        {
            return;
        }

        if (k <= 0)
        {
            Clear(c, m, n, ldc, add);
            return;
        }

        var parts = (int)Math.Min(threads, Math.Max(1, (long)m * n * k / ParallelWork));
        parts = Math.Min(parts, (k + KC - 1) / KC);
        if (parts <= 1)
        {
            LongPart(a, b, m, n, 0, k, c, ldc, add);
            return;
        }

        var size = (long)m * n;
        var buffers = (float*)NativeMemory.AlignedAlloc((nuint)(size * parts * sizeof(float)), 64);
        try
        {
            var blocks = (k + KC - 1) / KC;
            var bufferAddress = (nint)buffers;
            Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = threads }, part =>
            {
                // whole blocks of KC to each part, so the panels are full
                var from = (int)Math.Min(k, (long)blocks * part / parts * KC);
                var to = (int)Math.Min(k, (long)blocks * (part + 1) / parts * KC);
                var own = (float*)bufferAddress + size * part;
                if (to > from)
                {
                    LongPart(a, b, m, n, from, to, own, n, add: false);
                }
                else
                {
                    new Span<float>(own, (int)size).Clear();
                }
            });

            for (var i = 0; i < m; i++)
            {
                var target = new Span<float>(c + (long)i * ldc, n);
                if (!add)
                {
                    target.Clear();
                }

                for (var part = 0; part < parts; part++)
                {
                    Kernels.AddScaled(target, new ReadOnlySpan<float>(buffers + size * part + (long)i * n, n), 1f);
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffers);
        }
    }

    /// <summary>
    /// Writes MR rows (null = zeros) side by side: target[k · MR + i] = rows[i][k]. Six sequential reads and one
    /// sequential write – the packing of A at the speed of memory: eight values of each row at a time, turned in
    /// registers (unpack, shuffle) into the 48 values of eight columns, written as twelve quarter-lines.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Interleave(float** rows, int depth, float* target)
    {
        float* r0 = rows[0], r1 = rows[1], r2 = rows[2], r3 = rows[3], r4 = rows[4], r5 = rows[5];
        var k = 0;
        if (Avx.IsSupported && depth >= 8)
        {
            // a missing row reads from zeros (never longer than a block of k)
            if (r0 == null || r1 == null || r2 == null || r3 == null || r4 == null || r5 == null)
            {
                if (depth > KC)
                {
                    goto Scalar;
                }

                var zeros = Zeros;
                r0 = r0 == null ? zeros : r0;
                r1 = r1 == null ? zeros : r1;
                r2 = r2 == null ? zeros : r2;
                r3 = r3 == null ? zeros : r3;
                r4 = r4 == null ? zeros : r4;
                r5 = r5 == null ? zeros : r5;
            }

            for (; k <= depth - 8; k += 8)
            {
                var x0 = Avx.LoadVector256(r0 + k);
                var x1 = Avx.LoadVector256(r1 + k);
                var x2 = Avx.LoadVector256(r2 + k);
                var x3 = Avx.LoadVector256(r3 + k);
                var x4 = Avx.LoadVector256(r4 + k);
                var x5 = Avx.LoadVector256(r5 + k);

                // per 128-bit half (columns k..k+3 below, k+4..k+7 above):
                var t0 = Avx.UnpackLow(x0, x1);       // x00 x10 x01 x11
                var t1 = Avx.UnpackHigh(x0, x1);      // x02 x12 x03 x13
                var t2 = Avx.UnpackLow(x2, x3);       // x20 x30 x21 x31
                var t3 = Avx.UnpackHigh(x2, x3);      // x22 x32 x23 x33
                var u0 = Avx.UnpackLow(x4, x5);       // x40 x50 x41 x51
                var u1 = Avx.UnpackHigh(x4, x5);      // x42 x52 x43 x53
                var c0 = Avx.Shuffle(t0, t2, 0x44);   // x00 x10 x20 x30
                var c1 = Avx.Shuffle(t0, t2, 0xEE);   // x01 x11 x21 x31
                var c2 = Avx.Shuffle(t1, t3, 0x44);   // x02 x12 x22 x32
                var c3 = Avx.Shuffle(t1, t3, 0xEE);   // x03 x13 x23 x33
                var g1 = Avx.Shuffle(u0, c1, 0x44);   // x40 x50 x01 x11
                var g2 = Avx.Shuffle(c1, u0, 0xEE);   // x21 x31 x41 x51
                var g4 = Avx.Shuffle(u1, c3, 0x44);   // x42 x52 x03 x13
                var g5 = Avx.Shuffle(c3, u1, 0xEE);   // x23 x33 x43 x53

                // the lower halves are columns k..k+3 (24 values), the upper ones k+4..k+7
                var low = target;
                var high = target + 4 * MR;
                Sse.Store(low, c0.GetLower());
                Sse.Store(low + 4, g1.GetLower());
                Sse.Store(low + 8, g2.GetLower());
                Sse.Store(low + 12, c2.GetLower());
                Sse.Store(low + 16, g4.GetLower());
                Sse.Store(low + 20, g5.GetLower());
                Sse.Store(high, c0.GetUpper());
                Sse.Store(high + 4, g1.GetUpper());
                Sse.Store(high + 8, g2.GetUpper());
                Sse.Store(high + 12, c2.GetUpper());
                Sse.Store(high + 16, g4.GetUpper());
                Sse.Store(high + 20, g5.GetUpper());
                target += 8 * MR;
            }
        }

    Scalar:
        if (r0 != null && r1 != null && r2 != null && r3 != null && r4 != null && r5 != null)
        {
            for (; k < depth; k++)
            {
                target[0] = r0[k];
                target[1] = r1[k];
                target[2] = r2[k];
                target[3] = r3[k];
                target[4] = r4[k];
                target[5] = r5[k];
                target += MR;
            }

            return;
        }

        for (; k < depth; k++)
        {
            target[0] = r0 == null ? 0 : r0[k];
            target[1] = r1 == null ? 0 : r1[k];
            target[2] = r2 == null ? 0 : r2[k];
            target[3] = r3 == null ? 0 : r3[k];
            target[4] = r4 == null ? 0 : r4[k];
            target[5] = r5 == null ? 0 : r5[k];
            target += MR;
        }
    }

    private static float* AllocateZeros()
    {
        var zeros = (float*)NativeMemory.AlignedAlloc((KC + 16) * sizeof(float), 64);
        new Span<float>(zeros, KC + 16).Clear();
        return zeros;
    }

    internal static void ZeroRows(float* target, int rows, int depth)
    {
        if (rows == MR)
        {
            return;
        }

        for (var k = 0; k < depth; k++)
        {
            for (var i = rows; i < MR; i++)
            {
                target[k * MR + i] = 0;
            }
        }
    }

    private static void Clear(float* c, int m, int n, int ldc, bool add)
    {
        if (add)
        {
            return;
        }

        for (var i = 0; i < m; i++)
        {
            new Span<float>(c + (long)i * ldc, n).Clear();
        }
    }

    // rows [m0, m0 + mc) of C: block after block of k, the rows of A packed once per block and run against every panel of B
    private static void Block<TA>(TA a, PackedB b, int m0, int mc, float* c, int ldc, bool add)
        where TA : struct, IMatrixA
    {
        var panels = (mc + MR - 1) / MR;
        var buffer = Scratch(ref t_a, ref t_aSize, (long)panels * MR * KC);
        for (var k0 = 0; k0 < b.K; k0 += KC)
        {
            var kc = Math.Min(KC, b.K - k0);
            for (var p = 0; p < panels; p++)
            {
                a.Pack(buffer + (long)p * MR * kc, m0 + p * MR, Math.Min(MR, mc - p * MR), k0, kc);
            }

            var accumulate = add || k0 > 0;
            for (var j = 0; j < b.Panels; j++)
            {
                var panel = b.Panel(k0, j);
                var columns = Math.Min(NR, b.N - j * NR);
                for (var p = 0; p < panels; p++)
                {
                    Kernel(kc, buffer + (long)p * MR * kc, panel, c + (long)(m0 + p * MR) * ldc + j * NR, ldc, Math.Min(MR, mc - p * MR), columns, accumulate);
                }
            }
        }
    }

    // the k range [from, to) of A · B into C (add: onto what is there); B packed block by block as it is needed
    private static void LongPart<TA, TB>(TA a, TB b, int m, int n, int from, int to, float* c, int ldc, bool add)
        where TA : struct, IMatrixA
        where TB : struct, IMatrixB
    {
        var bPanels = (n + NR - 1) / NR;
        var bBuffer = Scratch(ref t_b, ref t_bSize, (long)bPanels * NR * KC);
        var aBuffer = Scratch(ref t_a, ref t_aSize, (long)MC * KC);
        for (var k0 = from; k0 < to; k0 += KC)
        {
            var kc = Math.Min(KC, to - k0);
            for (var j = 0; j < bPanels; j++)
            {
                b.Pack(bBuffer + (long)j * NR * kc, j * NR, Math.Min(NR, n - j * NR), k0, kc);
            }

            var accumulate = add || k0 > from;
            for (var m0 = 0; m0 < m; m0 += MC)
            {
                var mc = Math.Min(MC, m - m0);
                var panels = (mc + MR - 1) / MR;
                for (var p = 0; p < panels; p++)
                {
                    a.Pack(aBuffer + (long)p * MR * kc, m0 + p * MR, Math.Min(MR, mc - p * MR), k0, kc);
                }

                for (var j = 0; j < bPanels; j++)
                {
                    var columns = Math.Min(NR, n - j * NR);
                    for (var p = 0; p < panels; p++)
                    {
                        Kernel(kc, aBuffer + (long)p * MR * kc, bBuffer + (long)j * NR * kc, c + (long)(m0 + p * MR) * ldc + j * NR, ldc, Math.Min(MR, mc - p * MR), columns, accumulate);
                    }
                }
            }
        }
    }

    private static float* Scratch(ref float* buffer, ref long size, long needed)
    {
        if (size < needed)
        {
            NativeMemory.AlignedFree(buffer);
            buffer = (float*)NativeMemory.AlignedAlloc((nuint)(needed * sizeof(float)), 64);
            size = needed;
        }

        return buffer;
    }

    /// <summary>A block of up to MR × NR values of C from a panel of A and one of B, both of the given depth.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Kernel(int depth, float* a, float* b, float* c, int ldc, int rows, int columns, bool add)
    {
        if (!Fast)
        {
            PortableKernel(depth, a, b, c, ldc, rows, columns, add);
            return;
        }

        if (add)
        {
            // the block of C is read at the end: ask for it now, so it is near while the products run
            for (var i = 0; i < rows; i++)
            {
                Sse.Prefetch0(c + (long)i * ldc);
                Sse.Prefetch0(c + (long)i * ldc + 8);
            }
        }

        Vector256<float> c00 = default, c01 = default, c10 = default, c11 = default, c20 = default, c21 = default;
        Vector256<float> c30 = default, c31 = default, c40 = default, c41 = default, c50 = default, c51 = default;
        for (var k = 0; k < depth; k++)
        {
            var b0 = Avx.LoadVector256(b);
            var b1 = Avx.LoadVector256(b + 8);
            var x = Avx.BroadcastScalarToVector256(a);
            c00 = Fma.MultiplyAdd(x, b0, c00);
            c01 = Fma.MultiplyAdd(x, b1, c01);
            x = Avx.BroadcastScalarToVector256(a + 1);
            c10 = Fma.MultiplyAdd(x, b0, c10);
            c11 = Fma.MultiplyAdd(x, b1, c11);
            x = Avx.BroadcastScalarToVector256(a + 2);
            c20 = Fma.MultiplyAdd(x, b0, c20);
            c21 = Fma.MultiplyAdd(x, b1, c21);
            x = Avx.BroadcastScalarToVector256(a + 3);
            c30 = Fma.MultiplyAdd(x, b0, c30);
            c31 = Fma.MultiplyAdd(x, b1, c31);
            x = Avx.BroadcastScalarToVector256(a + 4);
            c40 = Fma.MultiplyAdd(x, b0, c40);
            c41 = Fma.MultiplyAdd(x, b1, c41);
            x = Avx.BroadcastScalarToVector256(a + 5);
            c50 = Fma.MultiplyAdd(x, b0, c50);
            c51 = Fma.MultiplyAdd(x, b1, c51);
            a += MR;
            b += NR;
        }

        if (rows == MR && columns == NR)
        {
            Store(c, c00, c01, add);
            Store(c + ldc, c10, c11, add);
            Store(c + 2 * ldc, c20, c21, add);
            Store(c + 3 * ldc, c30, c31, add);
            Store(c + 4 * ldc, c40, c41, add);
            Store(c + 5 * ldc, c50, c51, add);
            return;
        }

        // a block at the edge of C: through a small buffer, only the part that exists
        var t = stackalloc float[MR * NR];
        Avx.Store(t, c00);
        Avx.Store(t + 8, c01);
        Avx.Store(t + 16, c10);
        Avx.Store(t + 24, c11);
        Avx.Store(t + 32, c20);
        Avx.Store(t + 40, c21);
        Avx.Store(t + 48, c30);
        Avx.Store(t + 56, c31);
        Avx.Store(t + 64, c40);
        Avx.Store(t + 72, c41);
        Avx.Store(t + 80, c50);
        Avx.Store(t + 88, c51);
        StoreEdge(t, c, ldc, rows, columns, add);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(float* row, Vector256<float> low, Vector256<float> high, bool add)
    {
        if (add)
        {
            low = Avx.Add(low, Avx.LoadVector256(row));
            high = Avx.Add(high, Avx.LoadVector256(row + 8));
        }

        Avx.Store(row, low);
        Avx.Store(row + 8, high);
    }

    private static void StoreEdge(float* t, float* c, int ldc, int rows, int columns, bool add)
    {
        for (var i = 0; i < rows; i++)
        {
            var row = c + (long)i * ldc;
            for (var j = 0; j < columns; j++)
            {
                row[j] = add ? row[j] + t[i * NR + j] : t[i * NR + j];
            }
        }
    }

    // the same arithmetic without AVX2 (old processors): slower, the same results up to rounding
    private static void PortableKernel(int depth, float* a, float* b, float* c, int ldc, int rows, int columns, bool add)
    {
        var t = stackalloc float[MR * NR];
        new Span<float>(t, MR * NR).Clear();
        for (var k = 0; k < depth; k++)
        {
            for (var i = 0; i < MR; i++)
            {
                var x = a[i];
                var row = t + i * NR;
                for (var j = 0; j < NR; j++)
                {
                    row[j] += x * b[j];
                }
            }

            a += MR;
            b += NR;
        }

        StoreEdge(t, c, ldc, rows, columns, add);
    }

    /// <summary>
    /// B packed whole, ready for many products: for each block of <see cref="KC"/> rows the panels of <see cref="NR"/>
    /// columns one after another (unmanaged, 64-byte aligned; dispose when done).
    /// </summary>
    public sealed class PackedB : IDisposable
    {
        private float* _data;

        private PackedB(int n, int k)
        {
            (N, K) = (n, k);
            Panels = (n + NR - 1) / NR;
            _data = (float*)NativeMemory.AlignedAlloc((nuint)((long)Panels * NR * Math.Max(1, k) * sizeof(float)), 64);
        }

        public int N { get; }

        public int K { get; }

        public int Panels { get; }

        /// <summary>A packed B of zeros (n × k), to be filled value by value through <see cref="At"/>.</summary>
        public static PackedB Zeros(int n, int k)
        {
            var packed = new PackedB(n, k);
            new Span<float>(packed._data, checked(packed.Panels * NR * Math.Max(1, k))).Clear();
            return packed;
        }

        public static PackedB Create<TB>(TB b, int n, int k, int threads = 1)
            where TB : struct, IMatrixB
        {
            var packed = new PackedB(n, k);
            var blocks = (k + KC - 1) / KC;
            var jobs = blocks * packed.Panels;
            if (threads <= 1 || (long)n * k < ParallelWork)
            {
                for (var job = 0; job < jobs; job++)
                {
                    packed.PackPanel(b, job / packed.Panels, job % packed.Panels);
                }
            }
            else
            {
                Parallel.For(0, jobs, new ParallelOptions { MaxDegreeOfParallelism = threads }, job => packed.PackPanel(b, job / packed.Panels, job % packed.Panels));
            }

            return packed;
        }

        /// <summary>Where B[k, n] is kept; the values n … n + 7 follow it when n is a multiple of 8.</summary>
        public float* At(int k, int n)
        {
            var k0 = k / KC * KC;
            return Panel(k0, n / NR) + ((long)(k - k0) * NR) + (n % NR);
        }

        /// <summary>The panel j of the block of rows starting at k0 (kc × NR floats).</summary>
        public float* Panel(int k0, int j) => _data + (long)k0 * Panels * NR + (long)j * NR * Math.Min(KC, K - k0);

        public void Dispose()
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
        }

        private void PackPanel<TB>(TB b, int block, int j)
            where TB : struct, IMatrixB
        {
            var k0 = block * KC;
            b.Pack(Panel(k0, j), j * NR, Math.Min(NR, N - j * NR), k0, Math.Min(KC, K - k0));
        }
    }
}
