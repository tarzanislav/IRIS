using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Iris.Vision.Training;

/// <summary>
/// The first convolution of the reader: one input channel (the grey picture), 3 × 3, padding 1. As a product of
/// matrices its depth would be only 9 – packing would cost more than multiplying – so it is computed directly, row by
/// row of the picture with the three rows around it copied once with zeros at both ends: per pixel the nine
/// neighbours, each times the weights of 32 output channels held in four registers. The gradient of the weights is
/// nine sums per channel, worked three taps and 16 channels at a time so that the six sums stay in registers, and added up in
/// double row by row.
/// </summary>
internal static unsafe class FirstConvolution
{
    /// <summary>Whether a layer is such a first convolution (and the processor has vectors for it).</summary>
    public static bool Fits(ConvParameters p, int pad) =>
        (Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated) && p.Inputs == 1 && p.Kh == 3 && p.Kw == 3 && pad == 1 && p.Outputs % 8 == 0;

    /// <summary>z [pixels, outputs] = the convolution of the picture x [pixels] (same geometry) with weights [outputs, 3, 3, 1].</summary>
    public static void Convolve(float* x, Level level, float[] weight, int outputs, float* z, int threads)
    {
        // the weights turned to [tap][output], a row of outputs per tap
        var turned = new float[9 * outputs];
        for (var o = 0; o < outputs; o++)
        {
            for (var tap = 0; tap < 9; tap++)
            {
                turned[(tap * outputs) + o] = weight[(o * 9) + tap];
            }
        }

        var xs = (nint)x;
        var zs = (nint)z;
        Work.For(level.Height * level.Widths.Length, (long)level.Pixels * 9 * outputs, threads, (from, to) =>
        {
            var stride = level.Widths.Max() + 2;
            var padded = new float[3 * stride];
            fixed (float* rows = padded, w = turned)
            {
                for (var row = from; row < to; row++)
                {
                    var line = row / level.Height;
                    var y = row - (line * level.Height);
                    var width = level.Widths[line];
                    Surroundings((float*)xs, level, line, y, rows, stride);
                    var output = (float*)zs + ((long)(level.Offsets[line] + (y * width)) * outputs);
                    var o = 0;
                    for (; o + 32 <= outputs; o += 32)
                    {
                        Row32(rows, stride, width, w + o, outputs, output + o);
                    }

                    for (; o < outputs; o += 8)
                    {
                        Row8(rows, stride, width, w + o, outputs, output + o);
                    }
                }
            }
        });
    }

    /// <summary>Adds the gradient of the weights [outputs, 3, 3, 1] to gw, from the picture x and the gradient dz [pixels, outputs].</summary>
    public static void WeightGradient(float* x, Level level, float* dz, int outputs, float[] gw, int threads)
    {
        var total = new double[9 * outputs]; // [tap][output]
        var xs = (nint)x;
        var ds = (nint)dz;
        Work.For(level.Height * level.Widths.Length, (long)level.Pixels * 9 * outputs, threads, (from, to) =>
        {
            var sums = new double[9 * outputs];
            var stride = level.Widths.Max() + 2;
            var padded = new float[3 * stride];
            var part = stackalloc float[3 * 16];
            fixed (float* rows = padded)
            {
                for (var row = from; row < to; row++)
                {
                    var line = row / level.Height;
                    var y = row - (line * level.Height);
                    var width = level.Widths[line];
                    Surroundings((float*)xs, level, line, y, rows, stride);
                    var gradient = (float*)ds + ((long)(level.Offsets[line] + (y * width)) * outputs);
                    for (var ky = 0; ky < 3; ky++)
                    {
                        var o = 0;
                        for (; o + 16 <= outputs; o += 16)
                        {
                            Sums16(rows + (ky * stride), width, gradient + o, outputs, part);
                            for (var kx = 0; kx < 3; kx++)
                            {
                                for (var c = 0; c < 16; c++)
                                {
                                    sums[(((ky * 3) + kx) * outputs) + o + c] += part[(kx * 16) + c];
                                }
                            }
                        }

                        for (; o < outputs; o += 8)
                        {
                            Sums8(rows + (ky * stride), width, gradient + o, outputs, part);
                            for (var kx = 0; kx < 3; kx++)
                            {
                                for (var c = 0; c < 8; c++)
                                {
                                    sums[(((ky * 3) + kx) * outputs) + o + c] += part[(kx * 8) + c];
                                }
                            }
                        }
                    }
                }
            }

            return sums;
        }, sums =>
        {
            for (var k = 0; k < total.Length; k++)
            {
                total[k] += sums[k];
            }
        });

        for (var o = 0; o < outputs; o++)
        {
            for (var tap = 0; tap < 9; tap++)
            {
                gw[(o * 9) + tap] += (float)total[(tap * outputs) + o];
            }
        }
    }

    // the rows y − 1, y, y + 1 of a line, each with a zero before and after it (and zeros outside the picture)
    private static void Surroundings(float* x, Level level, int line, int y, float* rows, int stride)
    {
        var width = level.Widths[line];
        for (var ky = 0; ky < 3; ky++)
        {
            var target = rows + (ky * stride);
            var sy = y + ky - 1;
            if (sy < 0 || sy >= level.Height)
            {
                new Span<float>(target, width + 2).Clear();
                continue;
            }

            target[0] = 0;
            target[width + 1] = 0;
            System.Buffer.MemoryCopy(x + level.Offsets[line] + (sy * width), target + 1, width * sizeof(float), width * sizeof(float));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> MulAdd(Vector256<float> a, Vector256<float> b, Vector256<float> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : (a * b) + c;

    // one row of 32 output channels: z[x] = Σ over the nine taps of the neighbour times the weights of the tap
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Row32(float* rows, int stride, int width, float* w, int outputs, float* z)
    {
        float* above = rows, here = rows + stride, below = rows + (2 * stride);
        for (var x = 0; x < width; x++)
        {
            Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
            Tap32(ref a0, ref a1, ref a2, ref a3, above[x], w);
            Tap32(ref a0, ref a1, ref a2, ref a3, above[x + 1], w + outputs);
            Tap32(ref a0, ref a1, ref a2, ref a3, above[x + 2], w + (2 * outputs));
            Tap32(ref a0, ref a1, ref a2, ref a3, here[x], w + (3 * outputs));
            Tap32(ref a0, ref a1, ref a2, ref a3, here[x + 1], w + (4 * outputs));
            Tap32(ref a0, ref a1, ref a2, ref a3, here[x + 2], w + (5 * outputs));
            Tap32(ref a0, ref a1, ref a2, ref a3, below[x], w + (6 * outputs));
            Tap32(ref a0, ref a1, ref a2, ref a3, below[x + 1], w + (7 * outputs));
            Tap32(ref a0, ref a1, ref a2, ref a3, below[x + 2], w + (8 * outputs));
            var target = z + ((long)x * outputs);
            Vector256.Store(a0, target);
            Vector256.Store(a1, target + 8);
            Vector256.Store(a2, target + 16);
            Vector256.Store(a3, target + 24);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tap32(ref Vector256<float> a0, ref Vector256<float> a1, ref Vector256<float> a2, ref Vector256<float> a3, float value, float* w)
    {
        var v = Vector256.Create(value);
        a0 = MulAdd(v, Vector256.Load(w), a0);
        a1 = MulAdd(v, Vector256.Load(w + 8), a1);
        a2 = MulAdd(v, Vector256.Load(w + 16), a2);
        a3 = MulAdd(v, Vector256.Load(w + 24), a3);
    }

    // the same for eight output channels (the rest of a count that is not a multiple of 32)
    private static void Row8(float* rows, int stride, int width, float* w, int outputs, float* z)
    {
        for (var x = 0; x < width; x++)
        {
            var a = Vector256<float>.Zero;
            for (var ky = 0; ky < 3; ky++)
            {
                for (var kx = 0; kx < 3; kx++)
                {
                    a = MulAdd(Vector256.Create(rows[(ky * stride) + x + kx]), Vector256.Load(w + ((((ky * 3) + kx)) * outputs)), a);
                }
            }

            Vector256.Store(a, z + ((long)x * outputs));
        }
    }

    // Σ over a row of dz[x] times the neighbours x − 1, x, x + 1 of one row of the picture: three taps × 16 channels
    // (six sums, two gradients and three neighbours: all in registers)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Sums16(float* source, int width, float* dz, int outputs, float* part)
    {
        Vector256<float> s00 = default, s01 = default, s10 = default, s11 = default, s20 = default, s21 = default;
        for (var x = 0; x < width; x++)
        {
            var d = dz + ((long)x * outputs);
            var d0 = Vector256.Load(d);
            var d1 = Vector256.Load(d + 8);
            var b = Vector256.Create(source[x]);
            s00 = MulAdd(b, d0, s00);
            s01 = MulAdd(b, d1, s01);
            b = Vector256.Create(source[x + 1]);
            s10 = MulAdd(b, d0, s10);
            s11 = MulAdd(b, d1, s11);
            b = Vector256.Create(source[x + 2]);
            s20 = MulAdd(b, d0, s20);
            s21 = MulAdd(b, d1, s21);
        }

        Vector256.Store(s00, part);
        Vector256.Store(s01, part + 8);
        Vector256.Store(s10, part + 16);
        Vector256.Store(s11, part + 24);
        Vector256.Store(s20, part + 32);
        Vector256.Store(s21, part + 40);
    }

    private static void Sums8(float* source, int width, float* dz, int outputs, float* part)
    {
        Vector256<float> s0 = default, s1 = default, s2 = default;
        for (var x = 0; x < width; x++)
        {
            var d = Vector256.Load(dz + ((long)x * outputs));
            s0 = MulAdd(Vector256.Create(source[x]), d, s0);
            s1 = MulAdd(Vector256.Create(source[x + 1]), d, s1);
            s2 = MulAdd(Vector256.Create(source[x + 2]), d, s2);
        }

        Vector256.Store(s0, part);
        Vector256.Store(s1, part + 8);
        Vector256.Store(s2, part + 16);
    }
}
