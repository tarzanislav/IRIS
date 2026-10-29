using System.Text;
using System.Text.Json;

namespace Iris.Memory;

/// <summary>A sentence and its translation as the owner wants it, with the time of the last change.</summary>
public sealed record MemoryEntry(string Source, string Target, DateTimeOffset Updated);

/// <summary>
/// The translation memory: sentences whose translation the owner corrected are translated the same way the next
/// time. Kept in one local file of JSON lines – every change is a new line (a correction or "forget"), so nothing is
/// rewritten while it is used; <see cref="Compact"/> keeps only what holds. A line cut off by a crash is skipped and the
/// next change starts on a new line. Sentences are matched exactly, after joining runs of whitespace and Unicode
/// normalization (NFC). Safe to use from several threads of one program; nothing ever leaves the computer.
/// </summary>
public sealed class TranslationMemory
{
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string? _path;

    private TranslationMemory(string? path) => _path = path;

    /// <summary>A memory that lives only while the program runs.</summary>
    public static TranslationMemory InMemory() => new(null);

    /// <summary>Opens the memory in a file (created with the first correction). Damaged lines are skipped.</summary>
    public static TranslationMemory Open(string path)
    {
        var memory = new TranslationMemory(path);
        if (!File.Exists(path))
        {
            return memory;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var source = root.GetProperty("source").GetString();
                if (source is null)
                {
                    continue;
                }

                if (root.TryGetProperty("forget", out var forget) && forget.ValueKind == JsonValueKind.True)
                {
                    memory._entries.Remove(Key(source));
                }
                else if (root.GetProperty("target").GetString() is { } target)
                {
                    var updated = root.TryGetProperty("updated", out var u) && u.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.MinValue;
                    memory._entries[Key(source)] = new MemoryEntry(source, target, updated);
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // a line cut off by a crash or written by hand: the rest still counts
            }
        }

        return memory;
    }

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

    /// <summary>A copy of the remembered sentences.</summary>
    public IReadOnlyList<MemoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    /// <summary>The remembered translation of a sentence, if there is one.</summary>
    public bool TryGet(string source, out string target)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(Key(source), out var entry))
            {
                target = entry.Target;
                return true;
            }
        }

        target = "";
        return false;
    }

    /// <summary>Remembers the translation of a sentence (a correction of the owner); written to the file first.</summary>
    public void Remember(string source, string target)
    {
        var key = Key(source);
        if (key.Length == 0)
        {
            throw new ArgumentException("an empty sentence cannot be remembered");
        }

        var entry = new MemoryEntry(source.Trim(), target.Trim(), DateTimeOffset.UtcNow);
        lock (_gate)
        {
            Append(Line(entry));
            _entries[key] = entry;
        }
    }

    /// <summary>Forgets the translation of a sentence; false when it was not remembered.</summary>
    public bool Forget(string source)
    {
        var key = Key(source);
        lock (_gate)
        {
            if (!_entries.ContainsKey(key))
            {
                return false;
            }

            Append(JsonLine(writer =>
            {
                writer.WriteString("source", source.Trim());
                writer.WriteBoolean("forget", true);
                writer.WriteString("updated", DateTimeOffset.UtcNow);
            }));
            _entries.Remove(key);
            return true;
        }
    }

    /// <summary>Rewrites the file with only what holds now (through a temporary file, so a crash loses nothing).</summary>
    public void Compact()
    {
        if (_path is null)
        {
            return;
        }

        lock (_gate)
        {
            var temporary = _path + ".tmp";
            var text = string.Concat(_entries.Values.OrderBy(e => e.Updated).Select(e => Line(e) + "\n"));
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    /// <summary>The key of a sentence: trimmed, runs of whitespace as one space, Unicode NFC.</summary>
    public static string Key(string sentence)
    {
        var text = new StringBuilder(sentence.Length);
        var space = false;
        foreach (var c in sentence.Normalize(NormalizationForm.FormC).Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    // one line at the end of the file; when the file does not end with a line break (a line cut off by a crash),
    // the new line starts on a line of its own
    private void Append(string line)
    {
        if (_path is null)
        {
            return;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var text = line + "\n";
        if (file.Length > 0)
        {
            file.Seek(-1, SeekOrigin.End);
            if (file.ReadByte() != '\n')
            {
                text = "\n" + text;
            }
        }

        file.Seek(0, SeekOrigin.End);
        file.Write(Encoding.UTF8.GetBytes(text));
        file.Flush(flushToDisk: true);
    }

    private static string Line(MemoryEntry entry) => JsonLine(writer =>
    {
        writer.WriteString("source", entry.Source);
        writer.WriteString("target", entry.Target);
        writer.WriteString("updated", entry.Updated);
    });

    private static string JsonLine(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
