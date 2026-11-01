using System.Text;
using System.Text.Json;

namespace Iris.Assistant.Study;

/// <summary>
/// A sentence and its verified translation (e.g. from a printed book), kept for learning. Source says where it came
/// from in the owner's words (a book, a field) – free text, it stays in the owner's file only.
/// </summary>
public sealed record StudyPair(string Source, string Target, string? Label, DateTimeOffset Added);

/// <summary>
/// The verified pairs of one direction, in a local file of JSON lines (IrisPaths.Study). Only these and the owner's
/// corrections are learned from – never a translation of another program. Nothing leaves the computer.
/// </summary>
public sealed class StudyRoom
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<StudyPair> _pairs = [];

    private StudyRoom(string path) => _path = path;

    public static StudyRoom Open(string path)
    {
        var room = new StudyRoom(path);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                try
                {
                    if (JsonSerializer.Deserialize(line, HistoryJson.Default.StudyPair) is { } pair && Usable(pair.Source, pair.Target))
                    {
                        room._pairs.Add(pair);
                    }
                }
                catch (JsonException)
                {
                    // a line cut off by a crash: the rest still counts
                }
            }
        }

        return room;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pairs.Count;
            }
        }
    }

    /// <summary>The pairs in the order they were added.</summary>
    public IReadOnlyList<StudyPair> Pairs
    {
        get
        {
            lock (_gate)
            {
                return [.. _pairs];
            }
        }
    }

    /// <summary>Adds pairs (empty ones and exact repeats are left out); returns how many were added.</summary>
    public int Add(IEnumerable<(string Source, string Target)> pairs, string? label = null)
    {
        lock (_gate)
        {
            var known = _pairs.Select(p => (p.Source, p.Target)).ToHashSet();
            var added = new List<StudyPair>();
            foreach (var (source, target) in pairs)
            {
                var (s, t) = (source.Trim(), target.Trim());
                if (Usable(s, t) && known.Add((s, t)))
                {
                    added.Add(new StudyPair(s, t, string.IsNullOrWhiteSpace(label) ? null : label.Trim(), DateTimeOffset.Now));
                }
            }

            if (added.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.AppendAllText(_path, string.Concat(added.Select(p => JsonSerializer.Serialize(p, HistoryJson.Default.StudyPair) + "\n")),
                    new UTF8Encoding(false));
                _pairs.AddRange(added);
            }

            return added.Count;
        }
    }

    /// <summary>Removes one pair (the file is rewritten).</summary>
    public bool Remove(StudyPair pair)
    {
        lock (_gate)
        {
            if (!_pairs.Remove(pair))
            {
                return false;
            }

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, string.Concat(_pairs.Select(p => JsonSerializer.Serialize(p, HistoryJson.Default.StudyPair) + "\n")),
                new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
    }

    private static bool Usable(string source, string target) =>
        source.Trim().Length > 0 && target.Trim().Length > 0 && !source.Contains('\n') && !target.Contains('\n');
}
