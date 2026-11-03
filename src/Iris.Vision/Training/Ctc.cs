namespace Iris.Vision.Training;

/// <summary>
/// Connectionist temporal classification: how likely the frames of a line spell its text, over every way to place
/// the characters (with blanks between and repeats), by the forward (α) and backward (β) sums in log space. The
/// gradient with respect to the logits is softmax − posterior: what the model gives minus how much each frame
/// belongs to each character over all placements.
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
        var logProbabilities = new double[frames * classes];
        for (var t = 0; t < frames; t++)
        {
            var row = logits + (long)t * classes;
            double max = double.NegativeInfinity;
            for (var k = 0; k < classes; k++)
            {
                max = Math.Max(max, row[k]);
            }

            double sum = 0;
            for (var k = 0; k < classes; k++)
            {
                sum += Math.Exp(row[k] - max);
            }

            var log = max + Math.Log(sum);
            for (var k = 0; k < classes; k++)
            {
                logProbabilities[t * classes + k] = row[k] - log;
            }
        }

        // the extended text: blank, c₁, blank, c₂, …, blank
        var extended = new int[states];
        for (var s = 0; s < states; s++)
        {
            extended[s] = s % 2 == 0 ? 0 : labels[s / 2];
        }

        double Emit(int t, int s) => logProbabilities[t * classes + extended[s]];
        bool Skips(int s) => s >= 2 && extended[s] != 0 && extended[s] != extended[s - 2];

        var alpha = new double[frames * states];
        var beta = new double[frames * states];
        Array.Fill(alpha, double.NegativeInfinity);
        Array.Fill(beta, double.NegativeInfinity);
        if (frames > 0)
        {
            alpha[0] = Emit(0, 0);
            if (states > 1)
            {
                alpha[1] = Emit(0, 1);
            }

            for (var t = 1; t < frames; t++)
            {
                for (var s = 0; s < states; s++)
                {
                    var a = alpha[(t - 1) * states + s];
                    if (s >= 1)
                    {
                        a = Add(a, alpha[(t - 1) * states + s - 1]);
                    }

                    if (Skips(s))
                    {
                        a = Add(a, alpha[(t - 1) * states + s - 2]);
                    }

                    alpha[t * states + s] = a + Emit(t, s);
                }
            }

            var last = (frames - 1) * states;
            beta[last + states - 1] = Emit(frames - 1, states - 1);
            if (states > 1)
            {
                beta[last + states - 2] = Emit(frames - 1, states - 2);
            }

            for (var t = frames - 2; t >= 0; t--)
            {
                for (var s = 0; s < states; s++)
                {
                    var b = beta[(t + 1) * states + s];
                    if (s + 1 < states)
                    {
                        b = Add(b, beta[(t + 1) * states + s + 1]);
                    }

                    if (s + 2 < states && Skips(s + 2))
                    {
                        b = Add(b, beta[(t + 1) * states + s + 2]);
                    }

                    beta[t * states + s] = b + Emit(t, s);
                }
            }
        }

        var logP = frames == 0 ? double.NegativeInfinity :
            states > 1 ? Add(alpha[(frames - 1) * states + states - 1], alpha[(frames - 1) * states + states - 2]) : alpha[(frames - 1) * states];
        if (double.IsNegativeInfinity(logP) || double.IsNaN(logP))
        {
            new Span<float>(gradient, frames * classes).Clear();
            return 0;
        }

        var posterior = new double[classes];
        for (var t = 0; t < frames; t++)
        {
            Array.Fill(posterior, double.NegativeInfinity);
            for (var s = 0; s < states; s++)
            {
                var k = extended[s];
                posterior[k] = Add(posterior[k], alpha[t * states + s] + beta[t * states + s] - Emit(t, s));
            }

            var row = gradient + (long)t * classes;
            for (var k = 0; k < classes; k++)
            {
                row[k] = (float)(scale * (Math.Exp(logProbabilities[t * classes + k]) - Math.Exp(posterior[k] - logP)));
            }
        }

        return -logP;
    }

    // log(eᵃ + eᵇ)
    private static double Add(double a, double b)
    {
        if (double.IsNegativeInfinity(a))
        {
            return b;
        }

        if (double.IsNegativeInfinity(b))
        {
            return a;
        }

        return a > b ? a + Math.Log(1 + Math.Exp(b - a)) : b + Math.Log(1 + Math.Exp(a - b));
    }
}
