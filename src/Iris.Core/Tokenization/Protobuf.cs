namespace Iris.Tokenization;

/// <summary>
/// A minimal reader of the protocol buffers wire format – enough for the model files of SentencePiece: varints,
/// fixed 32/64-bit values and length-delimited fields. Unknown fields are skipped.
/// </summary>
internal ref struct ProtobufReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public bool End => _position >= _data.Length;

    /// <summary>The next field: its number and wire type (0 varint, 1 fixed64, 2 length-delimited, 5 fixed32).</summary>
    public (int Field, int WireType) ReadTag()
    {
        var tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 7));
    }

    public ulong ReadVarint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _data.Length)
            {
                throw new FormatException("protobuf: varint past the end");
            }

            var b = _data[_position++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }

        throw new FormatException("protobuf: varint too long");
    }

    public float ReadFloat()
    {
        var value = BitConverter.ToSingle(Take(4));
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        return length > (ulong)(_data.Length - _position) ? throw new FormatException("protobuf: field past the end") : Take((int)length);
    }

    public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadBytes());

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                Take(8);
                break;
            case 2:
                ReadBytes();
                break;
            case 5:
                Take(4);
                break;
            default:
                throw new FormatException($"protobuf: wire type {wireType} is not supported");
        }
    }

    private ReadOnlySpan<byte> Take(int length)
    {
        if (_position + length > _data.Length)
        {
            throw new FormatException("protobuf: value past the end");
        }

        var slice = _data.Slice(_position, length);
        _position += length;
        return slice;
    }
}
