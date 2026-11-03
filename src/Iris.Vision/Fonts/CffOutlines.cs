using System.Buffers.Binary;

namespace Iris.Vision.Fonts;

/// <summary>
/// The outlines of an OpenType font with PostScript (CFF) glyphs: the INDEX and DICT structures, and an interpreter of
/// Type 2 charstrings – moves, lines, the curve operators with their many short forms, flex, local and global
/// subroutines, hints skipped. CID-keyed fonts (with several private dictionaries) are read too.
/// </summary>
internal sealed class CffOutlines
{
    private readonly byte[] _data;
    private readonly int _start;
    private readonly List<(int Offset, int Length)> _charStrings;
    private readonly List<(int Offset, int Length)> _globalSubrs;
    private readonly List<List<(int Offset, int Length)>> _localSubrs = [];
    private readonly Func<int, int> _fontDictOf;

    public CffOutlines(byte[] data, int start, int length)
    {
        _data = data;
        _start = start;
        var at = start + data[start + 2]; // header size
        var names = Index(ref at);
        var tops = Index(ref at);
        _ = Index(ref at); // strings
        _globalSubrs = Index(ref at);
        if (names.Count == 0 || tops.Count == 0)
        {
            throw new FormatException("empty CFF");
        }

        var top = Dict(tops[0].Offset, tops[0].Length);
        if (!top.TryGetValue(17, out var charStrings))
        {
            throw new FormatException("CFF without charstrings");
        }

        var cs = start + (int)charStrings[0];
        _charStrings = Index(ref cs);
        if (top.TryGetValue(1230, out var fdArray) && top.TryGetValue(1231, out var fdSelect))
        {
            // a CID font: every glyph names its font dictionary, each with its own private dictionary
            var fa = start + (int)fdArray[0];
            foreach (var fd in Index(ref fa))
            {
                _localSubrs.Add(LocalSubrs(Dict(fd.Offset, fd.Length)));
            }

            var select = ReadFdSelect(start + (int)fdSelect[0], _charStrings.Count);
            _fontDictOf = g => g < select.Length ? select[g] : 0;
        }
        else
        {
            _localSubrs.Add(LocalSubrs(top));
            _fontDictOf = _ => 0;
        }
    }

    public List<Contour> Glyph(int glyph)
    {
        if (glyph < 0 || glyph >= _charStrings.Count)
        {
            return [];
        }

        var builder = new Interpreter(this, _localSubrs[Math.Clamp(_fontDictOf(glyph), 0, _localSubrs.Count - 1)]);
        builder.Run(_charStrings[glyph], 0);
        builder.Finish();
        return builder.Contours;
    }

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private List<(int Offset, int Length)> LocalSubrs(Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(18, out var privateDict) || privateDict.Length < 2)
        {
            return [];
        }

        var size = (int)privateDict[0];
        var offset = _start + (int)privateDict[1];
        var p = Dict(offset, size);
        if (!p.TryGetValue(19, out var subrs))
        {
            return [];
        }

        var at = offset + (int)subrs[0];
        return Index(ref at);
    }

    private int[] ReadFdSelect(int at, int glyphs)
    {
        var result = new int[glyphs];
        var format = _data[at];
        if (format == 0)
        {
            for (var g = 0; g < glyphs; g++)
            {
                result[g] = _data[at + 1 + g];
            }
        }
        else if (format == 3)
        {
            var ranges = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 1));
            for (var r = 0; r < ranges; r++)
            {
                var range = at + 3 + 3 * r;
                int first = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(range));
                int fd = _data[range + 2];
                int next = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(range + 3));
                for (var g = first; g < Math.Min(next, glyphs); g++)
                {
                    result[g] = fd;
                }
            }
        }

        return result;
    }

    private List<(int Offset, int Length)> Index(ref int at)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
        if (count == 0)
        {
            at += 2;
            return [];
        }

        int offSize = _data[at + 2];
        var offsets = at + 3;
        var dataStart = offsets + (count + 1) * offSize - 1;
        int Offset(int i)
        {
            var value = 0;
            for (var b = 0; b < offSize; b++)
            {
                value = (value << 8) | _data[offsets + i * offSize + b];
            }

            return value;
        }

        var items = new List<(int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            var from = Offset(i);
            items.Add((dataStart + from, Offset(i + 1) - from));
        }

        at = dataStart + Offset(count);
        return items;
    }

    // a DICT: operator → its operands (two-byte operators as 1200 + second byte)
    private Dictionary<int, double[]> Dict(int at, int length)
    {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var end = at + length;
        while (at < end)
        {
            int b0 = _data[at];
            if (b0 <= 21)
            {
                var op = b0 == 12 ? 1200 + _data[at + 1] : b0;
                at += b0 == 12 ? 2 : 1;
                result[op] = [.. operands];
                operands.Clear();
            }
            else if (b0 == 28)
            {
                operands.Add(BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at + 1)));
                at += 3;
            }
            else if (b0 == 29)
            {
                operands.Add(BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(at + 1)));
                at += 5;
            }
            else if (b0 == 30)
            {
                // a real number in nibbles: only its end matters here (the values used are integers)
                at++;
                while (at < end && (_data[at] & 0x0F) != 0x0F && (_data[at] & 0xF0) != 0xF0)
                {
                    at++;
                }

                at++;
                operands.Add(0);
            }
            else if (b0 >= 32 && b0 <= 246)
            {
                operands.Add(b0 - 139);
                at++;
            }
            else if (b0 >= 247 && b0 <= 250)
            {
                operands.Add((b0 - 247) * 256 + _data[at + 1] + 108);
                at += 2;
            }
            else if (b0 >= 251 && b0 <= 254)
            {
                operands.Add(-(b0 - 251) * 256 - _data[at + 1] - 108);
                at += 2;
            }
            else
            {
                at++;
            }
        }

        return result;
    }

    /// <summary>A Type 2 charstring run: the stack, the current point and the contours built so far.</summary>
    private sealed class Interpreter(CffOutlines cff, List<(int Offset, int Length)> localSubrs)
    {
        private readonly double[] _stack = new double[64];
        private int _count;
        private int _stems;
        private bool _widthDone;
        private double _x;
        private double _y;
        private List<ContourPoint>? _contour;
        private bool _ended;

        public List<Contour> Contours { get; } = [];

        public void Finish() => CloseContour();

        public void Run((int Offset, int Length) code, int depth)
        {
            if (depth > 10)
            {
                throw new FormatException("charstring subroutines nest too deep");
            }

            var data = cff._data;
            var at = code.Offset;
            var end = code.Offset + code.Length;
            while (at < end && !_ended)
            {
                int b0 = data[at++];
                if (b0 >= 32 || b0 == 28)
                {
                    if (_count >= _stack.Length)
                    {
                        throw new FormatException("charstring stack overflow");
                    }

                    if (b0 == 28)
                    {
                        _stack[_count++] = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at));
                        at += 2;
                    }
                    else if (b0 <= 246)
                    {
                        _stack[_count++] = b0 - 139;
                    }
                    else if (b0 <= 250)
                    {
                        _stack[_count++] = (b0 - 247) * 256 + data[at++] + 108;
                    }
                    else if (b0 <= 254)
                    {
                        _stack[_count++] = -(b0 - 251) * 256 - data[at++] - 108;
                    }
                    else
                    {
                        _stack[_count++] = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)) / 65536.0;
                        at += 4;
                    }

                    continue;
                }

                switch (b0)
                {
                    case 1 or 3 or 18 or 23: // stems
                        Width(_count % 2 == 1);
                        _stems += _count / 2;
                        _count = 0;
                        break;
                    case 19 or 20: // hintmask, cntrmask (an implied vstem before)
                        Width(_count % 2 == 1);
                        _stems += _count / 2;
                        _count = 0;
                        at += (_stems + 7) / 8;
                        break;
                    case 21: // rmoveto
                        Width(_count > 2);
                        MoveTo(_x + _stack[0], _y + _stack[1]);
                        _count = 0;
                        break;
                    case 22: // hmoveto
                        Width(_count > 1);
                        MoveTo(_x + _stack[0], _y);
                        _count = 0;
                        break;
                    case 4: // vmoveto
                        Width(_count > 1);
                        MoveTo(_x, _y + _stack[0]);
                        _count = 0;
                        break;
                    case 5: // rlineto
                        for (var i = 0; i + 1 < _count; i += 2)
                        {
                            LineTo(_x + _stack[i], _y + _stack[i + 1]);
                        }

                        _count = 0;
                        break;
                    case 6 or 7: // hlineto, vlineto: alternating
                        {
                            var horizontal = b0 == 6;
                            for (var i = 0; i < _count; i++)
                            {
                                if (horizontal)
                                {
                                    LineTo(_x + _stack[i], _y);
                                }
                                else
                                {
                                    LineTo(_x, _y + _stack[i]);
                                }

                                horizontal = !horizontal;
                            }

                            _count = 0;
                            break;
                        }

                    case 8: // rrcurveto
                        for (var i = 0; i + 5 < _count; i += 6)
                        {
                            Curve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                        }

                        _count = 0;
                        break;
                    case 24: // rcurveline
                        {
                            var i = 0;
                            for (; i + 5 < _count - 2; i += 6)
                            {
                                Curve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            }

                            LineTo(_x + _stack[i], _y + _stack[i + 1]);
                            _count = 0;
                            break;
                        }

                    case 25: // rlinecurve
                        {
                            var i = 0;
                            for (; i + 1 < _count - 6; i += 2)
                            {
                                LineTo(_x + _stack[i], _y + _stack[i + 1]);
                            }

                            Curve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            _count = 0;
                            break;
                        }

                    case 26: // vvcurveto
                        {
                            var i = 0;
                            double dx1 = 0;
                            if (_count % 4 == 1)
                            {
                                dx1 = _stack[i++];
                            }

                            for (; i + 3 < _count; i += 4)
                            {
                                Curve(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
                                dx1 = 0;
                            }

                            _count = 0;
                            break;
                        }

                    case 27: // hhcurveto
                        {
                            var i = 0;
                            double dy1 = 0;
                            if (_count % 4 == 1)
                            {
                                dy1 = _stack[i++];
                            }

                            for (; i + 3 < _count; i += 4)
                            {
                                Curve(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
                                dy1 = 0;
                            }

                            _count = 0;
                            break;
                        }

                    case 30 or 31: // vhcurveto, hvcurveto: alternating, a last odd argument ends the last curve off its axis
                        {
                            var horizontal = b0 == 31;
                            var i = 0;
                            while (i + 3 < _count)
                            {
                                var last = _count - i == 5 ? _stack[i + 4] : 0;
                                if (horizontal)
                                {
                                    Curve(_stack[i], 0, _stack[i + 1], _stack[i + 2], last, _stack[i + 3]);
                                }
                                else
                                {
                                    Curve(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], last);
                                }

                                horizontal = !horizontal;
                                i += 4;
                            }

                            _count = 0;
                            break;
                        }

                    case 10 or 29: // callsubr, callgsubr
                        {
                            var subrs = b0 == 10 ? localSubrs : cff._globalSubrs;
                            var index = (int)_stack[--_count] + Bias(subrs.Count);
                            if (index < 0 || index >= subrs.Count)
                            {
                                throw new FormatException("charstring subroutine out of range");
                            }

                            Run(subrs[index], depth + 1);
                            break;
                        }

                    case 11: // return
                        return;
                    case 14: // endchar
                        Width(_count == 1 || _count == 5);
                        CloseContour();
                        _ended = true;
                        return;
                    case 12:
                        Escape(data[at++]);
                        break;
                    default:
                        _count = 0;
                        break;
                }
            }
        }

        private void Escape(int op)
        {
            var s = _stack;
            switch (op)
            {
                case 35: // flex
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    Curve(s[6], s[7], s[8], s[9], s[10], s[11]);
                    break;
                case 34: // hflex
                    {
                        var y = _y;
                        Curve(s[0], 0, s[1], s[2], s[3], 0);
                        Curve(s[4], 0, s[5], y - _y, s[6], 0);
                        break;
                    }

                case 36: // hflex1
                    {
                        var y = _y;
                        Curve(s[0], s[1], s[2], s[3], s[4], 0);
                        Curve(s[5], 0, s[6], s[7], s[8], y - (_y + s[7]));
                        break;
                    }

                case 37: // flex1: the last point moves along the axis of the larger extent
                    {
                        double dx = s[0] + s[2] + s[4] + s[6] + s[8], dy = s[1] + s[3] + s[5] + s[7] + s[9];
                        Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                        if (Math.Abs(dx) > Math.Abs(dy))
                        {
                            Curve(s[6], s[7], s[8], s[9], s[10], -dy);
                        }
                        else
                        {
                            Curve(s[6], s[7], s[8], s[9], -dx, s[10]);
                        }

                        break;
                    }

                case 9 when _count >= 1: // abs
                    s[_count - 1] = Math.Abs(s[_count - 1]);
                    return;
                case 10 when _count >= 2: // add
                    s[_count - 2] += s[_count - 1];
                    _count--;
                    return;
                case 11 when _count >= 2: // sub
                    s[_count - 2] -= s[_count - 1];
                    _count--;
                    return;
                case 12 when _count >= 2: // div
                    s[_count - 2] /= s[_count - 1];
                    _count--;
                    return;
                case 14 when _count >= 1: // neg
                    s[_count - 1] = -s[_count - 1];
                    return;
                case 18 when _count >= 1: // drop
                    _count--;
                    return;
                case 24 when _count >= 2: // mul
                    s[_count - 2] *= s[_count - 1];
                    _count--;
                    return;
                case 27 when _count >= 1 && _count < s.Length: // dup
                    s[_count] = s[_count - 1];
                    _count++;
                    return;
                case 28 when _count >= 2: // exch
                    (s[_count - 2], s[_count - 1]) = (s[_count - 1], s[_count - 2]);
                    return;
                default:
                    break;
            }

            _count = 0;
        }

        // the first stack-clearing operator may carry the advance width first: it is not needed (hmtx has it)
        private void Width(bool present)
        {
            if (!_widthDone && present && _count > 0)
            {
                Array.Copy(_stack, 1, _stack, 0, _count - 1);
                _count--;
            }

            _widthDone = true;
        }

        private void MoveTo(double x, double y)
        {
            CloseContour();
            (_x, _y) = (x, y);
            _contour = [new ContourPoint((float)x, (float)y, PointKind.On)];
        }

        private void LineTo(double x, double y)
        {
            _contour ??= [new ContourPoint((float)_x, (float)_y, PointKind.On)];
            (_x, _y) = (x, y);
            _contour.Add(new ContourPoint((float)x, (float)y, PointKind.On));
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            _contour ??= [new ContourPoint((float)_x, (float)_y, PointKind.On)];
            double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
            (_x, _y) = (x2 + dx3, y2 + dy3);
            _contour.Add(new ContourPoint((float)x1, (float)y1, PointKind.Cubic));
            _contour.Add(new ContourPoint((float)x2, (float)y2, PointKind.Cubic));
            _contour.Add(new ContourPoint((float)_x, (float)_y, PointKind.On));
        }

        private void CloseContour()
        {
            if (_contour is { Count: > 1 })
            {
                // the closing point is the start again: drop it so the contour does not draw a zero edge
                if (_contour[^1].Kind == PointKind.On && _contour[^1].X == _contour[0].X && _contour[^1].Y == _contour[0].Y)
                {
                    _contour.RemoveAt(_contour.Count - 1);
                }

                Contours.Add(new Contour(_contour));
            }

            _contour = null;
        }
    }
}
