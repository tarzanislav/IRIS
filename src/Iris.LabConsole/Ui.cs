using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Iris.LabConsole;

/// <summary>Small pieces of the console's look, built in code with the styles of the IRIS application.</summary>
internal static class Ui
{
    public static T Resource<T>(string key) => (T)Application.Current.FindResource(key);

    /// <summary>A color of the look; some names are also styles (Danger), so the colors are asked first.</summary>
    public static Brush Brush(string key) =>
        Application.Current.FindResource(key) as Brush ?? (Brush)Application.Current.Resources.MergedDictionaries[0][key];

    public static Border Card(UIElement child, double margin = 8) => new()
    {
        Style = Resource<Style>("Card"),
        Margin = new Thickness(margin),
        Child = child,
    };

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("PageTitle"),
        Margin = new Thickness(8, 0, 8, 12),
    };

    public static TextBlock Text(string text, double size = 14, bool bold = false, string brush = "Text") => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(brush),
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("Caption"),
    };

    /// <summary>A heading inside a card, with an optional note under it.</summary>
    public static StackPanel Heading(string text, string? note = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(Text(text, 16, bold: true));
        if (note is not null)
        {
            var muted = Text(note, 12.5, brush: "TextMuted");
            muted.Margin = new Thickness(0, 2, 0, 0);
            panel.Children.Add(muted);
        }

        return panel;
    }

    public static Button Button(string text, Action click, bool primary = false, bool danger = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        if (primary || danger)
        {
            button.Style = Resource<Style>(danger ? "Danger" : "Primary");
        }

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A smaller button for rows and panels.</summary>
    public static Button Small(string text, Action click, bool primary = false, bool danger = false)
    {
        var button = Button(text, click, primary, danger);
        button.MinHeight = 30;
        button.Padding = new Thickness(12, 0, 12, 0);
        button.FontSize = 12.5;
        button.Margin = new Thickness(0, 0, 6, 6);
        return button;
    }

    /// <summary>A small rounded label in a soft color.</summary>
    public static Border Badge(string text, string background, string foreground) => new()
    {
        Background = Brush(background),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(9, 2, 9, 3),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush(foreground) },
    };

    /// <summary>A chip of the header: a colored dot and a text on the surface.</summary>
    public static Border Chip(string text, string dot, string? tooltip = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush(dot), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Brush("Text"), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = Brush("Surface"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 5, 14, 6),
            Margin = new Thickness(6, 0, 0, 6),
            Child = panel,
            ToolTip = tooltip,
        };
    }

    /// <summary>An icon (a path of the IRIS style) in a soft circle.</summary>
    public static Border Icon(string data, string tint, string deep, double size = 38) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Brush(tint),
        Child = new Path
        {
            Style = Resource<Style>("IconPath"),
            Stroke = Brush(deep),
            Data = Geometry.Parse(data),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Choices side by side (segments); returns the bar.</summary>
    public static Border Segments(string group, IEnumerable<(string Key, string Text)> choices, string chosen, Action<string> choose)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, text) in choices)
        {
            var radio = new RadioButton { Style = Resource<Style>("Segment"), GroupName = group, Content = text, IsChecked = key == chosen };
            radio.Checked += (_, _) => choose(key);
            panel.Children.Add(radio);
        }

        return new Border
        {
            Background = Brush("SurfaceAlt"),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
    }

    /// <summary>A switch with its text.</summary>
    public static CheckBox Switch(string text, bool on) => new()
    {
        Style = Resource<Style>("Switch"),
        Content = text,
        IsChecked = on,
        Margin = new Thickness(0, 4, 0, 4),
    };

    /// <summary>A label and a value in one line of a table of facts.</summary>
    public static Grid Fact(string label, string? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = Text(label, 12.5, brush: "TextMuted");
        var text = Text(string.IsNullOrWhiteSpace(value) ? "–" : value, 13);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>A bar of a share (0 – 100 %), the learning's part darker within all of it.</summary>
    public static Grid Bar(double total, double own)
    {
        var grid = new Grid { Height = 8, Margin = new Thickness(0, 3, 0, 3) };
        grid.Children.Add(new Border { Background = Brush("SurfaceAlt"), CornerRadius = new CornerRadius(4) });
        grid.Children.Add(Fill(total, "Peach"));
        grid.Children.Add(Fill(own, "Accent"));
        return grid;

        static Border Fill(double percent, string brush) => new()
        {
            Background = Brush(brush),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            Tag = Math.Clamp(percent, 0, 100),
        };
    }

    /// <summary>Gives the bars of a panel their widths once the layout knows the space.</summary>
    public static void SizeBars(FrameworkElement root)
    {
        foreach (var grid in Descendants<Grid>(root).Where(g => g.Height == 8))
        {
            foreach (var fill in grid.Children.OfType<Border>().Where(b => b.Tag is double))
            {
                fill.Width = grid.ActualWidth * (double)fill.Tag / 100;
            }
        }
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The temperature, or why there is none (the firmware gives a fixed number, or none at all).</summary>
    public static string Temperature(JsonObject agent) =>
        agent["status"]?["signals"]?["temperature_fixed"]?.GetValue<bool>() == true ? "teplota nedostupná" : Number(agent["status"]?["machine"]?["temperature"], "0", " °C");

    public static string Number(JsonNode? node, string format = "0", string unit = "") =>
        Value(node) is var d && double.IsFinite(d) ? d.ToString(format, CultureInfo.InvariantCulture) + unit : "–";

    /// <summary>A number of a JSON value, whatever it was made from (a parsed number, an int, a long, a double); NaN when none.</summary>
    public static double Value(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out int i) => i,
        JsonValue v when v.TryGetValue(out long l) => l,
        JsonValue v when v.TryGetValue(out float f) => f,
        _ => double.NaN,
    };

    public static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>The name the console shows: the one given in the console, else the computer's own.</summary>
    public static string Name(JsonObject agent) => String(agent["alias"]) ?? String(agent["name"]) ?? "?";

    /// <summary>The state of a computer in words, with its colors.</summary>
    public static (string Text, string Background, string Foreground) State(JsonObject agent)
    {
        if (agent["connected"]?.GetValue<bool>() != true)
        {
            return String(agent["state"]) == "uninstalled" ? ("odinštalovaný", "SurfaceAlt", "TextMuted")
                : agent["leaving"]?.GetValue<bool>() == true ? ("odinštaluje sa po pripojení", "WarnSoft", "Warn")
                : agent["on"]?.GetValue<bool>() == true ? ("zapnutý, IRIS nebeží", "WarnSoft", "Danger")
                : agent["on"]?.GetValue<bool>() == false ? ("vypnutý", "SurfaceAlt", "TextMuted")
                : ("nepripojený", "SurfaceAlt", "TextMuted");
        }

        if (agent["status"]?["too_hot"]?.GetValue<bool>() == true)
        {
            return ("chladne", "WarnSoft", "Warn");
        }

        return (String(agent["status"]?["state"]) ?? String(agent["state"])) switch
        {
            "learning" => ("učí sa", "GoodSoft", "Good"),
            "paused" => ("pozastavený", "WarnSoft", "Warn"),
            "waiting" => ("čaká na čas učenia", "Sky", "SkyDeep"),
            "updating" => ("aktualizuje sa", "Lilac", "LilacDeep"),
            "leaving" => ("odinštaluje sa po kole", "WarnSoft", "Warn"),
            "uninstalling" => ("odinštalúva sa", "WarnSoft", "Warn"),
            "no fonts" => ("chýbajú písma", "WarnSoft", "Warn"),
            _ => ("pripravený", "Lavender", "LavenderDeep"),
        };
    }

    /// <summary>The color of the stripe of a tile: the state's deeper tone.</summary>
    public static string StateColor(JsonObject agent) => State(agent).Foreground;

    /// <summary>
    /// How hard a computer works (agents from 0.4.5): "v pohode", "vyťažený" or "trápi sa" with the reasons in words,
    /// the color, and the level (null when the agent does not tell).
    /// </summary>
    public static (string Text, string Reasons, string Brush, int? Level) Strain(JsonObject agent)
    {
        if (agent["connected"]?.GetValue<bool>() != true || agent["status"]?["strain"] is not JsonObject strain)
        {
            return ("", "", "TextMuted", null);
        }

        var level = strain["level"]?.GetValue<int>() ?? 0;
        // the other programs by name (agents from 0.4.6), with words for the usual ones of Windows
        var others = string.Join(", ", agent["status"]?["signals"]?["others"]?.AsArray().OfType<JsonObject>()
            .Select(o => $"{Program(String(o["name"]))} {Value(o["cpu"]):0} %") ?? []);
        var reasons = string.Join(", ", strain["reasons"]?.AsArray().OfType<JsonObject>()
            .Select(r => Reason(String(r["code"]), Value(r["value"])) + (String(r["code"]) == "others" && others.Length > 0 ? $" ({others})" : "")) ?? []);
        return level switch
        {
            >= 2 => ("trápi sa", reasons, "Danger", 2),
            1 => ("vyťažený", reasons, "Warn", 1),
            _ => ("v pohode", "", "Good", 0),
        };
    }

    private static string Program(string? name) => name?.ToLowerInvariant() switch
    {
        "msmpeng" or "mpdefendercoreservice" or "nissrv" => "antivírus Defender",
        "tiworker" or "trustedinstaller" or "moucoreworker" or "mousocoreworker" or "usoclient" or "wuauclt" => "aktualizácia Windows",
        "mscorsvw" or "ngen" or "ngentask" => "optimalizácia .NET",
        "searchindexer" or "searchprotocolhost" or "searchfilterhost" => "indexovanie vyhľadávania",
        "compattelrunner" or "devicecensus" => "zber údajov Windows",
        "msiexec" => "inštalácia programu",
        "sihost" or "explorer" or "dwm" => "pracovná plocha Windows",
        null => "?",
        _ => name,
    };

    private static string Reason(string? code, double value) => code switch
    {
        "limit" => $"firmvér znížil výkon na {value:0} % (teplo alebo napájanie)",
        "slow" => $"procesor spomalil na {value:0} % svojej bežnej rýchlosti – pravdepodobne teplo",
        "queue" => $"na procesor čaká {value:0} vlákien",
        "full" => $"procesor naplno ({value:0} %)",
        "others" => $"iné programy berú {value:0} % procesora",
        "memory" => $"málo voľnej pamäte ({value:0} MB)",
        "paging" => $"Windows odkladá pamäť na disk ({value:0} strán/s)",
        "disk" => $"disk stále zaneprázdnený ({value:0} %)",
        "learning" => $"učenie ide na {value:0} % obvyklej rýchlosti",
        _ => code ?? "",
    };

    public static string Ago(JsonNode? time)
    {
        if (time is not JsonValue v || !v.TryGetValue(out string? text) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
        {
            return "–";
        }

        var ago = DateTime.Now - t.ToLocalTime();
        return ago.TotalSeconds < 60 ? "teraz" : ago.TotalMinutes < 60 ? $"pred {ago.TotalMinutes:0} min" : ago.TotalHours < 48 ? $"pred {ago.TotalHours:0} h" : t.ToLocalTime().ToString("d. M. HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>The icons of the console (paths in a 20 × 20 box).</summary>
    public static class Icons
    {
        public const string Computers = "M2,4 H18 V13 H2 Z M7,17 H13 M10,13 V17";
        public const string Learning = "M3,15 L8,9 L11,12 L17,5 M13,5 H17 V9";
        public const string Speed = "M3,14 A7,7 0 1 1 17,14 M10,14 L13,8";
        public const string Model = "M10,2 L17,6 V14 L10,18 L3,14 V6 Z M3,6 L10,10 L17,6 M10,10 V18";
        public const string Round = "M10,3 A7,7 0 1 1 3,10 M3,4 V10 H9";
        public const string Warning = "M10,3 L18,17 H2 Z M10,8 V12 M10,14.5 V15";
        public const string Heat = "M10,3 V12 M7,15 A3,3 0 1 0 13,15 A3,3 0 1 0 7,15 M13,6 H15 M13,9 H15";
        public const string Update = "M16,8 A6,6 0 1 0 16,12 M16,3 V8 H11";
    }
}
