using Iris.Learning;
using Iris.Model;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>F3 – learning: the adapter file, the hand-written backward pass and learning itself, on a small random model.</summary>
public static class LearningTests
{
    private static readonly string[] AllLayers = ["q", "k", "v", "o", "1", "2"];

    [Test]
    public static void GradientsAreTheSlopesOfTheLoss()
    {
        foreach (var activation in new[] { "swish", "relu" })
        {
            using var tiny = TinyModel.Create(activation: activation);
            var translator = Translator.Load(tiny.Path);
            var adapter = Adapter.Create(translator.Model, "test", rank: 2, alpha: 4, targets: AllLayers);
            var random = new Random(7);
            foreach (var low in adapter.Layers.Values)
            {
                // B is zero in a new adapter, so A would get no gradient; for the check both are random
                for (var i = 0; i < low.B.Length; i++)
                {
                    low.B[i] = (float)(random.NextDouble() - 0.5) * 0.4f;
                }
            }

            var trainer = new MarianTrainer(translator.Model, adapter);
            var vocabulary = translator.Model.Config.VocabSize - 1; // without the pad token
            var pair = new TrainingPair([5, 9, 13, 2, 0], [7, 3, 11, 0]);
            Assert.True(pair.Source.Concat(pair.Target).All(id => id < vocabulary), "ids of the test pair are in the vocabulary");

            var gradients = new Gradients(adapter);
            trainer.Backward(pair, gradients, 0.1f);
            double Loss() => trainer.Backward(pair, new Gradients(adapter), 0.1f);

            var checkedValues = 0;
            var worst = 0.0;
            foreach (var (name, low) in adapter.Layers)
            {
                foreach (var (values, gradient) in new[] { (low.A, gradients.A[name]), (low.B, gradients.B[name]) })
                {
                    for (var probe = 0; probe < 2; probe++)
                    {
                        var i = random.Next(values.Length);
                        var keep = values[i];
                        const float step = 1e-2f;
                        values[i] = keep + step;
                        var up = Loss();
                        values[i] = keep - step;
                        var down = Loss();
                        values[i] = keep;
                        var slope = (up - down) / (2 * step);
                        var error = Math.Abs(slope - gradient[i]) / (Math.Max(Math.Abs(slope), Math.Abs(gradient[i])) + 2e-3);
                        worst = Math.Max(worst, error);
                        Assert.True(error < 0.03, $"{activation} {name} [{i}]: gradient {gradient[i]:g5}, slope of the loss {slope:g5}");
                        checkedValues++;
                    }
                }
            }

            Assert.True(checkedValues >= 4 * 30, $"{activation}: {checkedValues} values of {adapter.Layers.Count} layers checked (worst {worst:p2})");
        }
    }

    [Test]
    public static void LearningSeesWhatTranslationComputes()
    {
        // the loss of the whole translation at once equals the loss of the decoder step by step (as it translates)
        using var tiny = TinyModel.Create();
        var translator = Translator.Load(tiny.Path);
        var adapter = Adapter.Create(translator.Model, "test", rank: 2, alpha: 4, targets: AllLayers);
        var random = new Random(5);
        foreach (var low in adapter.Layers.Values)
        {
            for (var i = 0; i < low.B.Length; i++)
            {
                low.B[i] = (float)(random.NextDouble() - 0.5) * 0.4f;
            }
        }

        var trainer = new MarianTrainer(translator.Model, adapter);
        var pair = new TrainingPair([5, 9, 13, 2, 0], [7, 3, 11, 4, 0]);
        var model = trainer.Model;
        var session = model.StartDecoding(model.Encode(pair.Source), 1);
        var last = model.Config.DecoderStartId;
        double loss = 0;
        foreach (var token in pair.Target)
        {
            var logits = session.Step([last]);
            loss -= logits[token] - Numerics.Kernels.LogSumExp(logits);
            last = token;
        }

        Assert.Near(loss / pair.Target.Length, trainer.Loss([pair]), 1e-4, "loss per token");
    }

    [Test]
    public static void ANewAdapterChangesNothingAndLearningTeachesAPair()
    {
        using var tiny = TinyModel.Create(learnable: true);
        var translator = Translator.Load(tiny.Path);
        var source = new[] { 5, 9, 13, 2, 0 };
        var target = new[] { 7, 3, 11, 4, 0 };
        var before = translator.Generate(source, beams: 1).Tokens;
        Assert.True(!before.SequenceEqual(target), "the random model does not know the pair");

        var adapter = Adapter.Create(translator.Model, "test", rank: 4, alpha: 8);
        var fresh = new Translator(translator.Model.WithAdapter(adapter), translator.Tokenizer);
        Assert.Sequence(before, fresh.Generate(source, beams: 1).Tokens, "a new adapter (B = 0) changes nothing");
        Assert.True(fresh.Model.HasAdapter && !translator.Model.HasAdapter, "the base model stays without the adapter");

        adapter = Adapter.Create(translator.Model, "test", rank: 4, alpha: 8, targets: AllLayers);
        var trainer = new MarianTrainer(translator.Model, adapter);
        var pairs = new[] { new TrainingPair(source, target) };
        var start = trainer.Loss(pairs);
        var steps = 0;
        var settings = new LearningSettings { Epochs = 150, BatchSize = 1, LearningRate = 1e-2f, LabelSmoothing = 0 };
        trainer.Train(pairs, settings, new Progress(p => steps = p.Step));
        var end = trainer.Loss(pairs);
        Assert.True(end < start * 0.05, $"the loss fell from {start:0.000} to {end:0.000}");
        Assert.Equal(150, steps, "one step per pair and epoch");

        var learned = new Translator(translator.Model.WithAdapter(adapter), translator.Tokenizer);
        Assert.Sequence(target, learned.Generate(source, beams: 1).Tokens, "the learned translation");
        Assert.Sequence(before, translator.Generate(source, beams: 1).Tokens, "the model itself did not change");
    }

    [Test]
    public static void AdapterFilesKeepEverythingAndFitOnlyTheirModel()
    {
        using var tiny = TinyModel.Create();
        var translator = Translator.Load(tiny.Path);
        var sha = ModelFile.Sha256(tiny.Path);
        var adapter = Adapter.Create(translator.Model, sha, rank: 3, alpha: 6);
        var random = new Random(3);
        foreach (var low in adapter.Layers.Values)
        {
            for (var i = 0; i < low.B.Length; i++)
            {
                low.B[i] = (float)(random.NextDouble() - 0.5);
            }
        }

        Assert.Equal(2 * 3 * 2, adapter.Layers.Count, "q and v of the 2 + 2×2 attentions");
        var path = tiny.Path + ".adapter";
        try
        {
            adapter.With(new System.Text.Json.Nodes.JsonObject { ["pairs"] = 12 }).Save(path);
            var read = Adapter.Load(path);
            Assert.Equal(sha, read.BaseSha256, "the model it belongs to");
            Assert.Equal(12, read.Provenance["pairs"]!.GetValue<int>(), "provenance");
            Assert.Equal(adapter.Parameters, read.Parameters, "the number of learned values");
            var source = new[] { 5, 9, 13, 2, 0 };
            Assert.Sequence(new Translator(translator.Model.WithAdapter(adapter), translator.Tokenizer).Generate(source, 3).Tokens,
                new Translator(translator.Model.WithAdapter(read), translator.Tokenizer).Generate(source, 3).Tokens, "the same translation after reading");

            using var bigger = TinyModel.Create(d: 24, heads: 4);
            var other = Translator.Load(bigger.Path);
            Assert.Throws<FormatException>(() => other.Model.WithAdapter(read), "an adapter of another model is refused");
            Assert.Throws<ArgumentException>(() => new MarianTrainer(translator.Model.WithAdapter(read), read), "learning starts from the model alone");
            Assert.Throws<FormatException>(() => Adapter.Load(tiny.Path), "a model file is not an adapter");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class Progress(Action<LearningProgress> report) : IProgress<LearningProgress>
    {
        public void Report(LearningProgress value) => report(value);
    }
}
