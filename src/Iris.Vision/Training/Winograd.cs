using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Iris.Numerics;

namespace Iris.Vision.Training;

/// <summary>
/// The 3 × 3 convolutions ("same", padding 1) of the reader by the minimal filtering of Winograd, F(4 × 4, 3 × 3):
/// a 6 × 6 piece of the input and the 3 × 3 kernel are both turned into 6 × 6 values (Bᵀ d B and G g Gᵀ), multiplied
/// value by value – which for all channels at once are 36 matrix products – and turned back into 4 × 4 outputs
/// (Aᵀ m A). That is 36 multiplications for 16 outputs instead of 144: four times fewer, for the forward pass, for the
/// gradient of the input (the same convolution with the kernel turned) and for the gradient of the weights
/// (dU = Σ V ⊙ dM, dg = Gᵀ dU G). The transforms use the points 0, ±1, ±2 and ∞; in float32 their error stays near
/// 10⁻⁵ of the largest value, far below what learning notices.
/// Tiles are worked in chunks that stay in the cache: gather and transform a chunk, 36 products with the prepacked
/// kernels, transform back – chunks shared among the threads.
/// </summary>
internal static unsafe class Winograd
{
    /// <summary>Tiles worked at a time in a convolution (a multiple of the rows of a panel of the product).</summary>
    public const int Chunk = 24;

    /// <summary>Tiles worked at a time for the gradient of the weights (longer: its sums are the large part).</summary>
    public const int GradientChunk = 48;

    // values of a transformed tile (6 × 6), outputs of a tile per side, size of the input piece per side
    private const int Values = 36;
    private const int Side = 4;
    private const int Piece = 6;

    [ThreadStatic]
    private static float* t_v;

    [ThreadStatic]
    private static long t_vSize;

    [ThreadStatic]
    private static float* t_m;

    [ThreadStatic]
    private static long t_mSize;

    /// <summary>Whether a layer can use it: a 3 × 3 kernel, padding 1, channels in whole vectors, vector hardware.</summary>
    public static bool Fits(ConvParameters p, int pad) =>
        (Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated) && p.Kh == 3 && p.Kw == 3 && pad == 1 && p.Inputs % 8 == 0 && p.Outputs % 8 == 0;

    /// <summary>
    /// The 36 transformed kernels U[ξ] = G g Gᵀ as packed right matrices [inputs, outputs], from weights
    /// [outputs, 3, 3, inputs]; flipped: the kernel turned by 180° with inputs and outputs swapped (the gradient of the input).
    /// Eight output channels at a time, written straight into the packed panels.
    /// </summary>
    public static Gemm.PackedB[] TransformKernels(float[] weight, int inputs, int outputs, bool flipped)
    {
        // channels of the transformed convolution
        int ci = flipped ? outputs : inputs, co = flipped ? inputs : outputs;

        // taps[tap][i][o]: the kernel of the transformed convolution, a row of co values per input channel and tap
        var taps = (float*)NativeMemory.AlignedAlloc((nuint)(9L * ci * co * sizeof(float)), 64);
        var packed = new Gemm.PackedB[Values];
        try
        {
            fixed (float* w = weight)
            {
                for (var tap = 0; tap < 9; tap++)
                {
                    var target = taps + ((long)tap * ci * co);
                    if (flipped)
                    {
                        // W[out = i][2 − ky][2 − kx][in = o]: already a row over o
                        for (var i = 0; i < ci; i++)
                        {
                            var source = w + ((long)((i * 9) + (8 - tap)) * inputs);
                            System.Buffer.MemoryCopy(source, target + ((long)i * co), co * sizeof(float), co * sizeof(float));
                        }
                    }
                    else
                    {
                        // W[o][ky][kx][i]: turned so that o runs along the row
                        for (var o = 0; o < co; o++)
                        {
                            var source = w + ((long)((o * 9) + tap) * inputs);
                            for (var i = 0; i < ci; i++)
                            {
                                target[((long)i * co) + o] = source[i];
                            }
                        }
                    }
                }
            }

            for (var xi = 0; xi < Values; xi++)
            {
                packed[xi] = Gemm.PackedB.Zeros(co, ci);
            }

            var gg = stackalloc Vector256<float>[3 * Piece];
            var plane = (long)ci * co;
            for (var i = 0; i < ci; i++)
            {
                for (var o = 0; o < co; o += 8)
                {
                    var g = taps + ((long)i * co) + o;

                    // G g (6 × 3), column by column
                    for (var c = 0; c < 3; c++)
                    {
                        KernelLine(Vector256.Load(g + (c * plane)), Vector256.Load(g + ((3 + c) * plane)), Vector256.Load(g + ((6 + c) * plane)),
                            out gg[c], out gg[3 + c], out gg[6 + c], out gg[9 + c], out gg[12 + c], out gg[15 + c]);
                    }

                    // (G g) Gᵀ (6 × 6), row by row, into the panels
                    for (var r = 0; r < Piece; r++)
                    {
                        KernelLine(gg[r * 3], gg[(r * 3) + 1], gg[(r * 3) + 2], out var u0, out var u1, out var u2, out var u3, out var u4, out var u5);
                        Vector256.Store(u0, packed[(r * Piece) + 0].At(i, o));
                        Vector256.Store(u1, packed[(r * Piece) + 1].At(i, o));
                        Vector256.Store(u2, packed[(r * Piece) + 2].At(i, o));
                        Vector256.Store(u3, packed[(r * Piece) + 3].At(i, o));
                        Vector256.Store(u4, packed[(r * Piece) + 4].At(i, o));
                        Vector256.Store(u5, packed[(r * Piece) + 5].At(i, o));
                    }
                }
            }

            return packed;
        }
        catch
        {
            foreach (var p in packed)
            {
                p?.Dispose();
            }

            throw;
        }
        finally
        {
            NativeMemory.AlignedFree(taps);
        }
    }

    // G · (a, b, c): the six values of a kernel line at the points 0, −1, 1, 1/2, −1/2 and ∞ (scaled)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void KernelLine(Vector256<float> a, Vector256<float> b, Vector256<float> c,
        out Vector256<float> u0, out Vector256<float> u1, out Vector256<float> u2, out Vector256<float> u3, out Vector256<float> u4, out Vector256<float> u5)
    {
        var sixth = Vector256.Create(1f / 6);
        var outer = a + c;
        var middle = MulAdd(Vector256.Create(1f / 24), a, c * sixth);    // a / 24 + c / 6
        var twelfth = b * Vector256.Create(1f / 12);
        u0 = a * Vector256.Create(0.25f);
        u1 = -(outer + b) * sixth;
        u2 = (b - outer) * sixth;
        u3 = middle + twelfth;
        u4 = middle - twelfth;
        u5 = c;
    }

    /// <summary>y [pixels, outputs] = the convolution of x [pixels, inputs] (same geometry, padding 1) with the kernels.</summary>
    public static void Convolve(float* x, int inputs, Level level, Gemm.PackedB[] kernels, int outputs, float* y, int threads)
    {
        var tiles = new Tiles(level);
        var chunk = Chunk;
        var chunks = (tiles.Count + chunk - 1) / chunk;
        var xs = (nint)x;
        var ys = (nint)y;
        Work.For(chunks, (long)tiles.Count * Values * inputs * outputs, threads, (from, to) =>
        {
            var v = Scratch(ref t_v, ref t_vSize, (long)Values * chunk * inputs);
            var m = Scratch(ref t_m, ref t_mSize, (long)Values * chunk * outputs);
            for (var part = from; part < to; part++)
            {
                var first = part * chunk;
                var count = Math.Min(chunk, tiles.Count - first);
                for (var t = 0; t < count; t++)
                {
                    InputTile((float*)xs, inputs, tiles, first + t, v + (long)t * inputs, (long)chunk * inputs);
                }

                for (var xi = 0; xi < Values; xi++)
                {
                    Gemm.Multiply(new RowsA(v + (long)xi * chunk * inputs, inputs), kernels[xi], count, m + (long)xi * chunk * outputs, outputs, add: false, threads: 1);
                }

                for (var t = 0; t < count; t++)
                {
                    OutputTile(m + (long)t * outputs, (long)chunk * outputs, outputs, tiles, first + t, (float*)ys);
                }
            }
        });
    }

    /// <summary>
    /// The gradient of the weights [outputs, 3, 3, inputs] (added to gw) from the input x [pixels, inputs] and the
    /// gradient of the output dz [pixels, outputs]: per tile V = Bᵀ d B and dM = A dZ Aᵀ, dU[ξ] = Σ Vᵀ dM, dg = Gᵀ dU G.
    /// </summary>
    public static void WeightGradient(float* x, int inputs, float* dz, int outputs, Level level, float[] gw, int threads)
    {
        var tiles = new Tiles(level);
        var chunk = GradientChunk;
        var chunks = (tiles.Count + chunk - 1) / chunk;
        var parts = Math.Max(1, Math.Min(threads, chunks));
        var plane = inputs * outputs;
        var size = (long)Values * plane;
        var xs = (nint)x;
        var ds = (nint)dz;
        var sums = (float*)NativeMemory.AlignedAlloc((nuint)(parts * size * sizeof(float)), 64);
        var all = (nint)sums;
        try
        {
            // every part sums dU over its tiles into its own place
            Work.For(parts, (long)tiles.Count * Values * plane, parts, (fromPart, toPart) =>
            {
                for (var part = fromPart; part < toPart; part++)
                {
                    var du = (float*)all + (part * size);
                    new Span<float>(du, (int)size).Clear();
                    var v = Scratch(ref t_v, ref t_vSize, (long)Values * chunk * inputs);
                    var m = Scratch(ref t_m, ref t_mSize, (long)Values * chunk * outputs);
                    var from = (int)((long)chunks * part / parts);
                    var to = (int)((long)chunks * (part + 1) / parts);
                    for (var c = from; c < to; c++)
                    {
                        var first = c * chunk;
                        var count = Math.Min(chunk, tiles.Count - first);
                        for (var t = 0; t < count; t++)
                        {
                            InputTile((float*)xs, inputs, tiles, first + t, v + (long)t * inputs, (long)chunk * inputs);
                            GradientTile((float*)ds, outputs, tiles, first + t, m + (long)t * outputs, (long)chunk * outputs);
                        }

                        for (var xi = 0; xi < Values; xi++)
                        {
                            Gemm.MultiplyLong(new ColumnsA(v + (long)xi * chunk * inputs, inputs), new RowsB(m + (long)xi * chunk * outputs, outputs),
                                inputs, outputs, count, du + ((long)xi * plane), outputs, add: true, threads: 1);
                        }
                    }
                }
            });

            // the parts together (into the first), value by value in parallel
            if (parts > 1)
            {
                Work.For(Values, size * parts, threads, (from, to) =>
                {
                    for (var xi = from; xi < to; xi++)
                    {
                        var target = new Span<float>((float*)all + ((long)xi * plane), plane);
                        for (var part = 1; part < parts; part++)
                        {
                            Kernels.AddScaled(target, new ReadOnlySpan<float>((float*)all + (part * size) + ((long)xi * plane), plane), 1f);
                        }
                    }
                });
            }

            // back from the 6 × 6 values to the 3 × 3 kernel: dg = Gᵀ dU G, input channel by input channel in parallel
            Work.For(inputs, size, threads, (from, to) =>
            {
                Span<double> du6 = stackalloc double[Values];
                Span<double> q = stackalloc double[3 * Piece];
                Span<double> back = stackalloc double[3];
                var total = (float*)all;
                for (var i = from; i < to; i++)
                {
                    for (var o = 0; o < outputs; o++)
                    {
                        for (var xi = 0; xi < Values; xi++)
                        {
                            du6[xi] = total[((long)xi * plane) + (i * outputs) + o];
                        }

                        // Q = Gᵀ dU (3 × 6), column by column
                        for (var c = 0; c < Piece; c++)
                        {
                            GradientColumn(du6[c], du6[Piece + c], du6[2 * Piece + c], du6[3 * Piece + c], du6[4 * Piece + c], du6[5 * Piece + c], back);
                            q[c] = back[0];
                            q[Piece + c] = back[1];
                            q[2 * Piece + c] = back[2];
                        }

                        // dg = Q G (3 × 3), row by row
                        for (var r = 0; r < 3; r++)
                        {
                            var row = q.Slice(r * Piece, Piece);
                            GradientColumn(row[0], row[1], row[2], row[3], row[4], row[5], back);
                            for (var c = 0; c < 3; c++)
                            {
                                gw[((((o * 3) + r) * 3) + c) * inputs + i] += (float)back[c];
                            }
                        }
                    }
                }
            });
        }
        finally
        {
            NativeMemory.AlignedFree(sums);
        }
    }

    // Gᵀ · (q0 … q5): back from six transformed values to three kernel values
    private static void GradientColumn(double q0, double q1, double q2, double q3, double q4, double q5, Span<double> r)
    {
        r[0] = (q0 / 4) - ((q1 + q2) / 6) + ((q3 + q4) / 24);
        r[1] = ((q2 - q1) / 6) + ((q3 - q4) / 12);
        r[2] = ((q3 + q4 - q1 - q2) / 6) + q5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> MulAdd(Vector256<float> a, Vector256<float> b, Vector256<float> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : (a * b) + c;

    // Bᵀ · (d0 … d5): one row or column of the input transform
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InputLine(Vector256<float> d0, Vector256<float> d1, Vector256<float> d2, Vector256<float> d3, Vector256<float> d4, Vector256<float> d5,
        out Vector256<float> t0, out Vector256<float> t1, out Vector256<float> t2, out Vector256<float> t3, out Vector256<float> t4, out Vector256<float> t5)
    {
        var four = Vector256.Create(4f);
        var minusFour = Vector256.Create(-4f);
        var minusFive = Vector256.Create(-5f);
        var two = Vector256.Create(2f);
        var minusTwo = Vector256.Create(-2f);
        var p = MulAdd(minusFour, d2, d4);    // d4 − 4 d2
        var q = MulAdd(minusFour, d1, d3);    // d3 − 4 d1
        var r = d4 - d2;
        var s = d3 - d1;
        t0 = MulAdd(four, d0, MulAdd(minusFive, d2, d4));   // 4 d0 − 5 d2 + d4
        t1 = p + q;                                          // −4 d1 − 4 d2 + d3 + d4
        t2 = p - q;                                          // 4 d1 − 4 d2 − d3 + d4
        t3 = MulAdd(two, s, r);                              // −2 d1 − d2 + 2 d3 + d4
        t4 = MulAdd(minusTwo, s, r);                         // 2 d1 − d2 − 2 d3 + d4
        t5 = MulAdd(four, d1, MulAdd(minusFive, d3, d5));   // 4 d1 − 5 d3 + d5
    }

    // Bᵀ d B of the 6 × 6 input piece of a tile, for all channels: into v[ξ · stride + channel]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InputTile(float* x, int channels, in Tiles tiles, int tile, float* v, long stride)
    {
        var rows = stackalloc float*[Values];
        tiles.Locate(tile, out var line, out var ty, out var tx);
        int height = tiles.Level.Height, width = tiles.Level.Widths[line], offset = tiles.Level.Offsets[line];
        for (var r = 0; r < Piece; r++)
        {
            var y = (Side * ty) - 1 + r;
            for (var c = 0; c < Piece; c++)
            {
                var xx = (Side * tx) - 1 + c;
                rows[r * Piece + c] = y < 0 || y >= height || xx < 0 || xx >= width ? null : x + ((long)(offset + (y * width) + xx) * channels);
            }
        }

        var t = stackalloc Vector256<float>[Values];
        for (var k = 0; k < channels; k += 8)
        {
            // Bᵀ d: every column of the piece
            for (var c = 0; c < Piece; c++)
            {
                InputLine(Load(rows[c], k), Load(rows[Piece + c], k), Load(rows[2 * Piece + c], k),
                    Load(rows[3 * Piece + c], k), Load(rows[4 * Piece + c], k), Load(rows[5 * Piece + c], k),
                    out t[c], out t[Piece + c], out t[2 * Piece + c], out t[3 * Piece + c], out t[4 * Piece + c], out t[5 * Piece + c]);
            }

            // (Bᵀ d) B: every row
            for (var r = 0; r < Piece; r++)
            {
                var row = t + (r * Piece);
                InputLine(row[0], row[1], row[2], row[3], row[4], row[5],
                    out var v0, out var v1, out var v2, out var v3, out var v4, out var v5);
                var target = v + (r * Piece * stride) + k;
                Vector256.Store(v0, target);
                Vector256.Store(v1, target + stride);
                Vector256.Store(v2, target + (2 * stride));
                Vector256.Store(v3, target + (3 * stride));
                Vector256.Store(v4, target + (4 * stride));
                Vector256.Store(v5, target + (5 * stride));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Load(float* row, int k) => row == null ? Vector256<float>.Zero : Vector256.Load(row + k);

    // Aᵀ · (m0 … m5): four outputs from six values
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OutputLine(Vector256<float> m0, Vector256<float> m1, Vector256<float> m2, Vector256<float> m3, Vector256<float> m4, Vector256<float> m5,
        out Vector256<float> y0, out Vector256<float> y1, out Vector256<float> y2, out Vector256<float> y3)
    {
        var a = m1 + m2;
        var b = m1 - m2;
        var c = m3 + m4;
        var e = m3 - m4;
        y0 = m0 + a + c;
        y1 = MulAdd(Vector256.Create(2f), e, b);
        y2 = MulAdd(Vector256.Create(4f), c, a);
        y3 = MulAdd(Vector256.Create(8f), e, b) + m5;
    }

    // Aᵀ m A: the 4 × 4 outputs of a tile from its 36 values, written where they are inside the picture
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void OutputTile(float* m, long stride, int channels, in Tiles tiles, int tile, float* y)
    {
        tiles.Locate(tile, out var line, out var ty, out var tx);
        int height = tiles.Level.Height, width = tiles.Level.Widths[line], offset = tiles.Level.Offsets[line];
        var targets = stackalloc float*[Side * Side];
        for (var r = 0; r < Side; r++)
        {
            for (var c = 0; c < Side; c++)
            {
                int yy = (Side * ty) + r, xx = (Side * tx) + c;
                targets[r * Side + c] = yy < height && xx < width ? y + ((long)(offset + (yy * width) + xx) * channels) : null;
            }
        }

        var s = stackalloc Vector256<float>[Side * Piece];
        for (var k = 0; k < channels; k += 8)
        {
            // Aᵀ m: every column of the 6 × 6 values
            for (var c = 0; c < Piece; c++)
            {
                var column = m + (c * stride) + k;
                OutputLine(Vector256.Load(column), Vector256.Load(column + (Piece * stride)), Vector256.Load(column + (2 * Piece * stride)),
                    Vector256.Load(column + (3 * Piece * stride)), Vector256.Load(column + (4 * Piece * stride)), Vector256.Load(column + (5 * Piece * stride)),
                    out s[c], out s[Piece + c], out s[2 * Piece + c], out s[3 * Piece + c]);
            }

            // (Aᵀ m) A: every row
            for (var r = 0; r < Side; r++)
            {
                var row = s + (r * Piece);
                OutputLine(row[0], row[1], row[2], row[3], row[4], row[5], out var y0, out var y1, out var y2, out var y3);
                Store(y0, targets[r * Side], k);
                Store(y1, targets[r * Side + 1], k);
                Store(y2, targets[r * Side + 2], k);
                Store(y3, targets[r * Side + 3], k);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector256<float> value, float* target, int k)
    {
        if (target != null)
        {
            Vector256.Store(value, target + k);
        }
    }

    // A · (y0 … y3): six values from four (the way back of the output transform)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GradientLine(Vector256<float> y0, Vector256<float> y1, Vector256<float> y2, Vector256<float> y3,
        out Vector256<float> p0, out Vector256<float> p1, out Vector256<float> p2, out Vector256<float> p3, out Vector256<float> p4, out Vector256<float> p5)
    {
        var even = y0 + y2;
        var odd = y1 + y3;
        var even4 = MulAdd(Vector256.Create(4f), y2, y0);                 // y0 + 4 y2
        var odd8 = MulAdd(Vector256.Create(8f), y3, y1 + y1);            // 2 y1 + 8 y3
        p0 = y0;
        p1 = even + odd;
        p2 = even - odd;
        p3 = even4 + odd8;
        p4 = even4 - odd8;
        p5 = y3;
    }

    // A dY Aᵀ: the 36 values of the gradient of a tile's 4 × 4 outputs (outside the picture: zero)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GradientTile(float* dz, int channels, in Tiles tiles, int tile, float* m, long stride)
    {
        tiles.Locate(tile, out var line, out var ty, out var tx);
        int height = tiles.Level.Height, width = tiles.Level.Widths[line], offset = tiles.Level.Offsets[line];
        var sources = stackalloc float*[Side * Side];
        for (var r = 0; r < Side; r++)
        {
            for (var c = 0; c < Side; c++)
            {
                int yy = (Side * ty) + r, xx = (Side * tx) + c;
                sources[r * Side + c] = yy < height && xx < width ? dz + ((long)(offset + (yy * width) + xx) * channels) : null;
            }
        }

        var p = stackalloc Vector256<float>[Piece * Side];
        for (var k = 0; k < channels; k += 8)
        {
            // A dY: every column of the 4 × 4 gradient
            for (var c = 0; c < Side; c++)
            {
                GradientLine(Load(sources[c], k), Load(sources[Side + c], k), Load(sources[2 * Side + c], k), Load(sources[3 * Side + c], k),
                    out p[c], out p[Side + c], out p[2 * Side + c], out p[3 * Side + c], out p[4 * Side + c], out p[5 * Side + c]);
            }

            // (A dY) Aᵀ: every row
            for (var r = 0; r < Piece; r++)
            {
                var row = p + (r * Side);
                GradientLine(row[0], row[1], row[2], row[3], out var m0, out var m1, out var m2, out var m3, out var m4, out var m5);
                var target = m + (r * Piece * stride) + k;
                Vector256.Store(m0, target);
                Vector256.Store(m1, target + stride);
                Vector256.Store(m2, target + (2 * stride));
                Vector256.Store(m3, target + (3 * stride));
                Vector256.Store(m4, target + (4 * stride));
                Vector256.Store(m5, target + (5 * stride));
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

    /// <summary>The 4 × 4 tiles of the outputs of every line, numbered line after line, row after row.</summary>
    private readonly struct Tiles
    {
        private readonly int[] _first;
        private readonly int[] _columns;

        public Tiles(Level level)
        {
            Level = level;
            var lines = level.Widths.Length;
            _first = new int[lines + 1];
            _columns = new int[lines];
            for (var i = 0; i < lines; i++)
            {
                _columns[i] = (level.Widths[i] + Side - 1) / Side;
                _first[i + 1] = _first[i] + (_columns[i] * ((level.Height + Side - 1) / Side));
            }

            Count = _first[lines];
        }

        public Level Level { get; }

        public int Count { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Locate(int tile, out int line, out int row, out int column)
        {
            // binary search of the line (a batch has a few dozen lines)
            int low = 0, high = _first.Length - 2;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (_first[middle] <= tile)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            line = low;
            var local = tile - _first[line];
            row = local / _columns[line];
            column = local - (row * _columns[line]);
        }
    }
}
