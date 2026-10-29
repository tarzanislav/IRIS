using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Iris.Memory;
using Iris.Model;
using Iris.Numerics;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>Damaged input, threads and limits: the core refuses clearly or copes, and never gives a different result.</summary>
public static class RobustnessTests
{
    [Test]
    public static void DamagedModelFilesAreRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iris-damaged-{Guid.NewGuid():N}.iris");
        try
        {
            ModelFile.Write(path, "test", [], [], [("w", [2, 2], [1f, 2f, 3f, 4f])], []);
            var good = File.ReadAllBytes(path);
            var headerLength = (int)BinaryPrimitives.ReadInt64LittleEndian(good.AsSpan(8));
            var data = good[(int)Align(16 + headerLength)..];
            var header = JsonNode.Parse(good.AsSpan(16, headerLength))!.AsObject();
            using (var file = ModelFile.Parse(good))
            {
                Assert.Sequence([1f, 2f, 3f, 4f], file.Floats("w", 2, 2), "the undamaged file");
            }

            // the same data behind another header
            byte[] With(byte[] headerBytes)
            {
                var start = (int)Align(16 + headerBytes.Length);
                var bytes = new byte[start + data.Length];
                "IRISMDL1"u8.CopyTo(bytes);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), headerBytes.Length);
                headerBytes.CopyTo(bytes, 16);
                data.CopyTo(bytes, start);
                return bytes;
            }

            byte[] Changed(Action<JsonObject> change)
            {
                var copy = header.DeepClone().AsObject();
                change(copy);
                return With(Encoding.UTF8.GetBytes(copy.ToJsonString()));
            }

            JsonObject Tensor(JsonObject h) => h["tensors"]![0]!.AsObject();

            void Refused(byte[] bytes, string what)
            {
                Assert.Throws<FormatException>(() => ModelFile.Parse(bytes).Dispose(), $"{what} (in memory)");
                File.WriteAllBytes(path, bytes);
                Assert.Throws<FormatException>(() => ModelFile.Load(path).Dispose(), $"{what} (from a file)");
            }

            Refused(good[..^4], "the data cut short");
            Refused(good[..12], "the start cut short");
            Refused(Encoding.UTF8.GetBytes("IRISMDL2" + new string(' ', 64)), "another magic");
            var tooLong = (byte[])good.Clone();
            BinaryPrimitives.WriteInt64LittleEndian(tooLong.AsSpan(8), good.Length);
            Refused(tooLong, "a header longer than the file");
            BinaryPrimitives.WriteInt64LittleEndian(tooLong.AsSpan(8), -1);
            Refused(tooLong, "a negative header length");
            Refused(With("{\"format\": 1, \"tensors\": ["u8.ToArray()), "a header that is not JSON");
            Refused(With("[1, 2]"u8.ToArray()), "a header that is not an object");
            Refused(Changed(h => h["format"] = 2), "a newer format");
            Refused(Changed(h => h.Remove("format")), "no format");
            Refused(Changed(h => Tensor(h)["offset"] = long.MaxValue), "an offset far past the end");
            Refused(Changed(h => Tensor(h)["offset"] = -64), "a negative offset");
            Refused(Changed(h => Tensor(h)["length"] = 12), "a length that does not match the shape");
            Refused(Changed(h => Tensor(h)["dtype"] = "f64"), "an unknown element type");
            Refused(Changed(h => Tensor(h)["shape"] = new JsonArray(1 << 30, 1 << 30, 4)), "a shape whose size overflows");
            Refused(Changed(h => Tensor(h)["shape"] = new JsonArray(int.MaxValue, int.MaxValue, int.MaxValue)), "a shape whose count overflows");
            Refused(Changed(h => Tensor(h)["shape"] = new JsonArray(-2, -2)), "a negative shape");
            Refused(Changed(h => Tensor(h).Remove("name")), "a tensor without a name");
        }
        finally
        {
            File.Delete(path);
        }

        static long Align(long value) => (value + 63) / 64 * 64;
    }

    [Test]
    public static void MemoryContinuesAfterALineCutOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iris-memory-{Guid.NewGuid():N}.jsonl");
        try
        {
            var memory = TranslationMemory.Open(path);
            memory.Remember("The premiere is tonight.", "Premiéra je dnes večer.");
            File.AppendAllText(path, "{\"source\": \"Cut off by a cra");
            var reopened = TranslationMemory.Open(path);
            Assert.Equal(1, reopened.Count, "the cut-off line is skipped");
            reopened.Remember("Tickets are sold out.", "Lístky sú vypredané.");
            var third = TranslationMemory.Open(path);
            Assert.Equal(2, third.Count, "the next correction starts on a line of its own");
            Assert.True(third.TryGet("Tickets are sold out.", out var target) && target == "Lístky sú vypredané.", "and is read back");
            Assert.True(File.ReadAllText(path).EndsWith('\n'), "the file ends with a line break");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void RowsGiveTheSameNumbersAloneOrInABatch()
    {
        // a sentence must not translate differently because other beams or rows are computed with it
        var random = new Random(11);
        float[] Values(int count) => [.. Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 2 - 1))];
        foreach (var (inputs, outputs) in new[] { (37, 13), (512, 300) })
        {
            const int n = 5;
            var x = Values(n * inputs);
            var w = Values(inputs * outputs);
            var b = Values(outputs);
            var floats = Matrix.FromFloats(w, outputs, inputs);
            foreach (var (name, matrix) in new[] { ("float32", floats), ("int8", floats.ToInt8()) })
            {
                var batch = matrix.Linear(x, n, b);
                for (var t = 0; t < n; t++)
                {
                    var alone = matrix.Linear(x[(t * inputs)..((t + 1) * inputs)], 1, b);
                    Assert.Sequence(alone, batch[(t * outputs)..((t + 1) * outputs)], $"{name} {inputs}×{outputs}, row {t}");
                }
            }
        }
    }

    [Test]
    public static void ThreadsTranslateLikeOne()
    {
        using var tiny = TinyModel.Create();
        var translator = Translator.Load(tiny.Path);
        string[] sentences = ["the film premiere is tonight.", "night", "the premiere", "film film film", "is it tonight?", "tonight the film"];
        var expected = sentences.Select(s => translator.RunModel(s).OutputIds).ToArray();
        var results = new int[sentences.Length * 8][];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => results[i] = translator.RunModel(sentences[i % sentences.Length]).OutputIds);
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Sequence(expected[i % sentences.Length], results[i], $"run {i} on several threads");
        }
    }

    [Test]
    public static void LongSentencesAreTranslatedInParts()
    {
        using var tiny = TinyModel.Create(); // 32 positions
        var translator = Translator.Load(tiny.Path);
        var sentence = string.Join(", ", Enumerable.Repeat("the film premiere is tonight", 12)) + ".";
        Assert.Throws<ArgumentException>(() => translator.RunModel(sentence), "the bare model refuses a sentence that is too long");
        var translation = translator.TranslateSentence(sentence, beams: 1);
        Assert.True(translation.Note?.Contains("parts", StringComparison.Ordinal) == true, $"the note says so: {translation.Note}");
        Assert.True(translation.Target.Length > 0, "and there is a translation");
        var word = string.Concat(Enumerable.Range(0, 200).Select(i => (char)('a' + i * 7 % 26)));
        Assert.True(translator.Tokenizer.Encode(word).Length > translator.Model.Config.MaxPositions, "the word is too long for the model");
        var noSpaces = translator.TranslateText(word, beams: 1);
        Assert.Equal(1, noSpaces.Sentences.Count, "one word too long for the model is cut too");
        Assert.True(noSpaces.Sentences[0].Note is not null, "with a note");
    }

    [Test]
    public static void BigGlossariesStayFast()
    {
        var glossary = new Glossary();
        for (var i = 0; i < 20_000; i++)
        {
            glossary.Add($"The Harbor {i:00000}", i % 2 == 0 ? null : $"Prístav {i:00000}");
        }

        glossary.Add("The Harbor");
        const string sentence = "The Harbor 01234 and The Harbor 00007 open the season, then The Harbor returns and The Harbor 99999 does not exist.";
        var found = glossary.Find(sentence);
        Assert.Sequence(["The Harbor 01234", "The Harbor 00007", "The Harbor", "The Harbor"],
            found.Select(m => sentence.Substring(m.Start, m.Length)), "the longest term at each place");
        Assert.Equal("Prístav 00007", found[1].Entry.Output, "with its translation");

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 2_000; i++)
        {
            glossary.Find(sentence);
        }

        Assert.True(watch.Elapsed.TotalSeconds < 2, $"2000 sentences against 20 000 terms in {watch.Elapsed.TotalSeconds:0.00} s");
    }

    [Test]
    public static void OtherActivationsLoadOrAreRefused()
    {
        using (var relu = TinyModel.Create(activation: "relu"))
        {
            var translator = Translator.Load(relu.Path);
            Assert.True(translator.RunModel("the film premiere is tonight.", beams: 1).OutputIds.Length > 0, "a relu model translates");
        }

        using var gelu = TinyModel.Create(activation: "gelu");
        Assert.Throws<FormatException>(() => Translator.Load(gelu.Path), "an activation IRIS does not have");
        var x = new float[] { -2f, 0f, 3f };
        Kernels.Relu(x);
        Assert.Sequence([0f, 0f, 3f], x, "relu");
    }

    [Test]
    public static void NoBeforeAFullStopIsAWord()
    {
        string[] Cut(string text) => [.. Segmenter.Sentences(text)];
        Assert.Sequence(["She said no.", "Then she left."], Cut("She said no. Then she left."), "\"no\" ends a sentence");
        Assert.Sequence(["See no. 5 on the list."], Cut("See no. 5 on the list."), "\"no.\" before a number");
        Assert.Sequence(["Pozri str. 12 a obr. 3."], Cut("Pozri str. 12 a obr. 3."), "Slovak abbreviations before numbers");
    }
}
