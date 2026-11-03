using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Iris.Vision.Fonts;

/// <summary>
/// Kerning from the GPOS table (what text programs apply in print): the pair-positioning lookups of the "kern"
/// feature, by single pairs (format 1) or by classes of glyphs (format 2), extension lookups followed. Only the
/// horizontal advance of the first glyph is used – that is kerning.
/// </summary>
internal sealed class GposKerning
{
    private readonly byte[] _data;
    private readonly List<int> _subtables;
    private readonly ConcurrentDictionary<long, short> _cache = new();

    private GposKerning(byte[] data, List<int> subtables) => (_data, _subtables) = (data, subtables);

    public static GposKerning? Read(byte[] data, int gpos, int length)
    {
        try
        {
            ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            var features = gpos + U16(gpos + 6);
            var lookups = gpos + U16(gpos + 8);
            var indices = new SortedSet<int>();
            var featureCount = U16(features);
            for (var f = 0; f < featureCount; f++)
            {
                var record = features + 2 + 6 * f;
                if (data[record] != 'k' || data[record + 1] != 'e' || data[record + 2] != 'r' || data[record + 3] != 'n')
                {
                    continue;
                }

                var feature = features + U16(record + 4);
                var count = U16(feature + 2);
                for (var i = 0; i < count; i++)
                {
                    indices.Add(U16(feature + 4 + 2 * i));
                }
            }

            var subtables = new List<int>();
            var lookupCount = U16(lookups);
            foreach (var index in indices.Where(i => i < lookupCount))
            {
                var lookup = lookups + U16(lookups + 2 + 2 * index);
                var type = U16(lookup);
                var count = U16(lookup + 4);
                for (var s = 0; s < count; s++)
                {
                    var subtable = lookup + U16(lookup + 6 + 2 * s);
                    if (type == 9 && U16(subtable + 2) == 2)
                    {
                        subtables.Add(subtable + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(subtable + 4)));
                    }
                    else if (type == 2)
                    {
                        subtables.Add(subtable);
                    }
                }
            }

            return subtables.Count == 0 ? null : new GposKerning(data, subtables);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public short Kerning(int left, int right) => _cache.GetOrAdd(((long)left << 16) | (uint)right, _ => Find(left, right));

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));

    private short S16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at));

    private short Find(int left, int right)
    {
        try
        {
            foreach (var subtable in _subtables)
            {
                var coverage = Coverage(subtable + U16(subtable + 2), left);
                if (coverage < 0)
                {
                    continue;
                }

                int format1 = U16(subtable + 4), format2 = U16(subtable + 6);
                int size1 = Size(format1), size2 = Size(format2);
                if (U16(subtable) == 1)
                {
                    var pairSets = U16(subtable + 8);
                    if (coverage >= pairSets)
                    {
                        continue;
                    }

                    var set = subtable + U16(subtable + 10 + 2 * coverage);
                    var count = U16(set);
                    var record = 2 + size1 + size2;
                    // the second glyphs are sorted: binary search
                    int low = 0, high = count - 1;
                    while (low <= high)
                    {
                        var middle = (low + high) / 2;
                        var at = set + 2 + middle * record;
                        var glyph = U16(at);
                        if (glyph == right)
                        {
                            return Advance(at + 2, format1);
                        }

                        if (glyph < right)
                        {
                            low = middle + 1;
                        }
                        else
                        {
                            high = middle - 1;
                        }
                    }
                }
                else if (U16(subtable) == 2)
                {
                    var class1 = ClassOf(subtable + U16(subtable + 8), left);
                    var class2 = ClassOf(subtable + U16(subtable + 10), right);
                    int count1 = U16(subtable + 12), count2 = U16(subtable + 14);
                    if (class1 < count1 && class2 < count2)
                    {
                        var at = subtable + 16 + (class1 * count2 + class2) * (size1 + size2);
                        return Advance(at, format1);
                    }
                }
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // a damaged table: no kerning
        }

        return 0;
    }

    // bytes of a value record of this format (each set bit is one 16-bit field)
    private static int Size(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    private short Advance(int record, int format)
    {
        if ((format & 0x4) == 0)
        {
            return 0;
        }

        // fields come in bit order: x placement, y placement, x advance
        var skip = ((format & 0x1) != 0 ? 2 : 0) + ((format & 0x2) != 0 ? 2 : 0);
        return S16(record + skip);
    }

    private int Coverage(int table, int glyph)
    {
        var count = U16(table + 2);
        if (U16(table) == 1)
        {
            int low = 0, high = count - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var g = U16(table + 4 + 2 * middle);
                if (g == glyph)
                {
                    return middle;
                }

                if (g < glyph)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return -1;
        }

        for (var i = 0; i < count; i++)
        {
            var range = table + 4 + 6 * i;
            int start = U16(range), end = U16(range + 2);
            if (glyph >= start && glyph <= end)
            {
                return U16(range + 4) + glyph - start;
            }
        }

        return -1;
    }

    private int ClassOf(int table, int glyph)
    {
        if (U16(table) == 1)
        {
            int start = U16(table + 2), count = U16(table + 4);
            return glyph >= start && glyph < start + count ? U16(table + 6 + 2 * (glyph - start)) : 0;
        }

        var ranges = U16(table + 2);
        for (var i = 0; i < ranges; i++)
        {
            var range = table + 4 + 6 * i;
            if (glyph >= U16(range) && glyph <= U16(range + 2))
            {
                return U16(range + 4);
            }
        }

        return 0;
    }
}
