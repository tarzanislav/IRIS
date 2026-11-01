using System.Buffers.Binary;
using System.IO.Compression;

namespace Iris.Vision.Imaging;

/// <summary>
/// PNG (ISO/IEC 15948) read into gray, written from gray. Reads every standard kind: gray, gray with alpha, RGB, RGBA
/// and palette, 1 to 16 bits, interlaced (Adam7) or not; transparent pixels are laid on white, as on paper. The data
/// stream is zlib (the deflate decoder of .NET); filters, interlace and colours are this file.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static GrayImage Read(byte[] data)
    {
        if (!IsPng(data))
        {
            throw new InvalidDataException("not a PNG image");
        }

        int width = 0, height = 0, depth = 0, kind = -1, interlace = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var compressed = new MemoryStream();
        var position = Signature.Length;
        var ended = false;
        while (!ended)
        {
            if (position + 12 > data.Length)
            {
                throw new InvalidDataException("PNG: the image is cut off");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position));
            if (length < 0 || position + 12L + length > data.Length)
            {
                throw new InvalidDataException("PNG: a chunk is cut off");
            }

            var type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            var body = data.AsSpan(position + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 8 + length));
            if (Crc(data.AsSpan(position + 4, length + 4)) != crc)
            {
                throw new InvalidDataException($"PNG: the chunk {type} is damaged");
            }

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    (depth, kind, interlace) = (body[8], body[9], body[12]);
                    if (width <= 0 || height <= 0 || body[10] != 0 || body[11] != 0 || interlace > 1 || !Valid(kind, depth))
                    {
                        throw new InvalidDataException("PNG: unsupported header");
                    }

                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS" when kind == 3:
                    paletteAlpha = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
                case "IEND":
                    ended = true;
                    break;
            }

            position += 12 + length;
        }

        if (width == 0 || (kind == 3 && palette is null))
        {
            throw new InvalidDataException("PNG: the header or palette is missing");
        }

        compressed.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.GetBuffer().AsSpan(0, (int)inflated.Length);
        var image = new GrayImage(width, height);
        var channels = kind switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * depth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var offset = 0;
        // Adam7: seven passes over a sparser and sparser grid; without interlace, one pass over every pixel
        var passes = interlace == 0
            ? new[] { (0, 0, 1, 1) }
            : [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var w = (width - x0 + dx - 1) / dx;
            var h = (height - y0 + dy - 1) / dy;
            if (w <= 0 || h <= 0)
            {
                continue;
            }

            var stride = (int)(((long)w * bitsPerPixel + 7) / 8);
            var previous = new byte[stride];
            var current = new byte[stride];
            for (var r = 0; r < h; r++)
            {
                if (offset + 1 + stride > raw.Length)
                {
                    throw new InvalidDataException("PNG: the image data is cut off");
                }

                var filter = raw[offset];
                raw.Slice(offset + 1, stride).CopyTo(current);
                offset += 1 + stride;
                Unfilter(filter, current, previous, bytesPerPixel);
                for (var c = 0; c < w; c++)
                {
                    image[x0 + c * dx, y0 + r * dy] = Gray(current, c, kind, depth, palette, paletteAlpha);
                }

                (previous, current) = (current, previous);
            }
        }

        return image;
    }

    /// <summary>Writes a gray image as an 8-bit gray PNG.</summary>
    public static byte[] Write(GrayImage image)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        Chunk(output, "IHDR", header);
        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
        {
            var line = new byte[image.Width + 1];
            for (var y = 0; y < image.Height; y++)
            {
                // filter "up": text pages have long equal runs from row to row
                line[0] = (byte)(y == 0 ? 0 : 2);
                var row = image.Row(y);
                for (var x = 0; x < image.Width; x++)
                {
                    line[x + 1] = (byte)(row[x] - (y == 0 ? 0 : image[x, y - 1]));
                }

                zlib.Write(line);
            }
        }

        Chunk(output, "IDAT", data.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static bool Valid(int kind, int depth) => kind switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => depth is 8 or 16,
        _ => false,
    };

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> above, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, up = above[i], corner = i >= bpp ? above[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + ((left + up) >> 1)),
                4 => (byte)(row[i] + Paeth(left, up, corner)),
                _ => throw new InvalidDataException($"PNG: unknown filter {filter}"),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // one sample of an unfiltered row (16 bits: the high byte; below 8 bits: stretched to 0–255, palette indices not)
    private static int Sample(ReadOnlySpan<byte> row, int index, int kind, int depth)
    {
        if (depth == 16)
        {
            return row[index * 2];
        }

        if (depth == 8)
        {
            return row[index];
        }

        var perByte = 8 / depth;
        var value = (row[index / perByte] >> (8 - depth * (index % perByte + 1))) & ((1 << depth) - 1);
        return kind == 3 ? value : value * 255 / ((1 << depth) - 1);
    }

    // the gray of pixel c of an unfiltered row; alpha lays it on white
    private static byte Gray(ReadOnlySpan<byte> row, int c, int kind, int depth, byte[]? palette, byte[]? paletteAlpha)
    {
        int gray, alpha = 255;
        switch (kind)
        {
            case 0:
                gray = Sample(row, c, kind, depth);
                break;
            case 3:
                var entry = Sample(row, c, kind, depth);
                if (entry * 3 + 2 >= palette!.Length)
                {
                    throw new InvalidDataException("PNG: a pixel points outside the palette");
                }

                gray = GrayImage.Luma(palette[entry * 3], palette[entry * 3 + 1], palette[entry * 3 + 2]);
                alpha = paletteAlpha is not null && entry < paletteAlpha.Length ? paletteAlpha[entry] : 255;
                break;
            case 4:
                gray = Sample(row, c * 2, kind, depth);
                alpha = Sample(row, c * 2 + 1, kind, depth);
                break;
            case 2:
                gray = GrayImage.Luma(Sample(row, c * 3, kind, depth), Sample(row, c * 3 + 1, kind, depth), Sample(row, c * 3 + 2, kind, depth));
                break;
            default:
                gray = GrayImage.Luma(Sample(row, c * 4, kind, depth), Sample(row, c * 4 + 1, kind, depth), Sample(row, c * 4 + 2, kind, depth));
                alpha = Sample(row, c * 4 + 3, kind, depth);
                break;
        }

        return (byte)((gray * alpha + 255 * (255 - alpha) + 127) / 255);
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    })];

    // CRC-32 of PNG chunks (ISO 3309, polynomial 0xEDB88320)
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream output, string type, byte[] body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, body.Length);
        output.Write(number);
        var typed = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        body.CopyTo(typed, 4);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        output.Write(number);
    }
}
