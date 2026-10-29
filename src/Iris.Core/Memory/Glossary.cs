using System.Text;

namespace Iris.Memory;

/// <summary>A name or title: kept as it is written (Target null) or always translated as Target.</summary>
public sealed record GlossaryEntry(string Source, string? Target)
{
    public string Output => Target ?? Source;
}

/// <summary>A place in a sentence where a glossary term stands.</summary>
public readonly record struct TermMatch(int Start, int Length, GlossaryEntry Entry);

/// <summary>
/// The glossary of names and titles (films, series, people – UNIVERSUM fills it from TMDB and HISTRA): terms the
/// model must not translate on its own. Terms are found as whole words, with exact letters, longest first; the
/// translator hides them from the model behind placeholders and puts the wanted form back into the translation.
/// File format: one term per line, "source TAB target" (no target = keep as written), "#" starts a comment.
/// Safe to use from several threads of one program.
/// </summary>
public sealed class Glossary
{
    private readonly Dictionary<string, GlossaryEntry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    // the lengths of the terms, longest first; found again when first needed after a change
    private int[]? _lengths;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>A copy of the terms.</summary>
    public IReadOnlyList<GlossaryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    public static Glossary Load(string path)
    {
        var glossary = new Glossary();
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t');
            glossary.Add(parts[0], parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1] : null);
        }

        return glossary;
    }

    public void Save(string path) =>
        File.WriteAllText(path, string.Concat(Entries.Select(e => (e.Target is null ? e.Source : $"{e.Source}\t{e.Target}") + "\n")),
            new UTF8Encoding(false));

    /// <summary>Adds or replaces a term; target null = keep it as written.</summary>
    public void Add(string source, string? target = null)
    {
        source = source.Trim();
        if (source.Length == 0 || source.Contains('\t') || (target?.Contains('\t') ?? false))
        {
            throw new ArgumentException("a glossary term must have text and no tab");
        }

        lock (_gate)
        {
            _entries[source] = new GlossaryEntry(source, target?.Trim());
            _lengths = null;
        }
    }

    public bool Remove(string source)
    {
        lock (_gate)
        {
            _lengths = null;
            return _entries.Remove(source.Trim());
        }
    }

    /// <summary>The terms in a sentence, left to right, not overlapping; at each place the longest term wins.</summary>
    public List<TermMatch> Find(string sentence)
    {
        lock (_gate)
        {
            // at the start of each word, a hash lookup for each length of term (longest first): the time does not
            // grow with the number of terms, even when thousands of titles begin alike
            var lengths = _lengths ??= [.. _entries.Keys.Select(term => term.Length).Distinct().OrderDescending()];
            var lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
            var matches = new List<TermMatch>();
            var i = 0;
            while (i < sentence.Length)
            {
                GlossaryEntry? found = null;
                if (i == 0 || !IsWordCharacter(sentence[i - 1]))
                {
                    foreach (var length in lengths)
                    {
                        var end = i + length;
                        if (end <= sentence.Length && (end == sentence.Length || !IsWordCharacter(sentence[end])) &&
                            lookup.TryGetValue(sentence.AsSpan(i, length), out found))
                        {
                            break;
                        }
                    }
                }

                if (found is null)
                {
                    i++;
                    continue;
                }

                matches.Add(new TermMatch(i, found.Source.Length, found));
                i += found.Source.Length;
            }

            return matches;
        }
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
