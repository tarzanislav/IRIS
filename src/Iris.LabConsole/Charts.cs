using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Iris.LabConsole;

/// <summary>A line of a chart: a name, a color and its points (time, value).</summary>
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<(DateTime Time, double Value)> Points);

/// <summary>
/// A time chart drawn by hand (no chart library): soft grid, values on the left, times below, one line per series
/// with its legend, an empty chart says so. The range of values comes from the data unless fixed (0 – 100 for %).
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private const double Left = 46;
    private const double Bottom = 26;
    private const double Top = 54;
    private const double Right = 12;

    public string Title { get; set; } = "";

    public string Unit { get; set; } = "";

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    /// <summary>The x axis counts versions (rounds) instead of time.</summary>
    public bool ByNumber { get; set; }

    public IReadOnlyList<ChartSeries> Series { get; set; } = [];

    public IReadOnlyList<(string Name, Color Color, IReadOnlyList<(double X, double Value)> Points)> NumberSeries { get; set; } = [];

    /// <summary>A shaded band (lowest – highest of all computers at each time), drawn under the lines.</summary>
    public IReadOnlyList<(DateTime Time, double Low, double High)> Band { get; set; } = [];

    public Color BandColor { get; set; } = Color.FromArgb(60, 124, 108, 242);

    public void Refresh() => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : Math.Min(availableSize.Height, 260));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var text = Brush("Text", Colors.Black);
        var muted = Brush("TextMuted", Colors.Gray);
        var grid = new Pen(Brush("Border", Colors.LightGray), 1);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        dc.DrawText(Format(Title, 14, text, bold: true), new Point(4, 4));

        var lines = ByNumber
            ? NumberSeries.Select(s => (s.Name, s.Color, Points: s.Points.Select(p => (p.X, p.Value)).ToList())).ToList()
            : Series.Select(s => (s.Name, s.Color, Points: s.Points.Select(p => ((double)p.Time.Ticks, p.Value)).ToList())).ToList();
        var all = lines.SelectMany(l => l.Points).Where(p => double.IsFinite(p.Item2))
            .Concat(Band.SelectMany(b => new[] { ((double)b.Time.Ticks, b.Low), ((double)b.Time.Ticks, b.High) })).ToList();
        var plot = new Rect(Left, Top, Math.Max(10, width - Left - Right), Math.Max(10, height - Top - Bottom));
        if (all.Count == 0)
        {
            dc.DrawText(Format("zatiaľ žiadne údaje", 12, muted), new Point(plot.Left + 8, plot.Top + plot.Height / 2 - 8));
            return;
        }

        double xMin = all.Min(p => p.Item1), xMax = all.Max(p => p.Item1);
        double yMin = Minimum ?? Math.Min(0, all.Min(p => p.Item2)), yMax = Maximum ?? Nice(all.Max(p => p.Item2));
        if (xMax <= xMin)
        {
            xMax = xMin + 1;
        }

        if (yMax <= yMin)
        {
            yMax = yMin + 1;
        }

        double X(double x) => plot.Left + (x - xMin) / (xMax - xMin) * plot.Width;
        double Y(double y) => plot.Bottom - (Math.Clamp(y, yMin, yMax) - yMin) / (yMax - yMin) * plot.Height;

        for (var i = 0; i <= 4; i++)
        {
            var value = yMin + (yMax - yMin) * i / 4;
            var y = Y(value);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var digits = yMax - yMin >= 20 ? "0" : yMax - yMin >= 2 ? "0.0" : yMax - yMin >= 0.2 ? "0.00" : "0.000";
            var label = Format(value.ToString(digits, CultureInfo.InvariantCulture) + Unit, 11, muted);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        var steps = Math.Max(2, (int)(plot.Width / 110));
        for (var i = 0; i <= steps; i++)
        {
            var x = xMin + (xMax - xMin) * i / steps;
            var label = ByNumber
                ? Format(Math.Round(x).ToString(CultureInfo.InvariantCulture), 11, muted)
                : Format(new DateTime((long)x).ToString(xMax - xMin > TimeSpan.FromDays(1.5).Ticks ? "d.M. HH:mm" : "HH:mm", CultureInfo.InvariantCulture), 11, muted);
            var px = Math.Clamp(X(x) - label.Width / 2, plot.Left - 20, plot.Right - label.Width);
            dc.DrawText(label, new Point(px, plot.Bottom + 6));
        }

        if (Band.Count > 1)
        {
            var band = Band.OrderBy(b => b.Time).ToList();
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(X(band[0].Time.Ticks), Y(band[0].High)), true, true);
                g.PolyLineTo([.. band.Skip(1).Select(b => new Point(X(b.Time.Ticks), Y(b.High))), .. Enumerable.Reverse(band).Select(b => new Point(X(b.Time.Ticks), Y(b.Low)))], true, true);
            }

            area.Freeze();
            dc.DrawGeometry(new SolidColorBrush(BandColor), null, area);
        }

        foreach (var (_, color, points) in lines)
        {
            var visible = points.Where(p => double.IsFinite(p.Item2)).OrderBy(p => p.Item1).ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            var pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(visible[0].Item1), Y(visible[0].Item2)), false, false);
                g.PolyLineTo([.. visible.Skip(1).Select(p => new Point(X(p.Item1), Y(p.Item2)))], true, true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            if (visible.Count == 1)
            {
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(X(visible[0].Item1), Y(visible[0].Item2)), 3, 3);
            }
        }

        // the legend in a line under the title, as long as it fits
        var lx = 6.0;
        foreach (var (name, color, _) in lines.Take(24))
        {
            var label = Format(name, 11, text);
            if (lx + label.Width + 22 > width)
            {
                break;
            }

            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(lx, 31, 10, 10), 3, 3);
            dc.DrawText(label, new Point(lx + 14, 28));
            lx += label.Width + 26;
        }

        if (Band.Count > 1 && lx + 160 < width)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(BandColor), null, new Rect(lx, 31, 10, 10), 3, 3);
            dc.DrawText(Format("rozpätie počítačov", 11, text), new Point(lx + 14, 28));
        }
    }

    // a round upper end of the axis (1, 2, 2.5, 5 × 10ⁿ)
    internal static double Nice(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (step * power >= value)
            {
                return step * power;
            }
        }

        return 10 * power;
    }

    internal FormattedText Format(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    internal Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>
/// A map of hours: a row per computer, a column per hour, each cell colored from mint (low) through peach to red
/// (high) – where the classroom got warm or busy at a glance.
/// </summary>
public sealed class HeatMap : FrameworkElement
{
    public string Title { get; set; } = "";

    public string Unit { get; set; } = "";

    public double Low { get; set; }

    public double High { get; set; } = 100;

    /// <summary>Rows: a computer and its values per column (NaN = no data); column labels.</summary>
    public IReadOnlyList<(string Name, double[] Values)> Rows { get; set; } = [];

    public IReadOnlyList<string> Columns { get; set; } = [];

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, 40 + 22 * Math.Max(3, Rows.Count) + 24);

    protected override void OnRender(DrawingContext dc)
    {
        var text = Brush("Text", Colors.Black);
        var muted = Brush("TextMuted", Colors.Gray);
        dc.DrawText(Label(Title, 14, text, true), new Point(4, 4));
        if (Rows.Count == 0 || Columns.Count == 0)
        {
            dc.DrawText(Label("zatiaľ žiadne údaje", 12, muted), new Point(60, 40));
            return;
        }

        const double Name = 90, Top = 34, Row = 22;
        var cell = Math.Max(6, (ActualWidth - Name - 8) / Columns.Count);
        for (var r = 0; r < Rows.Count; r++)
        {
            var y = Top + r * Row;
            dc.DrawText(Label(Rows[r].Name, 11, text), new Point(4, y + 3));
            for (var c = 0; c < Columns.Count && c < Rows[r].Values.Length; c++)
            {
                var v = Rows[r].Values[c];
                var color = double.IsFinite(v) ? Scale((v - Low) / Math.Max(1e-9, High - Low)) : Color.FromArgb(30, 128, 128, 128);
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(Name + c * cell + 1, y + 1, cell - 2, Row - 3), 4, 4);
                if (double.IsFinite(v) && cell > 30)
                {
                    var label = Label(Math.Round(v).ToString(CultureInfo.InvariantCulture), 10, Brushes.Black);
                    dc.DrawText(label, new Point(Name + c * cell + (cell - label.Width) / 2, y + 4));
                }
            }
        }

        var every = Math.Max(1, (int)Math.Ceiling(40 / cell));
        for (var c = 0; c < Columns.Count; c += every)
        {
            dc.DrawText(Label(Columns[c], 10, muted), new Point(Name + c * cell, Top + Rows.Count * Row + 4));
        }
    }

    // mint → peach → red
    internal static Color Scale(double t)
    {
        t = Math.Clamp(t, 0, 1);
        Color a = Color.FromRgb(0xBF, 0xEB, 0xD5), b = Color.FromRgb(0xFF, 0xD6, 0xB0), c = Color.FromRgb(0xF2, 0x7A, 0x7A);
        static Color Mix(Color x, Color y, double s) => Color.FromRgb((byte)(x.R + (y.R - x.R) * s), (byte)(x.G + (y.G - x.G) * s), (byte)(x.B + (y.B - x.B) * s));
        return t < 0.5 ? Mix(a, b, t * 2) : Mix(b, c, (t - 0.5) * 2);
    }

    private FormattedText Label(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>Distinct, calm colors for up to twenty computers (hues spread around the circle).</summary>
public static class Palette
{
    public static Color For(int index)
    {
        var hue = (index * 137.508) % 360; // the golden angle: neighbours never look alike
        return FromHsl(hue, 0.55, 0.52);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
