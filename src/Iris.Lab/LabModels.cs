using System.Globalization;
using System.Text.Json.Nodes;
using Iris.Vision.Training;

namespace Iris.Lab;

/// <summary>
/// The server's own share of the learning, light enough for a small server: after every round it moves an average of
/// the classroom's model (it keeps nine tenths and takes a tenth of each new version, so about the last ten versions
/// count – the noise of single rounds evens out), the tests read both the model and the average, and the best model
/// any test has seen is kept as a reader file to save from the console. A new start of the classroom's model (from a
/// release) forgets both.
/// </summary>
public sealed class LabModels
{
    /// <summary>The share of the average it keeps when a new version comes.</summary>
    public const double Keep = 0.9;

    private readonly string _folder;
    private readonly Lock _gate = new();
    private readonly OcrWeights _average;
    private JsonObject? _best;

    public LabModels(string folder, OcrWeights start)
    {
        _folder = folder;
        _average = Load(AveragePath(folder), start.Shape) ?? start.Copy();
        try
        {
            _best = File.Exists(BestInfoPath(folder)) && File.Exists(BestPath(folder)) ? JsonNode.Parse(File.ReadAllText(BestInfoPath(folder))) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            _best = null;
        }
    }

    public static string AveragePath(string folder) => Path.Combine(folder, "lab-model-average.iris");

    /// <summary>The best model the tests have seen, as a reader file (what IRIS reads with).</summary>
    public static string BestPath(string folder) => Path.Combine(folder, "iris-ocr-best.iris");

    public static string BestInfoPath(string folder) => Path.Combine(folder, "best.json");

    /// <summary>The best model so far: version, which one (the model or its average), CER and when; null while none was tested.</summary>
    public JsonObject? Best
    {
        get
        {
            lock (_gate)
            {
                return _best?.DeepClone().AsObject();
            }
        }
    }

    /// <summary>A copy of the average as it is now.</summary>
    public OcrWeights Average()
    {
        lock (_gate)
        {
            return _average.Copy();
        }
    }

    /// <summary>Forgets the average and the best model (the classroom starts from another model).</summary>
    public static void Forget(string folder)
    {
        foreach (var path in new[] { AveragePath(folder), BestPath(folder), BestInfoPath(folder) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>A new version of the model into the average (the weights and the running statistics), saved.</summary>
    public void Add(OcrWeights model, int version)
    {
        lock (_gate)
        {
            foreach (var (mine, theirs) in _average.Learnable().Zip(model.Learnable()).Concat(_average.Statistics().Zip(model.Statistics())))
            {
                var a = mine.Values;
                var b = theirs.Values;
                for (var i = 0; i < a.Length; i++)
                {
                    a[i] = (float)((Keep * a[i]) + ((1 - Keep) * b[i]));
                }
            }

            var provenance = new JsonObject { ["source"] = "IRIS lab: the average of the classroom's model over the rounds", ["version"] = version };
            OcrFiles.SaveTraining(AveragePath(_folder), _average, new AdamW(), new OcrLearningState { Step = version }, provenance);
        }
    }

    /// <summary>
    /// A tested model (kind "model" or "average"): when it reads better than the best so far – the mean of the CER in
    /// the fonts it learns from and in the unseen font, or the first alone – it becomes the best. Returns whether it did.
    /// </summary>
    public bool Offer(OcrWeights model, int version, string kind, double cer, double? cerUnseen, DateTime time)
    {
        var score = Score(cer, cerUnseen);
        lock (_gate)
        {
            if (_best is { } best && Score(best["cer"]!.GetValue<double>(), best["cer_unseen"]?.GetValue<double>()) <= score)
            {
                return false;
            }

            var provenance = new JsonObject { ["source"] = "IRIS lab: the best model of the classroom's tests", ["version"] = version, ["kind"] = kind };
            OcrFiles.SaveReader(BestPath(_folder), model, provenance);
            _best = new JsonObject
            {
                ["version"] = version,
                ["kind"] = kind,
                ["cer"] = cer,
                ["cer_unseen"] = cerUnseen,
                ["time"] = time.ToString("o", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(BestInfoPath(_folder), _best.ToJsonString());
            return true;
        }
    }

    /// <summary>The reader file of the best model, or null while there is none.</summary>
    public byte[]? BestFile()
    {
        lock (_gate)
        {
            return _best is not null && File.Exists(BestPath(_folder)) ? File.ReadAllBytes(BestPath(_folder)) : null;
        }
    }

    private static double Score(double cer, double? cerUnseen) => cerUnseen is { } unseen ? (cer + unseen) / 2 : cer;

    private static OcrWeights? Load(string path, OcrShape shape)
    {
        try
        {
            if (File.Exists(path) && OcrFiles.LoadTraining(path) is var (weights, _, _) && weights.Shape.ToJson().ToJsonString() == shape.ToJson().ToJsonString())
            {
                return weights;
            }
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException or ArgumentException)
        {
        }

        return null;
    }
}
