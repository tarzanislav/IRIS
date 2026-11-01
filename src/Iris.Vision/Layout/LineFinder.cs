using Iris.Vision.Imaging;

namespace Iris.Vision.Layout;

/// <summary>A line of text on a straightened page: where it lies and its picture (gray, cut from the page).</summary>
public sealed record TextLine(int X, int Y, int Width, int Height, GrayImage Image);

/// <summary>
/// Finds the lines of text of a straight page with one column: the rows that carry ink form bands, a band is a line;
/// bands too thin to be text (dust, a rule) are dropped, and a band that holds two lines touching each other is cut
/// at its thinnest row. Each line is cut out with a margin, so accents above and descenders below stay with it.
/// </summary>
public static class LineFinder
{
    public static IReadOnlyList<TextLine> Find(GrayImage page, bool[] ink)
    {
        int w = page.Width, h = page.Height;
        var rows = new int[h];
        for (var y = 0; y < h; y++)
        {
            var count = 0;
            for (var x = 0; x < w; x++)
            {
                count += ink[y * w + x] ? 1 : 0;
            }

            rows[y] = count;
        }

        // a row is part of a line when it has a little ink (a few accents or dots alone do not make a line)
        var busiest = rows.Max();
        if (busiest == 0)
        {
            return [];
        }

        var threshold = Math.Max(2, busiest / 40);
        var bands = new List<(int Top, int Bottom)>();
        for (var y = 0; y < h;)
        {
            if (rows[y] < threshold)
            {
                y++;
                continue;
            }

            var top = y;
            while (y < h && rows[y] >= threshold)
            {
                y++;
            }

            bands.Add((top, y));
        }

        if (bands.Count == 0)
        {
            return [];
        }

        // join a band with its neighbour when the gap is tiny (the dot of an i, a háček above a capital)
        var heights = bands.Select(b => b.Bottom - b.Top).Order().ToList();
        var typical = heights[heights.Count / 2];
        var joined = new List<(int Top, int Bottom)> { bands[0] };
        foreach (var band in bands.Skip(1))
        {
            var last = joined[^1];
            var small = Math.Min(band.Bottom - band.Top, last.Bottom - last.Top) < typical / 2;
            if (small && band.Top - last.Bottom < typical / 3 && band.Bottom - last.Top < typical * 1.6)
            {
                joined[^1] = (last.Top, band.Bottom);
            }
            else
            {
                joined.Add(band);
            }
        }

        // a band twice as tall as usual holds two lines: cut it at its thinnest row near the middle
        var lines = new List<(int Top, int Bottom)>();
        foreach (var band in joined)
        {
            Split(band, rows, typical, lines);
        }

        var result = new List<TextLine>();
        foreach (var (top, bottom) in lines)
        {
            var height = bottom - top;
            if (height < Math.Max(4, typical / 3))
            {
                continue; // a rule or dust
            }

            int left = w, right = -1;
            for (var y = top; y < bottom; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (ink[y * w + x])
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
            }

            if (right < left)
            {
                continue;
            }

            var margin = Math.Max(2, height / 4);
            int x0 = Math.Max(0, left - margin), y0 = Math.Max(0, top - margin);
            int x1 = Math.Min(w, right + 1 + margin), y1 = Math.Min(h, bottom + margin);
            result.Add(new TextLine(x0, y0, x1 - x0, y1 - y0, page.Crop(x0, y0, x1 - x0, y1 - y0)));
        }

        return result;
    }

    private static void Split((int Top, int Bottom) band, int[] rows, int typical, List<(int Top, int Bottom)> lines)
    {
        var height = band.Bottom - band.Top;
        if (height < typical * 1.7 || typical < 4)
        {
            lines.Add(band);
            return;
        }

        var parts = (int)Math.Round((double)height / typical);
        var start = band.Top;
        for (var p = 1; p < parts; p++)
        {
            var guess = band.Top + height * p / parts;
            var from = Math.Max(start + typical / 2, guess - typical / 3);
            var to = Math.Min(band.Bottom - typical / 2, guess + typical / 3);
            var cut = guess;
            for (var y = from; y < to; y++)
            {
                if (rows[y] < rows[cut])
                {
                    cut = y;
                }
            }

            lines.Add((start, cut));
            start = cut;
        }

        lines.Add((start, band.Bottom));
    }
}
