using System.Globalization;
using System.Text.Json.Nodes;
using Iris.Model;

namespace Iris.Vision.Training;

/// <summary>Where a learning run stands: steps, minutes, and the plan of the learning rate (its cosine spans the plan).</summary>
public sealed record OcrLearningState
{
    public int Step { get; init; }

    public double Minutes { get; init; }

    public double PlanStart { get; init; }

    public double PlanEnd { get; init; }

    public double PlanRate { get; init; } = 1e-3;

    /// <summary>
    /// The rate at a moment of the plan: warm-up over the first 500 steps of a run, then the cosine from the plan's
    /// rate down to 2 % of it (the schedule of tools/ocr/train.py).
    /// </summary>
    public double Rate(double minutes, int stepsOfThisRun)
    {
        var span = Math.Max(1e-9, PlanEnd - PlanStart);
        var phase = Math.Clamp((minutes - PlanStart) / span, 0, 1);
        return PlanRate * Math.Min(1.0, (stepsOfThisRun + 1) / 500.0) * (0.02 + 0.98 * 0.5 * (1 + Math.Cos(Math.PI * phase)));
    }
}

/// <summary>
/// The files of the OCR learning: "iris-ocr-train" (everything to go on learning – the weights with the batch
/// normalization not folded, its running statistics, the moments of Adam and the state; tensors named and shaped as
/// in PyTorch, so tools/ocr can write and read them too) and "iris-ocr" (the reader IRIS uses: normalization folded
/// into the convolutions).
/// </summary>
public static class OcrFiles
{
    public const string TrainingKind = "iris-ocr-train";

    public const string ReaderKind = "iris-ocr";

    public static void SaveTraining(string path, OcrWeights w, AdamW adam, OcrLearningState state, JsonObject? provenance = null)
    {
        var config = w.Shape.ToJson();
        config["state"] = new JsonObject
        {
            ["step"] = state.Step,
            ["minutes"] = state.Minutes,
            ["plan"] = new JsonObject { ["start"] = state.PlanStart, ["end"] = state.PlanEnd, ["rate"] = state.PlanRate },
            ["adam_steps"] = adam.Steps,
        };
        var tensors = new List<(string, int[], float[])>();
        foreach (var t in w.Learnable())
        {
            tensors.Add((t.Name, t.Shape, t.ForFile()));
            var (m, v) = adam.Moments(t.Name, t.Values.Length);
            tensors.Add(($"adam.m.{t.Name}", t.Shape, t.ForFile(m)));
            tensors.Add(($"adam.v.{t.Name}", t.Shape, t.ForFile(v)));
        }

        foreach (var t in w.Statistics())
        {
            tensors.Add((t.Name, t.Shape, t.Values));
        }

        WriteAtomically(path, file => ModelFile.Write(file, TrainingKind, config, provenance ?? [], tensors, []));
    }

    public static (OcrWeights Weights, AdamW Adam, OcrLearningState State) LoadTraining(string path)
    {
        using var file = ModelFile.Load(path);
        if (file.Kind != TrainingKind)
        {
            throw new FormatException($"not an IRIS OCR learning file (kind \"{file.Kind}\")");
        }

        var shape = OcrShape.FromJson(file.Config);
        var w = new OcrWeights(shape);
        var state = file.Config["state"] as JsonObject ?? [];
        var adam = new AdamW { Steps = state["adam_steps"]?.GetValue<int>() ?? 0 };
        var names = file.Tensors.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var t in w.Learnable())
        {
            t.FromFile(file.Floats(t.Name, t.Shape)).CopyTo(t.Values, 0);
            var (m, v) = adam.Moments(t.Name, t.Values.Length);
            if (names.Contains($"adam.m.{t.Name}"))
            {
                t.FromFile(file.Floats($"adam.m.{t.Name}", t.Shape)).CopyTo(m, 0);
                t.FromFile(file.Floats($"adam.v.{t.Name}", t.Shape)).CopyTo(v, 0);
            }
        }

        foreach (var t in w.Statistics())
        {
            file.Floats(t.Name, t.Shape).CopyTo(t.Values, 0);
        }

        var plan = state["plan"] as JsonObject;
        var learning = new OcrLearningState
        {
            Step = state["step"]?.GetValue<int>() ?? 0,
            Minutes = state["minutes"]?.GetValue<double>() ?? 0,
            PlanStart = plan?["start"]?.GetValue<double>() ?? 0,
            PlanEnd = plan?["end"]?.GetValue<double>() ?? 0,
            PlanRate = plan?["rate"]?.GetValue<double>() ?? 1e-3,
        };
        return (w, adam, learning);
    }

    /// <summary>
    /// The reader file IRIS uses (kind "iris-ocr", what tools/ocr/model.py writes): each convolution with its batch
    /// normalization folded in (w · γ/σ, β − μ · γ/σ with the running statistics), the LSTM biases summed.
    /// </summary>
    public static void SaveReader(string path, OcrWeights w, JsonObject? provenance = null)
    {
        var tensors = new List<(string, int[], float[])>();
        for (var i = 0; i < w.Convs.Length; i++)
        {
            var c = w.Convs[i];
            var weight = c.WeightForFile();
            var bias = new float[c.Outputs];
            var perOutput = c.Inputs * c.Kh * c.Kw;
            for (var o = 0; o < c.Outputs; o++)
            {
                var scale = c.Gamma[o] / MathF.Sqrt(c.Variance[o] + ConvLayer.Epsilon);
                for (var k = 0; k < perOutput; k++)
                {
                    weight[o * perOutput + k] *= scale;
                }

                bias[o] = c.Beta[o] - c.Mean[o] * scale;
            }

            tensors.Add(($"conv.{i}.w", [c.Outputs, c.Inputs, c.Kh, c.Kw], weight));
            tensors.Add(($"conv.{i}.b", [c.Outputs], bias));
        }

        for (var l = 0; l < w.Lstm.Length; l++)
        {
            for (var d = 0; d < 2; d++)
            {
                var p = w.Lstm[l][d];
                var name = $"lstm.{l}.{(d == 0 ? "fw" : "bw")}";
                tensors.Add(($"{name}.ih", [4 * p.Hidden, p.Inputs], p.InputWeight));
                tensors.Add(($"{name}.hh", [4 * p.Hidden, p.Hidden], p.HiddenWeight));
                tensors.Add(($"{name}.b", [4 * p.Hidden], p.InputBias.Zip(p.HiddenBias, (a, b) => a + b).ToArray()));
            }
        }

        tensors.Add(("out.w", [w.Shape.Classes, 2 * w.Shape.Hidden], w.OutWeight));
        tensors.Add(("out.b", [w.Shape.Classes], w.OutBias));
        WriteAtomically(path, file => ModelFile.Write(file, ReaderKind, w.Shape.ToJson(), provenance ?? [], tensors, []));
    }

    // a file is written beside and then moved in place, so a reader never sees half of it
    private static void WriteAtomically(string path, Action<string> write)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp");
        write(temporary);
        File.Move(temporary, path, overwrite: true);
    }
}
