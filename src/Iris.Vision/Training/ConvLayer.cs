using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Iris.Numerics;

namespace Iris.Vision.Training;

/// <summary>
/// The gathered neighbourhoods of a feature map, read straight from the map while the product packs its panels –
/// the "matrix of neighbourhoods" of a convolution (one row per output pixel, kh · kw · channels values) exists only
/// a panel at a time in the cache. As A (rows = output pixels) it computes the convolution; as B (k = output pixels,
/// columns = neighbourhood values) the gradient of its weights.
/// </summary>
internal readonly unsafe struct Neighbourhoods : IMatrixA, IMatrixB
{
    private readonly float* _map;
    private readonly int _channels;
    private readonly int _kh;
    private readonly int _kw;
    private readonly int _pad;
    private readonly Level _input;
    private readonly Level _output;

    public Neighbourhoods(float* map, int channels, int kh, int kw, int pad, Level input, Level output)
    {
        _map = map;
        _channels = channels;
        _kh = kh;
        _kw = kw;
        _pad = pad;
        _input = input;
        _output = output;
    }

    public void Pack(float* target, int m0, int rows, int k0, int depth)
    {
        var line = stackalloc int[Gemm.MR];
        var y = stackalloc int[Gemm.MR];
        var x = stackalloc int[Gemm.MR];
        var sources = stackalloc float*[Gemm.MR];
        for (var i = 0; i < rows; i++)
        {
            Locate(m0 + i, out line[i], out y[i], out x[i]);
        }

        var end = k0 + depth;
        for (var f = k0; f < end;)
        {
            var tap = f / _channels;
            var c0 = f - tap * _channels;
            var run = Math.Min(_channels - c0, end - f);
            int ky = tap / _kw, kx = tap - ky * _kw;
            for (var i = 0; i < Gemm.MR; i++)
            {
                var source = i < rows ? Source(line[i], y[i], x[i], ky, kx) : -1;
                sources[i] = source < 0 ? null : _map + (long)source * _channels + c0;
            }

            Gemm.Interleave(sources, run, target + (long)(f - k0) * Gemm.MR);
            f += run;
        }
    }

    void IMatrixB.Pack(float* target, int n0, int columns, int k0, int depth)
    {
        for (var k = 0; k < depth; k++)
        {
            Locate(k0 + k, out var line, out var y, out var x);
            var row = target + (long)k * Gemm.NR;
            var j = 0;
            while (j < columns)
            {
                var f = n0 + j;
                var tap = f / _channels;
                var c0 = f - tap * _channels;
                var run = Math.Min(_channels - c0, columns - j);
                int ky = tap / _kw, kx = tap - ky * _kw;
                var source = Source(line, y, x, ky, kx);
                if (source < 0)
                {
                    new Span<float>(row + j, run).Clear();
                }
                else
                {
                    new ReadOnlySpan<float>(_map + (long)source * _channels + c0, run).CopyTo(new Span<float>(row + j, run));
                }

                j += run;
            }

            for (; j < Gemm.NR; j++)
            {
                row[j] = 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Locate(int pixel, out int line, out int y, out int x)
    {
        line = _output.LineOfPixel[pixel];
        var local = pixel - _output.Offsets[line];
        var width = _output.Widths[line];
        y = local / width;
        x = local - y * width;
    }

    // the input pixel under the tap (ky, kx) of an output pixel, or −1 in the padding
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Source(int line, int y, int x, int ky, int kx)
    {
        int sy = y + ky - _pad, sx = x + kx - _pad, width = _input.Widths[line];
        return sy < 0 || sy >= _input.Height || sx < 0 || sx >= width ? -1 : _input.Offsets[line] + sy * width + sx;
    }
}

/// <summary>
/// A convolution block of the line reader for learning: convolution (no bias), batch normalization, ReLU and maybe
/// max-pooling – forwards with what the way back needs, and backwards to the gradients of its weights and its input.
/// Normalization in learning uses the statistics of the batch (all pixels of all lines) and moves the running ones
/// like PyTorch (momentum 0.1, unbiased variance); in evaluation it uses the running ones.
/// </summary>
internal sealed unsafe class ConvLayer : IDisposable
{
    public const float Epsilon = 1e-5f;
    public const float Momentum = 0.1f;

    private readonly ConvParameters _p;
    private readonly int _pad;
    private readonly int _poolH;
    private readonly int _poolW;
    private readonly Buffer<float> _normalized = new();   // x̂ [conv pixels, outputs]
    private readonly Buffer<float> _activated = new();    // relu(γx̂ + β); on the way back its gradient
    private readonly Buffer<float> _pooled = new();
    private readonly Buffer<float> _inputGradient = new();
    private readonly Buffer<float> _flipped = new();      // weights for the gradient of the input (3 × 3)
    private readonly Buffer<float> _columns = new();      // gathered gradient (collapsing convolution)
    private readonly float[] _invStd;
    private float* _input;
    private int _inputChannels;

    public ConvLayer(ConvParameters p, int pad, int poolH, int poolW)
    {
        (_p, _pad, _poolH, _poolW) = (p, pad, poolH, poolW);
        _invStd = new float[p.Outputs];
    }

    /// <summary>The level of the convolution's output (before pooling).</summary>
    public Level Conv { get; } = new();

    /// <summary>The level of the block's output (after pooling, or the same as Conv).</summary>
    public Level Out { get; } = new();

    public Level In { get; private set; } = null!;

    public bool Pools => _poolH > 1 || _poolW > 1;

    /// <summary>
    /// The fast ways: 3 × 3 convolutions by Winograd's minimal filtering (4 × fewer multiplications) and the first
    /// convolution (one input channel) computed directly; off only to compare with the plain products.
    /// </summary>
    public static bool UseFastPaths { get; set; } = true;

    private bool Winograd => UseFastPaths && Training.Winograd.Fits(_p, _pad);

    private bool First => UseFastPaths && FirstConvolution.Fits(_p, _pad);

    /// <summary>Where the time of the layer goes (product, normalization, pooling), when set.</summary>
    public Dictionary<string, double>? Timings { get; set; }

    public string Name { get; set; } = "conv";

    /// <summary>The output of the forward pass (on Out).</summary>
    public float* Output => Pools ? _pooled.Data : _activated.Data;

    /// <summary>Forward through the block; input [In pixels, inputs]. Returns the output (Out pixels × outputs).</summary>
    public float* Forward(float* input, Level level, bool learning, int threads)
    {
        In = level;
        _input = input;
        _inputChannels = _p.Inputs;
        var outputs = _p.Outputs;
        int outHeight = level.Height + 2 * _pad - _p.Kh + 1;
        Span<int> widths = stackalloc int[level.Widths.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = level.Widths[i] + 2 * _pad - _p.Kw + 1;
        }

        Conv.Set(outHeight, widths);
        var clock = Timings is null ? null : new Clock(Timings, Name + " forward ");
        var pixels = Conv.Pixels;
        var z = _normalized.Ensure((long)pixels * outputs);
        if (First)
        {
            FirstConvolution.Convolve(input, level, _p.Weight, outputs, z, threads);
        }
        else if (Winograd)
        {
            var kernels = Training.Winograd.TransformKernels(_p.Weight, _p.Inputs, outputs, flipped: false);
            try
            {
                Training.Winograd.Convolve(input, _p.Inputs, level, kernels, outputs, z, threads);
            }
            finally
            {
                foreach (var k in kernels)
                {
                    k.Dispose();
                }
            }
        }
        else
        {
            fixed (float* w = _p.Weight)
            {
                Gemm.Multiply(new Neighbourhoods(input, _p.Inputs, _p.Kh, _p.Kw, _pad, level, Conv), new ColumnsB(w, _p.Features),
                    pixels, outputs, _p.Features, z, outputs, add: false, threads);
            }
        }

        clock?.Mark("product");
        var a = _activated.Ensure((long)pixels * outputs);
        if (learning)
        {
            var (mean, variance) = Statistics(z, pixels, outputs, threads);
            for (var c = 0; c < outputs; c++)
            {
                _invStd[c] = (float)(1 / Math.Sqrt(variance[c] + Epsilon));
                _p.Mean[c] = (float)((1 - Momentum) * _p.Mean[c] + Momentum * mean[c]);
                _p.Variance[c] = (float)((1 - Momentum) * _p.Variance[c] + Momentum * variance[c] * pixels / Math.Max(1, pixels - 1));
            }

            Normalize(z, a, pixels, mean.Select(m => (float)m).ToArray(), threads);
        }
        else
        {
            var mean = new float[outputs];
            for (var c = 0; c < outputs; c++)
            {
                _invStd[c] = 1 / MathF.Sqrt(_p.Variance[c] + Epsilon);
                mean[c] = _p.Mean[c];
            }

            Normalize(z, a, pixels, mean, threads);
        }

        clock?.Mark("normalization");
        if (!Pools)
        {
            Out.Set(Conv.Height, Conv.Widths);
            return a;
        }

        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = Conv.Widths[i] / _poolW;
        }

        Out.Set(Conv.Height / _poolH, widths);
        Pool(a, _pooled.Ensure((long)Out.Pixels * outputs), threads);
        clock?.Mark("pooling");
        return _pooled.Data;
    }

    /// <summary>
    /// Backward from the gradient of the output (Out pixels × outputs; may be overwritten): adds the gradients of the
    /// weights to g and returns the gradient of the input (In pixels × inputs) when asked for it, else null.
    /// </summary>
    public float* Backward(float* gradient, ConvParameters g, bool inputGradient, int threads)
    {
        var outputs = _p.Outputs;
        var pixels = Conv.Pixels;
        var clock = Timings is null ? null : new Clock(Timings, Name + " backward ");
        var d = _activated.Data; // the activations are not needed any more: their place takes their gradient
        if (Pools)
        {
            Unpool(gradient, d, threads); // reads the activations and writes their gradient over them
        }
        else if (gradient != d)
        {
            System.Buffer.MemoryCopy(gradient, d, (long)pixels * outputs * sizeof(float), (long)pixels * outputs * sizeof(float));
        }

        clock?.Mark("pooling");
        NormalizeBackward(d, g, pixels, threads);
        clock?.Mark("normalization");
        if (First)
        {
            FirstConvolution.WeightGradient(_input, In, d, outputs, g.Weight, threads);
        }
        else if (Winograd)
        {
            Training.Winograd.WeightGradient(_input, _p.Inputs, d, outputs, In, g.Weight, threads);
        }
        else
        {
            fixed (float* gw = g.Weight)
            {
                // dW [outputs, features] += dZᵀ · neighbourhoods
                Gemm.MultiplyLong(new ColumnsA(d, outputs), new Neighbourhoods(_input, _p.Inputs, _p.Kh, _p.Kw, _pad, In, Conv),
                    outputs, _p.Features, pixels, gw, _p.Features, add: true, threads);
            }
        }

        clock?.Mark("weight gradient");
        var result = inputGradient ? InputGradient(d, threads) : null;
        clock?.Mark("input gradient");
        return result;
    }

    public void Dispose()
    {
        _normalized.Dispose();
        _activated.Dispose();
        _pooled.Dispose();
        _inputGradient.Dispose();
        _flipped.Dispose();
        _columns.Dispose();
        Conv.Dispose();
        Out.Dispose();
    }

    // the gradient of the input from dZ (Conv pixels × outputs)
    private float* InputGradient(float* dz, int threads)
    {
        var inputs = _inputChannels;
        var outputs = _p.Outputs;
        var dx = _inputGradient.Ensure((long)In.Pixels * inputs);
        if (Winograd)
        {
            // the same convolution with the kernel turned and inputs and outputs swapped
            var kernels = Training.Winograd.TransformKernels(_p.Weight, inputs, outputs, flipped: true);
            try
            {
                Training.Winograd.Convolve(dz, outputs, Conv, kernels, inputs, dx, threads);
            }
            finally
            {
                foreach (var k in kernels)
                {
                    k.Dispose();
                }
            }

            return dx;
        }

        if (_pad * 2 == _p.Kh - 1 && _pad * 2 == _p.Kw - 1)
        {
            // a "same" convolution: the gradient of the input is the convolution of dZ with the weights turned by
            // 180° and with inputs and outputs swapped – the same fast product, no scattering
            var taps = _p.Kh * _p.Kw;
            var flipped = _flipped.Ensure((long)taps * outputs * inputs);
            for (var ky = 0; ky < _p.Kh; ky++)
            {
                for (var kx = 0; kx < _p.Kw; kx++)
                {
                    var tap = (_p.Kh - 1 - ky) * _p.Kw + (_p.Kw - 1 - kx);
                    for (var o = 0; o < outputs; o++)
                    {
                        for (var i = 0; i < inputs; i++)
                        {
                            flipped[((long)tap * outputs + o) * inputs + i] = _p.Weight[((o * _p.Kh + ky) * _p.Kw + kx) * inputs + i];
                        }
                    }
                }
            }

            Gemm.Multiply(new Neighbourhoods(dz, outputs, _p.Kh, _p.Kw, _pad, Conv, In), new RowsB(flipped, inputs),
                In.Pixels, inputs, taps * outputs, dx, inputs, add: false, threads);
            return dx;
        }

        // any other convolution (here the collapsing one): the gradient of every neighbourhood, then added back to
        // the pixels it was gathered from
        var features = _p.Features;
        var columns = _columns.Ensure((long)Conv.Pixels * features);
        fixed (float* w = _p.Weight)
        {
            Gemm.Multiply(new RowsA(dz, outputs), new RowsB(w, features), Conv.Pixels, features, outputs, columns, features, add: false, threads);
        }

        new Span<float>(dx, checked(In.Pixels * inputs)).Clear();
        for (var pixel = 0; pixel < Conv.Pixels; pixel++)
        {
            var line = Conv.LineOfPixel[pixel];
            var local = pixel - Conv.Offsets[line];
            int y = local / Conv.Widths[line], x = local - y * Conv.Widths[line];
            var width = In.Widths[line];
            for (var ky = 0; ky < _p.Kh; ky++)
            {
                for (var kx = 0; kx < _p.Kw; kx++)
                {
                    int sy = y + ky - _pad, sx = x + kx - _pad;
                    if (sy < 0 || sy >= In.Height || sx < 0 || sx >= width)
                    {
                        continue;
                    }

                    var target = new Span<float>(dx + (long)(In.Offsets[line] + sy * width + sx) * inputs, inputs);
                    Kernels.AddScaled(target, new ReadOnlySpan<float>(columns + (long)pixel * features + (ky * _p.Kw + kx) * inputs, inputs), 1f);
                }
            }
        }

        return dx;
    }

    // mean and (biased) variance of every channel over all pixels in one pass over memory: short runs of rows are
    // summed in float twice while they are in the cache (around their own mean), and the runs merged in double
    private static (double[] Mean, double[] Variance) Statistics(float* z, int pixels, int channels, int threads)
    {
        var count = 0L;
        var mean = new double[channels];
        var squares = new double[channels];
        Work.For(pixels, (long)pixels * channels, threads, (from, to) => Moments(z, from, to, channels),
            part => Merge(ref count, mean, squares, part.Count, part.Mean, part.Squares));
        var variance = new double[channels];
        for (var c = 0; c < channels; c++)
        {
            variance[c] = squares[c] / Math.Max(1, pixels);
        }

        return (mean, variance);
    }

    // count, mean and Σ (x − mean)² of each channel over the rows [from, to)
    private static (long Count, double[] Mean, double[] Squares) Moments(float* z, int from, int to, int channels)
    {
        var count = 0L;
        var mean = new double[channels];
        var squares = new double[channels];
        var runMean = new double[channels];
        var runSquares = new double[channels];
        var sum = stackalloc float[channels];
        var center = stackalloc float[channels];
        var offset = stackalloc float[channels];
        var square = stackalloc float[channels];
        const int Run = 256;
        for (var start = from; start < to; start += Run)
        {
            var end = Math.Min(to, start + Run);
            var n = end - start;
            new Span<float>(sum, channels).Clear();
            for (var r = start; r < end; r++)
            {
                Kernels.AddScaled(new Span<float>(sum, channels), new ReadOnlySpan<float>(z + (long)r * channels, channels), 1f);
            }

            for (var c = 0; c < channels; c++)
            {
                center[c] = sum[c] / n;
            }

            // around the rounded mean of the run: Σ (x − m) and Σ (x − m)², which give the exact mean and squares
            new Span<float>(offset, channels).Clear();
            new Span<float>(square, channels).Clear();
            for (var r = start; r < end; r++)
            {
                var row = z + (long)r * channels;
                var c = 0;
                if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
                {
                    for (; c <= channels - 8; c += 8)
                    {
                        var d = Vector256.Load(row + c) - Vector256.Load(center + c);
                        Vector256.Store(Vector256.Load(offset + c) + d, offset + c);
                        Vector256.Store(Vector256.Load(square + c) + (d * d), square + c);
                    }
                }

                for (; c < channels; c++)
                {
                    var d = row[c] - center[c];
                    offset[c] += d;
                    square[c] += d * d;
                }
            }

            for (var c = 0; c < channels; c++)
            {
                double shift = offset[c];
                runMean[c] = center[c] + (shift / n);
                runSquares[c] = Math.Max(0, square[c] - (shift * shift / n));
            }

            Merge(ref count, mean, squares, n, runMean, runSquares);
        }

        return (count, mean, squares);
    }

    // two groups of rows together (Chan's formula: the squares around the common mean)
    private static void Merge(ref long count, double[] mean, double[] squares, long n, double[] otherMean, double[] otherSquares)
    {
        if (n == 0)
        {
            return;
        }

        var total = count + n;
        for (var c = 0; c < mean.Length; c++)
        {
            var delta = otherMean[c] - mean[c];
            mean[c] += delta * n / total;
            squares[c] += otherSquares[c] + (delta * delta * count * n / total);
        }

        count = total;
    }

    // z → x̂ = (z − mean) / σ in place, a = relu(γ x̂ + β)
    private void Normalize(float* z, float* a, int pixels, float[] mean, int threads)
    {
        var channels = _p.Outputs;
        var invStd = _invStd;
        var gamma = _p.Gamma;
        var beta = _p.Beta;
        Work.For(pixels, (long)pixels * channels, threads, (from, to) =>
        {
            fixed (float* m = mean, s = invStd, g = gamma, b = beta)
            {
                for (var r = from; r < to; r++)
                {
                    var x = z + (long)r * channels;
                    var y = a + (long)r * channels;
                    var c = 0;
                    if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
                    {
                        for (; c <= channels - 8; c += 8)
                        {
                            var n = (Vector256.Load(x + c) - Vector256.Load(m + c)) * Vector256.Load(s + c);
                            Vector256.Store(n, x + c);
                            Vector256.Store(Vector256.Max(Vector256.Load(g + c) * n + Vector256.Load(b + c), Vector256<float>.Zero), y + c);
                        }
                    }

                    for (; c < channels; c++)
                    {
                        var n = (x[c] - m[c]) * s[c];
                        x[c] = n;
                        y[c] = MathF.Max(g[c] * n + b[c], 0);
                    }
                }
            }
        });
    }

    // d: gradient of the activations → gradient of z (in place); adds dγ and dβ to g
    private void NormalizeBackward(float* d, ConvParameters g, int pixels, int threads)
    {
        var channels = _p.Outputs;
        var xhat = _normalized.Data;
        var gamma = _p.Gamma;
        var beta = _p.Beta;
        // the ReLU first (where γx̂ + β ≤ 0 nothing flows back), with the two sums of the normalization
        var sumD = new double[channels];
        var sumDx = new double[channels];
        Work.For(pixels, (long)pixels * channels, threads, (from, to) =>
        {
            var s1 = new double[channels];
            var s2 = new double[channels];
            var p1 = stackalloc float[channels];
            var p2 = stackalloc float[channels];
            const int Chunk = 256;
            fixed (float* gp = gamma, bp = beta)
            {
                for (var start = from; start < to; start += Chunk)
                {
                    new Span<float>(p1, channels).Clear();
                    new Span<float>(p2, channels).Clear();
                    var end = Math.Min(to, start + Chunk);
                    for (var r = start; r < end; r++)
                    {
                        var dy = d + (long)r * channels;
                        var x = xhat + (long)r * channels;
                        var c = 0;
                        if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
                        {
                            for (; c <= channels - 8; c += 8)
                            {
                                var n = Vector256.Load(x + c);
                                var on = Vector256.GreaterThan(Vector256.Load(gp + c) * n + Vector256.Load(bp + c), Vector256<float>.Zero);
                                var v = Vector256.BitwiseAnd(Vector256.Load(dy + c), on);
                                Vector256.Store(v, dy + c);
                                Vector256.Store(Vector256.Load(p1 + c) + v, p1 + c);
                                Vector256.Store(Vector256.Load(p2 + c) + v * n, p2 + c);
                            }
                        }

                        for (; c < channels; c++)
                        {
                            var v = gp[c] * x[c] + bp[c] > 0 ? dy[c] : 0;
                            dy[c] = v;
                            p1[c] += v;
                            p2[c] += v * x[c];
                        }
                    }

                    for (var c = 0; c < channels; c++)
                    {
                        s1[c] += p1[c];
                        s2[c] += p2[c];
                    }
                }
            }

            return (s1, s2);
        }, part =>
        {
            for (var c = 0; c < channels; c++)
            {
                sumD[c] += part.s1[c];
                sumDx[c] += part.s2[c];
            }
        });

        var k1 = new float[channels]; // γ σ⁻¹
        var k2 = new float[channels]; // mean of d
        var k3 = new float[channels]; // mean of d · x̂
        for (var c = 0; c < channels; c++)
        {
            g.Gamma[c] += (float)sumDx[c];
            g.Beta[c] += (float)sumD[c];
            k1[c] = gamma[c] * _invStd[c];
            k2[c] = (float)(sumD[c] / pixels);
            k3[c] = (float)(sumDx[c] / pixels);
        }

        // dz = γσ⁻¹ (d − mean(d) − x̂ · mean(d x̂))
        Work.For(pixels, (long)pixels * channels, threads, (from, to) =>
        {
            fixed (float* a = k1, b = k2, e = k3)
            {
                for (var r = from; r < to; r++)
                {
                    var dy = d + (long)r * channels;
                    var x = xhat + (long)r * channels;
                    var c = 0;
                    if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
                    {
                        for (; c <= channels - 8; c += 8)
                        {
                            var v = Vector256.Load(a + c) * (Vector256.Load(dy + c) - Vector256.Load(b + c) - Vector256.Load(x + c) * Vector256.Load(e + c));
                            Vector256.Store(v, dy + c);
                        }
                    }

                    for (; c < channels; c++)
                    {
                        dy[c] = a[c] * (dy[c] - b[c] - x[c] * e[c]);
                    }
                }
            }
        });
    }

    // max-pooling of a (Conv) into pooled (Out), eight channels at a time
    private void Pool(float* a, float* pooled, int threads)
    {
        var channels = _p.Outputs;
        Work.For(Out.Widths.Length, (long)Conv.Pixels * channels, threads, (from, to) =>
        {
            var window = stackalloc float*[_poolH * _poolW];
            for (var line = from; line < to; line++)
            {
                int width = Out.Widths[line], convWidth = Conv.Widths[line];
                for (var y = 0; y < Out.Height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        Window(window, a, line, y, x, convWidth, channels);
                        var o = pooled + (long)(Out.Offsets[line] + y * width + x) * channels;
                        var c = 0;
                        if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
                        {
                            for (; c <= channels - 8; c += 8)
                            {
                                var best = Vector256.Load(window[0] + c);
                                for (var k = 1; k < _poolH * _poolW; k++)
                                {
                                    best = Vector256.Max(best, Vector256.Load(window[k] + c));
                                }

                                Vector256.Store(best, o + c);
                            }
                        }

                        for (; c < channels; c++)
                        {
                            var best = window[0][c];
                            for (var k = 1; k < _poolH * _poolW; k++)
                            {
                                best = MathF.Max(best, window[k][c]);
                            }

                            o[c] = best;
                        }
                    }
                }
            }
        });
    }

    // the gradient of the pooled output back to the winner of each window (the first one equal to the largest, as
    // PyTorch picks it); d holds the activations and gets their gradient, window by window in place
    private void Unpool(float* gradient, float* d, int threads)
    {
        var channels = _p.Outputs;
        var pooled = _pooled.Data;
        var taps = _poolH * _poolW;
        Work.For(Out.Widths.Length, (long)Conv.Pixels * channels, threads, (from, to) =>
        {
            var window = stackalloc float*[taps];
            for (var line = from; line < to; line++)
            {
                int width = Out.Widths[line], convWidth = Conv.Widths[line];
                // columns and rows left over by the pooling get no gradient
                for (var y = 0; y < Conv.Height; y++)
                {
                    for (var x = y < Out.Height * _poolH ? width * _poolW : 0; x < convWidth; x++)
                    {
                        new Span<float>(d + (long)(Conv.Offsets[line] + y * convWidth + x) * channels, channels).Clear();
                    }
                }

                for (var y = 0; y < Out.Height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        Window(window, d, line, y, x, convWidth, channels);
                        var index = (long)(Out.Offsets[line] + y * width + x) * channels;
                        var best = pooled + index;
                        var g = gradient + index;
                        var c = 0;
                        if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
                        {
                            for (; c <= channels - 8; c += 8)
                            {
                                var max = Vector256.Load(best + c);
                                var value = Vector256.Load(g + c);
                                var taken = Vector256<float>.Zero;
                                for (var k = 0; k < taps; k++)
                                {
                                    var mine = Vector256.AndNot(Vector256.Equals(Vector256.Load(window[k] + c), max), taken);
                                    taken |= mine;
                                    Vector256.Store(value & mine, window[k] + c);
                                }
                            }
                        }

                        for (; c < channels; c++)
                        {
                            var found = false;
                            for (var k = 0; k < taps; k++)
                            {
                                var mine = !found && window[k][c] == best[c];
                                found |= mine;
                                window[k][c] = mine ? g[c] : 0;
                            }
                        }
                    }
                }
            }
        });
    }

    // the pixels of a pooling window (row after row), as pointers to their channels
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Window(float** window, float* map, int line, int y, int x, int convWidth, int channels)
    {
        var k = 0;
        for (var dy = 0; dy < _poolH; dy++)
        {
            for (var dx = 0; dx < _poolW; dx++)
            {
                window[k++] = map + (long)(Conv.Offsets[line] + (y * _poolH + dy) * convWidth + x * _poolW + dx) * channels;
            }
        }
    }
}
