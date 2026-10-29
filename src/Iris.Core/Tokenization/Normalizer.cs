using System.Buffers.Binary;
using System.Text;

namespace Iris.Tokenization;

/// <summary>
/// The text normalization of a SentencePiece model, as the model file describes it: replacement rules compiled into
/// a double-array trie (e.g. NFKC), leading, trailing and repeated spaces dropped, a space put in front, and spaces
/// written as "▁". Works on UTF-8 bytes exactly as SentencePiece does, so the same text gives the same pieces.
/// </summary>
internal sealed class Normalizer
{
    /// <summary>"▁" (U+2581), how SentencePiece writes a space.</summary>
    public const string SpaceSymbol = "▁";

    private static readonly byte[] SpaceSymbolBytes = Encoding.UTF8.GetBytes(SpaceSymbol);
    private static readonly byte[] Replacement = [0xEF, 0xBF, 0xBD];

    private readonly uint[] _trie;
    private readonly byte[] _normalized;
    private readonly bool _addDummyPrefix;
    private readonly bool _removeExtraWhitespaces;
    private readonly bool _escapeWhitespaces;
    private readonly bool _treatWhitespaceAsSuffix;
    private readonly byte[][] _userDefined;

    public Normalizer(SentencePieceModel model)
    {
        (_trie, _normalized) = Decode(model.PrecompiledCharsmap);
        _addDummyPrefix = model.AddDummyPrefix;
        _removeExtraWhitespaces = model.RemoveExtraWhitespaces;
        _escapeWhitespaces = model.EscapeWhitespaces;
        _treatWhitespaceAsSuffix = model.TreatWhitespaceAsSuffix;

        // user-defined pieces are kept as they are written, never normalized
        _userDefined = model.Pieces.Where(p => p.Type == PieceType.UserDefined && p.Text.Length > 0)
            .Select(p => Encoding.UTF8.GetBytes(p.Text)).OrderByDescending(b => b.Length).ToArray();
    }

    /// <summary>The normalized text (UTF-8).</summary>
    public byte[] Normalize(string text) => Normalize(Encoding.UTF8.GetBytes(text));

    public byte[] Normalize(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + 8);
        if (input.IsEmpty)
        {
            return [];
        }

        // leading spaces
        if (_removeExtraWhitespaces)
        {
            while (!input.IsEmpty)
            {
                var (piece, consumed) = NormalizePrefix(input);
                if (piece.Length != 1 || piece[0] != (byte)' ')
                {
                    break;
                }

                input = input[consumed..];
            }
        }

        if (input.IsEmpty)
        {
            return [];
        }

        if (!_treatWhitespaceAsSuffix && _addDummyPrefix)
        {
            AddSpace(output);
        }

        var previousSpace = _removeExtraWhitespaces;
        while (!input.IsEmpty)
        {
            var (piece, consumed) = NormalizePrefix(input);
            var sp = piece.AsSpan();
            while (previousSpace && !sp.IsEmpty && sp[0] == (byte)' ')
            {
                sp = sp[1..];
            }

            if (!sp.IsEmpty)
            {
                foreach (var b in sp)
                {
                    if (_escapeWhitespaces && b == (byte)' ')
                    {
                        output.AddRange(SpaceSymbolBytes);
                    }
                    else
                    {
                        output.Add(b);
                    }
                }

                previousSpace = sp[^1] == (byte)' ';
            }

            input = input[consumed..];
            if (!_removeExtraWhitespaces)
            {
                previousSpace = false;
            }
        }

        // trailing spaces
        if (_removeExtraWhitespaces)
        {
            var space = _escapeWhitespaces ? SpaceSymbolBytes : new[] { (byte)' ' };
            while (output.Count >= space.Length && System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output)[^space.Length..].SequenceEqual(space))
            {
                output.RemoveRange(output.Count - space.Length, space.Length);
            }
        }

        if (_treatWhitespaceAsSuffix && _addDummyPrefix)
        {
            AddSpace(output);
        }

        return [.. output];
    }

    private void AddSpace(List<byte> output)
    {
        if (_escapeWhitespaces)
        {
            output.AddRange(SpaceSymbolBytes);
        }
        else
        {
            output.Add((byte)' ');
        }
    }

    // the replacement of the longest rule matching the start of the input, or its first character as it is
    private (byte[] Piece, int Consumed) NormalizePrefix(ReadOnlySpan<byte> input)
    {
        foreach (var symbol in _userDefined)
        {
            if (input.StartsWith(symbol))
            {
                return (symbol, symbol.Length);
            }
        }

        var (length, value) = LongestRule(input);
        if (length > 0)
        {
            var end = Array.IndexOf(_normalized, (byte)0, value);
            return (_normalized[value..(end < 0 ? _normalized.Length : end)], length);
        }

        var charLength = Utf8Length(input);
        return charLength == 0 ? (Replacement, 1) : (input[..charLength].ToArray(), charLength);
    }

    // the longest key of the double-array trie that starts the input: its length in bytes and its value
    private (int Length, int Value) LongestRule(ReadOnlySpan<byte> key)
    {
        if (_trie.Length == 0)
        {
            return (0, 0);
        }

        int longest = 0, value = 0;
        var node = (int)Offset(_trie[0]);
        for (var i = 0; i < key.Length; i++)
        {
            node ^= key[i];
            if ((uint)node >= (uint)_trie.Length)
            {
                break;
            }

            var unit = _trie[node];
            if ((unit & ((1u << 31) | 0xFF)) != key[i])
            {
                break;
            }

            node ^= (int)Offset(unit);
            if (((unit >> 8) & 1) != 0)
            {
                longest = i + 1;
                value = (int)(_trie[node] & ((1u << 31) - 1));
            }
        }

        return (longest, value);
    }

    private static uint Offset(uint unit) => (unit >> 10) << (int)((unit & (1u << 9)) >> 6);

    /// <summary>The length of the valid UTF-8 character at the start, or 0 when the bytes are not valid UTF-8.</summary>
    internal static int Utf8Length(ReadOnlySpan<byte> s)
    {
        var b = s[0];
        int length = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
        if (length == 0 || length > s.Length)
        {
            return 0;
        }

        for (var i = 1; i < length; i++)
        {
            if ((s[i] & 0xC0) != 0x80)
            {
                return 0;
            }
        }

        // overlong forms, surrogates and values past U+10FFFF are not valid
        var rune = length switch
        {
            1 => b,
            2 => ((b & 0x1F) << 6) | (s[1] & 0x3F),
            3 => ((b & 0x0F) << 12) | ((s[1] & 0x3F) << 6) | (s[2] & 0x3F),
            _ => ((b & 0x07) << 18) | ((s[1] & 0x3F) << 12) | ((s[2] & 0x3F) << 6) | (s[3] & 0x3F),
        };
        var valid = length switch
        {
            1 => true,
            2 => rune >= 0x80,
            3 => rune >= 0x800 && rune is < 0xD800 or > 0xDFFF,
            _ => rune is >= 0x10000 and <= 0x10FFFF,
        };
        return valid ? length : 0;
    }

    // the blob: the trie's size in bytes (little endian), the trie, then the replacements separated by zero bytes
    private static (uint[] Trie, byte[] Normalized) Decode(byte[] blob)
    {
        if (blob.Length == 0)
        {
            return ([], []);
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (size % 4 != 0 || 4 + size > blob.Length)
        {
            throw new FormatException("SentencePiece: damaged normalization rules");
        }

        var trie = new uint[size / 4];
        for (var i = 0; i < trie.Length; i++)
        {
            trie[i] = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4 + 4 * i));
        }

        return (trie, blob[(4 + (int)size)..]);
    }
}
