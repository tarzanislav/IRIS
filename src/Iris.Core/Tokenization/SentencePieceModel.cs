namespace Iris.Tokenization;

/// <summary>The kinds of pieces of a SentencePiece model.</summary>
public enum PieceType
{
    Normal = 1,
    Unknown = 2,
    Control = 3,
    UserDefined = 4,
    Unused = 5,
    Byte = 6,
}

/// <summary>A piece of the vocabulary: its text, its score (log probability for unigram) and its kind.</summary>
public sealed record Piece(string Text, float Score, PieceType Type);

/// <summary>
/// A SentencePiece model (the ".spm"/".model" file): its pieces, its algorithm and how text is normalized before it
/// is cut. Read with an own protobuf reader; only what tokenizing needs is kept.
/// </summary>
public sealed class SentencePieceModel
{
    public required IReadOnlyList<Piece> Pieces { get; init; }

    /// <summary>1 unigram, 2 BPE, 3 word, 4 char.</summary>
    public int ModelType { get; init; } = 1;

    public bool ByteFallback { get; init; }

    public int UnknownId { get; init; }

    public string UnknownSurface { get; init; } = " ⁇ ";

    public string NormalizerName { get; init; } = "nmt_nfkc";

    /// <summary>The compiled normalization rules (a double-array trie of replacements); empty = no rules.</summary>
    public byte[] PrecompiledCharsmap { get; init; } = [];

    public bool AddDummyPrefix { get; init; } = true;

    public bool RemoveExtraWhitespaces { get; init; } = true;

    public bool EscapeWhitespaces { get; init; } = true;

    public bool TreatWhitespaceAsSuffix { get; init; }

    public static SentencePieceModel Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Reads a serialized ModelProto.</summary>
    public static SentencePieceModel Parse(ReadOnlySpan<byte> data)
    {
        var pieces = new List<Piece>();
        int modelType = 1, unknownId = 0;
        var byteFallback = false;
        var treatWhitespaceAsSuffix = false;
        var unknownSurface = " ⁇ ";
        string name = "nmt_nfkc";
        byte[] charsmap = [];
        bool addDummyPrefix = true, removeExtra = true, escape = true;
        var reader = new ProtobufReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    pieces.Add(ReadPiece(reader.ReadBytes()));
                    break;
                case 2 when wire == 2:
                    var trainer = new ProtobufReader(reader.ReadBytes());
                    while (!trainer.End)
                    {
                        var (f, w) = trainer.ReadTag();
                        switch (f)
                        {
                            case 3 when w == 0: modelType = (int)trainer.ReadVarint(); break;
                            case 24 when w == 0: treatWhitespaceAsSuffix = trainer.ReadVarint() != 0; break;
                            case 35 when w == 0: byteFallback = trainer.ReadVarint() != 0; break;
                            case 40 when w == 0: unknownId = (int)trainer.ReadVarint(); break;
                            case 44 when w == 2: unknownSurface = trainer.ReadString(); break;
                            default: trainer.Skip(w); break;
                        }
                    }

                    break;
                case 3 when wire == 2:
                    var normalizer = new ProtobufReader(reader.ReadBytes());
                    while (!normalizer.End)
                    {
                        var (f, w) = normalizer.ReadTag();
                        switch (f)
                        {
                            case 1 when w == 2: name = normalizer.ReadString(); break;
                            case 2 when w == 2: charsmap = normalizer.ReadBytes().ToArray(); break;
                            case 3 when w == 0: addDummyPrefix = normalizer.ReadVarint() != 0; break;
                            case 4 when w == 0: removeExtra = normalizer.ReadVarint() != 0; break;
                            case 5 when w == 0: escape = normalizer.ReadVarint() != 0; break;
                            default: normalizer.Skip(w); break;
                        }
                    }

                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (pieces.Count == 0)
        {
            throw new FormatException("SentencePiece model without pieces");
        }

        return new SentencePieceModel
        {
            Pieces = pieces,
            ModelType = modelType,
            ByteFallback = byteFallback,
            UnknownId = unknownId,
            UnknownSurface = unknownSurface,
            NormalizerName = name,
            PrecompiledCharsmap = charsmap,
            AddDummyPrefix = addDummyPrefix,
            RemoveExtraWhitespaces = removeExtra,
            EscapeWhitespaces = escape,
            TreatWhitespaceAsSuffix = treatWhitespaceAsSuffix,
        };
    }

    private static Piece ReadPiece(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufReader(data);
        string text = "";
        float score = 0;
        var type = PieceType.Normal;
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2: text = reader.ReadString(); break;
                case 2 when wire == 5: score = reader.ReadFloat(); break;
                case 3 when wire == 0: type = (PieceType)(int)reader.ReadVarint(); break;
                default: reader.Skip(wire); break;
            }
        }

        return new Piece(text, score, type);
    }
}
