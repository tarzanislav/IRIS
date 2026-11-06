using System.Runtime.Intrinsics;
using Iris.Numerics;

namespace Iris.Vision.Training;

/// <summary>
/// Connectionist temporal classification: how likely the frames of a line spell its text, over every way to place
/// the characters (with blanks between and repeats), by the forward (α) and backward (β) sums. They are kept as
/// probabilities in double, scaled to a sum of one at every frame (the scales multiply to the probability of the
/// line), so the sums need no logarithms and no exponentials. The gradient with respect to the logits is
/// softmax − posterior: what the model gives minus how much each frame belongs to each character over all placements.
/// </summary>
internal static class Ctc
{
    /// <summary>
    /// −log p(labels | logits) of one line; the gradient of scale · loss goes to gradient (frames × classes). An
    /// impossible line (too few frames) gives 0 and no gradient – zero_infinity of PyTorch.
    /// </summary>
    public static unsafe double Loss(float* logits, int frames, int classes, ReadOnlySpan<int> labels, float* gradient, float scale)
    {
        var states = 2 * labels.Length + 1;
        if (frames == 0)
        {
            return 0;
        }

        // the extended text: blank, c₁, blank, c₂, …, blank – and the different classes in it
        var extended = new int[states];
        var kinds = new List<int> { 0 };
        var kind = new int[states];
        for (var s = 0; s < states; s++)
        {
            extended[s] = s % 2 == 0 ? 0 : labels[s / 2];
            var index = kinds.IndexOf(extended[s]);
            if (index < 0)
            {
                index = kinds.Count;
                kinds.Add(extended[s]);
            }

            kind[s] = index;
        }

        // per frame: the softmax of all classes into the gradient (scaled), and in double the probabilities of the
        // classes of the text
        var emission = new double[frames * kinds.Count];
        for (var t = 0; t < frames; t++)
        {
            var row = logits + ((long)t * classes);
            var normalizer = LogSumExp(row, classes);
            Softmax(row, classes, (float)normalizer, gradient + ((long)t * classes), scale);
            for (var u = 0; u < kinds.Count; u++)
            {
                emission[(t * kinds.Count) + u] = Math.Exp(row[kinds[u]] - normalizer);
            }
        }

        double Emit(int t, int s) => emission[(t * kinds.Count) + kind[s]];
        bool Skips(int s) => s >= 2 && extended[s] != 0 && extended[s] != extended[s - 2];

        // α, scaled at every frame; log p = Σ log of the scales + log of the end
        var alpha = new double[frames * states];
        var logP = 0.0;
        alpha[0] = Emit(0, 0);
        if (states > 1)
        {
            alpha[1] = Emit(0, 1);
        }

        logP += Normalize(alpha.AsSpan(0, states));
        for (var t = 1; t < frames && !double.IsNegativeInfinity(logP); t++)
        {
            var before = alpha.AsSpan((t - 1) * states, states);
            var now = alpha.AsSpan(t * states, states);
            for (var s = 0; s < states; s++)
            {
                var a = before[s];
                if (s >= 1)
                {
                    a += before[s - 1];
                }

                if (Skips(s))
                {
                    a += before[s - 2];
                }

                now[s] = a * Emit(t, s);
            }

            logP += Normalize(now);
        }

        var lastRow = (frames - 1) * states;
        var end = states > 1 ? alpha[lastRow + states - 1] + alpha[lastRow + states - 2] : alpha[lastRow];
        logP += end > 0 ? Math.Log(end) : double.NegativeInfinity;
        if (double.IsNegativeInfinity(logP) || double.IsNaN(logP))
        {
            new Span<float>(gradient, frames * classes).Clear();
            return 0;
        }

        // β, scaled the same way (its scales are not needed: the posterior of a frame sums to one)
        var beta = new double[frames * states];
        beta[lastRow + states - 1] = Emit(frames - 1, states - 1);
        if (states > 1)
        {
            beta[lastRow + states - 2] = Emit(frames - 1, states - 2);
        }

        Normalize(beta.AsSpan(lastRow, states));
        for (var t = frames - 2; t >= 0; t--)
        {
            var after = beta.AsSpan((t + 1) * states, states);
            var now = beta.AsSpan(t * states, states);
            for (var s = 0; s < states; s++)
            {
                var b = after[s];
                if (s + 1 < states)
                {
                    b += after[s + 1];
                }

                if (s + 2 < states && Skips(s + 2))
                {
                    b += after[s + 2];
                }

                now[s] = b * Emit(t, s);
            }

            Normalize(now);
        }

        // the posterior of every state at every frame: α β / emission, made to sum to one over the states
        var share = new double[states];
        for (var t = 0; t < frames; t++)
        {
            double total = 0;
            for (var s = 0; s < states; s++)
            {
                var e = Emit(t, s);
                share[s] = e > 0 ? alpha[(t * states) + s] * beta[(t * states) + s] / e : 0;
                total += share[s];
            }

            if (total <= 0)
            {
                continue;
            }

            var row = gradient + ((long)t * classes);
            for (var s = 0; s < states; s++)
            {
                row[extended[s]] -= (float)(scale * share[s] / total);
            }
        }

        return -logP;
    }

    // divides the values by their sum and returns its logarithm (−∞ when all are zero)
    private static double Normalize(Span<double> values)
    {
        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }

        if (!(sum > 0))
        {
            return double.NegativeInfinity;
        }

        var inverse = 1 / sum;
        for (var s = 0; s < values.Length; s++)
        {
            values[s] *= inverse;
        }

        return Math.Log(sum);
    }

    // log Σ e^x over a row of logits (the exponentials eight at a time, summed in double)
    private static unsafe double LogSumExp(float* row, int classes)
    {
        var max = float.NegativeInfinity;
        for (var k = 0; k < classes; k++)
        {
            max = MathF.Max(max, row[k]);
        }

        double sum = 0;
        var k0 = 0;
        if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
        {
            var m = Vector256.Create(max);
            for (; k0 <= classes - 8; k0 += 8)
            {
                sum += Vector256.Sum(Kernels.Exp(Vector256.Load(row + k0) - m));
            }
        }

        for (; k0 < classes; k0++)
        {
            sum += Math.Exp(row[k0] - max);
        }

        return max + Math.Log(sum);
    }

    // target = scale · softmax(row), given log Σ e^x
    private static unsafe void Softmax(float* row, int classes, float normalizer, float* target, float scale)
    {
        var k = 0;
        if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
        {
            var n = Vector256.Create(normalizer);
            var sc = Vector256.Create(scale);
            for (; k <= classes - 8; k += 8)
            {
                Vector256.Store(Kernels.Exp(Vector256.Load(row + k) - n) * sc, target + k);
            }
        }

        for (; k < classes; k++)
        {
            target[k] = scale * MathF.Exp(row[k] - normalizer);
        }
    }
}
