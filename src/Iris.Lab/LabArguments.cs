using System.Globalization;

namespace Iris.Lab;

/// <summary>Options "--name value"; anything else is a wrong use.</summary>
public sealed class LabArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static LabArguments Parse(string[] args)
    {
        var result = new LabArguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"unexpected '{args[i]}'");
            }

            result._values[args[i]] = args[++i];
        }

        return result;
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);

    public string Required(string name) => Value(name) ?? throw new ArgumentException($"{name} is needed");

    public double? Number(string name) => Value(name) is { } text
        ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"{name} needs a number")
        : null;
}
