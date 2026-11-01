using Iris.Vision.Imaging;

namespace Iris.Vision.Layout;

/// <summary>
/// Turns a photo or scan of a page into something to read: ink and paper told apart even under uneven light (Sauvola
/// thresholds from a window around each pixel, computed with integral images), the slant of the lines measured and
/// the page turned straight.
/// </summary>
public static class PageCleaner
{
    /// <summary>
    /// Ink (true) and paper (false) of every pixel. Each pixel is compared with mean · (1 + k · (σ / 128 − 1)) of
    /// a square window around it: a shadow lowers the mean of its window, so the threshold follows the light.
    /// </summary>
    public static bool[] Binarize(GrayImage image, int window = 0, double k = 0.3)
    {
        int w = image.Width, h = image.Height;
        if (window <= 0)
        {
            // about a sixtieth of the page: wider than a letter, narrower than a shadow
            window = Math.Clamp(Math.Max(w, h) / 60, 15, 101);
        }

        var radius = window / 2;
        var sum = new long[(w + 1) * (h + 1)];
        var squares = new double[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long rowSum = 0;
            double rowSquares = 0;
            for (var x = 0; x < w; x++)
            {
                var v = image.Pixels[y * w + x];
                rowSum += v;
                rowSquares += v * v;
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + rowSum;
                squares[(y + 1) * (w + 1) + x + 1] = squares[y * (w + 1) + x + 1] + rowSquares;
            }
        }

        var ink = new bool[w * h];
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h, y + radius + 1);
            for (var x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w, x + radius + 1);
                double n = (x1 - x0) * (y1 - y0);
                double s = sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0];
                var q = squares[y1 * (w + 1) + x1] - squares[y0 * (w + 1) + x1] - squares[y1 * (w + 1) + x0] + squares[y0 * (w + 1) + x0];
                var mean = s / n;
                var deviation = Math.Sqrt(Math.Max(0, q / n - mean * mean));
                var threshold = mean * (1 + k * (deviation / 128 - 1));
                // a flat window (paper only) has no ink even where it is a little darker
                ink[y * w + x] = image.Pixels[y * w + x] < threshold && deviation > 8;
            }
        });
        return ink;
    }

    /// <summary>
    /// The slant of the text lines in degrees (positive = the lines rise to the right; the page is to be turned by
    /// minus this). The ink is projected on the vertical at each candidate angle: at the right angle the lines and
    /// the gaps between them are sharpest, so the sum of squared row counts is largest. Coarse steps, then fine.
    /// </summary>
    public static double Skew(bool[] ink, int width, int height, double limit = 10)
    {
        // up to 300 000 ink pixels are enough to measure; take every n-th
        var count = ink.Count(b => b);
        if (count < 50)
        {
            return 0;
        }

        var step = Math.Max(1, count / 300_000);
        var points = new List<(int X, int Y)>(Math.Min(count, 300_000) + 1);
        var seen = 0;
        for (var i = 0; i < ink.Length; i++)
        {
            if (ink[i] && seen++ % step == 0)
            {
                points.Add((i % width, i / width));
            }
        }

        double Score(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            double sin = Math.Sin(radians), cos = Math.Cos(radians);
            var offset = width * Math.Abs(sin);
            var bins = new int[(int)(height + 2 * offset) + 2];
            foreach (var (x, y) in points)
            {
                // the row the point falls on when the page is turned by −degrees
                bins[(int)(y * cos + x * sin + offset)]++;
            }

            double score = 0;
            foreach (var b in bins)
            {
                score += (double)b * b;
            }

            return score;
        }

        var best = 0.0;
        foreach (var span in new[] { (Step: 0.5, Range: limit), (Step: 0.1, Range: 0.5), (Step: 0.02, Range: 0.1) })
        {
            var center = best;
            var top = double.NegativeInfinity;
            for (var a = center - span.Range; a <= center + span.Range + 1e-9; a += span.Step)
            {
                var score = Score(a);
                if (score > top)
                {
                    (top, best) = (score, a);
                }
            }
        }

        return Math.Round(best, 2);
    }

    /// <summary>The image turned by an angle in degrees (clockwise for positive: Rotate(image, −skew) makes rising lines level), bilinear, the corners white.</summary>
    public static GrayImage Rotate(GrayImage image, double degrees)
    {
        if (Math.Abs(degrees) < 0.01)
        {
            return image;
        }

        var radians = degrees * Math.PI / 180;
        double sin = Math.Sin(radians), cos = Math.Cos(radians);
        int w = image.Width, h = image.Height;
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        var result = new GrayImage(w, h);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                // the source of (x, y): the inverse turn
                var sx = cos * (x - cx) - sin * (y - cy) + cx;
                var sy = sin * (x - cx) + cos * (y - cy) + cy;
                if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1)
                {
                    result.Pixels[y * w + x] = 255;
                    continue;
                }

                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                double fx = sx - x0, fy = sy - y0;
                var top = image[x0, y0] * (1 - fx) + image[x1, y0] * fx;
                var bottom = image[x0, y1] * (1 - fx) + image[x1, y1] * fx;
                result.Pixels[y * w + x] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
            }
        });
        return result;
    }
}
