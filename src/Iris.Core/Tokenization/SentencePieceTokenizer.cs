using System.Text;

namespace Iris.Tokenization;

/// <summary>A piece of a cut sentence: its text (normalized, "▁" for spaces) and its id in the SentencePiece model.</summary>
public readonly record struct TokenPiece(string Text, int Id);

/// <summary>
/// Cuts text into the pieces of a SentencePiece unigram model – the most probable split (Viterbi) over the
/// normalized text, unknown characters as the unknown piece (consecutive ones merged, or as byte pieces when the
/// model has byte fallback) – and joins pieces back into text. Gives the same pieces as SentencePiece.
/// </summary>
public sealed class SentencePieceTokenizer
{
    private const float UnknownPenalty = 10f;

    private readonly SentencePieceModel _model;
    private readonly Normalizer _normalizer;
    private readonly Dictionary<string, int> _pieces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _allPieces = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, int> _bytes = [];
    private readonly int _maxPieceBytes;
    private readonly float _minScore = float.MaxValue;
    private readonly float _maxScore = float.MinValue;

    public SentencePieceTokenizer(SentencePieceModel model)
    {
        if (model.ModelType != 1)
        {
            throw new NotSupportedException($"SentencePiece: only unigram models are supported (model type {model.ModelType})");
        }

        _model = model;
        _normalizer = new Normalizer(model);
        for (var id = 0; id < model.Pieces.Count; id++)
        {
            var piece = model.Pieces[id];
            _allPieces.TryAdd(piece.Text, id);
            switch (piece.Type)
            {
                case PieceType.Normal or PieceType.UserDefined or PieceType.Unused:
                    _pieces.TryAdd(piece.Text, id);
                    _maxPieceBytes = Math.Max(_maxPieceBytes, Encoding.UTF8.GetByteCount(piece.Text));
                    break;
                case PieceType.Byte when piece.Text.Length == 6 && piece.Text.StartsWith("<0x", StringComparison.Ordinal):
                    _bytes[Convert.ToByte(piece.Text[3..5], 16)] = id;
                    break;
            }

            if (piece.Type == PieceType.Normal)
            {
                _minScore = Math.Min(_minScore, piece.Score);
                _maxScore = Math.Max(_maxScore, piece.Score);
            }
        }
    }

    /// <summary>The pieces of a text.</summary>
    public IReadOnlyList<TokenPiece> Encode(string text)
    {
        var normalized = _normalizer.Normalize(text);
        var best = Viterbi(normalized);
        var result = new List<TokenPiece>(best.Count);
        foreach (var (start, end, id) in best)
        {
            var surface = Encoding.UTF8.GetString(normalized, start, end - start);
            if (id == _model.UnknownId && _model.ByteFallback && _bytes.Count == 256)
            {
                for (var i = start; i < end; i++)
                {
                    result.Add(new TokenPiece(_model.Pieces[_bytes[normalized[i]]].Text, _bytes[normalized[i]]));
                }
            }
            else if (id == _model.UnknownId && result.Count > 0 && result[^1].Id == _model.UnknownId)
            {
                // a run of unknown characters is one piece
                result[^1] = new TokenPiece(result[^1].Text + surface, id);
            }
            else
            {
                result.Add(new TokenPiece(surface, id));
            }
        }

        return result;
    }

    /// <summary>The text of piece ids, as SentencePiece decodes them.</summary>
    public string Decode(IEnumerable<int> ids) => Decode(ids.Select(id => (uint)id < (uint)_model.Pieces.Count
        ? (_model.Pieces[id].Text, id)
        : throw new ArgumentOutOfRangeException(nameof(ids), $"piece id {id} is not in the model")));

    /// <summary>The text of pieces (a piece the model does not know is kept as it is written).</summary>
    public string Decode(IEnumerable<string> pieces) => Decode(pieces.Select(p => (p, IdOf(p))));

    /// <summary>The id of a piece; the unknown piece's id when the model does not have it.</summary>
    public int IdOf(string piece) => _allPieces.TryGetValue(piece, out var id) ? id : _model.UnknownId;

    // control pieces vanish, unknown ones show the unknown surface, byte pieces join into UTF-8, "▁" becomes a space
    // and the space SentencePiece put in front of the text is taken away again
    private string Decode(IEnumerable<(string Text, int Id)> pieces)
    {
        var text = new StringBuilder();
        var bytes = new List<byte>();
        var start = _model.AddDummyPrefix && !_model.TreatWhitespaceAsSuffix;
        void Flush()
        {
            if (bytes.Count > 0)
            {
                text.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
                start = false;
            }
        }

        foreach (var (piece, id) in pieces)
        {
            var type = _model.Pieces[id].Type;
            if (type == PieceType.Byte && TryByte(_model.Pieces[id].Text, out var b))
            {
                bytes.Add(b);
                continue;
            }

            Flush();
            string word;
            if (type == PieceType.Control)
            {
                continue;
            }
            else if (type == PieceType.Unknown)
            {
                word = piece == _model.Pieces[id].Text ? _model.UnknownSurface : piece;
            }
            else
            {
                var p = start && piece.StartsWith(Normalizer.SpaceSymbol, StringComparison.Ordinal) ? piece[1..] : piece;
                word = p.Replace(Normalizer.SpaceSymbol, " ", StringComparison.Ordinal);
            }

            text.Append(word);
            if (word.Length > 0)
            {
                start = false;
            }
        }

        Flush();
        return text.ToString();
    }

    private static bool TryByte(string text, out byte value)
    {
        value = 0;
        return text.Length == 6 && text.StartsWith("<0x", StringComparison.Ordinal) && text[5] == '>' &&
               byte.TryParse(text.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    // the best split: for each end position (in bytes) the best piece ending there, as SentencePiece finds it
    private List<(int Start, int End, int Id)> Viterbi(byte[] text)
    {
        var size = text.Length;
        var bestId = new int[size + 1];
        var bestScore = new float[size + 1];
        var bestStart = new int[size + 1];
        Array.Fill(bestStart, -1);
        var unknownScore = _minScore - UnknownPenalty;
        var lookup = _pieces.GetAlternateLookup<ReadOnlySpan<char>>();
        // a damaged model with a huge piece must not overflow the stack
        Span<char> chars = _maxPieceBytes < 512 ? stackalloc char[_maxPieceBytes + 1] : new char[_maxPieceBytes + 1];
        var start = 0;
        while (start < size)
        {
            var here = bestScore[start];
            var charLength = Math.Max(1, Math.Min(Normalizer.Utf8Length(text.AsSpan(start)), size - start));
            var single = false;
            for (var end = start + 1; end <= size && end - start <= _maxPieceBytes; end++)
            {
                if (end < size && (text[end] & 0xC0) == 0x80)
                {
                    continue;
                }

                var count = Encoding.UTF8.GetChars(text.AsSpan(start, end - start), chars);
                if (!lookup.TryGetValue(chars[..count], out var id) || _model.Pieces[id].Type == PieceType.Unused)
                {
                    continue;
                }

                var length = end - start;
                float candidate;
                if (_model.Pieces[id].Type == PieceType.UserDefined)
                {
                    candidate = (float)(length * _maxScore - 0.1 + here);
                }
                else
                {
                    candidate = _model.Pieces[id].Score + here;
                }

                if (bestStart[end] == -1 || candidate > bestScore[end])
                {
                    bestScore[end] = candidate;
                    bestStart[end] = start;
                    bestId[end] = id;
                }

                if (length == charLength)
                {
                    single = true;
                }
            }

            if (!single)
            {
                var end = start + charLength;
                var candidate = unknownScore + here;
                if (bestStart[end] == -1 || candidate > bestScore[end])
                {
                    bestScore[end] = candidate;
                    bestStart[end] = start;
                    bestId[end] = _model.UnknownId;
                }
            }

            start += charLength;
        }

        var path = new List<(int, int, int)>();
        for (var end = size; end > 0; end = bestStart[end])
        {
            path.Add((bestStart[end], end, bestId[end]));
        }

        path.Reverse();
        return path;
    }
}
