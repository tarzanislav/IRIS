using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Iris.Numerics;

namespace Iris.Vision.Training;

/// <summary>
/// The frames of the lines of a batch for the LSTM: line i has Frames[i] frames starting at Offsets[i]; the lines go
/// from the longest to the shortest, so at every step the lines still running are the first ones.
/// </summary>
internal sealed class Sequences
{
    public int[] Frames { get; private set; } = [];

    public int[] Offsets { get; private set; } = [];

    public int Total { get; private set; }

    public int Longest { get; private set; }

    public void Set(Level level)
    {
        if (level.Height != 1)
        {
            throw new InvalidOperationException("the LSTM reads one row of features");
        }

        Frames = (int[])level.Widths.Clone();
        Offsets = (int[])level.Offsets.Clone();
        Total = level.Pixels;
        Longest = Frames.Length == 0 ? 0 : Frames.Max();
        for (var i = 1; i < Frames.Length; i++)
        {
            if (Frames[i] > Frames[i - 1])
            {
                throw new InvalidOperationException("the lines of a batch go from the longest to the shortest");
            }
        }
    }

    /// <summary>The frame a line reads at the given step (the backward direction starts at its last frame).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Frame(int line, int step, int direction) => Offsets[line] + (direction == 0 ? step : Frames[line] - 1 - step);
}

/// <summary>
/// A bidirectional LSTM layer for learning (gates i, f, g, o, two biases – exactly PyTorch's LSTM). Every line runs
/// over its own frames only, like a packed sequence in PyTorch and like the C# reader: the backward direction starts
/// at the real end of each line, not at padding. The input part of all gates is one product for all frames; the
/// steps then run as small products of the lines still running with the hidden weights (packed once). The two
/// directions – and, with more threads, groups of lines – run side by side without waiting for each other.
/// </summary>
internal sealed unsafe class LstmLayer : IDisposable
{
    private readonly LstmParameters[] _p;
    private readonly int _inputs;
    private readonly int _hidden;
    private readonly Buffer<float>[] _gates = [new(), new()];     // activated i, f, g, o of every frame [frames, 4H]
    private readonly Buffer<float>[] _cells = [new(), new()];     // c of every frame [frames, H]
    private readonly Buffer<float>[] _dz = [new(), new()];        // gradient of the gates before activation
    private readonly Buffer<float> _output = new();               // [frames, 2H]: forward then backward direction
    private readonly Buffer<float> _previous = new();             // h of the step before, per frame
    private readonly Buffer<float> _inputGradient = new();
    private Sequences _sequences = null!;
    private float* _input;

    public LstmLayer(LstmParameters forward, LstmParameters backward)
    {
        _p = [forward, backward];
        _inputs = forward.Inputs;
        _hidden = forward.Hidden;
    }

    public float* Output => _output.Data;

    public float* Forward(float* input, Sequences sequences, int threads)
    {
        _input = input;
        _sequences = sequences;
        var frames = sequences.Total;
        int h = _hidden, g4 = 4 * _hidden;
        var output = _output.Ensure((long)frames * 2 * h);
        var packed = new Gemm.PackedB[2];
        for (var d = 0; d < 2; d++)
        {
            var p = _p[d];
            var gates = _gates[d].Ensure((long)frames * g4);
            _cells[d].Ensure((long)frames * h);
            fixed (float* w = p.InputWeight)
            {
                // the input part of every gate, for all frames at once (with both biases)
                Gemm.Multiply(new RowsA(input, _inputs), new ColumnsB(w, _inputs), frames, g4, _inputs, gates, g4, add: false, threads);
            }

            var bias = new float[g4];
            for (var k = 0; k < g4; k++)
            {
                bias[k] = p.InputBias[k] + p.HiddenBias[k];
            }

            Work.For(frames, (long)frames * g4, threads, (from, to) =>
            {
                for (var f = from; f < to; f++)
                {
                    Kernels.AddScaled(new Span<float>(gates + (long)f * g4, g4), bias, 1f);
                }
            });

            fixed (float* w = p.HiddenWeight)
            {
                packed[d] = Gemm.PackedB.Create(new ColumnsB(w, h), g4, h); // B[k, n] = W_hh[n, k]
            }
        }

        try
        {
            RunTasks(threads, (direction, lines) => ForwardTask(direction, lines, packed[direction], output));
        }
        finally
        {
            packed[0].Dispose();
            packed[1].Dispose();
        }

        return output;
    }

    /// <summary>
    /// Backward from the gradient of the output ([frames, 2H]); adds to the gradients in g (forward, backward
    /// direction) and returns the gradient of the input when asked for it.
    /// </summary>
    public float* Backward(float* gradient, LstmParameters[] g, bool inputGradient, int threads)
    {
        var frames = _sequences.Total;
        int h = _hidden, g4 = 4 * _hidden;
        var packed = new Gemm.PackedB[2];
        for (var d = 0; d < 2; d++)
        {
            _dz[d].Ensure((long)frames * g4);
            fixed (float* w = _p[d].HiddenWeight)
            {
                packed[d] = Gemm.PackedB.Create(new RowsB(w, h), h, g4); // B[k, n] = W_hh[k, n]
            }
        }

        try
        {
            RunTasks(threads, (direction, lines) => BackwardTask(direction, lines, packed[direction], gradient));
        }
        finally
        {
            packed[0].Dispose();
            packed[1].Dispose();
        }

        var previous = _previous.Ensure((long)frames * h);
        float* dx = inputGradient ? _inputGradient.Ensure((long)frames * _inputs) : null;
        for (var d = 0; d < 2; d++)
        {
            var dz = _dz[d].Data;
            var gd = g[d];

            // h of the step before each frame (zero at the first step of a line)
            for (var line = 0; line < _sequences.Frames.Length; line++)
            {
                for (var step = 0; step < _sequences.Frames[line]; step++)
                {
                    var target = new Span<float>(previous + (long)_sequences.Frame(line, step, d) * h, h);
                    if (step == 0)
                    {
                        target.Clear();
                    }
                    else
                    {
                        new ReadOnlySpan<float>(_output.Data + (long)_sequences.Frame(line, step - 1, d) * 2 * h + d * h, h).CopyTo(target);
                    }
                }
            }

            fixed (float* ghh = gd.HiddenWeight, gih = gd.InputWeight, wih = _p[d].InputWeight)
            {
                Gemm.MultiplyLong(new ColumnsA(dz, g4), new RowsB(previous, h), g4, h, frames, ghh, h, add: true, threads);
                Gemm.MultiplyLong(new ColumnsA(dz, g4), new RowsB(_input, _inputs), g4, _inputs, frames, gih, _inputs, add: true, threads);
                if (dx != null)
                {
                    Gemm.Multiply(new RowsA(dz, g4), new RowsB(wih, _inputs), frames, _inputs, g4, dx, _inputs, add: d == 1, threads);
                }
            }

            var bias = new double[g4];
            Work.For(frames, (long)frames * g4, threads, (from, to) =>
            {
                var sum = new double[g4];
                for (var f = from; f < to; f++)
                {
                    var row = dz + (long)f * g4;
                    for (var k = 0; k < g4; k++)
                    {
                        sum[k] += row[k];
                    }
                }

                return sum;
            }, part =>
            {
                for (var k = 0; k < g4; k++)
                {
                    bias[k] += part[k];
                }
            });
            for (var k = 0; k < g4; k++)
            {
                gd.InputBias[k] += (float)bias[k];
                gd.HiddenBias[k] += (float)bias[k];
            }
        }

        return dx;
    }

    public void Dispose()
    {
        foreach (var b in _gates.Concat(_cells).Concat(_dz))
        {
            b.Dispose();
        }

        _output.Dispose();
        _previous.Dispose();
        _inputGradient.Dispose();
    }

    // the recurrences are independent per direction and per line: both directions, and groups of lines when there
    // are threads to spare (each group keeps its lines from the longest to the shortest)
    private void RunTasks(int threads, Action<int, int[]> task)
    {
        var lines = _sequences.Frames.Length;
        var groups = Math.Max(1, Math.Min(threads / 2, lines / 8));
        var jobs = new List<(int Direction, int[] Lines)>();
        for (var d = 0; d < 2; d++)
        {
            for (var group = 0; group < groups; group++)
            {
                var members = Enumerable.Range(0, lines).Where(i => i % groups == group).ToArray();
                if (members.Length > 0)
                {
                    jobs.Add((d, members));
                }
            }
        }

        if (threads <= 1)
        {
            foreach (var (d, members) in jobs)
            {
                task(d, members);
            }

            return;
        }

        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = threads }, job => task(job.Direction, job.Lines));
    }

    private void ForwardTask(int d, int[] lines, Gemm.PackedB hiddenWeight, float* output)
    {
        int n = lines.Length, h = _hidden, g4 = 4 * _hidden;
        var gates = _gates[d].Data;
        var cells = _cells[d].Data;
        using var state = new Buffer<float>();
        var hs = state.Ensure((long)n * (2 * h + g4), clear: true);
        var cs = hs + (long)n * h;
        var z = cs + (long)n * h;
        var running = n;
        var longest = _sequences.Frames[lines[0]];
        for (var step = 0; step < longest; step++)
        {
            while (running > 0 && _sequences.Frames[lines[running - 1]] <= step)
            {
                running--;
            }

            Gemm.Multiply(new RowsA(hs, h), hiddenWeight, running, z, g4, add: false, threads: 1);
            for (var j = 0; j < running; j++)
            {
                var frame = _sequences.Frame(lines[j], step, d);
                var gate = gates + (long)frame * g4;
                Kernels.AddScaled(new Span<float>(gate, g4), new ReadOnlySpan<float>(z + (long)j * g4, g4), 1f);
                Cell(gate, cs + (long)j * h, hs + (long)j * h, h);
                new ReadOnlySpan<float>(cs + (long)j * h, h).CopyTo(new Span<float>(cells + (long)frame * h, h));
                new ReadOnlySpan<float>(hs + (long)j * h, h).CopyTo(new Span<float>(output + (long)frame * 2 * h + d * h, h));
            }
        }
    }

    private void BackwardTask(int d, int[] lines, Gemm.PackedB hiddenWeight, float* gradient)
    {
        int n = lines.Length, h = _hidden, g4 = 4 * _hidden;
        var gates = _gates[d].Data;
        var cells = _cells[d].Data;
        var dzAll = _dz[d].Data;
        using var state = new Buffer<float>();
        var dh = state.Ensure((long)n * (2 * h + g4), clear: true);
        var dc = dh + (long)n * h;
        var dz = dc + (long)n * h;
        var longest = _sequences.Frames[lines[0]];
        for (var step = longest - 1; step >= 0; step--)
        {
            var running = 0;
            while (running < n && _sequences.Frames[lines[running]] > step)
            {
                running++;
            }

            for (var j = 0; j < running; j++)
            {
                var frame = _sequences.Frame(lines[j], step, d);
                var before = step > 0 ? cells + (long)_sequences.Frame(lines[j], step - 1, d) * h : null;
                var dhj = dh + (long)j * h;
                Kernels.AddScaled(new Span<float>(dhj, h), new ReadOnlySpan<float>(gradient + (long)frame * 2 * h + d * h, h), 1f);
                CellBackward(gates + (long)frame * g4, cells + (long)frame * h, before, dhj, dc + (long)j * h, dz + (long)j * g4, h);
                new ReadOnlySpan<float>(dz + (long)j * g4, g4).CopyTo(new Span<float>(dzAll + (long)frame * g4, g4));
            }

            // the gradient of h at the step before: dz · W_hh
            Gemm.Multiply(new RowsA(dz, g4), hiddenWeight, running, dh, h, add: false, threads: 1);
        }
    }

    // gate (4H, pre-activations in, activations out), c and h updated in place
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Cell(float* gate, float* c, float* hOut, int h)
    {
        var k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k <= h - 8; k += 8)
            {
                var i = Sigmoid(Vector256.Load(gate + k));
                var f = Sigmoid(Vector256.Load(gate + h + k));
                var g = Tanh(Vector256.Load(gate + 2 * h + k));
                var o = Sigmoid(Vector256.Load(gate + 3 * h + k));
                Vector256.Store(i, gate + k);
                Vector256.Store(f, gate + h + k);
                Vector256.Store(g, gate + 2 * h + k);
                Vector256.Store(o, gate + 3 * h + k);
                var cell = f * Vector256.Load(c + k) + i * g;
                Vector256.Store(cell, c + k);
                Vector256.Store(o * Tanh(cell), hOut + k);
            }
        }

        for (; k < h; k++)
        {
            var i = Sigmoid(gate[k]);
            var f = Sigmoid(gate[h + k]);
            var g = MathF.Tanh(gate[2 * h + k]);
            var o = Sigmoid(gate[3 * h + k]);
            gate[k] = i;
            gate[h + k] = f;
            gate[2 * h + k] = g;
            gate[3 * h + k] = o;
            c[k] = f * c[k] + i * g;
            hOut[k] = o * MathF.Tanh(c[k]);
        }
    }

    // from dh (in) and dc (in: from the step after; out: to the step before) to dz of the four gates
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CellBackward(float* gate, float* cell, float* before, float* dh, float* dc, float* dz, int h)
    {
        var k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var one = Vector256.Create(1f);
            for (; k <= h - 8; k += 8)
            {
                var i = Vector256.Load(gate + k);
                var f = Vector256.Load(gate + h + k);
                var g = Vector256.Load(gate + 2 * h + k);
                var o = Vector256.Load(gate + 3 * h + k);
                var tc = Tanh(Vector256.Load(cell + k));
                var d = Vector256.Load(dh + k);
                var dC = Vector256.Load(dc + k) + d * o * (one - tc * tc);
                var cPrev = before == null ? Vector256<float>.Zero : Vector256.Load(before + k);
                Vector256.Store(dC * g * i * (one - i), dz + k);
                Vector256.Store(dC * cPrev * f * (one - f), dz + h + k);
                Vector256.Store(dC * i * (one - g * g), dz + 2 * h + k);
                Vector256.Store(d * tc * o * (one - o), dz + 3 * h + k);
                Vector256.Store(dC * f, dc + k);
            }
        }

        for (; k < h; k++)
        {
            float i = gate[k], f = gate[h + k], g = gate[2 * h + k], o = gate[3 * h + k];
            var tc = MathF.Tanh(cell[k]);
            var d = dh[k];
            var dC = dc[k] + d * o * (1 - tc * tc);
            var cPrev = before == null ? 0 : before[k];
            dz[k] = dC * g * i * (1 - i);
            dz[h + k] = dC * cPrev * f * (1 - f);
            dz[2 * h + k] = dC * i * (1 - g * g);
            dz[3 * h + k] = d * tc * o * (1 - o);
            dc[k] = dC * f;
        }
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Sigmoid(Vector256<float> x) => Vector256.Create(1f) / (Vector256.Create(1f) + Vector256.Exp(-x));

    // tanh x = 1 − 2 / (e^{2x} + 1); exact enough everywhere, and saturates cleanly (e^{2x} = ∞ → 1, 0 → −1)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Tanh(Vector256<float> x) =>
        Vector256.Create(1f) - Vector256.Create(2f) / (Vector256.Exp(x + x) + Vector256.Create(1f));
}
