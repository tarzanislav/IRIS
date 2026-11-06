using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// What the administrator set in the console: the names of the computers as the console shows them (the computers
/// themselves keep theirs), their groups, and settings for all computers, for a group and for one computer – the
/// narrower one wins. A computer's group is its hardware model unless it was put into a group by hand. Kept in
/// lab-config.json in the hub's folder.
/// </summary>
public sealed class LabConfig
{
    /// <summary>
    /// The settings there are, and their ranges: the share of time and of cores (with the moving load their lower
    /// bounds), the upper bounds of the moving load, the temperature limit.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> Numbers = new Dictionary<string, (double, double)>(StringComparer.Ordinal)
    {
        ["duty"] = (0.05, 1),
        ["cores"] = (0.05, 1),
        ["duty_max"] = (0.05, 1),
        ["cores_max"] = (0.05, 1),
        ["hot"] = (50, 100),
    };

    /// <summary>
    /// The switches: the moving load, the efficiency mode, taking part in the learning, being woken by the server,
    /// getting new programs by itself.
    /// </summary>
    public static readonly IReadOnlySet<string> Switches = new HashSet<string>(StringComparer.Ordinal) { "moving", "eco", "learn", "wake", "update" };

    public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Groups set by hand (computer → group).</summary>
    public Dictionary<string, string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    public JsonObject All { get; private set; } = [];

    public Dictionary<string, JsonObject> GroupSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, JsonObject> ComputerSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static LabConfig Load(string path)
    {
        var config = new LabConfig();
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
            {
                return config;
            }

            foreach (var (name, alias) in json["aliases"] as JsonObject ?? [])
            {
                if (alias?.GetValue<string>() is { Length: > 0 } text)
                {
                    config.Aliases[name] = text;
                }
            }

            foreach (var (name, group) in json["groups"] as JsonObject ?? [])
            {
                if (group?.GetValue<string>() is { Length: > 0 } text)
                {
                    config.Groups[name] = text;
                }
            }

            config.All = Clean(json["settings"]?["all"] as JsonObject);
            foreach (var (name, values) in json["settings"]?["groups"] as JsonObject ?? [])
            {
                config.GroupSettings[name] = Clean(values as JsonObject);
            }

            foreach (var (name, values) in json["settings"]?["computers"] as JsonObject ?? [])
            {
                config.ComputerSettings[name] = Clean(values as JsonObject);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            // a broken file: the defaults, and the next change writes a good one
        }

        return config;
    }

    public void Save(string path)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The group of a computer: the one set by hand, else its hardware model, else none ("").</summary>
    public string GroupOf(string computer, string? model) => Groups.TryGetValue(computer, out var group) ? group : model ?? "";

    /// <summary>The settings a computer works with: the hub's defaults, then all, its group and itself.</summary>
    public JsonObject Effective(JsonObject defaults, string computer, string? model)
    {
        var result = (JsonObject)defaults.DeepClone();
        foreach (var layer in new[] { All, GroupSettings.GetValueOrDefault(GroupOf(computer, model)), ComputerSettings.GetValueOrDefault(computer) })
        {
            foreach (var (key, value) in layer ?? [])
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// A change from the console: {"alias": {computer, name}}, {"group": {computer, name}} (an empty name removes it)
    /// or {"settings": {scope: all | group | computer, name, values}} (values replace that scope's settings; empty
    /// values remove them). Returns false for anything else.
    /// </summary>
    public bool Apply(JsonObject change)
    {
        if (change["alias"] is JsonObject alias && alias["computer"]?.GetValue<string>() is { Length: > 0 } computer)
        {
            Set(Aliases, computer, alias["name"]?.GetValue<string>()?.Trim());
            return true;
        }

        if (change["group"] is JsonObject group && group["computer"]?.GetValue<string>() is { Length: > 0 } member)
        {
            Set(Groups, member, group["name"]?.GetValue<string>()?.Trim());
            return true;
        }

        if (change["settings"] is JsonObject settings)
        {
            var values = Clean(settings["values"] as JsonObject);
            var name = settings["name"]?.GetValue<string>() ?? "";
            switch (settings["scope"]?.GetValue<string>())
            {
                case "all":
                    All = values;
                    return true;
                case "group":
                    Replace(GroupSettings, name, values);
                    return true;
                case "computer" when name.Length > 0:
                    Replace(ComputerSettings, name, values);
                    return true;
            }
        }

        return false;
    }

    /// <summary>A computer that left the classroom: its name in the console, its group and its own settings go.</summary>
    public void Forget(string computer)
    {
        Aliases.Remove(computer);
        Groups.Remove(computer);
        ComputerSettings.Remove(computer);
    }

    public JsonObject ToJson() => new()
    {
        ["aliases"] = Object(Aliases),
        ["groups"] = Object(Groups),
        ["settings"] = new JsonObject
        {
            ["all"] = All.DeepClone(),
            ["groups"] = new JsonObject(GroupSettings.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value.DeepClone()))),
            ["computers"] = new JsonObject(ComputerSettings.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value.DeepClone()))),
        },
    };

    /// <summary>Only the known settings, numbers held in their range.</summary>
    public static JsonObject Clean(JsonObject? values)
    {
        var clean = new JsonObject();
        foreach (var (key, value) in values ?? [])
        {
            if (value is not JsonValue v)
            {
                continue;
            }

            if (Numbers.TryGetValue(key, out var range) && (v.TryGetValue(out double d) || (v.TryGetValue(out int i) && (d = i) == i)) && double.IsFinite(d))
            {
                clean[key] = Math.Clamp(d, range.Min, range.Max);
            }
            else if (Switches.Contains(key) && v.TryGetValue(out bool b))
            {
                clean[key] = b;
            }
        }

        return clean;
    }

    private static void Set(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            map.Remove(key);
        }
        else
        {
            map[key] = value.Length > 60 ? value[..60] : value;
        }
    }

    private static void Replace(Dictionary<string, JsonObject> map, string key, JsonObject values)
    {
        if (values.Count == 0)
        {
            map.Remove(key);
        }
        else
        {
            map[key] = values;
        }
    }

    private static JsonObject Object(Dictionary<string, string> map) =>
        new(map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
}
