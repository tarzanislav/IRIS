using System.Globalization;
using System.Text;

namespace Iris.Lab;

/// <summary>
/// The hub's own log in its folder (logs/hub.log, logs/update.log): one line per event with the time, kept small (at
/// 2 MB the file becomes .1.log and a new one begins). The console reads its end – the server needs nothing else for it.
/// </summary>
public sealed class HubLog
{
    public const long Limit = 2 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly Func<DateTime> _now;

    public HubLog(string folder, string name, Func<DateTime>? now = null)
    {
        if (!Names.Contains(name))
        {
            throw new ArgumentException($"unknown log '{name}'", nameof(name));
        }

        Folder = Path.Combine(folder, "logs");
        FilePath = Path.Combine(Folder, name + ".log");
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>The logs there are: the hub's and its updater's.</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal) { "hub", "update" };

    public string Folder { get; }

    public string FilePath { get; }

    /// <summary>The last line written (for the result of the updater).</summary>
    public string? Last { get; private set; }

    /// <summary>Writes one line (several lines of a text each get the time); a log that cannot be written is skipped.</summary>
    public void Write(string text)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > Limit)
                {
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".1.log"), overwrite: true);
                }

                var time = _now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var lines = new StringBuilder();
                foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
                {
                    if (line.Length > 0)
                    {
                        lines.Append(time).Append("  ").Append(line).Append('\n');
                        Last = line;
                    }
                }

                File.AppendAllText(FilePath, lines.ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the log is a help, never a reason for the hub to fail
            }
        }
    }

    /// <summary>The last lines of a log of the hub's folder (also from .1.log when the new one is short).</summary>
    public static List<string> Tail(string folder, string name, int count)
    {
        if (!Names.Contains(name))
        {
            return [];
        }

        count = Math.Clamp(count, 1, 5000);
        var path = Path.Combine(folder, "logs", name + ".log");
        var lines = new List<string>();
        foreach (var file in new[] { Path.ChangeExtension(path, ".1.log"), path })
        {
            try
            {
                if (File.Exists(file))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    while (reader.ReadLine() is { } line)
                    {
                        lines.Add(line);
                        if (lines.Count > count)
                        {
                            lines.RemoveAt(0);
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return lines;
    }

    /// <summary>A writer that passes everything on and also writes every whole line into the log (the output of the updater).</summary>
    public TextWriter Tee(TextWriter inner) => new TeeWriter(inner, this);

    private sealed class TeeWriter(TextWriter inner, HubLog log) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            if (value == '\n')
            {
                log.Write(_line.ToString());
                _line.Clear();
            }
            else
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "")
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void Flush()
        {
            inner.Flush();
            if (_line.Length > 0)
            {
                log.Write(_line.ToString());
                _line.Clear();
            }
        }
    }
}
