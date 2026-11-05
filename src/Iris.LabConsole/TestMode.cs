using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// The test mode: the chosen computers (all, a group, or picked by hand) learn now – also outside the time of
/// learning – with a load chosen here, for some minutes, so that one can stand in the classroom and hear, feel and see
/// how they cope. What they learn is kept. After the time the hub gives them their own settings back by itself.
/// </summary>
public sealed class TestPage : ConsolePage
{
    /// <summary>The first hub with the test mode.</summary>
    public const string FirstVersion = "0.4.4";

    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private readonly Border _choiceHost = new();
    private ConsoleData? _last;
    private readonly Slider _duty = Slider(0.5);
    private readonly Slider _cores = Slider(0.5);
    private readonly TextBlock _dutyText = Ui.Text("", 13.5, bold: true);
    private readonly TextBlock _coresText = Ui.Text("", 13.5, bold: true);
    private readonly CheckBox _eco = Ui.Switch("úsporný režim Windows (chladnejšie, tichšie)", true);
    private readonly ComboBox _group = new() { MinWidth = 220, Margin = new Thickness(0, 0, 0, 8) };
    private readonly HashSet<string> _picked = new(StringComparer.OrdinalIgnoreCase);
    private string _who = "all";
    private string _minutes = "20";
    private string? _note;

    public TestPage()
    {
        Content = Scroll(_root);
        _duty.ValueChanged += (_, _) => _dutyText.Text = Ui.Percent(_duty.Value);
        _cores.ValueChanged += (_, _) => _coresText.Text = Ui.Percent(_cores.Value);
        _dutyText.Text = Ui.Percent(_duty.Value);
        _coresText.Text = Ui.Percent(_cores.Value);
    }

    public override string Heading => "Testovací režim";

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _root.Children.Add(error);
        }

        var hub = Ui.String(data.Snapshot?["hub_version"]);
        if (data.Snapshot is not null && (hub is null || LabUpdates.IsNewer(FirstVersion, hub)))
        {
            var old = new StackPanel();
            old.Children.Add(Ui.Heading($"Server má verziu {hub}", $"testovací režim vie server od verzie {FirstVersion}"));
            var buttons = new WrapPanel();
            buttons.Children.Add(Ui.Button("Aktualizovať server teraz", () => _ = Actions?.UpdateServer(), primary: true));
            old.Children.Add(buttons);
            _root.Children.Add(Ui.Card(old));
            return;
        }

        var test = data.Snapshot?["test"] as JsonObject;
        if (test is not null)
        {
            _root.Children.Add(Ui.Card(Running(test, data)));
        }

        // the choice is built anew only while the mouse and the keyboard are elsewhere (a slider in the hand stays)
        if (_choiceHost.Child is null || !(_choiceHost.IsMouseOver || _choiceHost.IsKeyboardFocusWithin) || _last?.Snapshot?["test"] is null != test is null)
        {
            _choiceHost.Child = Ui.Card(Choice(data, test is not null));
        }

        _last = data;
        Detach(_choiceHost);
        _root.Children.Add(_choiceHost);
        _root.Children.Add(Ui.Card(Live(data, test)));
    }

    private StackPanel Running(JsonObject test, ConsoleData data)
    {
        var panel = new StackPanel();
        var until = DateTime.TryParse(Ui.String(test["until"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var u) ? u : DateTime.Now;
        var left = Math.Max(0, (until - DateTime.Now).TotalMinutes);
        var count = data.Agents.Count(a => a["test"]?.GetValue<bool>() == true);
        panel.Children.Add(Ui.Heading($"Test beží do {until:HH:mm} – zostáva {Math.Ceiling(left):0} min",
            $"{count} {Computers(count)} · {Ui.Percent(Ui.Value(test["duty"]))} času na {Ui.Percent(Ui.Value(test["cores"]))} jadier · úsporný režim {(test["eco"]?.GetValue<bool>() == true ? "áno" : "nie")} · spustila konzola {Ui.String(test["console"])}"));
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("Ukončiť test (kolo dobehne)", async () =>
        {
            if (Actions is not null)
            {
                _note = await Actions.StopTest()
                    ? "Test končí: kolo, ktoré beží, počítače dokončia a jeho výsledok sa použije; potom sa vrátia k svojim nastaveniam."
                    : "Server ukončenie neprijal.";
                Redraw(data);
            }
        }, danger: true));
        panel.Children.Add(buttons);
        panel.Children.Add(Ui.Text("Záťaž sa dá počas testu meniť nižšie („Zmeniť záťaž“) – počítače ju prevezmú hneď, koniec testu ostáva. Pri ukončení aj po skončení času kolo, ktoré beží, ešte dobehne a jeho výsledok sa použije.", 12.5, brush: "TextMuted"));
        return panel;
    }

    private StackPanel Choice(ConsoleData data, bool running)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading(running ? "Zmeniť test" : "Spustiť test",
            "počítače sa začnú učiť hneď, aj mimo času učenia – so záťažou, ktorú tu zvolíte; čo sa naučia, sa ponechá"));

        // the load: three presets and the two shares by hand
        panel.Children.Add(Ui.Caption("Záťaž"));
        var preset = Ui.Segments("test-preset", [("low", "Jemne 25 %"), ("mid", "Bežne 50 %"), ("full", "Naplno 100 %")], Preset(), key =>
        {
            var share = key switch { "low" => 0.25, "full" => 1.0, _ => 0.5 };
            _duty.Value = share;
            _cores.Value = share;
            _eco.IsChecked = key != "full";
            Redraw(data);
        });
        preset.Margin = new Thickness(0, 4, 0, 10);
        panel.Children.Add(preset);
        panel.Children.Add(Row("podiel času", "koľko času z každej minúty počítač počíta", _duty, _dutyText));
        panel.Children.Add(Row("podiel jadier", "koľko jadier procesora použije", _cores, _coresText));
        Detach(_eco);
        panel.Children.Add(_eco);

        panel.Children.Add(Ui.Caption("Ako dlho"));
        var minutes = Ui.Segments("test-minutes", [("10", "10 min"), ("20", "20 min"), ("30", "30 min"), ("60", "1 hod"), ("120", "2 hod")], _minutes, key =>
        {
            _minutes = key;
            Redraw(data);
        });
        minutes.Margin = new Thickness(0, 4, 0, 10);
        panel.Children.Add(minutes);

        // which computers
        panel.Children.Add(Ui.Caption("Počítače"));
        var who = Ui.Segments("test-who", [("all", "Všetky pripojené"), ("group", "Skupina"), ("picked", "Vybrané")], _who, key =>
        {
            _who = key;
            Redraw(data);
        });
        who.Margin = new Thickness(0, 4, 0, 8);
        panel.Children.Add(who);
        var connected = data.Agents.Where(a => a["connected"]?.GetValue<bool>() == true).ToList();
        if (_who == "group")
        {
            var groups = data.Groups.Select(g => g.Group).ToList();
            var chosen = _group.SelectedItem as string;
            _group.Items.Clear();
            foreach (var g in groups)
            {
                _group.Items.Add(g.Length == 0 ? "(bez skupiny)" : g);
            }

            _group.SelectedItem = chosen is not null && _group.Items.Contains(chosen) ? chosen : _group.Items.Count > 0 ? _group.Items[0] : null;
            Detach(_group);
            panel.Children.Add(_group);
        }
        else if (_who == "picked")
        {
            var list = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var agent in connected)
            {
                var name = Ui.String(agent["name"]) ?? "?";
                var box = new CheckBox { Content = Ui.Name(agent), IsChecked = _picked.Contains(name), Margin = new Thickness(0, 4, 18, 4), Foreground = Ui.Brush("Text") };
                box.Checked += (_, _) => _picked.Add(name);
                box.Unchecked += (_, _) => _picked.Remove(name);
                list.Children.Add(box);
            }

            panel.Children.Add(list);
        }

        var start = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (running)
        {
            start.Children.Add(Ui.Button("Zmeniť záťaž (koniec ostáva)", () => _ = Start(data, keepTime: true), primary: true));
            start.Children.Add(Ui.Button($"Začať znova na {_minutes} min", () => _ = Start(data)));
        }
        else
        {
            start.Children.Add(Ui.Button("Spustiť test", () => _ = Start(data), primary: true));
        }
        panel.Children.Add(start);
        if (_note is not null)
        {
            panel.Children.Add(Ui.Text(_note, 13, bold: true, brush: "Accent"));
        }

        var open = data.Snapshot?["open"]?.GetValue<bool>() == true;
        panel.Children.Add(Ui.Text(open
            ? "Teraz je čas učenia: test zmení len záťaž zvolených počítačov."
            : "Pozor, ak pri počítačoch sedia žiaci: test ich zaťaží. Počítač učenie aj tak pribrzdí, keď ho niekto naplno používa.", 12.5, brush: "TextMuted"));
        return panel;
    }

    private async Task Start(ConsoleData data, bool keepTime = false)
    {
        if (Actions is null)
        {
            return;
        }

        List<string>? computers = _who switch
        {
            "group" => [.. data.Groups.Where(g => (g.Group.Length == 0 ? "(bez skupiny)" : g.Group) == _group.SelectedItem as string)
                .SelectMany(g => g.Computers).Select(a => Ui.String(a["name"]) ?? "")],
            "picked" => [.. _picked],
            _ => null,
        };
        if (computers is { Count: 0 })
        {
            _note = "Vyberte aspoň jeden počítač.";
            Redraw(data);
            return;
        }

        var minutes = double.Parse(_minutes, CultureInfo.InvariantCulture);
        var count = computers?.Count ?? data.Agents.Count(a => a["connected"]?.GetValue<bool>() == true);
        var load = $"{Ui.Percent(_duty.Value)} času na {Ui.Percent(_cores.Value)} jadier, úsporný režim {(_eco.IsChecked == true ? "áno" : "nie")}";
        if (!keepTime && MessageBox.Show(
                $"{count} {Computers(count)} sa začne učiť hneď na {minutes:0} min: {load}.\n\nSpustiť test?",
                "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var answer = await Actions.StartTest(computers, minutes, Math.Round(_duty.Value, 2), Math.Round(_cores.Value, 2), _eco.IsChecked == true, keepTime);
        _note = answer is null ? "Server test neprijal (nie je pripojený)."
            : Ui.String(answer["error"]) is { } e ? $"Server test neprijal: {e}"
            : keepTime ? $"Nová záťaž ({load}) platí hneď na {Ui.Number(answer["computers"])} počítačoch."
            : $"Test beží na {Ui.Number(answer["computers"])} počítačoch – prvé kolo sa začne do pár sekúnd.";
        Redraw(data);
    }

    // the computers of the test (or all connected) with what they do now
    private StackPanel Live(ConsoleData data, JsonObject? test)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading(test is null ? "Počítače teraz" : "Počítače v teste", "obnovuje sa každých 5 sekúnd; teplotu hlási len počítač, ktorého firmvér ju dáva"));
        var grid = new Grid();
        string[] headers = ["Počítač", "Stav", "Procesor", "z toho učenie", "Teplota", "Vlákna", "Riadky/s", "Loss", "Námaha"];
        foreach (var _ in headers)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        grid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < headers.Length; c++)
        {
            Place(grid, Ui.Caption(headers[c]), 0, c);
        }

        var row = 1;
        foreach (var agent in data.Agents.Where(a => a["connected"]?.GetValue<bool>() == true && (test is null || a["test"]?.GetValue<bool>() == true)))
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var status = agent["status"];
            var machine = status?["machine"];
            var (state, back, fore) = Ui.State(agent);
            var hot = status?["too_hot"]?.GetValue<bool>() == true;
            Place(grid, Ui.Text(Ui.Name(agent), 14, bold: true), row, 0);
            Place(grid, Ui.Badge(hot ? state + " · prehriaty, čaká" : state, back, hot ? "Danger" : fore), row, 1);
            Place(grid, Ui.Text(Ui.Number(machine?["cpu"], "0", " %"), 13), row, 2);
            Place(grid, Ui.Text(Ui.Number(machine?["cpu_own"], "0", " %"), 13, bold: true), row, 3);
            var temperature = Ui.Value(machine?["temperature"]);
            Place(grid, Ui.Text(Ui.Temperature(agent), 13, bold: temperature >= 80, brush: temperature >= 80 ? "Danger" : "Text"), row, 4);
            Place(grid, Ui.Text(Ui.Number(status?["threads"]), 13), row, 5);
            Place(grid, Ui.Text(Ui.Number(status?["last"]?["lines_per_second"], "0.0"), 13), row, 6);
            Place(grid, Ui.Text(Ui.Number(status?["last"]?["loss"], "0.000"), 13, brush: "TextMuted"), row, 7);
            var strain = Ui.Strain(agent);
            var strainText = Ui.Text(strain.Level is null ? "–" : strain.Text + (strain.Reasons.Length > 0 ? " – " + strain.Reasons : ""), 13, bold: strain.Level > 0, brush: strain.Brush);
            strainText.MaxWidth = 420;
            Place(grid, strainText, row, 8);
            row++;
        }

        panel.Children.Add(row == 1 ? Ui.Text("Žiadny pripojený počítač.", 13, brush: "TextMuted") : grid);
        return panel;
    }

    // after a click here: the choice anew even with the mouse over it
    private void Redraw(ConsoleData data)
    {
        _choiceHost.Child = null;
        Show(data);
    }

    private string Preset() => Math.Abs(_duty.Value - _cores.Value) > 0.001 ? "" : _duty.Value switch
    {
        0.25 => "low",
        0.5 => "mid",
        1.0 => "full",
        _ => "",
    };

    private static Grid Row(string label, string note, Slider slider, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var text = new StackPanel();
        text.Children.Add(Ui.Text(label, 13.5, bold: true));
        text.Children.Add(Ui.Text(note, 12, brush: "TextMuted"));
        Detach(slider);
        Detach(value);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    private static Slider Slider(double value) => new()
    {
        Minimum = 0.1,
        Maximum = 1,
        SmallChange = 0.05,
        LargeChange = 0.1,
        TickFrequency = 0.05,
        IsSnapToTickEnabled = true,
        Value = value,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // the controls kept across refreshes leave their old place first
    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    private static string Computers(int count) => count == 1 ? "počítač" : count is >= 2 and <= 4 ? "počítače" : "počítačov";

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 5, 26, 5);
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
