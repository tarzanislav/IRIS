using Iris.Evaluation;
using Iris.Model;
using Iris.Numerics;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>F2: sentences, int8 weights and their kernels, chrF.</summary>
public static class SpeedAndQualityTests
{
    [Test]
    public static void TextIsCutIntoSentences()
    {
        string[] Cut(string text) => [.. Segmenter.Sentences(text)];

        Assert.Sequence(["The premiere is tonight.", "Tickets cost 9.50 euros!", "Will you come?"],
            Cut("The premiere is tonight. Tickets cost 9.50 euros! Will you come?"), "simple sentences");
        Assert.Sequence(["Mr. Hale and Dr. Velin met in the U.S. in 2019.", "They talked."],
            Cut("Mr. Hale and Dr. Velin met in the U.S. in 2019. They talked."), "abbreviations");
        Assert.Sequence(["\"I wanted silence,\" she said.", "\"It worked.\"", "The end."],
            Cut("\"I wanted silence,\" she said. \"It worked.\" The end."), "quotes");
        Assert.Sequence(["Written by J. K. Morrow, it sold well.", "Napr. tento film."],
            Cut("Written by J. K. Morrow, it sold well. Napr. tento film."), "initials and a Slovak abbreviation at the start");
        Assert.Sequence(["Title without a full stop", "First line.", "Second... and more"],
            Cut("Title without a full stop\n\nFirst line.\nSecond... and more"), "lines");
        Assert.Sequence(["It ends at 9 p.m. today."], Cut("It ends at 9 p.m. today."), "a.m./p.m. inside a sentence");

        const string text = "  Title\n\nOne. Two!  Three?\n";
        var segments = Segmenter.Split(text);
        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)), "the segments give the text back");
        Assert.Equal("  TITLE\n\nONE. TWO!  THREE?\n",
            Segmenter.Join(segments, [.. segments.Where(s => s.IsSentence).Select(s => s.Text.ToUpperInvariant())]), "layout is kept");
        Assert.Sequence([], Cut("   \n "), "only whitespace");
    }

    [Test]
    public static void KernelsAgreeWithPlainArithmetic()
    {
        var random = new Random(3);
        float[] Values(int count) => [.. Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 2 - 1))];

        // sizes that are not multiples of the vector width or of four, rows from one to more than a block of four
        foreach (var (inputs, outputs) in new[] { (37, 13), (64, 8), (512, 61), (7, 3) })
        {
            foreach (var n in new[] { 1, 2, 3, 4, 5, 9 })
            {
                var x = Values(n * inputs);
                var w = Values(inputs * outputs);
                var b = Values(outputs);
                var floats = Kernels.Linear(x, n, w, b, inputs, outputs);
                var int8 = Matrix.FromFloats(w, outputs, inputs).ToInt8();
                var quantized = int8.Linear(x, n, b);
                var dequantized = new float[inputs * outputs];
                for (var o = 0; o < outputs; o++)
                {
                    int8.Row(o, dequantized.AsSpan(o * inputs, inputs));
                }

                for (var t = 0; t < n; t++)
                {
                    for (var o = 0; o < outputs; o++)
                    {
                        double exact = b[o], exact8 = b[o];
                        for (var i = 0; i < inputs; i++)
                        {
                            exact += (double)x[t * inputs + i] * w[o * inputs + i];
                            exact8 += (double)x[t * inputs + i] * dequantized[o * inputs + i];
                        }

                        Assert.Near(exact, floats[t * outputs + o], 1e-4, $"float32 {inputs}×{outputs}, n {n}");
                        Assert.Near(exact8, quantized[t * outputs + o], 1e-4, $"int8 {inputs}×{outputs}, n {n}");
                    }
                }
            }
        }
    }

    [Test]
    public static void QuantizedRowsStayClose()
    {
        float[] w = [0.5f, -1f, 0.25f, 0f, 2f, -2f, 1e-3f, 0f];
        var (values, scales) = Kernels.QuantizeRows(w, 2, 4);
        Assert.Sequence([64, -127, 32, 0, 127, -127, 0, 0], values.Select(v => (int)v), "values");
        Assert.Near(1 / 127.0, scales[0], 1e-7, "scale = max / 127");
        var row = new float[4];
        Matrix.FromInt8(values, scales, 2, 4).Row(0, row);
        Assert.Near(0.5, row[0], 1 / 254.0, "back within half a step");
        Assert.Sequence([0f, 0, 0, 0], Kernels.QuantizeRows(new float[4], 1, 4).Values.Select(v => (float)v), "a row of zeros");
    }

    [Test]
    public static void Int8ModelFileTranslatesLikeQuantizingWhenRead()
    {
        using var tiny = TinyModel.Create(d: 32);
        var int8Path = tiny.Path + ".int8";
        try
        {
            var source = ModelFile.Load(tiny.Path);
            Quantizer.Write(source, int8Path);
            var file = ModelFile.Load(int8Path);
            Assert.Equal("i8", file.DTypeOf("embed"), "token vectors in int8");
            Assert.Equal("i8", file.DTypeOf("dec.0.ffn.1.w"), "weights in int8");
            Assert.Equal("f32", file.DTypeOf("dec.0.ffn.1.b"), "biases stay float");
            Assert.True(file.Provenance["quantized"] is not null && file.Provenance["source"] is not null, "provenance is kept and says so");
            Assert.True(file.Tensors.Sum(t => t.Length) < source.Tensors.Sum(t => t.Length) * 0.35, "weights take about a quarter");

            var stored = Translator.Load(int8Path);
            var whenRead = Translator.Load(tiny.Path, int8: true);
            Assert.True(stored.Model.IsInt8 && whenRead.Model.IsInt8, "both int8");
            foreach (var text in new[] { "the film premiere is tonight.", "night", "" })
            {
                Assert.Sequence(whenRead.Translate(text).OutputIds, stored.Translate(text).OutputIds, $"the same translation of '{text}'");
            }
        }
        finally
        {
            File.Delete(int8Path);
        }
    }

    [Test]
    public static void ChrfIsTheSameAsSacreBleu()
    {
        // values of sacreBLEU 2.6.0 (CHRF() defaults), computed outside IRIS
        (string, string, double)[] cases =
        [
            ("Premiéra je dnes večer.", "Premiéra bude dnes večer.", 72.272257),
            ("Kritici chválili hudbu.", "Kritici chválili hudbu.", 100.0),
            ("Druhá séria bude mať osem epizód.", "Druhá sezóna bude mať osem dielov.", 52.861863),
            ("", "Kde je kino?", 0.0),
            ("ok", "a", 0.0),
            ("🎬 Film 漢字", "🎬 film 漢字", 49.285714),
        ];
        foreach (var (hypothesis, reference, expected) in cases)
        {
            Assert.Near(expected, Chrf.Score(Chrf.Statistics(hypothesis, reference)), 1e-5, $"chrF of '{hypothesis}'");
        }

        Assert.Near(65.136014, Chrf.Corpus(cases.Select(c => (c.Item1, c.Item2))), 1e-5, "chrF of the whole set");
    }
}
