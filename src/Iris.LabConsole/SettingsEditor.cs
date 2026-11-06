using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Iris.LabConsole;

/// <summary>
/// The settings of one scope – all computers, a group, or one computer. Every setting has a switch "vlastné": off, the
/// scope takes what comes from above (shown dimmed); on, its own value. Saving sends only the scope's own settings, so
/// a narrower scope keeps winning and a wider one keeps applying where nothing narrower is set.
/// </summary>
public sealed class SettingsEditor : StackPanel
{
    private static readonly (string Key, string Text, string Note, double Min, double Max, double Step, string Unit)[] Numbers =
    [
        ("duty", "Podiel času", "koľko času učenie pracuje (zvyšok odpočíva)", 0.05, 1, 0.05, "%"),
        ("cores", "Podiel jadier", "koľko jadier procesora učenie použije", 0.05, 1, 0.05, "%"),
        ("duty_max", "Najviac času", "pohyblivá záťaž: kam až smie podiel času stúpnuť", 0.05, 1, 0.05, "%"),
        ("cores_max", "Najviac jadier", "pohyblivá záťaž: kam až smie podiel jadier stúpnuť (nad fyzické jadrá nejde)", 0.05, 1, 0.05, "%"),
        ("hot", "Hranica teploty", "nad ňou učenie čaká, kým sa počítač neochladí o 5 °C", 50, 100, 1, "°C"),
    ];

    private static readonly (string Key, string Text, string Note)[] Switches =
    [
        ("moving", "Pohyblivá záťaž", "počítač sám pridáva od nastavenej záťaže po najviac, kým je pokojný; uberá, keď sa trápi, je teplý alebo ho niekto používa"),
        ("eco", "Úsporný režim Windows", "úsporné jadrá a frekvencia – menej tepla a hluku"),
        ("learn", "Učí sa", "vypnuté: počítač sa do kôl nezapája"),
        ("wake", "Budiť na začiatku učenia", "Wake-on-LAN o začiatku času učenia, potom ho vypne"),
        ("update", "Aktualizovať samo", "nový podpísaný program dostane medzi kolami"),
    ];

    private readonly List<(string Key, CheckBox Own, Func<JsonNode> Read)> _rows = [];
    private readonly bool _narrow;

    /// <param name="narrow">For a narrow panel: the name of each setting above its control.</param>
    public SettingsEditor(string scope, string name, JsonObject own, JsonObject inherited, Func<JsonObject, Task<bool>> save, bool narrow = false)
    {
        _narrow = narrow;
        var ownText = scope == "all" ? "zmeniť" : "vlastné";
        foreach (var (key, text, note, min, max, step, unit) in Numbers)
        {
            var current = Ui.Value(own[key]) is var o && double.IsFinite(o) ? o : Ui.Value(inherited[key]) is var i && double.IsFinite(i) ? i : min;
            var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 2, TickFrequency = step, IsSnapToTickEnabled = true, Value = current, VerticalAlignment = VerticalAlignment.Center };
            var value = Ui.Text(Show(current, unit), 13.5, bold: true);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextAlignment = TextAlignment.Right;
            slider.ValueChanged += (_, e) => value.Text = Show(e.NewValue, unit);
            AddRow(key, text, note, slider, value, own[key] is not null, ownText, () => slider.Value, inherited[key]);
        }

        foreach (var (key, text, note) in Switches)
        {
            var on = Ui.Bool(own[key]) ?? Ui.Bool(inherited[key]) ?? key != "moving";
            var toggle = Ui.Switch(on ? "áno" : "nie", on);
            toggle.Checked += (_, _) => toggle.Content = "áno";
            toggle.Unchecked += (_, _) => toggle.Content = "nie";
            AddRow(key, text, note, toggle, null, own[key] is not null, ownText, () => toggle.IsChecked == true, inherited[key]);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var result = Ui.Text("", 12.5, brush: "TextMuted");
        result.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(Ui.Button("Uložiť nastavenia", async () =>
        {
            var values = new JsonObject();
            foreach (var (key, check, read) in _rows.Where(r => r.Own.IsChecked == true))
            {
                values[key] = read();
            }

            result.Text = await save(new JsonObject { ["settings"] = new JsonObject { ["scope"] = scope, ["name"] = name, ["values"] = values } })
                ? "Uložené – počítače dostali nové nastavenia."
                : "Nepodarilo sa uložiť (server nie je pripojený).";
        }, primary: true));
        if (own.Count > 0)
        {
            buttons.Children.Add(Ui.Button(scope == "all" ? "Vrátiť predvolené" : "Zrušiť vlastné", async () =>
            {
                result.Text = await save(new JsonObject { ["settings"] = new JsonObject { ["scope"] = scope, ["name"] = name, ["values"] = new JsonObject() } })
                    ? "Vlastné nastavenia zrušené."
                    : "Nepodarilo sa uložiť (server nie je pripojený).";
            }));
        }

        buttons.Children.Add(result);
        Children.Add(buttons);
    }

    private static string Show(double value, string unit) => unit == "%"
        ? (value * 100).ToString("0", CultureInfo.InvariantCulture) + " %"
        : value.ToString("0", CultureInfo.InvariantCulture) + " " + unit;

    private void AddRow(string key, string text, string note, FrameworkElement control, TextBlock? value, bool own, string ownText, Func<object> read, JsonNode? inherited)
    {
        // wide: name | control | value | own; narrow: the name on a line of its own above the rest
        var grid = new Grid { Margin = new Thickness(0, 4, 0, _narrow ? 8 : 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_narrow ? 0 : 210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value is null ? 0 : 64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = _narrow ? 1 : 0;
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, _narrow ? 4 : 0) };
        label.Children.Add(Ui.Text(text, 13.5, bold: true));
        label.Children.Add(Ui.Text(note, 11.5, brush: "TextMuted"));
        if (_narrow)
        {
            Grid.SetColumnSpan(label, 4);
        }

        grid.Children.Add(label);
        Grid.SetColumn(control, 1);
        Grid.SetRow(control, row);
        grid.Children.Add(control);
        if (value is not null)
        {
            Grid.SetColumn(value, 2);
            Grid.SetRow(value, row);
            grid.Children.Add(value);
        }

        var check = Ui.Switch(ownText, own);
        check.ToolTip = inherited is null ? null : $"inak: {Describe(key, inherited)}";
        check.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(check, 3);
        Grid.SetRow(check, row);
        grid.Children.Add(check);
        control.IsEnabled = own;
        check.Checked += (_, _) => control.IsEnabled = true;
        check.Unchecked += (_, _) => control.IsEnabled = false;
        Children.Add(grid);
        _rows.Add((key, check, () => read() switch
        {
            bool b => JsonValue.Create(b),
            double d => JsonValue.Create(Math.Round(d, 2)),
            _ => JsonValue.Create(0),
        }));
    }

    private static string Describe(string key, JsonNode value) => key switch
    {
        "duty" or "cores" or "duty_max" or "cores_max" => Show(Ui.Value(value), "%"),
        "hot" => Show(Ui.Value(value), "°C"),
        _ => Ui.Bool(value) == true ? "áno" : "nie",
    };
}
