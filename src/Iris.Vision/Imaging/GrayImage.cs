namespace Iris.Vision.Imaging;

/// <summary>
/// An image of gray values, one byte per pixel (0 black … 255 white), row after row. Everything OCR does works on
/// this: colours carry nothing for printed text, and one byte per pixel keeps a page photo small.
/// </summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > MaxPixels)
        {
            throw new ArgumentException($"an image of {width} × {height} is larger than IRIS reads ({MaxPixels / 1_000_000} megapixels)");
        }

        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height];
        ArgumentOutOfRangeException.ThrowIfNotEqual(Pixels.Length, width * height);
    }

    /// <summary>The largest image read: 200 megapixels (a page scanned at 1200 dpi is about 140).</summary>
    public const long MaxPixels = 200_000_000;

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public Span<byte> Row(int y) => Pixels.AsSpan(y * Width, Width);

    /// <summary>The gray of a colour: 0.299 R + 0.587 G + 0.114 B (ITU-R BT.601), in integers, rounded.</summary>
    public static byte Luma(int r, int g, int b) => (byte)((r * 19595 + g * 38470 + b * 7471 + 32768) >> 16);

    /// <summary>A part of the image (clipped to it).</summary>
    public GrayImage Crop(int x, int y, int width, int height)
    {
        var left = Math.Clamp(x, 0, Width - 1);
        var top = Math.Clamp(y, 0, Height - 1);
        var w = Math.Clamp(width - (left - x), 1, Width - left);
        var h = Math.Clamp(height - (top - y), 1, Height - top);
        var part = new GrayImage(w, h);
        for (var r = 0; r < h; r++)
        {
            Pixels.AsSpan((top + r) * Width + left, w).CopyTo(part.Row(r));
        }

        return part;
    }

    /// <summary>The image turned by a multiple of 90° clockwise, or mirrored (the eight EXIF orientations, 1–8).</summary>
    public GrayImage Oriented(int exifOrientation)
    {
        if (exifOrientation is < 2 or > 8)
        {
            return this;
        }

        var swap = exifOrientation >= 5;
        int w = swap ? Height : Width, h = swap ? Width : Height;
        var result = new GrayImage(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // where the shown pixel (x, y) lies in the stored image
                var (sx, sy) = exifOrientation switch
                {
                    2 => (Width - 1 - x, y),
                    3 => (Width - 1 - x, Height - 1 - y),
                    4 => (x, Height - 1 - y),
                    5 => (y, x),
                    6 => (y, Height - 1 - x),
                    7 => (Width - 1 - y, Height - 1 - x),
                    _ => (Width - 1 - y, x),
                };
                result.Pixels[y * w + x] = Pixels[sy * Width + sx];
            }
        }

        return result;
    }

    /// <summary>
    /// The image scaled to a new size by the mean of the pixels each new pixel covers (shrinking) or bilinearly
    /// (growing) – what text needs: no lost thin strokes when shrinking, no blocks when growing.
    /// </summary>
    public GrayImage Resized(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var result = new GrayImage(width, height);
        double sx = (double)Width / width, sy = (double)Height / height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result.Pixels[y * width + x] = sx >= 1 && sy >= 1 ? AreaMean(x * sx, y * sy, sx, sy) : Bilinear((x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5);
            }
        }

        return result;
    }

    private byte AreaMean(double x0, double y0, double w, double h)
    {
        double sum = 0, area = 0;
        for (var y = (int)y0; y < Math.Min(Height, (int)Math.Ceiling(y0 + h)); y++)
        {
            var hy = Math.Min(y + 1, y0 + h) - Math.Max(y, y0);
            for (var x = (int)x0; x < Math.Min(Width, (int)Math.Ceiling(x0 + w)); x++)
            {
                var part = (Math.Min(x + 1, x0 + w) - Math.Max(x, x0)) * hy;
                sum += part * Pixels[y * Width + x];
                area += part;
            }
        }

        return (byte)Math.Clamp(Math.Round(sum / area), 0, 255);
    }

    private byte Bilinear(double x, double y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double fx = x - x0, fy = y - y0;
        var top = this[x0, y0] * (1 - fx) + this[x1, y0] * fx;
        var bottom = this[x0, y1] * (1 - fx) + this[x1, y1] * fx;
        return (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
    }
}
