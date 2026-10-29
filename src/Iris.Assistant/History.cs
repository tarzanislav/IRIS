using System.Text;
using System.Text.Json;

namespace Iris.Assistant;

/// <summary>One translation the user made: when, which direction, the text and its translation.</summary>
public sealed record HistoryEntry(DateTimeOffset Time, string Pair, string Source, string Target);

/// <summary>
/// The history of translations, newest first, kept in a local file of JSON lines and only when the user wants it
/// (Settings.SaveHistory). It holds at most <see cref="Limit"/> entries; older ones are dropped when the file is
/// rewritten. Nothing leaves the computer.
/// </summary>
public sealed class History
{
    public const int Limit = 500;

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<HistoryEntry> _entries = [];

    private History(string path) => _path = path;

    public static History Open(string path)
    {
        var history = new History(path);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                try
                {
                    if (JsonSerializer.Deserialize(line, HistoryJson.Default.HistoryEntry) is { } entry)
                    {
                        history._entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // a line cut off by a crash: the rest still counts
                }
            }
        }

        history._entries.Reverse();
        if (history._entries.Count > Limit)
        {
            history._entries.RemoveRange(Limit, history._entries.Count - Limit);
        }

        return history;
    }

    /// <summary>The entries, newest first; with a search text only those that contain it in the source or translation.</summary>
    public IReadOnlyList<HistoryEntry> Entries(string? search = null)
    {
        lock (_gate)
        {
            return string.IsNullOrWhiteSpace(search)
                ? [.. _entries]
                : [.. _entries.Where(e => e.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                                          e.Target.Contains(search, StringComparison.CurrentCultureIgnoreCase))];
        }
    }

    public void Add(string pair, string source, string target)
    {
        var entry = new HistoryEntry(DateTimeOffset.Now, pair, source, target);
        lock (_gate)
        {
            _entries.Insert(0, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            if (_entries.Count > Limit)
            {
                _entries.RemoveRange(Limit, _entries.Count - Limit);
                Rewrite();
            }
            else
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, HistoryJson.Default.HistoryEntry) + "\n", new UTF8Encoding(false));
            }
        }
    }

    /// <summary>Deletes the whole history (the file too).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            File.Delete(_path);
        }
    }

    private void Rewrite()
    {
        var lines = _entries.AsEnumerable().Reverse().Select(e => JsonSerializer.Serialize(e, HistoryJson.Default.HistoryEntry) + "\n");
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, string.Concat(lines), new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }
}
