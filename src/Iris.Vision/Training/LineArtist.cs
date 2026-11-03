using System.Runtime.Intrinsics;
using Iris.Vision.Fonts;
using Iris.Vision.Imaging;

namespace Iris.Vision.Training;

/// <summary>
/// Pictures of lines of text as the OCR sees them, drawn with own font code: a line set in a font, then spoiled like
/// a photo or a scan (bolder or thinner ink, a slight slant, uneven light, paper grain, blur, few pixels, JPEG
/// blocks) and cut out the way the line finder cuts lines (the ink with a margin of a quarter of its height, give or
/// take). The same recipe as tools/ocr/render.py, so the C# and the Python learning see the same kind of lines.
/// </summary>
public sealed class LineArtist
{
    private readonly IReadOnlyList<FontFace> _fonts;

    public LineArtist(IReadOnlyList<FontFace> fonts)
    {
        if (fonts.Count == 0)
        {
            throw new ArgumentException("no font to draw with");
        }

        _fonts = fonts;
    }

    public IReadOnlyList<FontFace> Fonts => _fonts;

    /// <summary>
    /// The faces in the font files of these folders (also in subfolders) that have every character of the charset;
    /// a face whose name contains one of the excluded words is left out.
    /// </summary>
    public static List<FontFace> FindFonts(IEnumerable<string> folders, string charset, IEnumerable<string>? exclude = null)
    {
        var excluded = exclude?.ToArray() ?? [];
        var faces = new List<FontFace>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc")
                .Order(StringComparer.Ordinal);
            foreach (var file in files)
            {
                IReadOnlyList<FontFace> loaded;
                try
                {
                    loaded = FontFace.Load(file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                faces.AddRange(loaded.Where(f => !excluded.Any(e => f.Name.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(f.File).Contains(e, StringComparison.OrdinalIgnoreCase)) && f.Covers(charset)));
            }
        }

        return faces;
    }

    /// <summary>A line to learn from: the spoiled picture scaled to the model's height, or null when it does not fit (CTC, width).</summary>
    public TrainingLine? Line(string text, OcrShape shape, Random random, int maxWidth = 1600)
    {
        var picture = Spoiled(text, random);
        var width = Math.Max(8, (int)Math.Round(picture.Width * (double)shape.Height / picture.Height));
        if (width > maxWidth)
        {
            return null;
        }

        var labels = shape.Encode(text);
        var repeats = 0;
        for (var i = 1; i < labels.Length; i++)
        {
            repeats += labels[i] == labels[i - 1] ? 1 : 0;
        }

        // CTC needs a frame for every character and one more between two equal neighbours
        if (labels.Any(l => l < 0) || width / shape.Stride < labels.Length + repeats)
        {
            return null;
        }

        var scaled = picture.Resized(width, shape.Height);
        var ink = new float[scaled.Pixels.Length];
        for (var i = 0; i < ink.Length; i++)
        {
            ink[i] = 1f - scaled.Pixels[i] / 255f;
        }

        return new TrainingLine(ink, width, text, labels);
    }

    /// <summary>A spoiled picture of a line of text, cut out like the line finder does (gray, any size).</summary>
    public GrayImage Spoiled(string text, Random r)
    {
        var font = _fonts[r.Next(_fonts.Count)];
        var size = r.Next(16, 61);
        var paper = r.Next(170, 256);
        var ink = r.Next(0, Math.Min(110, paper - 60) + 1);
        var picture = Typeset(text, font, size, paper, ink);

        if (r.NextDouble() < 0.15)
        {
            picture = picture.Filter3(thicker: r.NextDouble() < 0.5);
        }

        if (r.NextDouble() < 0.5)
        {
            picture = picture.Rotated((r.NextDouble() * 2.4 - 1.2) * Math.PI / 180, paper);
        }

        if (r.NextDouble() < 0.6)
        {
            picture.Shade(r.NextDouble() * 0.3 + 0.7, r.NextDouble() * 0.2 + 0.85, r.NextDouble() * 0.1 + 0.9);
        }

        if (r.NextDouble() < 0.5)
        {
            picture.Noise(r, r.NextDouble() * 9 + 1);
        }

        picture.Clip();
        if (r.NextDouble() < 0.5)
        {
            picture = picture.Blurred((r.NextDouble() * 1.1 + 0.2) * size / 32);
        }

        if (r.NextDouble() < 0.4)
        {
            var factor = r.NextDouble() * 0.45 + 0.25;
            picture = picture.Resampled(Math.Max(1, (int)(picture.Width * factor)), Math.Max(1, (int)(picture.Height * factor)))
                .Resampled(picture.Width, picture.Height);
        }

        if (r.NextDouble() < 0.5)
        {
            picture.Jpeg(r.Next(25, 91));
        }

        return picture.CutOut(r).ToGray();
    }

    /// <summary>The text set in a font of the given pixel size: paper and ink, the ink box with a margin of the size on each side.</summary>
    public static Canvas Typeset(string text, FontFace font, int size, double paper, double ink)
    {
        var scale = size / (float)font.UnitsPerEm;
        var glyphs = new int[text.Length];
        var positions = new float[text.Length];
        float pen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            glyphs[i] = font.GlyphOf(text[i]);
            if (i > 0)
            {
                pen += font.Kerning(glyphs[i - 1], glyphs[i]) * scale;
            }

            positions[i] = pen;
            pen += font.Advance(glyphs[i]) * scale;
        }

        var ascent = Math.Max(font.Ascender, font.UnitsPerEm) * scale;
        var descent = Math.Max(-font.Descender, font.UnitsPerEm / 3) * scale;
        var width = (int)Math.Ceiling(pen + 4 * size);
        var height = (int)Math.Ceiling(ascent + descent + 2 * size);
        var rasterizer = new Rasterizer(width, height);
        for (var i = 0; i < text.Length; i++)
        {
            font.Draw(glyphs[i], rasterizer, 2 * size + positions[i], size + ascent, scale);
        }

        var coverage = rasterizer.Coverage();

        // the box of the ink, then the margin
        int top = height, bottom = -1, left = width, right = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (coverage[y * width + x] > 0.02f)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        if (bottom < 0)
        {
            (top, bottom, left, right) = (0, Math.Min(height - 1, size), 0, Math.Min(width - 1, size));
        }

        var canvas = new Canvas(right - left + 1 + 2 * size, bottom - top + 1 + 2 * size);
        for (var y = 0; y < canvas.Height; y++)
        {
            var sy = top - size + y;
            for (var x = 0; x < canvas.Width; x++)
            {
                var sx = left - size + x;
                var c = sy >= 0 && sy < height && sx >= 0 && sx < width ? coverage[sy * width + sx] : 0;
                canvas.Values[y * canvas.Width + x] = (float)(paper + (ink - paper) * c);
            }
        }

        return canvas;
    }
}

/// <summary>A gray picture in floats (0 – 255) for the spoiling steps.</summary>
public sealed class Canvas
{
    public Canvas(int width, int height, float[]? values = null)
    {
        (Width, Height) = (width, height);
        Values = values ?? new float[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public float[] Values { get; }

    public float At(int x, int y) => Values[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    /// <summary>The darkest (thicker ink) or the lightest (thinner ink) of each 3 × 3 neighbourhood (rows, then columns).</summary>
    public Canvas Filter3(bool thicker)
    {
        var across = new float[Values.Length];
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
            {
                var v = Values[row + x];
                var l = Values[row + Math.Max(0, x - 1)];
                var r = Values[row + Math.Min(Width - 1, x + 1)];
                across[row + x] = thicker ? MathF.Min(v, MathF.Min(l, r)) : MathF.Max(v, MathF.Max(l, r));
            }
        }

        var result = new Canvas(Width, Height);
        for (var y = 0; y < Height; y++)
        {
            int up = Math.Max(0, y - 1) * Width, row = y * Width, down = Math.Min(Height - 1, y + 1) * Width;
            for (var x = 0; x < Width; x++)
            {
                float a = across[up + x], b = across[row + x], c = across[down + x];
                result.Values[row + x] = thicker ? MathF.Min(b, MathF.Min(a, c)) : MathF.Max(b, MathF.Max(a, c));
            }
        }

        return result;
    }

    /// <summary>Turned by the angle (radians, counter-clockwise), grown to hold all of it, the new corners filled with paper.</summary>
    public Canvas Rotated(double angle, float paper)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var width = (int)Math.Ceiling(Math.Abs(Width * cos) + Math.Abs(Height * sin));
        var height = (int)Math.Ceiling(Math.Abs(Width * sin) + Math.Abs(Height * cos));
        var result = new Canvas(width, height);
        double cx = Width / 2.0, cy = Height / 2.0, ncx = width / 2.0, ncy = height / 2.0;
        for (var y = 0; y < height; y++)
        {
            var dy = y + 0.5 - ncy;
            // the source point moves by (cos, sin) for every step to the right
            var sx = cos * (0.5 - ncx) - sin * dy + cx - 0.5;
            var sy = sin * (0.5 - ncx) + cos * dy + cy - 0.5;
            var target = y * width;
            for (var x = 0; x < width; x++, sx += cos, sy += sin)
            {
                if (sx < -0.5 || sy < -0.5 || sx > Width - 0.5 || sy > Height - 0.5)
                {
                    result.Values[target + x] = paper;
                    continue;
                }

                var fx = Math.Clamp(sx, 0, Width - 1);
                var fy = Math.Clamp(sy, 0, Height - 1);
                int x0 = (int)fx, y0 = (int)fy;
                int x1 = x0 + 1 < Width ? x0 + 1 : x0, y1 = y0 + 1 < Height ? y0 + 1 : y0;
                float ax = (float)(fx - x0), ay = (float)(fy - y0);
                int r0 = y0 * Width, r1 = y1 * Width;
                var top = Values[r0 + x0] + (Values[r0 + x1] - Values[r0 + x0]) * ax;
                var bottom = Values[r1 + x0] + (Values[r1 + x1] - Values[r1 + x0]) * ax;
                result.Values[target + x] = top + (bottom - top) * ay;
            }
        }

        return result;
    }

    /// <summary>Uneven light: a gradient from left to right and a lighter one from top to bottom.</summary>
    public void Shade(double left, double right, double top)
    {
        for (var y = 0; y < Height; y++)
        {
            var vertical = top + (1 - top) * (Height == 1 ? 1 : y / (double)(Height - 1));
            for (var x = 0; x < Width; x++)
            {
                var horizontal = left + (right - left) * (Width == 1 ? 0 : x / (double)(Width - 1));
                Values[y * Width + x] *= (float)(horizontal * vertical);
            }
        }
    }

    /// <summary>
    /// Grain of the paper: nearly Gaussian noise, the sum of four uniform values from one 64-bit xorshift draw
    /// (fast – a line has up to half a million pixels).
    /// </summary>
    public void Noise(Random r, double sigma)
    {
        var state = (ulong)r.NextInt64() | 1;
        var scale = (float)(sigma * Math.Sqrt(12.0 / 4) / 65536);
        for (var i = 0; i < Values.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            var sum = (long)(state & 0xFFFF) + (long)((state >> 16) & 0xFFFF) + (long)((state >> 32) & 0xFFFF) + (long)(state >> 48) - 2 * 65535;
            Values[i] += sum * scale;
        }
    }

    /// <summary>Into 0 – 255 whole numbers (as a picture file keeps it).</summary>
    public void Clip()
    {
        for (var i = 0; i < Values.Length; i++)
        {
            Values[i] = MathF.Floor(Math.Clamp(Values[i], 0, 255));
        }
    }

    /// <summary>
    /// A Gaussian blur of the given sigma, made of three box blurs in each direction (what Pillow's GaussianBlur does;
    /// the time does not depend on the radius).
    /// </summary>
    public Canvas Blurred(double sigma)
    {
        if (sigma <= 0.05)
        {
            return this;
        }

        // three boxes whose variances add up to σ²: widths w (and w + 2) around √(12σ²/3 + 1)
        var ideal = Math.Sqrt(12 * sigma * sigma / 3 + 1);
        var lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0)
        {
            lower--;
        }

        var upper = lower + 2;
        var m = (int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4.0 * lower - 4));
        var current = (float[])Values.Clone();
        var other = new float[Values.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            var radius = ((pass < m ? lower : upper) - 1) / 2;
            BoxRows(current, other, radius);
            BoxColumns(other, current, radius);
        }

        for (var i = 0; i < current.Length; i++)
        {
            current[i] = MathF.Round(current[i]);
        }

        return new Canvas(Width, Height, current);
    }

    private void BoxRows(float[] source, float[] target, int radius)
    {
        if (radius <= 0)
        {
            Array.Copy(source, target, source.Length);
            return;
        }

        var scale = 1f / (2 * radius + 1);
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            float sum = 0;
            for (var k = -radius; k <= radius; k++)
            {
                sum += source[row + Math.Clamp(k, 0, Width - 1)];
            }

            for (var x = 0; x < Width; x++)
            {
                target[row + x] = sum * scale;
                sum += source[row + Math.Min(Width - 1, x + radius + 1)] - source[row + Math.Max(0, x - radius)];
            }
        }
    }

    private void BoxColumns(float[] source, float[] target, int radius)
    {
        if (radius <= 0)
        {
            Array.Copy(source, target, source.Length);
            return;
        }

        var scale = 1f / (2 * radius + 1);
        var sums = new float[Width];
        for (var k = -radius; k <= radius; k++)
        {
            var row = Math.Clamp(k, 0, Height - 1) * Width;
            for (var x = 0; x < Width; x++)
            {
                sums[x] += source[row + x];
            }
        }

        for (var y = 0; y < Height; y++)
        {
            int target0 = y * Width, add = Math.Min(Height - 1, y + radius + 1) * Width, remove = Math.Max(0, y - radius) * Width;
            for (var x = 0; x < Width; x++)
            {
                target[target0 + x] = sums[x] * scale;
                sums[x] += source[add + x] - source[remove + x];
            }
        }
    }

    /// <summary>Another size: the mean of the covered area when smaller, bilinear when larger (rows, then columns).</summary>
    public Canvas Resampled(int width, int height)
    {
        var (xStart, xCount, xWeights, xTaps) = Weights(Width, width);
        var (yStart, yCount, yWeights, yTaps) = Weights(Height, height);
        var across = new float[width * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < width; x++)
            {
                float v = 0;
                for (var k = 0; k < xCount[x]; k++)
                {
                    v += xWeights[x * xTaps + k] * Values[row + xStart[x] + k];
                }

                across[y * width + x] = v;
            }
        }

        var result = new Canvas(width, height);
        for (var y = 0; y < height; y++)
        {
            var target = result.Values.AsSpan(y * width, width);
            for (var k = 0; k < yCount[y]; k++)
            {
                var weight = yWeights[y * yTaps + k];
                var source = across.AsSpan((yStart[y] + k) * width, width);
                for (var x = 0; x < width; x++)
                {
                    target[x] += weight * source[x];
                }
            }

            for (var x = 0; x < width; x++)
            {
                target[x] = MathF.Round(target[x]);
            }
        }

        return result;
    }

    // for every target position the source cells it takes and their weights: the covered share of each cell when
    // shrinking, the two neighbours when growing
    private static (int[] Start, int[] Count, float[] Weights, int Taps) Weights(int source, int target)
    {
        var scale = (double)source / target;
        var taps = scale >= 1 ? (int)Math.Ceiling(scale) + 1 : 2;
        var start = new int[target];
        var count = new int[target];
        var weights = new float[target * taps];
        for (var t = 0; t < target; t++)
        {
            if (scale >= 1)
            {
                var from = t * scale;
                var to = Math.Min(source, from + scale);
                var first = (int)from;
                var last = Math.Min(source, (int)Math.Ceiling(to));
                start[t] = first;
                count[t] = Math.Min(taps, last - first);
                double total = 0;
                for (var k = 0; k < count[t]; k++)
                {
                    var part = Math.Min(first + k + 1, to) - Math.Max(first + k, from);
                    weights[t * taps + k] = (float)part;
                    total += part;
                }

                for (var k = 0; k < count[t]; k++)
                {
                    weights[t * taps + k] /= (float)total;
                }
            }
            else
            {
                var position = Math.Clamp((t + 0.5) * scale - 0.5, 0, source - 1);
                var first = Math.Min((int)position, Math.Max(0, source - 2));
                var fraction = (float)(position - first);
                start[t] = first;
                count[t] = source > 1 ? 2 : 1;
                weights[t * taps] = source > 1 ? 1 - fraction : 1;
                weights[t * taps + 1] = fraction;
            }
        }

        return (start, count, weights, taps);
    }

    /// <summary>
    /// What a JPEG of the given quality makes of the picture: blocks of 8 × 8 through the cosine transform, their
    /// frequencies rounded by the standard luminance table (scaled by quality as the IJG library does), and back.
    /// </summary>
    public void Jpeg(int quality)
    {
        var table = new float[64];
        var factor = quality < 50 ? 5000 / quality : 200 - 2 * quality;
        for (var i = 0; i < 64; i++)
        {
            table[i] = Math.Clamp((LuminanceTable[i] * factor + 50) / 100, 1, 255);
        }

        var block = new float[64];
        var frequencies = new float[64];
        for (var by = 0; by < Height; by += 8)
        {
            for (var bx = 0; bx < Width; bx += 8)
            {
                var inside = bx + 8 <= Width && by + 8 <= Height;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        block[y * 8 + x] = (inside ? Values[(by + y) * Width + bx + x] : At(bx + x, by + y)) - 128;
                    }
                }

                Dct(block, frequencies, inverse: false);
                for (var i = 0; i < 64; i++)
                {
                    frequencies[i] = MathF.Round(frequencies[i] / table[i]) * table[i];
                }

                Dct(frequencies, block, inverse: true);
                for (var y = 0; y < 8 && by + y < Height; y++)
                {
                    for (var x = 0; x < 8 && bx + x < Width; x++)
                    {
                        Values[(by + y) * Width + bx + x] = Math.Clamp(MathF.Round(block[y * 8 + x] + 128), 0, 255);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Cut out like the line finder: the box of what is clearly darker than the mean (by 30), with a margin of a
    /// quarter of its height, each side moved a little at random.
    /// </summary>
    public Canvas CutOut(Random r)
    {
        double mean = 0;
        foreach (var v in Values)
        {
            mean += v;
        }

        var limit = (int)(mean / Values.Length) - 30;
        int top = Height, bottom = -1, left = Width, right = -1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (Values[y * Width + x] < limit)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        if (bottom < 0)
        {
            return this;
        }

        bottom++;
        right++;
        var margin = Math.Max(2, (bottom - top) / 4);
        int Jitter() => (int)(margin * (r.NextDouble() * 0.6 - 0.3));
        var x0 = Math.Max(0, left - margin + Jitter());
        var y0 = Math.Max(0, top - margin + Jitter());
        var x1 = Math.Min(Width, right + margin + Jitter());
        var y1 = Math.Min(Height, bottom + margin + Jitter());
        if (x1 <= x0 || y1 <= y0)
        {
            return this;
        }

        var result = new Canvas(x1 - x0, y1 - y0);
        for (var y = 0; y < result.Height; y++)
        {
            Array.Copy(Values, (y0 + y) * Width + x0, result.Values, y * result.Width, result.Width);
        }

        return result;
    }

    public GrayImage ToGray()
    {
        var gray = new GrayImage(Width, Height);
        for (var i = 0; i < Values.Length; i++)
        {
            gray.Pixels[i] = (byte)Math.Clamp(MathF.Round(Values[i]), 0, 255);
        }

        return gray;
    }

    private static readonly int[] LuminanceTable =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly float[] Cosines = MakeCosines();

    private static float[] MakeCosines()
    {
        var c = new float[64];
        for (var u = 0; u < 8; u++)
        {
            var scale = u == 0 ? Math.Sqrt(0.125) : 0.5;
            for (var x = 0; x < 8; x++)
            {
                c[u * 8 + x] = (float)(scale * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            }
        }

        return c;
    }

    // the orthonormal 2-D DCT-II of an 8 × 8 block (inverse: DCT-III): every row and then every column as eight
    // fused multiply-adds of whole rows of eight
    private static void Dct(float[] input, float[] output, bool inverse)
    {
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];
        for (var y = 0; y < 8; y++)
        {
            var sum = Vector256<float>.Zero;
            for (var x = 0; x < 8; x++)
            {
                // forward: Σx in[y,x] · C[u,x] over u; inverse: Σu in[y,u] · C[u,x] over x
                sum += Vector256.Create(input[y * 8 + x]) * (inverse ? CosineRows[x] : CosineColumns[x]);
            }

            rows[y] = sum;
        }

        for (var v = 0; v < 8; v++)
        {
            var sum = Vector256<float>.Zero;
            for (var y = 0; y < 8; y++)
            {
                // forward: out[v] = Σy C[v,y] · rows[y]; inverse: out[y'] = Σv C[v,y'] · rows[v]
                sum += Vector256.Create(inverse ? Cosines[y * 8 + v] : Cosines[v * 8 + y]) * rows[y];
            }

            sum.CopyTo(output, v * 8);
        }
    }

    // CosineColumns[x] = (C[0,x] … C[7,x]); CosineRows[u] = (C[u,0] … C[u,7])
    private static readonly Vector256<float>[] CosineColumns = [.. Enumerable.Range(0, 8).Select(x => Vector256.Create(Enumerable.Range(0, 8).Select(u => Cosines[u * 8 + x]).ToArray()))];

    private static readonly Vector256<float>[] CosineRows = [.. Enumerable.Range(0, 8).Select(u => Vector256.Create(Cosines.AsSpan(u * 8, 8)))];

    private float Bilinear(double x, double y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double fx = x - x0, fy = y - y0;
        var top = Values[y0 * Width + x0] * (1 - fx) + Values[y0 * Width + x1] * fx;
        var bottom = Values[y1 * Width + x0] * (1 - fx) + Values[y1 * Width + x1] * fx;
        return (float)(top * (1 - fy) + bottom * fy);
    }

    private double AreaMean(double x0, double y0, double w, double h)
    {
        double sum = 0, area = 0;
        for (var y = (int)y0; y < Math.Min(Height, (int)Math.Ceiling(y0 + h)); y++)
        {
            var hy = Math.Min(y + 1, y0 + h) - Math.Max(y, y0);
            for (var x = (int)x0; x < Math.Min(Width, (int)Math.Ceiling(x0 + w)); x++)
            {
                var part = (Math.Min(x + 1, x0 + w) - Math.Max(x, x0)) * hy;
                sum += part * Values[y * Width + x];
                area += part;
            }
        }

        return area > 0 ? sum / area : 0;
    }
}
