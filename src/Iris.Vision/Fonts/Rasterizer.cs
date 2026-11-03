namespace Iris.Vision.Fonts;

/// <summary>Where the outline of a glyph is drawn: moves, lines and quadratic or cubic Bézier curves, in pixels (y down).</summary>
public interface IOutlineSink
{
    void MoveTo(float x, float y);

    void LineTo(float x, float y);

    void QuadTo(float cx, float cy, float x, float y);

    void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y);

    /// <summary>Ends the contour (back to its start with a line).</summary>
    void Close();
}

/// <summary>
/// Fills outlines with exact coverage: every edge adds, to each pixel it crosses, the signed area it leaves to its
/// right within that pixel (and the rest of its height to the next pixel); a running sum along each row then gives
/// how much of every pixel lies inside the outline (non-zero winding, clamped to 1). Curves are cut into short lines
/// first, finely enough that the error is below a fifth of a pixel. Anti-aliasing comes from the areas themselves,
/// no sub-sampling.
/// </summary>
public sealed class Rasterizer : IOutlineSink
{
    private const float Tolerance = 0.05f;
    private readonly float[] _area;     // [height, width + 2]
    private readonly int _stride;
    private float _x;
    private float _y;
    private float _startX;
    private float _startY;
    private bool _open;

    public Rasterizer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        (Width, Height) = (width, height);
        _stride = width + 2;
        _area = new float[(long)_stride * height <= int.MaxValue ? _stride * height : throw new ArgumentException("the canvas is too large")];
    }

    public int Width { get; }

    public int Height { get; }

    public void MoveTo(float x, float y)
    {
        Close();
        (_x, _y, _startX, _startY) = (x, y, x, y);
        _open = true;
    }

    public void LineTo(float x, float y)
    {
        Edge(_x, _y, x, y);
        (_x, _y) = (x, y);
    }

    public void QuadTo(float cx, float cy, float x, float y)
    {
        // pieces so that the curve departs from its chords by less than the tolerance
        var deviation = MathF.Abs(_x - 2 * cx + x) + MathF.Abs(_y - 2 * cy + y);
        var pieces = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(deviation / (4 * Tolerance))), 1, 256);
        float x0 = _x, y0 = _y;
        for (var i = 1; i <= pieces; i++)
        {
            var t = i / (float)pieces;
            var u = 1 - t;
            LineTo(u * u * x0 + 2 * u * t * cx + t * t * x, u * u * y0 + 2 * u * t * cy + t * t * y);
        }
    }

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        float x0 = _x, y0 = _y;
        var deviation = MathF.Max(MathF.Abs(x0 - 2 * c1x + c2x) + MathF.Abs(y0 - 2 * c1y + c2y),
            MathF.Abs(c1x - 2 * c2x + x) + MathF.Abs(c1y - 2 * c2y + y));
        var pieces = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(3 * deviation / (4 * Tolerance))), 1, 256);
        for (var i = 1; i <= pieces; i++)
        {
            var t = i / (float)pieces;
            var u = 1 - t;
            LineTo(u * u * u * x0 + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x,
                u * u * u * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y);
        }
    }

    public void Close()
    {
        if (_open && (_x != _startX || _y != _startY))
        {
            Edge(_x, _y, _startX, _startY);
        }

        (_x, _y) = (_startX, _startY);
        _open = false;
    }

    /// <summary>The coverage of every pixel (0 – 1), row after row.</summary>
    public float[] Coverage()
    {
        Close();
        var result = new float[Width * Height];
        for (var row = 0; row < Height; row++)
        {
            float sum = 0;
            var source = row * _stride;
            var target = row * Width;
            for (var x = 0; x < Width; x++)
            {
                sum += _area[source + x];
                result[target + x] = MathF.Min(1, MathF.Abs(sum));
            }
        }

        return result;
    }

    // one straight edge: row by row, the part of the edge in each row, cell by cell
    private void Edge(float x0, float y0, float x1, float y1)
    {
        if (y0 == y1 || float.IsNaN(x0 + y0 + x1 + y1))
        {
            return;
        }

        var direction = 1f;
        if (y0 > y1)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
            direction = -1f;
        }

        var slope = (x1 - x0) / (y1 - y0);
        var first = Math.Max(0, (int)MathF.Floor(y0));
        var last = Math.Min(Height - 1, (int)MathF.Ceiling(y1) - 1);
        for (var row = first; row <= last; row++)
        {
            var top = MathF.Max(y0, row);
            var bottom = MathF.Min(y1, row + 1);
            if (bottom <= top)
            {
                continue;
            }

            Cells(row, x0 + (top - y0) * slope, x0 + (bottom - y0) * slope, (bottom - top) * direction);
        }
    }

    // the part of an edge inside one row (from x a to x b, height dy with its sign), split at the pixel borders;
    // what lies left of the canvas covers the whole row from its first pixel, what lies right of it nothing
    private void Cells(int row, float a, float b, float dy)
    {
        var left = MathF.Min(a, b);
        var right = MathF.Max(a, b);
        var line = row * _stride;
        var span = right - left;
        if (span < 1e-6f)
        {
            var x = Math.Clamp(left, 0, Width);
            var cell = Math.Min((int)x, Width);
            var middle = x - cell;
            _area[line + cell] += dy * (1 - middle);
            _area[line + cell + 1] += dy * middle;
            return;
        }

        if (left < 0)
        {
            _area[line] += dy * (MathF.Min(right, 0) - left) / span;
            left = 0;
        }

        right = MathF.Min(right, Width);
        for (var x = (int)left; x < right; x++)
        {
            var from = MathF.Max(left, x);
            var to = MathF.Min(right, x + 1);
            if (to <= from)
            {
                continue;
            }

            var part = dy * (to - from) / span;
            var middle = (from + to) / 2 - x;
            _area[line + x] += part * (1 - middle);
            _area[line + x + 1] += part * middle;
        }
    }
}
