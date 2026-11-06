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
/// The block keeps only the convolution z (and the output it hands on): normalization, ReLU and pooling are one pass
/// from z to the output, and the way back works x̂, the activations and the winners of the pooling out of z again –
/// the same operations on the same values give the same bits – instead of storing them. These steps wait for memory,
/// not for the processor, so fewer maps written and read is what makes them faster (and the learning smaller).
/// </summary>
internal sealed unsafe class ConvLayer : IDisposable
{
    public const float Epsilon = 1e-5f;
    public const float Momentum = 0.1f;

    private readonly ConvParameters _p;
    private readonly int _pad;
    private readonly int _poolH;
    private readonly int _poolW;
    private readonly Buffer<float> _z = new();            // the convolution [conv pixels, outputs]; on the way back its gradient
    private readonly Buffer<float> _activated = new();    // relu(γx̂ + β) of a block without pooling (the next one's input)
    private readonly Buffer<float> _pooled = new();
    private readonly Buffer<float> _inputGradient = new();
    private readonly Buffer<float> _flipped = new();      // weights for the gradient of the input (3 × 3)
    private readonly Buffer<float> _columns = new();      // gathered gradient (collapsing convolution)
    private readonly float[] _mean;                       // the normalization of the last forward pass: x̂ = (z − mean) · invStd
    private readonly float[] _invStd;
    private readonly float[] _k1;                         // the way back through the normalization: γσ⁻¹, mean(d), mean(d x̂)
    private readonly float[] _k2;
    private readonly float[] _k3;
    private float* _input;
    private int _inputChannels;
    private Gemm.PackedB[]? _kernels;                     // the transformed kernels of Winograd's way, kept for the next step
    private Gemm.PackedB[]? _turnedKernels;               // the same for the gradient of the input (turned, inputs and outputs swapped)

    public ConvLayer(ConvParameters p, int pad, int poolH, int poolW)
    {
        (_p, _pad, _poolH, _poolW) = (p, pad, poolH, poolW);
        _mean = new float[p.Outputs];
        _invStd = new float[p.Outputs];
        _k1 = new float[p.Outputs];
        _k2 = new float[p.Outputs];
        _k3 = new float[p.Outputs];
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

    /// <summary>Where the time of the layer goes (product, statistics, activation and pooling, the way back), when set.</summary>
    public Dictionary<string, double>? Timings { get; set; }

    public string Name { get; set; } = "conv";

    /// <summary>A buffer the layers of one network share for passing work (one layer at a time uses it), or null.</summary>
    public Buffer<float>? Shared { get; set; }

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
        var z = _z.Ensure((long)pixels * outputs);
        if (First)
        {
            FirstConvolution.Convolve(input, level, _p.Weight, outputs, z, threads);
        }
        else if (Winograd)
        {
            _kernels = Training.Winograd.TransformKernels(_p.Weight, _p.Inputs, outputs, flipped: false, threads, _kernels);
            Training.Winograd.Convolve(input, _p.Inputs, level, _kernels, outputs, z, threads);
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
        if (learning)
        {
            var (mean, variance) = Statistics(z, pixels, outputs, threads);
            for (var c = 0; c < outputs; c++)
            {
                _mean[c] = (float)mean[c];
                _invStd[c] = (float)(1 / Math.Sqrt(variance[c] + Epsilon));
                _p.Mean[c] = (float)((1 - Momentum) * _p.Mean[c] + Momentum * mean[c]);
                _p.Variance[c] = (float)((1 - Momentum) * _p.Variance[c] + Momentum * variance[c] * pixels / Math.Max(1, pixels - 1));
            }
        }
        else
        {
            for (var c = 0; c < outputs; c++)
            {
                _invStd[c] = 1 / MathF.Sqrt(_p.Variance[c] + Epsilon);
                _mean[c] = _p.Mean[c];
            }
        }

        clock?.Mark("statistics");
        if (!Pools)
        {
            Out.Set(Conv.Height, Conv.Widths);
            var a = _activated.Ensure((long)pixels * outputs);
            Activate(z, a, threads);
            clock?.Mark("activation");
            return a;
        }

        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = Conv.Widths[i] / _poolW;
        }

        Out.Set(Conv.Height / _poolH, widths);
        ActivateAndPool(z, _pooled.Ensure((long)Out.Pixels * outputs), threads);
        clock?.Mark("activation");
        return _pooled.Data;
    }

    /// <summary>
    /// Backward from the gradient of the output (Out pixels × outputs, only read): adds the gradients of the weights to g
    /// and returns the gradient of the input (In pixels × inputs) when asked for it, else null. z becomes its gradient.
    /// </summary>
    public float* Backward(float* gradient, ConvParameters g, bool inputGradient, int threads)
    {
        var outputs = _p.Outputs;
        var pixels = Conv.Pixels;
        var clock = Timings is null ? null : new Clock(Timings, Name + " backward ");
        var d = NormalizeBackward(gradient, g, threads);
        clock?.Mark("normalization");
        if (First)
        {
            FirstConvolution.WeightGradient(_input, In, d, outputs, g.Weight, threads);
        }
        else if (Winograd)
        {
            Training.Winograd.WeightGradient(_input, _p.Inputs, d, outputs, In, g.Weight, threads, Shared);
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
        _z.Dispose();
        _activated.Dispose();
        _pooled.Dispose();
        _inputGradient.Dispose();
        _flipped.Dispose();
        _columns.Dispose();
        Training.Winograd.Free(_kernels);
        Training.Winograd.Free(_turnedKernels);
        _kernels = _turnedKernels = null;
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
            _turnedKernels = Training.Winograd.TransformKernels(_p.Weight, inputs, outputs, flipped: true, threads, _turnedKernels);
            Training.Winograd.Convolve(dz, outputs, Conv, _turnedKernels, inputs, dx, threads);

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

    private static bool Wide => Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated;

    // a = relu(γ x̂ + β) of every pixel of a block without pooling, x̂ = (z − mean) · invStd
    private void Activate(float* z, float* a, int threads)
    {
        var channels = _p.Outputs;
        Work.For(Conv.Pixels, (long)Conv.Pixels * channels, threads, (from, to) =>
        {
            fixed (float* m = _mean, s = _invStd, g = _p.Gamma, b = _p.Beta)
            {
                for (var r = from; r < to; r++)
                {
                    var x = z + (long)r * channels;
                    var y = a + (long)r * channels;
                    var c = 0;
                    if (Wide)
                    {
                        for (; c <= channels - 8; c += 8)
                        {
                            var n = (Vector256.Load(x + c) - Vector256.Load(m + c)) * Vector256.Load(s + c);
                            Vector256.Store(Vector256.Max(Vector256.Load(g + c) * n + Vector256.Load(b + c), Vector256<float>.Zero), y + c);
                        }
                    }

                    for (; c < channels; c++)
                    {
                        var n = (x[c] - m[c]) * s[c];
                        y[c] = MathF.Max(g[c] * n + b[c], 0);
                    }
                }
            }
        });
    }

    // the pooled output straight from z: the activations of every window worked out and the largest written, eight
    // channels at a time (the activations themselves are never stored)
    private void ActivateAndPool(float* z, float* pooled, int threads)
    {
        var channels = _p.Outputs;
        var taps = _poolH * _poolW;
        Work.For(Out.Widths.Length * Out.Height, (long)Conv.Pixels * channels, threads, (from, to) =>
        {
            var window = stackalloc float*[taps];
            fixed (float* m = _mean, s = _invStd, g = _p.Gamma, b = _p.Beta)
            {
                for (var band = from; band < to; band++)
                {
                    var line = band / Out.Height;
                    var y = band - line * Out.Height;
                    var width = Out.Widths[line];
                    for (var x = 0; x < width; x++)
                    {
                        Window(window, z, line, y, x, Conv.Widths[line], channels);
                        var o = pooled + (long)(Out.Offsets[line] + y * width + x) * channels;
                        var c = 0;
                        if (Wide)
                        {
                            for (; c <= channels - 8; c += 8)
                            {
                                var mean = Vector256.Load(m + c);
                                var invStd = Vector256.Load(s + c);
                                var gamma = Vector256.Load(g + c);
                                var beta = Vector256.Load(b + c);
                                var best = Vector256.Max(gamma * ((Vector256.Load(window[0] + c) - mean) * invStd) + beta, Vector256<float>.Zero);
                                for (var k = 1; k < taps; k++)
                                {
                                    best = Vector256.Max(best, Vector256.Max(gamma * ((Vector256.Load(window[k] + c) - mean) * invStd) + beta, Vector256<float>.Zero));
                                }

                                Vector256.Store(best, o + c);
                            }
                        }

                        for (; c < channels; c++)
                        {
                            var best = MathF.Max(g[c] * ((window[0][c] - m[c]) * s[c]) + b[c], 0);
                            for (var k = 1; k < taps; k++)
                            {
                                best = MathF.Max(best, MathF.Max(g[c] * ((window[k][c] - m[c]) * s[c]) + b[c], 0));
                            }

                            o[c] = best;
                        }
                    }
                }
            }
        });
    }

    // From the gradient of the block's output to the gradient of z, written over z. The pooling sends each window's
    // gradient to its winner (the first value equal to the largest, as PyTorch picks it), the ReLU lets it through
    // where γx̂ + β > 0, and the normalization spreads it: dz = γσ⁻¹ (d − mean(d) − x̂ · mean(d x̂)), dγ += Σ d x̂,
    // dβ += Σ d. Two passes over z: the sums, then dz – x̂, the activations and the winners worked out again each time.
    private float* NormalizeBackward(float* gradient, ConvParameters g, int threads)
    {
        var channels = _p.Outputs;
        var pixels = Conv.Pixels;
        var z = _z.Data;
        var items = Pools ? Out.Widths.Length * Out.Height : (pixels + Chunk - 1) / Chunk;
        var sumD = new double[channels];
        var sumDx = new double[channels];
        Work.For(items, (long)pixels * channels, threads, (from, to) =>
        {
            var s1 = new double[channels];
            var s2 = new double[channels];
            var p1 = stackalloc float[channels];
            var p2 = stackalloc float[channels];
            var window = stackalloc float*[_poolH * _poolW];
            for (var item = from; item < to; item++)
            {
                new Span<float>(p1, channels).Clear();
                new Span<float>(p2, channels).Clear();
                if (Pools)
                {
                    PooledSums(gradient, z, item, window, p1, p2);
                }
                else
                {
                    PlainSums(gradient, z, item * Chunk, Math.Min(pixels, (item + 1) * Chunk), p1, p2);
                }

                for (var c = 0; c < channels; c++)
                {
                    s1[c] += p1[c];
                    s2[c] += p2[c];
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

        for (var c = 0; c < channels; c++)
        {
            g.Gamma[c] += (float)sumDx[c];
            g.Beta[c] += (float)sumD[c];
            _k1[c] = _p.Gamma[c] * _invStd[c];
            _k2[c] = (float)(sumD[c] / pixels);
            _k3[c] = (float)(sumDx[c] / pixels);
        }

        // the bands of rows of the pooling, and one more of the rows it leaves over
        var bands = Pools ? Out.Height + (Conv.Height > Out.Height * _poolH ? 1 : 0) : 0;
        Work.For(Pools ? Conv.Widths.Length * bands : items, (long)pixels * channels, threads, (from, to) =>
        {
            var window = stackalloc float*[_poolH * _poolW];
            for (var item = from; item < to; item++)
            {
                if (Pools)
                {
                    PooledSpread(gradient, z, item / bands, item % bands, window);
                }
                else
                {
                    var at = (long)item * Chunk * channels;
                    Spread(z + at, gradient + at, Math.Min(pixels, (item + 1) * Chunk) - item * Chunk);
                }
            }
        });

        return z;
    }

    // pixels of a block without pooling worked at a time on the way back (their sums in float, between them in double)
    private const int Chunk = 256;

    // Σ d and Σ d x̂ of the pixels [from, to) of a block without pooling
    private void PlainSums(float* gradient, float* z, int from, int to, float* p1, float* p2)
    {
        var channels = _p.Outputs;
        fixed (float* m = _mean, s = _invStd, g = _p.Gamma, b = _p.Beta)
        {
            for (var r = from; r < to; r++)
            {
                var x = z + (long)r * channels;
                var dy = gradient + (long)r * channels;
                var c = 0;
                if (Wide)
                {
                    for (; c <= channels - 8; c += 8)
                    {
                        var n = (Vector256.Load(x + c) - Vector256.Load(m + c)) * Vector256.Load(s + c);
                        var on = Vector256.GreaterThan(Vector256.Load(g + c) * n + Vector256.Load(b + c), Vector256<float>.Zero);
                        var v = Vector256.BitwiseAnd(Vector256.Load(dy + c), on);
                        Vector256.Store(Vector256.Load(p1 + c) + v, p1 + c);
                        Vector256.Store(Vector256.Load(p2 + c) + v * n, p2 + c);
                    }
                }

                for (; c < channels; c++)
                {
                    var n = (x[c] - m[c]) * s[c];
                    var v = g[c] * n + b[c] > 0 ? dy[c] : 0;
                    p1[c] += v;
                    p2[c] += v * n;
                }
            }
        }
    }

    // Σ d and Σ d x̂ of the windows of one band of the pooling (only the winner of a window gets its gradient, and only
    // where it is above zero – a window whose largest activation is 0 sends nothing back)
    private void PooledSums(float* gradient, float* z, int band, float** window, float* p1, float* p2)
    {
        var channels = _p.Outputs;
        var taps = _poolH * _poolW;
        var line = band / Out.Height;
        var y = band - line * Out.Height;
        var width = Out.Widths[line];
        var pooled = _pooled.Data;
        fixed (float* m = _mean, s = _invStd, g = _p.Gamma, b = _p.Beta)
        {
            for (var x = 0; x < width; x++)
            {
                Window(window, z, line, y, x, Conv.Widths[line], channels);
                var at = (long)(Out.Offsets[line] + y * width + x) * channels;
                var c = 0;
                if (Wide)
                {
                    for (; c <= channels - 8; c += 8)
                    {
                        var mean = Vector256.Load(m + c);
                        var invStd = Vector256.Load(s + c);
                        var gamma = Vector256.Load(g + c);
                        var beta = Vector256.Load(b + c);
                        var best = Vector256.Load(pooled + at + c);
                        var taken = Vector256<float>.Zero;
                        var winner = Vector256<float>.Zero;
                        for (var k = 0; k < taps; k++)
                        {
                            var n = (Vector256.Load(window[k] + c) - mean) * invStd;
                            var mine = Vector256.AndNot(Vector256.Equals(Vector256.Max(gamma * n + beta, Vector256<float>.Zero), best), taken);
                            taken |= mine;
                            winner |= n & mine;
                        }

                        var v = Vector256.Load(gradient + at + c) & Vector256.GreaterThan(best, Vector256<float>.Zero);
                        Vector256.Store(Vector256.Load(p1 + c) + v, p1 + c);
                        Vector256.Store(Vector256.Load(p2 + c) + v * winner, p2 + c);
                    }
                }

                for (; c < channels; c++)
                {
                    var best = pooled[at + c];
                    if (best > 0)
                    {
                        for (var k = 0; k < taps; k++)
                        {
                            var n = (window[k][c] - m[c]) * s[c];
                            if (MathF.Max(g[c] * n + b[c], 0) == best)
                            {
                                var v = gradient[at + c];
                                p1[c] += v;
                                p2[c] += v * n;
                                break;
                            }
                        }
                    }
                }
            }
        }
    }

    // dz of one band of rows of a pooling block: its windows (each worked out whole before it is written over), the
    // columns the pooling leaves over, or – the last band – the rows it leaves over
    private void PooledSpread(float* gradient, float* z, int line, int band, float** window)
    {
        var channels = _p.Outputs;
        var taps = _poolH * _poolW;
        int convWidth = Conv.Widths[line], width = Out.Widths[line];
        var pooled = _pooled.Data;
        if (band == Out.Height)
        {
            var first = band * _poolH;
            Spread(z + (long)(Conv.Offsets[line] + first * convWidth) * channels, null, (Conv.Height - first) * convWidth);
            return;
        }

        var n = stackalloc Vector256<float>[taps];
        var mine = stackalloc Vector256<float>[taps];
        var values = stackalloc float[taps];
        fixed (float* m = _mean, s = _invStd, g = _p.Gamma, b = _p.Beta, a1 = _k1, a2 = _k2, a3 = _k3)
        {
            for (var x = 0; x < width; x++)
            {
                Window(window, z, line, band, x, convWidth, channels);
                var at = (long)(Out.Offsets[line] + band * width + x) * channels;
                var c = 0;
                if (Wide)
                {
                    for (; c <= channels - 8; c += 8)
                    {
                        var mean = Vector256.Load(m + c);
                        var invStd = Vector256.Load(s + c);
                        var gamma = Vector256.Load(g + c);
                        var beta = Vector256.Load(b + c);
                        var best = Vector256.Load(pooled + at + c);
                        var taken = Vector256<float>.Zero;
                        for (var k = 0; k < taps; k++)
                        {
                            n[k] = (Vector256.Load(window[k] + c) - mean) * invStd;
                            mine[k] = Vector256.AndNot(Vector256.Equals(Vector256.Max(gamma * n[k] + beta, Vector256<float>.Zero), best), taken);
                            taken |= mine[k];
                        }

                        var v = Vector256.Load(gradient + at + c) & Vector256.GreaterThan(best, Vector256<float>.Zero);
                        var k1 = Vector256.Load(a1 + c);
                        var k2 = Vector256.Load(a2 + c);
                        var k3 = Vector256.Load(a3 + c);
                        for (var k = 0; k < taps; k++)
                        {
                            Vector256.Store(k1 * ((v & mine[k]) - k2 - n[k] * k3), window[k] + c);
                        }
                    }
                }

                for (; c < channels; c++)
                {
                    var best = pooled[at + c];
                    var found = false;
                    for (var k = 0; k < taps; k++)
                    {
                        values[k] = (window[k][c] - m[c]) * s[c];
                    }

                    for (var k = 0; k < taps; k++)
                    {
                        var winner = !found && MathF.Max(g[c] * values[k] + b[c], 0) == best;
                        found |= winner;
                        var v = winner && best > 0 ? gradient[at + c] : 0;
                        window[k][c] = a1[c] * (v - a2[c] - values[k] * a3[c]);
                    }
                }
            }

            // the columns at the right the pooling leaves over (no gradient comes to them, the normalization still does)
            for (var r = band * _poolH; r < band * _poolH + _poolH && width * _poolW < convWidth; r++)
            {
                Spread(z + (long)(Conv.Offsets[line] + r * convWidth + width * _poolW) * channels, null, convWidth - width * _poolW);
            }
        }
    }

    // the last step of the way back through the normalization for a run of pixels, z in, dz out: dz = k1 (d − k2 − x̂ k3),
    // d the gradient of the activations through the ReLU (none: pixels no gradient comes to)
    private void Spread(float* z, float* d, int count)
    {
        var channels = _p.Outputs;
        fixed (float* m = _mean, s = _invStd, g = _p.Gamma, b = _p.Beta, a1 = _k1, a2 = _k2, a3 = _k3)
        {
            for (var pixel = 0; pixel < count; pixel++, z += channels, d = d == null ? null : d + channels)
            {
                var c = 0;
                if (Wide)
                {
                    for (; c <= channels - 8; c += 8)
                    {
                        var n = (Vector256.Load(z + c) - Vector256.Load(m + c)) * Vector256.Load(s + c);
                        var v = d == null ? Vector256<float>.Zero
                            : Vector256.BitwiseAnd(Vector256.Load(d + c), Vector256.GreaterThan(Vector256.Load(g + c) * n + Vector256.Load(b + c), Vector256<float>.Zero));
                        Vector256.Store(Vector256.Load(a1 + c) * (v - Vector256.Load(a2 + c) - n * Vector256.Load(a3 + c)), z + c);
                    }
                }

                for (; c < channels; c++)
                {
                    var n = (z[c] - m[c]) * s[c];
                    var v = d == null || !(g[c] * n + b[c] > 0) ? 0 : d[c];
                    z[c] = a1[c] * (v - a2[c] - n * a3[c]);
                }
            }
        }
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
