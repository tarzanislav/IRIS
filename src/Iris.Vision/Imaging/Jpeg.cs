using System.Buffers.Binary;

namespace Iris.Vision.Imaging;

/// <summary>
/// JPEG (ITU-T T.81) read into gray: baseline and extended sequential Huffman images (what phones and scanners write),
/// progressive images (what web pages and messengers often send), any sampling of the colour components, restart
/// intervals, and the orientation the camera wrote into EXIF. Only the brightness component (Y) is decoded through the
/// inverse DCT – the gray of a JFIF image is Y itself, so the colour components are read past and never computed (a
/// progressive scan of colour alone is skipped whole).
/// </summary>
public static class Jpeg
{
    // the order in which the 64 coefficients of a block are stored (zig-zag)
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // cos((2x + 1) u π / 16) · c(u) / 2, the inverse DCT as a product of two 8 × 8 matrices
    private static readonly float[] Basis = MakeBasis();

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>The image in gray and the EXIF orientation (1 = as stored) – <see cref="GrayImage.Oriented"/> turns it.</summary>
    public static (GrayImage Image, int Orientation) Read(byte[] data)
    {
        if (!IsJpeg(data))
        {
            throw new InvalidDataException("not a JPEG image");
        }

        var quant = new int[4][];
        var dc = new Huffman?[4];
        var ac = new Huffman?[4];
        Frame? frame = null;
        var restart = 0;
        var orientation = 1;
        var adobeTransform = -1;
        var progressive = false;
        var position = 2;
        while (position < data.Length)
        {
            if (data[position] != 0xFF)
            {
                throw new InvalidDataException("JPEG: a marker was expected");
            }

            var marker = data[position + 1];
            position += 2;
            if (marker == 0xFF)
            {
                position--; // fill byte
                continue;
            }

            if (marker == 0xD9)
            {
                break;
            }

            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                continue;
            }

            var length = Read16(data, position);
            if (length < 2 || position + length > data.Length)
            {
                throw new InvalidDataException("JPEG: a segment is cut off");
            }

            var body = data.AsSpan(position + 2, length - 2);
            switch (marker)
            {
                case 0xDB:
                    ReadQuantization(body, quant);
                    break;
                case 0xC4:
                    ReadHuffman(body, dc, ac);
                    break;
                case 0xC0 or 0xC1 or 0xC2:
                    frame = Frame.Read(body);
                    progressive = marker == 0xC2;
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    throw new NotSupportedException("JPEG: this kind of JPEG (lossless or arithmetic coding) is not read");
                case 0xDD:
                    restart = Read16(body, 0);
                    break;
                case 0xE1 when body.StartsWith("Exif\0\0"u8):
                    orientation = ExifOrientation(body[6..]);
                    break;
                case 0xEE when body.StartsWith("Adobe"u8) && body.Length >= 12:
                    adobeTransform = body[11];
                    break;
                case 0xDA:
                    if (frame is null)
                    {
                        throw new InvalidDataException("JPEG: the image data come before the frame");
                    }

                    if (frame.Components.Length == 4 || (frame.Components.Length == 3 && adobeTransform == 0))
                    {
                        throw new NotSupportedException("JPEG: CMYK and RGB-coded JPEGs are not read – save the image as PNG");
                    }

                    position = progressive
                        ? ProgressiveScan(data, position + length, body, frame, dc, ac, restart)
                        : Scan(data, position + length, body, frame, quant, dc, ac, restart);
                    continue;
            }

            position += length;
        }

        if (progressive && frame?.Components[0].Coefficients is { } coefficients)
        {
            // all scans read: now the brightness blocks are complete and go through the inverse DCT
            var luma = frame.Components[0];
            var lumaQuant = quant[luma.Quant] ?? throw new InvalidDataException("JPEG: a quantization table is missing");
            frame.Prepare();
            var block = new int[64];
            for (var by = 0; by < luma.BlocksHigh; by++)
            {
                for (var bx = 0; bx < luma.BlocksWide; bx++)
                {
                    coefficients.AsSpan((by * luma.BlocksWide + bx) * 64, 64).CopyTo(block);
                    Idct(block, lumaQuant, luma, bx, by);
                }
            }

            frame.Finish();
        }

        if (frame?.Gray is not { } gray)
        {
            throw new InvalidDataException("JPEG: the image has no data");
        }

        return (gray, orientation);
    }

    private static int Read16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static void ReadQuantization(ReadOnlySpan<byte> body, int[][] tables)
    {
        for (var i = 0; i < body.Length;)
        {
            var precision = body[i] >> 4;
            var id = body[i] & 15;
            if (id > 3 || i + 1 + 64 * (precision + 1) > body.Length)
            {
                throw new InvalidDataException("JPEG: a quantization table is damaged");
            }

            var table = new int[64];
            for (var k = 0; k < 64; k++)
            {
                table[ZigZag[k]] = precision == 0 ? body[i + 1 + k] : Read16(body, i + 1 + 2 * k);
            }

            tables[id] = table;
            i += 1 + 64 * (precision + 1);
        }
    }

    private static void ReadHuffman(ReadOnlySpan<byte> body, Huffman?[] dc, Huffman?[] ac)
    {
        for (var i = 0; i < body.Length;)
        {
            var kind = body[i] >> 4;
            var id = body[i] & 15;
            if (kind > 1 || id > 3 || i + 17 > body.Length)
            {
                throw new InvalidDataException("JPEG: a Huffman table is damaged");
            }

            var counts = body.Slice(i + 1, 16);
            var total = 0;
            foreach (var c in counts)
            {
                total += c;
            }

            if (total > 256 || i + 17 + total > body.Length)
            {
                throw new InvalidDataException("JPEG: a Huffman table is damaged");
            }

            var table = new Huffman(counts, body.Slice(i + 17, total));
            (kind == 0 ? dc : ac)[id] = table;
            i += 17 + total;
        }
    }

    // the orientation tag (0x0112) of the first IFD of the TIFF structure inside EXIF
    private static int ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return 1;
        }

        var little = tiff[0] == (byte)'I';
        var ifd = U32(tiff, 4, little);
        if (ifd <= 0 || ifd + 2 > tiff.Length)
        {
            return 1;
        }

        var count = U16(tiff, (int)ifd, little);
        for (var e = 0; e < count; e++)
        {
            var entry = (int)ifd + 2 + e * 12;
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            if (U16(tiff, entry, little) == 0x0112)
            {
                var value = U16(tiff, entry + 8, little);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }

    private static int U16(ReadOnlySpan<byte> t, int at, bool little) =>
        at + 2 > t.Length ? 0 : little ? BinaryPrimitives.ReadUInt16LittleEndian(t[at..]) : BinaryPrimitives.ReadUInt16BigEndian(t[at..]);

    private static long U32(ReadOnlySpan<byte> t, int at, bool little) =>
        at + 4 > t.Length ? 0 : little ? BinaryPrimitives.ReadUInt32LittleEndian(t[at..]) : BinaryPrimitives.ReadUInt32BigEndian(t[at..]);

    // one scan: entropy-coded data until the next marker (other than restart markers); returns where it ends
    private static int Scan(byte[] data, int start, ReadOnlySpan<byte> header, Frame frame, int[][] quant, Huffman?[] dc, Huffman?[] ac, int restart)
    {
        var count = header[0];
        if (count < 1 || header.Length < 1 + 2 * count + 3)
        {
            throw new InvalidDataException("JPEG: a scan header is damaged");
        }

        var components = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var id = header[1 + 2 * i];
            var component = frame.Components.FirstOrDefault(c => c.Id == id) ?? throw new InvalidDataException("JPEG: a scan names an unknown component");
            component.Dc = dc[header[2 + 2 * i] >> 4] ?? throw new InvalidDataException("JPEG: a Huffman table is missing");
            component.Ac = ac[header[2 + 2 * i] & 15] ?? throw new InvalidDataException("JPEG: a Huffman table is missing");
            component.Predictor = 0;
            components[i] = component;
        }

        var luma = frame.Components[0];
        var lumaQuant = quant[luma.Quant] ?? throw new InvalidDataException("JPEG: a quantization table is missing");
        frame.Prepare();
        var bits = new BitReader(data, start);
        var block = new int[64];
        var single = count == 1;
        // one component alone is coded block by block over its own size; several are coded MCU by MCU
        int mcusX = frame.McusX, mcusY = frame.McusY;
        if (single)
        {
            mcusX = (int)Math.Ceiling(Math.Ceiling(frame.Width * components[0].H / (double)frame.MaxH) / 8);
            mcusY = (int)Math.Ceiling(Math.Ceiling(frame.Height * components[0].V / (double)frame.MaxV) / 8);
        }

        var todo = restart > 0 ? restart : int.MaxValue;
        for (var my = 0; my < mcusY; my++)
        {
            for (var mx = 0; mx < mcusX; mx++)
            {
                if (todo == 0)
                {
                    bits.Restart();
                    foreach (var c in components)
                    {
                        c.Predictor = 0;
                    }

                    todo = restart;
                }

                todo--;
                foreach (var c in components)
                {
                    int bw = single ? 1 : c.H, bh = single ? 1 : c.V;
                    for (var by = 0; by < bh; by++)
                    {
                        for (var bx = 0; bx < bw; bx++)
                        {
                            DecodeBlock(ref bits, c, block);
                            if (ReferenceEquals(c, luma))
                            {
                                int blockX = single ? mx : mx * c.H + bx, blockY = single ? my : my * c.V + by;
                                Idct(block, lumaQuant, c, blockX, blockY);
                            }
                        }
                    }
                }
            }
        }

        frame.Finish();
        return bits.End();
    }

    /// <summary>
    /// One scan of a progressive image (T.81, annex G): the first pass or a refinement of the DC coefficients of one
    /// or more components, or of a band of AC coefficients (Ss … Se) of one component, each value shifted by Al. The
    /// coefficients of the brightness component are kept until the end of the image; a scan without it is skipped.
    /// </summary>
    private static int ProgressiveScan(byte[] data, int start, ReadOnlySpan<byte> header, Frame frame, Huffman?[] dc, Huffman?[] ac, int restart)
    {
        var count = header[0];
        if (count < 1 || header.Length < 1 + 2 * count + 3)
        {
            throw new InvalidDataException("JPEG: a scan header is damaged");
        }

        int first = header[1 + 2 * count], last = header[2 + 2 * count];
        int high = header[3 + 2 * count] >> 4, low = header[3 + 2 * count] & 15;
        if (first > last || last > 63 || (first == 0 && last != 0) || (first > 0 && count != 1))
        {
            throw new InvalidDataException("JPEG: a progressive scan header is damaged");
        }

        var luma = frame.Components[0];
        var components = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var id = header[1 + 2 * i];
            var component = frame.Components.FirstOrDefault(c => c.Id == id) ?? throw new InvalidDataException("JPEG: a scan names an unknown component");
            component.Dc = dc[header[2 + 2 * i] >> 4];
            component.Ac = ac[header[2 + 2 * i] & 15];
            component.Predictor = 0;
            components[i] = component;
        }

        var bits = new BitReader(data, start);
        if (!components.Contains(luma))
        {
            return bits.End(); // colour only: nothing of it is needed for gray
        }

        luma.Coefficients ??= new int[luma.BlocksWide * luma.BlocksHigh * 64];
        var scratch = new int[64];
        var single = count == 1;
        int mcusX = frame.McusX, mcusY = frame.McusY;
        if (single)
        {
            mcusX = (int)Math.Ceiling(Math.Ceiling(frame.Width * luma.H / (double)frame.MaxH) / 8);
            mcusY = (int)Math.Ceiling(Math.Ceiling(frame.Height * luma.V / (double)frame.MaxV) / 8);
        }

        var endOfBands = 0;
        var todo = restart > 0 ? restart : int.MaxValue;
        for (var my = 0; my < mcusY; my++)
        {
            for (var mx = 0; mx < mcusX; mx++)
            {
                if (todo == 0)
                {
                    bits.Restart();
                    foreach (var c in components)
                    {
                        c.Predictor = 0;
                    }

                    endOfBands = 0;
                    todo = restart;
                }

                todo--;
                foreach (var c in components)
                {
                    int bw = single ? 1 : c.H, bh = single ? 1 : c.V;
                    for (var by = 0; by < bh; by++)
                    {
                        for (var bx = 0; bx < bw; bx++)
                        {
                            var block = scratch.AsSpan();
                            if (ReferenceEquals(c, luma))
                            {
                                int blockX = single ? mx : mx * c.H + bx, blockY = single ? my : my * c.V + by;
                                block = luma.Coefficients.AsSpan((blockY * luma.BlocksWide + blockX) * 64, 64);
                            }

                            if (first == 0)
                            {
                                DcPass(ref bits, c, block, high, low);
                            }
                            else if (high == 0)
                            {
                                AcFirst(ref bits, c, block, first, last, low, ref endOfBands);
                            }
                            else
                            {
                                AcRefine(ref bits, c, block, first, last, low, ref endOfBands);
                            }
                        }
                    }
                }
            }
        }

        return bits.End();
    }

    // DC: the first pass gives the difference to the previous block (shifted by Al), a refinement one more low bit
    private static void DcPass(ref BitReader bits, Component c, Span<int> block, int high, int low)
    {
        if (high == 0)
        {
            var table = c.Dc ?? throw new InvalidDataException("JPEG: a Huffman table is missing");
            var size = table.Decode(ref bits);
            c.Predictor += size == 0 ? 0 : Extend(bits.Bits(size), size);
            block[0] = c.Predictor * (1 << low);
        }
        else if (bits.Bit() == 1)
        {
            block[0] |= 1 << low;
        }
    }

    // AC, first pass of a band: runs of zeros and values as in a baseline block, and runs of whole blocks that end
    // early (end-of-band runs) – a run left over is counted down on the following blocks
    private static void AcFirst(ref BitReader bits, Component c, Span<int> block, int first, int last, int low, ref int endOfBands)
    {
        if (endOfBands > 0)
        {
            endOfBands--;
            return;
        }

        var table = c.Ac ?? throw new InvalidDataException("JPEG: a Huffman table is missing");
        for (var k = first; k <= last;)
        {
            var symbol = table.Decode(ref bits);
            int run = symbol >> 4, size = symbol & 15;
            if (size == 0)
            {
                if (run < 15)
                {
                    endOfBands = (1 << run) - 1 + (run > 0 ? bits.Bits(run) : 0);
                    return;
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > last)
            {
                throw new InvalidDataException("JPEG: a band has too many coefficients");
            }

            block[ZigZag[k]] = Extend(bits.Bits(size), size) * (1 << low);
            k++;
        }
    }

    // AC, refinement of a band: coefficients already non-zero get one more low bit (towards their sign); a new
    // coefficient of ±2^Al is placed after a given number of still-zero positions; end-of-band runs refine the rest
    private static void AcRefine(ref BitReader bits, Component c, Span<int> block, int first, int last, int low, ref int endOfBands)
    {
        var one = 1 << low;
        var k = first;
        if (endOfBands == 0)
        {
            var table = c.Ac ?? throw new InvalidDataException("JPEG: a Huffman table is missing");
            while (k <= last)
            {
                var symbol = table.Decode(ref bits);
                int run = symbol >> 4, size = symbol & 15;
                var value = 0;
                if (size != 0)
                {
                    value = bits.Bit() == 1 ? one : -one;
                }
                else if (run != 15)
                {
                    endOfBands = (1 << run) + (run > 0 ? bits.Bits(run) : 0);
                    break;
                }

                // walk forward: correct the non-zero coefficients met, count down the zeros to skip
                while (k <= last)
                {
                    var index = ZigZag[k];
                    if (block[index] != 0)
                    {
                        Correct(ref bits, ref block[index], one);
                    }
                    else
                    {
                        if (run == 0)
                        {
                            break;
                        }

                        run--;
                    }

                    k++;
                }

                if (value != 0 && k <= last)
                {
                    block[ZigZag[k]] = value;
                }

                k++;
            }
        }

        if (endOfBands > 0)
        {
            for (; k <= last; k++)
            {
                var index = ZigZag[k];
                if (block[index] != 0)
                {
                    Correct(ref bits, ref block[index], one);
                }
            }

            endOfBands--;
        }
    }

    private static void Correct(ref BitReader bits, ref int coefficient, int one)
    {
        if (bits.Bit() == 1 && (coefficient & one) == 0)
        {
            coefficient += coefficient >= 0 ? one : -one;
        }
    }

    private static void DecodeBlock(ref BitReader bits, Component c, Span<int> block)
    {
        block.Clear();
        var size = c.Dc!.Decode(ref bits);
        c.Predictor += size == 0 ? 0 : Extend(bits.Bits(size), size);
        block[0] = c.Predictor;
        for (var k = 1; k < 64;)
        {
            var symbol = c.Ac!.Decode(ref bits);
            int run = symbol >> 4, s = symbol & 15;
            if (s == 0)
            {
                if (run != 15)
                {
                    break; // end of block
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > 63)
            {
                throw new InvalidDataException("JPEG: a block has too many coefficients");
            }

            block[ZigZag[k]] = Extend(bits.Bits(s), s);
            k++;
        }
    }

    private static int Extend(int value, int size) => value < 1 << (size - 1) ? value - (1 << size) + 1 : value;

    // dequantize, inverse DCT and store the 8 × 8 pixels of a block of the brightness component
    private static void Idct(Span<int> block, int[] quant, Component c, int blockX, int blockY)
    {
        Span<float> coefficients = stackalloc float[64];
        Span<float> rows = stackalloc float[64];
        for (var i = 0; i < 64; i++)
        {
            coefficients[i] = block[i] * quant[i];
        }

        // rows[y, u] = Σ_v B[y, v] · F[v, u]; then pixel[y, x] = Σ_u B[x, u] · rows[y, u]
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                float sum = 0;
                for (var v = 0; v < 8; v++)
                {
                    sum += Basis[y * 8 + v] * coefficients[v * 8 + u];
                }

                rows[y * 8 + u] = sum;
            }
        }

        var plane = c.Plane!;
        var stride = c.BlocksWide * 8;
        for (var y = 0; y < 8; y++)
        {
            var row = (blockY * 8 + y) * stride + blockX * 8;
            if (blockY * 8 + y >= c.BlocksHigh * 8)
            {
                break;
            }

            for (var x = 0; x < 8; x++)
            {
                float sum = 0;
                for (var u = 0; u < 8; u++)
                {
                    sum += Basis[x * 8 + u] * rows[y * 8 + u];
                }

                plane[row + x] = (byte)Math.Clamp((int)MathF.Round(sum + 128), 0, 255);
            }
        }
    }

    private static float[] MakeBasis()
    {
        var basis = new float[64];
        for (var x = 0; x < 8; x++)
        {
            for (var u = 0; u < 8; u++)
            {
                var cu = u == 0 ? Math.Sqrt(0.5) : 1;
                basis[x * 8 + u] = (float)(cu / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            }
        }

        return basis;
    }

    private sealed class Component(int id, int h, int v, int quant)
    {
        public int Id { get; } = id;

        public int H { get; } = h;

        public int V { get; } = v;

        public int Quant { get; } = quant;

        public Huffman? Dc { get; set; }

        public Huffman? Ac { get; set; }

        public int Predictor { get; set; }

        public int BlocksWide { get; set; }

        public int BlocksHigh { get; set; }

        public byte[]? Plane { get; set; }

        /// <summary>The coefficients of every block, kept between the scans of a progressive image (brightness only).</summary>
        public int[]? Coefficients { get; set; }
    }

    private sealed class Frame
    {
        private Frame(int width, int height, Component[] components)
        {
            Width = width;
            Height = height;
            Components = components;
            MaxH = components.Max(c => c.H);
            MaxV = components.Max(c => c.V);
            McusX = (width + 8 * MaxH - 1) / (8 * MaxH);
            McusY = (height + 8 * MaxV - 1) / (8 * MaxV);
            foreach (var c in components)
            {
                c.BlocksWide = McusX * c.H;
                c.BlocksHigh = McusY * c.V;
            }
        }

        public int Width { get; }

        public int Height { get; }

        public Component[] Components { get; }

        public int MaxH { get; }

        public int MaxV { get; }

        public int McusX { get; }

        public int McusY { get; }

        public GrayImage? Gray { get; private set; }

        public static Frame Read(ReadOnlySpan<byte> body)
        {
            if (body.Length < 6 || body[0] != 8)
            {
                throw new NotSupportedException("JPEG: only 8-bit images are read");
            }

            int height = Read16(body, 1), width = Read16(body, 3), count = body[5];
            if (width == 0 || height == 0 || count is not (1 or 3 or 4) || body.Length < 6 + 3 * count)
            {
                throw new InvalidDataException("JPEG: the frame header is damaged");
            }

            if ((long)width * height > GrayImage.MaxPixels)
            {
                throw new InvalidDataException($"JPEG: an image of {width} × {height} is larger than IRIS reads");
            }

            var components = new Component[count];
            for (var i = 0; i < count; i++)
            {
                var sampling = body[7 + 3 * i];
                int h = sampling >> 4, v = sampling & 15;
                if (h is < 1 or > 4 || v is < 1 or > 4 || body[8 + 3 * i] > 3)
                {
                    throw new InvalidDataException("JPEG: a component header is damaged");
                }

                components[i] = new Component(body[6 + 3 * i], h, v, body[8 + 3 * i]);
            }

            return new Frame(width, height, components);
        }

        public void Prepare()
        {
            var luma = Components[0];
            luma.Plane ??= new byte[luma.BlocksWide * 8 * luma.BlocksHigh * 8];
        }

        // the brightness plane at the size of the image (stretched when its sampling is below the largest)
        public void Finish()
        {
            var luma = Components[0];
            var stride = luma.BlocksWide * 8;
            var gray = new GrayImage(Width, Height);
            for (var y = 0; y < Height; y++)
            {
                var sy = y * luma.V / MaxV;
                for (var x = 0; x < Width; x++)
                {
                    gray.Pixels[y * Width + x] = luma.Plane![sy * stride + x * luma.H / MaxH];
                }
            }

            Gray = gray;
        }
    }

    // a Huffman table: for each code length the first code, the last code and where its symbols start
    private sealed class Huffman
    {
        private readonly int[] _maxCode = new int[18];
        private readonly int[] _offset = new int[17];
        private readonly byte[] _symbols;

        public Huffman(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            _symbols = symbols.ToArray();
            int code = 0, k = 0;
            for (var length = 1; length <= 16; length++)
            {
                _offset[length] = k - code;
                code += counts[length - 1];
                k += counts[length - 1];
                _maxCode[length] = counts[length - 1] == 0 ? -1 : code - 1;
                code <<= 1;
            }

            _maxCode[17] = int.MaxValue;
        }

        public int Decode(ref BitReader bits)
        {
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                code = (code << 1) | bits.Bit();
                if (code <= _maxCode[length])
                {
                    var index = code + _offset[length];
                    if ((uint)index >= (uint)_symbols.Length)
                    {
                        break;
                    }

                    return _symbols[index];
                }
            }

            throw new InvalidDataException("JPEG: a Huffman code is damaged");
        }
    }

    // reads bits of the entropy-coded data: a 0xFF byte is followed by a stuffed 0x00; a marker stops the data
    private ref struct BitReader(byte[] data, int position)
    {
        private readonly byte[] _data = data;
        private int _position = position;
        private int _buffer;
        private int _count;
        private bool _marker;

        public int Bit()
        {
            if (_count == 0)
            {
                Fill();
            }

            _count--;
            return (_buffer >> _count) & 1;
        }

        public int Bits(int n)
        {
            var value = 0;
            for (var i = 0; i < n; i++)
            {
                value = (value << 1) | Bit();
            }

            return value;
        }

        // after a restart interval: drop the rest of the byte and step over the RSTn marker
        public void Restart()
        {
            _count = 0;
            if (!_marker)
            {
                while (_position + 1 < _data.Length && !(_data[_position] == 0xFF && _data[_position + 1] is >= 0xD0 and <= 0xD7))
                {
                    _position++;
                }
            }

            if (_position + 1 < _data.Length && _data[_position] == 0xFF && _data[_position + 1] is >= 0xD0 and <= 0xD7)
            {
                _position += 2;
            }

            _marker = false;
        }

        // where the scan ends: the next marker that is not a restart marker
        public readonly int End()
        {
            var p = _position;
            while (p + 1 < _data.Length && !(_data[p] == 0xFF && _data[p + 1] != 0 && _data[p + 1] is not (>= 0xD0 and <= 0xD7)))
            {
                p++;
            }

            return p;
        }

        private void Fill()
        {
            if (_marker || _position >= _data.Length)
            {
                // past the data: zero bits (a damaged file ends in gray, it does not crash)
                _buffer = 0;
                _count = 8;
                return;
            }

            var b = _data[_position];
            if (b == 0xFF)
            {
                var next = _position + 1 < _data.Length ? _data[_position + 1] : 0;
                if (next == 0)
                {
                    _position += 2;
                }
                else
                {
                    _marker = true;
                    _buffer = 0;
                    _count = 8;
                    return;
                }
            }
            else
            {
                _position++;
            }

            _buffer = b;
            _count = 8;
        }
    }
}
