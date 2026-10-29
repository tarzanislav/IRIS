using System.Text.Json;
using Iris.Tokenization;

namespace Iris.Translation;

/// <summary>
/// The tokenizer of Marian models: the source text is cut by the source SentencePiece model, the pieces become ids
/// of the shared vocabulary (unknown pieces the unknown id) and the end of sentence is added. Special tokens written
/// in the text stay whole, a language code "&gt;&gt;xx&lt;&lt;" at the start becomes one token. Translations are put back
/// together by the target SentencePiece model, without special tokens. Gives the same ids and text as the reference
/// (MarianTokenizer of Hugging Face transformers, without Moses punctuation normalization).
/// </summary>
public sealed class MarianTokenizer
{
    private const string SpaceSymbol = "▁";

    private readonly SentencePieceTokenizer _source;
    private readonly SentencePieceTokenizer _target;
    private readonly Dictionary<string, int> _vocabulary;
    private readonly string?[] _pieces;
    private readonly string[] _specials;
    private readonly HashSet<int> _specialIds;

    public MarianTokenizer(SentencePieceModel source, SentencePieceModel target, IReadOnlyDictionary<string, int> vocabulary,
        IReadOnlyDictionary<string, int> specialTokens, int unknownId, int eosId)
    {
        _source = new SentencePieceTokenizer(source);
        _target = new SentencePieceTokenizer(target);
        _vocabulary = new Dictionary<string, int>(vocabulary, StringComparer.Ordinal);
        UnknownId = unknownId;
        EosId = eosId;
        var size = vocabulary.Count == 0 ? 0 : vocabulary.Values.Max() + 1;
        _pieces = new string?[size];
        foreach (var (piece, id) in vocabulary)
        {
            if (id >= 0)
            {
                _pieces[id] = piece;
            }
        }

        // longest first, so that the longest special token wins where several start
        _specials = [.. specialTokens.Keys.Where(s => s.Length > 0).OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal)];
        _specialIds = [.. specialTokens.Values, unknownId, eosId];
    }

    public int UnknownId { get; }

    public int EosId { get; }

    public int VocabularySize => _pieces.Length;

    /// <summary>Reads the vocabulary (vocab.json of a Marian model: piece → id).</summary>
    public static Dictionary<string, int> ReadVocabulary(ReadOnlySpan<byte> json)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);
        foreach (var item in document.RootElement.EnumerateObject())
        {
            vocabulary[item.Name] = item.Value.GetInt32();
        }

        return vocabulary;
    }

    /// <summary>The ids of a source text, the end of sentence included.</summary>
    public int[] Encode(string text)
    {
        var ids = new List<int>();
        foreach (var (part, special) in Split(text))
        {
            if (special)
            {
                ids.Add(IdOf(part));
                continue;
            }

            var rest = part;
            if (rest.StartsWith(">>", StringComparison.Ordinal) && rest.IndexOf("<<", StringComparison.Ordinal) is var end and >= 0)
            {
                ids.Add(IdOf(rest[..(end + 2)]));
                rest = rest[(end + 2)..];
            }

            foreach (var piece in _source.Encode(rest))
            {
                ids.Add(IdOf(piece.Text));
            }
        }

        ids.Add(EosId);
        return [.. ids];
    }

    /// <summary>The text of a translation: special tokens are left out, the pieces are joined by the target model.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var pieces = ids.Where(id => !_specialIds.Contains(id)).Select(PieceOf);
        return _target.Decode(pieces).Replace(SpaceSymbol, " ", StringComparison.Ordinal).Trim();
    }

    private int IdOf(string piece) => _vocabulary.TryGetValue(piece, out var id) ? id : UnknownId;

    private string PieceOf(int id) => (uint)id < (uint)_pieces.Length && _pieces[id] is { } piece ? piece : _pieces[UnknownId] ?? "<unk>";

    // the text cut at special tokens (leftmost, longest first); empty parts are dropped
    private IEnumerable<(string Part, bool Special)> Split(string text)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var match = _specials.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0);
            if (match is null)
            {
                i++;
                continue;
            }

            if (i > start)
            {
                yield return (text[start..i], false);
            }

            yield return (match, true);
            i += match.Length;
            start = i;
        }

        if (text.Length > start)
        {
            yield return (text[start..], false);
        }
    }
}
