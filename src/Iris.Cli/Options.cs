using System.Globalization;

namespace Iris.Cli;

/// <summary>A wrong use of the command line (the usage is shown, exit code 2).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The options of one command: known flags, known options with a value, and the remaining words. Anything else that
/// starts with "--" is an error, so a mistyped option never ends up in the text to translate.
/// </summary>
internal sealed class Options
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private Options()
    {
    }

    public List<string> Words { get; } = [];

    public static Options Parse(string[] args, string[] flags, string[] valued)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (flags.Contains(arg))
            {
                options._flags.Add(arg);
            }
            else if (valued.Contains(arg))
            {
                options._values[arg] = i + 1 < args.Length ? args[++i] : throw new UsageException($"{arg} needs a value");
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"unknown option {arg}");
            }
            else
            {
                options.Words.Add(arg);
            }
        }

        return options;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string name) => _values.GetValueOrDefault(name);

    /// <summary>A whole number of at least min, or null when the option is not given.</summary>
    public int? Int(string name, int min) =>
        Value(name) is not { } text ? null
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min ? value
        : throw new UsageException($"{name} needs a whole number of at least {min}, not '{text}'");

    /// <summary>A number, or null when the option is not given.</summary>
    public double? Number(string name) =>
        Value(name) is not { } text ? null
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value
        : throw new UsageException($"{name} needs a number, not '{text}'");

    /// <summary>The command takes no free words.</summary>
    public void NoWords()
    {
        if (Words.Count > 0)
        {
            throw new UsageException($"unexpected '{Words[0]}'");
        }
    }
}
