using System.Buffers.Binary;
using System.Text;

namespace Iris.Vision.Fonts;

/// <summary>
/// A font face read straight from its file (TrueType .ttf, OpenType .otf with CFF outlines, collections .ttc): the
/// characters it has (cmap), the advance of each glyph (hmtx), kerning (the kern table and GPOS pair positioning)
/// and the outlines – quadratic TrueType contours (also composite glyphs) or cubic CFF charstrings. Own reader of
/// the OpenType format; no hinting (lines are drawn large and then scaled down, as a scanner or camera sees print).
/// </summary>
public sealed class FontFace
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly Dictionary<int, int> _cmap = [];
    private readonly ushort[] _advances;
    private readonly Dictionary<long, short> _kern = [];
    private readonly GposKerning? _gpos;
    private readonly CffOutlines? _cff;
    private readonly int[]? _loca;
    private readonly Dictionary<int, List<Contour>> _glyphs = [];
    private readonly Lock _lock = new();

    private FontFace(byte[] data, int offset, string file, int index)
    {
        _data = data;
        _tables = Tables(data, offset);
        var head = Table("head");
        UnitsPerEm = U16(head + 18);
        var hhea = Table("hhea");
        Ascender = S16(hhea + 4);
        Descender = S16(hhea + 6);
        var metrics = U16(hhea + 34);
        GlyphCount = U16(Table("maxp") + 4);
        _advances = new ushort[GlyphCount];
        var hmtx = Table("hmtx");
        for (var g = 0; g < GlyphCount; g++)
        {
            _advances[g] = U16(hmtx + 4 * Math.Min(g, Math.Max(0, metrics - 1)));
        }

        ReadCmap();
        if (_tables.ContainsKey("kern"))
        {
            ReadKern();
        }

        if (_tables.TryGetValue("GPOS", out var gpos))
        {
            _gpos = GposKerning.Read(data, gpos.Offset, gpos.Length);
        }

        if (_tables.ContainsKey("glyf"))
        {
            var longOffsets = S16(head + 50) == 1;
            var loca = Table("loca");
            _loca = new int[GlyphCount + 1];
            for (var g = 0; g <= GlyphCount; g++)
            {
                _loca[g] = longOffsets ? checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(loca + 4 * g))) : 2 * U16(loca + 2 * g);
            }
        }
        else if (_tables.TryGetValue("CFF ", out var cff))
        {
            _cff = new CffOutlines(data, cff.Offset, cff.Length);
        }
        else
        {
            throw new FormatException("no glyf or CFF outlines");
        }

        Name = ReadName() ?? (index == 0 ? Path.GetFileNameWithoutExtension(file) : $"{Path.GetFileNameWithoutExtension(file)} {index}");
        File = file;
    }

    /// <summary>The full name of the face (from the name table), e.g. "DejaVu Serif Bold".</summary>
    public string Name { get; }

    /// <summary>The file it came from.</summary>
    public string File { get; }

    public int UnitsPerEm { get; }

    public int Ascender { get; }

    public int Descender { get; }

    public int GlyphCount { get; }

    /// <summary>The faces of a font file (a collection has several); faces that cannot be read are left out.</summary>
    public static IReadOnlyList<FontFace> Load(string path)
    {
        var data = System.IO.File.ReadAllBytes(path);
        var faces = new List<FontFace>();
        if (data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "ttcf")
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            for (var i = 0; i < Math.Min(count, 64); i++)
            {
                TryAdd(faces, data, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + 4 * i)), path, i);
            }
        }
        else
        {
            TryAdd(faces, data, 0, path, 0);
        }

        return faces;
    }

    /// <summary>The glyph of a character (0 = the font does not have it).</summary>
    public int GlyphOf(int codePoint) => _cmap.GetValueOrDefault(codePoint);

    /// <summary>Whether every character of the text that is not a space has a glyph with an outline.</summary>
    public bool Covers(string text)
    {
        foreach (var c in text)
        {
            if (c == ' ')
            {
                continue;
            }

            var glyph = GlyphOf(c);
            if (glyph == 0 || Outline(glyph).Count == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The advance of a glyph, in font units.</summary>
    public int Advance(int glyph) => glyph < _advances.Length ? _advances[glyph] : 0;

    /// <summary>The kerning between two glyphs, in font units (kern table, else GPOS pair positioning).</summary>
    public int Kerning(int left, int right)
    {
        if (_kern.TryGetValue(((long)left << 16) | (uint)right, out var value))
        {
            return value;
        }

        return _gpos?.Kerning(left, right) ?? 0;
    }

    /// <summary>Draws a glyph with its origin at (x, y) in pixels, scale = pixels per font unit (y of the font points up).</summary>
    public void Draw(int glyph, IOutlineSink sink, float x, float y, float scale)
    {
        foreach (var contour in Outline(glyph))
        {
            contour.Draw(sink, x, y, scale);
        }
    }

    /// <summary>The contours of a glyph in font units (cached).</summary>
    internal List<Contour> Outline(int glyph)
    {
        lock (_lock)
        {
            if (!_glyphs.TryGetValue(glyph, out var contours))
            {
                try
                {
                    contours = _cff is not null ? _cff.Glyph(glyph) : TrueTypeGlyph(glyph, 0);
                }
                catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
                {
                    contours = [];
                }

                _glyphs[glyph] = contours;
            }

            return contours;
        }
    }

    private static void TryAdd(List<FontFace> faces, byte[] data, int offset, string path, int index)
    {
        try
        {
            faces.Add(new FontFace(data, offset, path, index));
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or KeyNotFoundException or OverflowException)
        {
            // not a face IRIS can read (symbols, bitmaps only, damaged): left out
        }
    }

    private static Dictionary<string, (int, int)> Tables(byte[] data, int offset)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = offset + 12 + 16 * i;
            var tag = Encoding.ASCII.GetString(data, record, 4);
            var start = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12));
            if (start < 0 || length < 0 || (long)start + length > data.Length)
            {
                throw new FormatException($"table {tag} outside the file");
            }

            tables[tag] = (start, length);
        }

        return tables;
    }

    private int Table(string tag) => _tables.TryGetValue(tag, out var t) ? t.Offset : throw new FormatException($"no {tag} table");

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));

    private short S16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));

    private void ReadCmap()
    {
        var cmap = Table("cmap");
        var count = U16(cmap + 2);
        int best = -1, bestRank = -1;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + 8 * i;
            int platform = U16(record), encoding = U16(record + 2);
            var subtable = cmap + (int)U32(record + 4);
            var format = U16(subtable);
            // Unicode full repertoire first, then the basic plane
            var rank = (platform, encoding, format) switch
            {
                (3, 10, 12) or (0, _, 12) => 3,
                (3, 1, 4) or (0, _, 4) => 2,
                _ => -1,
            };
            if (rank > bestRank)
            {
                (best, bestRank) = (subtable, rank);
            }
        }

        if (best < 0)
        {
            throw new FormatException("no Unicode cmap");
        }

        if (U16(best) == 4)
        {
            var segments = U16(best + 6) / 2;
            var ends = best + 14;
            var starts = ends + 2 * segments + 2;
            var deltas = starts + 2 * segments;
            var ranges = deltas + 2 * segments;
            for (var s = 0; s < segments; s++)
            {
                int end = U16(ends + 2 * s), start = U16(starts + 2 * s), delta = S16(deltas + 2 * s), range = U16(ranges + 2 * s);
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (range == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        var at = ranges + 2 * s + range + 2 * (c - start);
                        glyph = at + 2 <= _data.Length ? U16(at) : 0;
                        if (glyph != 0)
                        {
                            glyph = (glyph + delta) & 0xFFFF;
                        }
                    }

                    if (glyph != 0 && glyph < GlyphCount)
                    {
                        _cmap[c] = glyph;
                    }
                }
            }
        }
        else
        {
            var groups = (int)U32(best + 12);
            for (var i = 0; i < groups; i++)
            {
                var group = best + 16 + 12 * i;
                uint start = U32(group), end = U32(group + 4), glyph = U32(group + 8);
                for (var c = start; c <= end && c <= 0x10FFFF && c - start < 0x10000; c++)
                {
                    var g = (int)(glyph + c - start);
                    if (g > 0 && g < GlyphCount)
                    {
                        _cmap[(int)c] = g;
                    }
                }
            }
        }
    }

    private void ReadKern()
    {
        var kern = Table("kern");
        if (U16(kern) != 0)
        {
            return; // the Apple version of the table: rare, left out
        }

        var tables = U16(kern + 2);
        var at = kern + 4;
        for (var t = 0; t < tables; t++)
        {
            var length = U16(at + 2);
            var coverage = U16(at + 4);
            // format 0, horizontal, not cross-stream, not minimum
            if ((coverage >> 8) == 0 && (coverage & 0x7) == 1)
            {
                var pairs = U16(at + 6);
                for (var p = 0; p < pairs; p++)
                {
                    var pair = at + 14 + 6 * p;
                    if (pair + 6 > _data.Length)
                    {
                        break;
                    }

                    _kern.TryAdd(((long)U16(pair) << 16) | U16(pair + 2), S16(pair + 4));
                }
            }

            at += Math.Max(6, (int)length);
        }
    }

    private string? ReadName()
    {
        if (!_tables.TryGetValue("name", out var table))
        {
            return null;
        }

        var name = table.Offset;
        var count = U16(name + 2);
        var strings = name + U16(name + 4);
        string? fallback = null;
        for (var i = 0; i < count; i++)
        {
            var record = name + 6 + 12 * i;
            int platform = U16(record), id = U16(record + 6), length = U16(record + 8), offset = U16(record + 10);
            if (id != 4 || strings + offset + length > _data.Length)
            {
                continue;
            }

            if (platform == 3 || platform == 0)
            {
                return Encoding.BigEndianUnicode.GetString(_data, strings + offset, length);
            }

            fallback ??= Encoding.Latin1.GetString(_data, strings + offset, length);
        }

        return fallback;
    }

    // a TrueType glyph: simple contours, or components put together (their points moved, scaled, matched)
    private List<Contour> TrueTypeGlyph(int glyph, int depth)
    {
        if (depth > 8 || glyph < 0 || glyph >= GlyphCount)
        {
            return [];
        }

        var start = _loca![glyph];
        var end = _loca[glyph + 1];
        if (end <= start)
        {
            return []; // an empty glyph (a space)
        }

        var at = Table("glyf") + start;
        var contours = S16(at);
        at += 10;
        if (contours >= 0)
        {
            return SimpleGlyph(at, contours);
        }

        var result = new List<Contour>();
        const int Words = 0x1, XyValues = 0x2, Scale = 0x8, More = 0x20, XyScale = 0x40, TwoByTwo = 0x80, ScaledOffset = 0x800;
        int flags;
        do
        {
            flags = U16(at);
            var component = U16(at + 2);
            at += 4;
            int arg1, arg2;
            if ((flags & Words) != 0)
            {
                (arg1, arg2) = (flags & XyValues) != 0 ? ((int)S16(at), (int)S16(at + 2)) : ((int)U16(at), (int)U16(at + 2));
                at += 4;
            }
            else
            {
                (arg1, arg2) = (flags & XyValues) != 0 ? ((int)(sbyte)_data[at], (int)(sbyte)_data[at + 1]) : ((int)_data[at], (int)_data[at + 1]);
                at += 2;
            }

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & Scale) != 0)
            {
                a = d = F2Dot14(at);
                at += 2;
            }
            else if ((flags & XyScale) != 0)
            {
                a = F2Dot14(at);
                d = F2Dot14(at + 2);
                at += 4;
            }
            else if ((flags & TwoByTwo) != 0)
            {
                (a, b, c, d) = (F2Dot14(at), F2Dot14(at + 2), F2Dot14(at + 4), F2Dot14(at + 6));
                at += 8;
            }

            var parts = TrueTypeGlyph(component, depth + 1).Select(p => p.Transformed(a, b, c, d, 0, 0)).ToList();
            float dx, dy;
            if ((flags & XyValues) != 0)
            {
                (dx, dy) = (arg1, arg2);
                if ((flags & ScaledOffset) != 0)
                {
                    (dx, dy) = (a * dx + c * dy, b * dx + d * dy);
                }
            }
            else
            {
                // point matching: the point arg2 of the component lands on the point arg1 of what is there so far
                var ours = result.SelectMany(p => p.Points).ElementAtOrDefault(arg1);
                var theirs = parts.SelectMany(p => p.Points).ElementAtOrDefault(arg2);
                (dx, dy) = (ours.X - theirs.X, ours.Y - theirs.Y);
            }

            result.AddRange(parts.Select(p => p.Transformed(1, 0, 0, 1, dx, dy)));
        }
        while ((flags & More) != 0);
        return result;
    }

    private float F2Dot14(int at) => S16(at) / 16384f;

    private List<Contour> SimpleGlyph(int at, int contours)
    {
        var ends = new int[contours];
        for (var i = 0; i < contours; i++)
        {
            ends[i] = U16(at + 2 * i);
        }

        var points = contours == 0 ? 0 : ends[^1] + 1;
        at += 2 * contours;
        at += 2 + U16(at); // instructions are not used
        var flags = new byte[points];
        for (var i = 0; i < points;)
        {
            var flag = _data[at++];
            flags[i++] = flag;
            if ((flag & 8) != 0)
            {
                var repeat = _data[at++];
                for (var r = 0; r < repeat && i < points; r++)
                {
                    flags[i++] = flag;
                }
            }
        }

        var xs = new int[points];
        var ys = new int[points];
        var value = 0;
        for (var i = 0; i < points; i++)
        {
            var flag = flags[i];
            if ((flag & 2) != 0)
            {
                value += (flag & 16) != 0 ? _data[at] : -_data[at];
                at++;
            }
            else if ((flag & 16) == 0)
            {
                value += S16(at);
                at += 2;
            }

            xs[i] = value;
        }

        value = 0;
        for (var i = 0; i < points; i++)
        {
            var flag = flags[i];
            if ((flag & 4) != 0)
            {
                value += (flag & 32) != 0 ? _data[at] : -_data[at];
                at++;
            }
            else if ((flag & 32) == 0)
            {
                value += S16(at);
                at += 2;
            }

            ys[i] = value;
        }

        var result = new List<Contour>(contours);
        var first = 0;
        foreach (var end in ends)
        {
            var contour = new List<ContourPoint>(end - first + 1);
            for (var i = first; i <= end; i++)
            {
                contour.Add(new ContourPoint(xs[i], ys[i], (flags[i] & 1) != 0 ? PointKind.On : PointKind.Quadratic));
            }

            if (contour.Count > 0)
            {
                result.Add(new Contour(contour));
            }

            first = end + 1;
        }

        return result;
    }
}

internal enum PointKind : byte
{
    On,
    Quadratic,
    Cubic,
}

internal readonly record struct ContourPoint(float X, float Y, PointKind Kind);

/// <summary>
/// One closed contour of a glyph in font units: points on the curve, quadratic control points (two in a row imply an
/// on-curve point between them) or cubic control points (always two before an on-curve point).
/// </summary>
internal sealed class Contour(List<ContourPoint> points)
{
    public List<ContourPoint> Points { get; } = points;

    public Contour Transformed(float a, float b, float c, float d, float dx, float dy) =>
        new([.. Points.Select(p => p with { X = a * p.X + c * p.Y + dx, Y = b * p.X + d * p.Y + dy })]);

    public void Draw(IOutlineSink sink, float x, float y, float scale)
    {
        var count = Points.Count;
        if (count == 0)
        {
            return;
        }

        (float X, float Y) At(ContourPoint p) => (x + p.X * scale, y - p.Y * scale);

        // start at a point on the curve (or between two quadratic controls)
        var start = Points.FindIndex(p => p.Kind == PointKind.On);
        (float X, float Y) first;
        if (start < 0)
        {
            var a = At(Points[0]);
            var b = At(Points[^1]);
            first = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            start = 0;
        }
        else
        {
            first = At(Points[start]);
            start++;
        }

        sink.MoveTo(first.X, first.Y);
        (float X, float Y)? control = null;
        for (var n = 0; n < count; n++)
        {
            var point = Points[(start + n) % count];
            var p = At(point);
            if (point.Kind == PointKind.Cubic)
            {
                var second = At(Points[(start + n + 1) % count]);
                var end = At(Points[(start + n + 2) % count]);
                sink.CubicTo(p.X, p.Y, second.X, second.Y, end.X, end.Y);
                n += 2;
                continue;
            }

            if (point.Kind == PointKind.On)
            {
                if (control is { } c)
                {
                    sink.QuadTo(c.X, c.Y, p.X, p.Y);
                    control = null;
                }
                else
                {
                    sink.LineTo(p.X, p.Y);
                }

                continue;
            }

            if (control is { } previous)
            {
                // two quadratic controls in a row: the curve passes the point between them
                var middle = ((previous.X + p.X) / 2, (previous.Y + p.Y) / 2);
                sink.QuadTo(previous.X, previous.Y, middle.Item1, middle.Item2);
            }

            control = p;
        }

        if (control is { } last)
        {
            sink.QuadTo(last.X, last.Y, first.X, first.Y);
        }

        sink.Close();
    }
}
