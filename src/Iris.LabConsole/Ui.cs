using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Iris.LabConsole;

/// <summary>Small pieces of the console's look, built in code with the styles of the IRIS application.</summary>
internal static class Ui
{
    public static Border Card(UIElement child, double margin = 8) => new()
    {
        Style = (Style)Application.Current.FindResource("Card"),
        Margin = new Thickness(margin),
        Child = child,
    };

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("PageTitle"),
        Margin = new Thickness(8, 0, 8, 12),
    };

    public static TextBlock Text(string text, double size = 14, bool bold = false, string brush = "Text") => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource(brush),
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Caption"),
    };

    public static Button Button(string text, Action click, bool primary = false, bool danger = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        if (primary || danger)
        {
            button.Style = (Style)Application.Current.FindResource(danger ? "Danger" : "Primary");
        }

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A small rounded label in a soft color.</summary>
    public static Border Badge(string text, string background, string foreground) => new()
    {
        Background = (Brush)Application.Current.FindResource(background),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(9, 2, 9, 3),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.FindResource(foreground) },
    };

    /// <summary>A bar of a share (0 – 100 %), the learning's part darker within all of it.</summary>
    public static Grid Bar(double total, double own)
    {
        var grid = new Grid { Height = 8, Margin = new Thickness(0, 3, 0, 3) };
        grid.Children.Add(new Border { Background = (Brush)Application.Current.FindResource("SurfaceAlt"), CornerRadius = new CornerRadius(4) });
        grid.Children.Add(Fill(total, "Peach"));
        grid.Children.Add(Fill(own, "Accent"));
        return grid;

        static Border Fill(double percent, string brush) => new()
        {
            Background = (Brush)Application.Current.FindResource(brush),
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

    /// <summary>The state of a computer in words, with its colors.</summary>
    public static (string Text, string Background, string Foreground) State(JsonObject agent)
    {
        if (agent["connected"]?.GetValue<bool>() != true)
        {
            return ("odpojený", "SurfaceAlt", "TextMuted");
        }

        return (agent["status"]?["state"]?.GetValue<string>() ?? agent["state"]?.GetValue<string>()) switch
        {
            "learning" => ("učí sa", "GoodSoft", "Good"),
            "paused" => ("pozastavený", "WarnSoft", "Warn"),
            "waiting" => ("čaká na čas učenia", "Sky", "SkyDeep"),
            "no fonts" => ("chýbajú písma", "WarnSoft", "Warn"),
            _ => ("pripravený", "Lavender", "LavenderDeep"),
        };
    }

    public static string Ago(JsonNode? time)
    {
        if (time is not JsonValue v || !v.TryGetValue(out string? text) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
        {
            return "–";
        }

        var ago = DateTime.Now - t.ToLocalTime();
        return ago.TotalSeconds < 60 ? "teraz" : ago.TotalMinutes < 60 ? $"pred {ago.TotalMinutes:0} min" : ago.TotalHours < 48 ? $"pred {ago.TotalHours:0} h" : t.ToLocalTime().ToString("d. M. HH:mm", CultureInfo.InvariantCulture);
    }
}
