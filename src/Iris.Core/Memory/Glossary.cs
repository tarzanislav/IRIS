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
/// </summary>
public sealed class Glossary
{
    private readonly Dictionary<string, GlossaryEntry> _entries = new(StringComparer.Ordinal);
    private string[] _byLength = [];

    public int Count => _entries.Count;

    public IEnumerable<GlossaryEntry> Entries => _entries.Values;

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
        File.WriteAllLines(path, _entries.Values.Select(e => e.Target is null ? e.Source : $"{e.Source}\t{e.Target}"), new UTF8Encoding(false));

    /// <summary>Adds or replaces a term; target null = keep it as written.</summary>
    public void Add(string source, string? target = null)
    {
        source = source.Trim();
        if (source.Length == 0 || source.Contains('\t'))
        {
            throw new ArgumentException("a glossary term must have text and no tab");
        }

        _entries[source] = new GlossaryEntry(source, target?.Trim());
        _byLength = [.. _entries.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)];
    }

    public bool Remove(string source)
    {
        var removed = _entries.Remove(source.Trim());
        _byLength = [.. _entries.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)];
        return removed;
    }

    /// <summary>The terms in a sentence, left to right, not overlapping; at each place the longest term wins.</summary>
    public List<TermMatch> Find(string sentence)
    {
        var matches = new List<TermMatch>();
        var i = 0;
        while (i < sentence.Length)
        {
            var found = false;
            if (i == 0 || !IsWordCharacter(sentence[i - 1]))
            {
                foreach (var term in _byLength)
                {
                    var end = i + term.Length;
                    if (end <= sentence.Length && string.CompareOrdinal(sentence, i, term, 0, term.Length) == 0 &&
                        (end == sentence.Length || !IsWordCharacter(sentence[end])))
                    {
                        matches.Add(new TermMatch(i, term.Length, _entries[term]));
                        i = end;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                i++;
            }
        }

        return matches;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
