using System.Text;
using System.Text.Json.Nodes;

namespace Iris.Vision.Training;

/// <summary>One convolution block of the line reader: 3 × 3 convolution, batch normalization, ReLU, maybe max-pooling.</summary>
public readonly record struct OcrBlock(int Channels, int PoolHeight, int PoolWidth)
{
    public bool Pools => PoolHeight > 1 || PoolWidth > 1;
}

/// <summary>
/// The shape of the IRIS line reader (the same as tools/ocr/model.py): the convolution blocks, the collapsing
/// convolution to one row of features, the bidirectional LSTM layers and the characters it reads.
/// </summary>
public sealed record OcrShape(int Height, OcrBlock[] Blocks, int Features, int Hidden, int Layers, string Charset)
{
    /// <summary>
    /// The characters of the IRIS OCR (Slovak, Czech, English and common signs) – the same list, in the same order, as
    /// tools/ocr/charset.py; class k + 1 is Charset[k], class 0 is the CTC blank.
    /// </summary>
    public const string DefaultCharset =
        "abcdefghijklmnopqrstuvwxyz" + "áäčďéěíĺľňóôöŕřšťúůüýž" + "ëß" +
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ" + "ÁÄČĎÉĚÍĹĽŇÓÔÖŔŘŠŤÚŮÜÝŽ" + "Ë" +
        "0123456789" +
        " .,;:!?-–—'\"„“”‚‘’()[]/\\%&*+=<>@#$€£§°…·•_|~^";

    /// <summary>The reader as IRIS has it: 32 rows, blocks 32, 64, 128, 128, 192, 192, features 256, 2 × BiLSTM 192.</summary>
    public static OcrShape Default { get; } = new(32,
        [new(32, 2, 2), new(64, 2, 2), new(128, 1, 1), new(128, 2, 1), new(192, 1, 1), new(192, 2, 1)],
        256, 192, 2, DefaultCharset);

    public int Classes => Charset.Length + 1;

    /// <summary>Rows left after the blocks (the height of the collapsing convolution).</summary>
    public int Rows => Blocks.Aggregate(Height, (h, b) => h / b.PoolHeight);

    /// <summary>How many columns of the picture make one frame (column step) of the reader.</summary>
    public int Stride => Blocks.Aggregate(1, (w, b) => w * b.PoolWidth);

    /// <summary>The configuration as the model files keep it (also "iris-ocr", so the C# reader reads it).</summary>
    public JsonObject ToJson()
    {
        var blocks = new JsonArray();
        foreach (var b in Blocks)
        {
            blocks.Add((JsonNode)new JsonObject
            {
                ["channels"] = b.Channels,
                ["pool"] = b.Pools ? new JsonArray(b.PoolHeight, b.PoolWidth) : null,
            });
        }

        return new JsonObject
        {
            ["height"] = Height,
            ["blocks"] = blocks,
            ["features"] = Features,
            ["hidden"] = Hidden,
            ["layers"] = Layers,
            ["charset"] = Charset,
            ["blank"] = 0,
            ["input"] = "ink = 1 − gray / 255, height 32, width in proportion",
        };
    }

    public static OcrShape FromJson(JsonObject config)
    {
        var blocks = config["blocks"]!.AsArray().Select(b =>
        {
            var pool = b!["pool"] as JsonArray;
            return new OcrBlock(b["channels"]!.GetValue<int>(), pool is null ? 1 : pool[0]!.GetValue<int>(), pool is null ? 1 : pool[1]!.GetValue<int>());
        }).ToArray();
        return new OcrShape(config["height"]!.GetValue<int>(), blocks, config["features"]!.GetValue<int>(), config["hidden"]!.GetValue<int>(),
            config["layers"]!.GetValue<int>(), config["charset"]!.GetValue<string>());
    }

    /// <summary>The class of every character of the text (−1 when it is not in the charset).</summary>
    public int[] Encode(string text)
    {
        var labels = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var index = Charset.IndexOf(text[i], StringComparison.Ordinal);
            labels[i] = index < 0 ? -1 : index + 1;
        }

        return labels;
    }

    /// <summary>
    /// The text with only charset characters and single spaces – the same as clean() in tools/ocr/charset.py
    /// (special spaces and dashes written the usual way, a soft hyphen dropped, other characters dropped).
    /// </summary>
    public string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = true;
        foreach (var raw in text)
        {
            var c = raw switch
            {
                ' ' or ' ' or ' ' or '\t' => ' ',
                '−' or '‐' or '‑' => '-',
                '­' => '\0',
                '«' => '„',
                '»' => '“',
                '″' => '"',
                '′' => '\'',
                _ => raw,
            };
            if (c == '\0' || Charset.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            if (c == ' ')
            {
                if (!space)
                {
                    builder.Append(' ');
                }

                space = true;
                continue;
            }

            builder.Append(c);
            space = false;
        }

        return builder.ToString().TrimEnd(' ');
    }
}
