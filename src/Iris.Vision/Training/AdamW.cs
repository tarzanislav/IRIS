using System.Runtime.Intrinsics;

namespace Iris.Vision.Training;

/// <summary>
/// Adam with decoupled weight decay (the optimizer of tools/ocr/train.py: β 0.9 and 0.999, ε 1e-8, decay 1e-4, the
/// same arithmetic as PyTorch's AdamW), with clipping of the whole gradient to a largest norm before the step.
/// </summary>
public sealed class AdamW
{
    private readonly Dictionary<string, (float[] M, float[] V)> _moments = new(StringComparer.Ordinal);

    public float Beta1 { get; init; } = 0.9f;

    public float Beta2 { get; init; } = 0.999f;

    public float Epsilon { get; init; } = 1e-8f;

    public float WeightDecay { get; init; } = 1e-4f;

    /// <summary>Steps done so far (the bias correction depends on it).</summary>
    public int Steps { get; set; }

    /// <summary>The moments of a tensor (created as zeros when first asked for).</summary>
    public (float[] M, float[] V) Moments(string name, int length)
    {
        if (!_moments.TryGetValue(name, out var moments))
        {
            moments = (new float[length], new float[length]);
            _moments[name] = moments;
        }

        return moments;
    }

    /// <summary>The length of the whole gradient (all tensors as one vector).</summary>
    public static double Norm(OcrWeights gradients)
    {
        double sum = 0;
        foreach (var t in gradients.Learnable())
        {
            foreach (var v in t.Values)
            {
                sum += (double)v * v;
            }
        }

        return Math.Sqrt(sum);
    }

    /// <summary>Scales the gradient down to the largest norm when it is longer (PyTorch's clip_grad_norm_); returns its norm before.</summary>
    public static double Clip(OcrWeights gradients, double maxNorm)
    {
        var norm = Norm(gradients);
        var factor = (float)Math.Min(1.0, maxNorm / (norm + 1e-6));
        if (factor < 1)
        {
            foreach (var t in gradients.Learnable())
            {
                var values = t.Values;
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] *= factor;
                }
            }
        }

        return norm;
    }

    /// <summary>One step of all learnable tensors of w with the gradients in g at the given rate.</summary>
    public void Step(OcrWeights w, OcrWeights g, float rate)
    {
        Steps++;
        var correction1 = 1 - Math.Pow(Beta1, Steps);
        var correction2 = Math.Sqrt(1 - Math.Pow(Beta2, Steps));
        var stepSize = (float)(rate / correction1);
        var decay = 1 - rate * WeightDecay;
        foreach (var (weight, gradient) in w.Learnable().Zip(g.Learnable()))
        {
            var (m, v) = Moments(weight.Name, weight.Values.Length);
            Update(weight.Values, gradient.Values, m, v, decay, stepSize, (float)correction2);
        }
    }

    private unsafe void Update(float[] p, float[] g, float[] m, float[] v, float decay, float stepSize, float correction2)
    {
        float b1 = Beta1, b2 = Beta2, eps = Epsilon;
        var i = 0;
        fixed (float* pp = p, pg = g, pm = m, pv = v)
        {
            if ((Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
            {
                var vb1 = Vector256.Create(b1);
                var vb2 = Vector256.Create(b2);
                var va1 = Vector256.Create(1 - b1);
                var va2 = Vector256.Create(1 - b2);
                var vd = Vector256.Create(decay);
                var vs = Vector256.Create(stepSize);
                var vc = Vector256.Create(correction2);
                var ve = Vector256.Create(eps);
                for (; i <= p.Length - 8; i += 8)
                {
                    var grad = Vector256.Load(pg + i);
                    var mi = vb1 * Vector256.Load(pm + i) + va1 * grad;
                    var vi = vb2 * Vector256.Load(pv + i) + va2 * grad * grad;
                    Vector256.Store(mi, pm + i);
                    Vector256.Store(vi, pv + i);
                    var param = Vector256.Load(pp + i) * vd;
                    Vector256.Store(param - vs * mi / (Vector256.Sqrt(vi) / vc + ve), pp + i);
                }
            }

            for (; i < p.Length; i++)
            {
                var grad = pg[i];
                pm[i] = b1 * pm[i] + (1 - b1) * grad;
                pv[i] = b2 * pv[i] + (1 - b2) * grad * grad;
                pp[i] = pp[i] * decay - stepSize * pm[i] / (MathF.Sqrt(pv[i]) / correction2 + eps);
            }
        }
    }
}
