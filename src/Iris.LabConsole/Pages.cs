using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Iris.LabConsole;

/// <summary>What the console knows now: the hub's snapshot and history (or why there is none).</summary>
public sealed class ConsoleData
{
    public JsonObject? Snapshot { get; set; }

    public JsonObject? History { get; set; }

    public int HistoryHours { get; set; } = 12;

    public string? Error { get; set; }

    public IEnumerable<JsonObject> Agents => Snapshot?["agents"]?.AsArray().OfType<JsonObject>() ?? [];

    public JsonObject? Agent(string name) => Agents.FirstOrDefault(a => string.Equals(Ui.String(a["name"]), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The configuration of the hub: names, groups, settings by scope, and its defaults.</summary>
    public JsonObject Config => Snapshot?["config"] as JsonObject ?? [];

    public JsonObject Defaults => Config["defaults"] as JsonObject ?? [];

    /// <summary>The newer signed version the server has for the computers and the console (null: none).</summary>
    public string? Available => Ui.String(Snapshot?["update"]?["version"]);

    /// <summary>The groups of the classroom with their computers (the hardware model unless set by hand).</summary>
    public IEnumerable<(string Group, List<JsonObject> Computers)> Groups =>
        Agents.GroupBy(a => Ui.String(a["group"]) ?? "").OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.OrderBy(a => Ui.Name(a), StringComparer.OrdinalIgnoreCase).ToList()));

    /// <summary>What one scope sets itself (all, a group, a computer).</summary>
    public JsonObject Own(string scope, string name) => (scope switch
    {
        "all" => Config["settings"]?["all"],
        "group" => Config["settings"]?["groups"]?[name],
        _ => Config["settings"]?["computers"]?[name],
    }) as JsonObject ?? [];

    /// <summary>What a scope gets from above when it sets nothing itself: the defaults, all, the group.</summary>
    public JsonObject Inherited(string scope, string? group)
    {
        var result = (JsonObject)Defaults.DeepClone();
        JsonObject[] layers = scope switch
        {
            "all" => [],
            "group" => new[] { Own("all", "") },
            _ => [Own("all", ""), Own("group", group ?? "")],
        };
        foreach (var layer in layers)
        {
            foreach (var (key, value) in layer)
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }
}

/// <summary>What the pages may ask the window to do (with the hub).</summary>
public interface IConsoleActions
{
    /// <summary>One of the fixed commands for a computer ("*" = all).</summary>
    Task Command(string target, string command);

    /// <summary>A change of names, groups or settings; true when the hub took it.</summary>
    Task<bool> Configure(JsonObject change);

    Task UpdateComputers(string target);

    Task UpdateServer();

    Task UpdateConsole();

    /// <summary>Opens a computer on the page of computers.</summary>
    void Open(string computer);
}

/// <summary>A page of the console.</summary>
public abstract class ConsolePage : UserControl
{
    public IConsoleActions? Actions { get; set; }

    /// <summary>The title of the page in the header of the window.</summary>
    public abstract string Heading { get; }

    public abstract void Show(ConsoleData data);

    protected static ScrollViewer Scroll(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content,
        Padding = new Thickness(0, 0, 8, 0),
    };

    protected async void Ask(string target, string command, string? question = null)
    {
        if (question is not null && MessageBox.Show(question, "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (Actions is { } actions)
        {
            await actions.Command(target, command);
        }
    }

    /// <summary>A note of an error over the page (the server cannot be reached …).</summary>
    protected static UIElement? ErrorNote(ConsoleData data) => data.Error is { } error ? Ui.Card(Ui.Text(error, 14, brush: "Danger")) : null;
}

/// <summary>The history in charts: load, temperature, speed of the whole classroom, and a map of hours.</summary>
public sealed class ChartsPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private bool _each;

    public ChartsPage() => Content = Scroll(_root);

    public override string Heading => "Grafy";

    /// <summary>The span the user chose (hours); the window fetches the history.</summary>
    public Action<int>? Span { get; set; }

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        var spans = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
        foreach (var (hours, text) in new[] { (6, "6 hodín"), (12, "12 hodín"), (24, "24 hodín"), (72, "3 dni"), (168, "týždeň") })
        {
            var toggle = new RadioButton { Content = text, GroupName = "span", IsChecked = data.HistoryHours == hours, Margin = new Thickness(0, 0, 14, 8) };
            toggle.Checked += (_, _) => Span?.Invoke(hours);
            spans.Children.Add(toggle);
        }

        var each = new CheckBox { Content = "každý počítač zvlášť", IsChecked = _each, Margin = new Thickness(24, 0, 0, 8) };
        each.Checked += (_, _) => { _each = true; Show(data); };
        each.Unchecked += (_, _) => { _each = false; Show(data); };
        spans.Children.Add(each);
        _root.Children.Add(spans);
        var series = data.History?["series"] as JsonObject ?? [];
        var names = series.Select(p => p.Key).ToList();
        List<ChartSeries> Lines(string key) => [.. names.Select((name, i) => new ChartSeries(name, Palette.For(i),
            [.. series[name]!.AsArray().OfType<JsonObject>().Select(p => (Time(p), Ui.Value(p[key]))).Where(p => double.IsFinite(p.Item2))]))];
        var violet = (Color)ColorConverter.ConvertFromString("#7C6CF2");

        // the classroom as one line (the mean) in the band of all computers, or every computer on its own
        LineChart Chart(string title, string key, string unit, double? min, double? max)
        {
            var lines = Lines(key);
            var chart = new LineChart { Title = title, Unit = unit, Minimum = min, Maximum = max, Height = 260 };
            if (_each)
            {
                chart.Series = lines;
                return chart;
            }

            var byTime = lines.SelectMany(l => l.Points).GroupBy(p => p.Time).OrderBy(g => g.Key).ToList();
            chart.Series = [new ChartSeries("priemer učebne", violet, [.. byTime.Select(g => (g.Key, g.Average(p => p.Value)))])];
            chart.Band = [.. byTime.Select(g => (g.Key, g.Min(p => p.Value), g.Max(p => p.Value)))];
            return chart;
        }

        _root.Children.Add(Ui.Card(Chart("Vyťaženie procesora (celý počítač)", "cpu", " %", 0, 100)));
        _root.Children.Add(Ui.Card(Chart("Učenie (jeho podiel na procesore)", "own", " %", 0, 100)));
        var temperatures = Lines("temperature");
        if (temperatures.Any(s => s.Points.Count > 0))
        {
            _root.Children.Add(Ui.Card(Chart("Teplota", "temperature", " °C", 20, null)));
        }

        // the speed of the whole classroom: the sum over computers in each bucket
        var total = series.SelectMany(p => p.Value!.AsArray().OfType<JsonObject>()).GroupBy(Time)
            .Select(g => (g.Key, g.Sum(p => Ui.Value(p["lps"]) is var v && double.IsFinite(v) ? v : 0))).OrderBy(p => p.Key).ToList();
        _root.Children.Add(Ui.Card(new LineChart
        {
            Title = "Riadky za sekundu – celá učebňa",
            Series = [new ChartSeries("spolu", violet, total)],
            Height = 230,
        }));

        // a map of hours: temperature where there is one, else the load
        var key = temperatures.Any(s => s.Points.Count > 0) ? "temperature" : "cpu";
        var hourList = series.SelectMany(p => p.Value!.AsArray().OfType<JsonObject>()).Select(p => Hour(Time(p))).Distinct().Order().TakeLast(48).ToList();
        var rows = names.Select(name => (name, hourList.Select(h =>
        {
            var values = series[name]!.AsArray().OfType<JsonObject>().Where(p => Hour(Time(p)) == h).Select(p => Ui.Value(p[key])).Where(double.IsFinite).ToList();
            return values.Count > 0 ? values.Average() : double.NaN;
        }).ToArray())).ToList();
        _root.Children.Add(Ui.Card(new HeatMap
        {
            Title = key == "temperature" ? "Teplota po hodinách" : "Vyťaženie po hodinách",
            Unit = key == "temperature" ? " °C" : " %",
            Low = key == "temperature" ? 35 : 0,
            High = key == "temperature" ? 85 : 100,
            Rows = rows,
            Columns = [.. hourList.Select(h => h.ToString("HH", CultureInfo.InvariantCulture))],
        }));
    }

    private static DateTime Time(JsonObject point) =>
        DateTime.ParseExact(point["t"]!.GetValue<string>(), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static DateTime Hour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0);
}

/// <summary>The learning itself: the loss of every round, how well the model reads (CER), the last rounds.</summary>
public sealed class RoundsPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };

    public RoundsPage() => Content = Scroll(_root);

    public override string Heading => "Kolá a učenie";

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        var rounds = data.Snapshot?["rounds"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var tests = data.Snapshot?["tests"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var violet = (Color)ColorConverter.ConvertFromString("#7C6CF2");
        var pink = (Color)ColorConverter.ConvertFromString("#E86FA8");
        var green = (Color)ColorConverter.ConvertFromString("#2FA67A");
        _root.Children.Add(Ui.Card(new LineChart
        {
            Title = "Chyba učenia (loss) po kolách",
            ByNumber = true,
            NumberSeries = [("loss", violet, [.. rounds.Select(r => (Ui.Value(r["round"]), Ui.Value(r["loss"]))).Where(p => double.IsFinite(p.Item2))])],
            Height = 240,
        }));
        _root.Children.Add(Ui.Card(new LineChart
        {
            Title = "Koľko znakov čítačka číta zle (CER) – podľa verzie modelu",
            Unit = " %",
            Minimum = 0,
            ByNumber = true,
            NumberSeries =
            [
                ("známe písma", green, [.. tests.Select(t => (Ui.Value(t["version"]), Ui.Value(t["cer"])))]),
                ("neznáme písmo", pink, [.. tests.Select(t => (Ui.Value(t["version"]), Ui.Value(t["cer_unseen"]))).Where(p => double.IsFinite(p.Item2))]),
            ],
            Height = 240,
        }));

        var table = new Grid();
        string[] headers = ["Kolo", "Verzia", "Počítače", "Riadky", "Loss", "Rýchlosť učenia", "Uzavreté"];
        foreach (var _ in headers)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        table.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < headers.Length; c++)
        {
            Place(table, Ui.Caption(headers[c]), 0, c);
        }

        var row = 1;
        foreach (var r in Enumerable.Reverse(rounds).Take(30))
        {
            table.RowDefinitions.Add(new RowDefinition());
            Place(table, Ui.Text(Ui.Number(r["round"]), 13, bold: true), row, 0);
            Place(table, Ui.Text(Ui.Number(r["version"]), 13), row, 1);
            Place(table, Ui.Text(Ui.Number(r["computers"]), 13), row, 2);
            Place(table, Ui.Text(Ui.Number(r["lines"], "#,0"), 13), row, 3);
            Place(table, Ui.Text(Ui.Number(r["loss"], "0.000"), 13), row, 4);
            Place(table, Ui.Text(Ui.Number(r["rate"], "0.00000"), 13), row, 5);
            Place(table, Ui.Text(Ui.Ago(r["closed"]), 13, brush: "TextMuted"), row, 6);
            row++;
        }

        _root.Children.Add(Ui.Card(table));
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 5, 26, 5);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}

/// <summary>
/// The network: the computers of the classroom with their cards (wake one, or all that are off), and a look through
/// the network by the hub – addresses, cards, names – with waking for any card that allows it.
/// </summary>
public sealed class NetworkPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private JsonObject? _scan;
    private string? _note;
    private ConsoleData? _last;

    public NetworkPage()
    {
        var scroll = Scroll(_root);
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = scroll;
    }

    public override string Heading => "Sieť";

    /// <summary>Asks the hub to look through the network; set by the window.</summary>
    public Func<Task<JsonObject?>>? Scan { get; set; }

    /// <summary>The result of a scan (the self-test sets one).</summary>
    public JsonObject? Found
    {
        get => _scan;
        set => _scan = value;
    }

    public override void Show(ConsoleData data)
    {
        _last = data;
        _root.Children.Clear();
        var tools = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
        tools.Children.Add(Ui.Button("Prehľadať sieť", async () =>
        {
            if (Scan is null)
            {
                return;
            }

            _note = "Server prehľadáva sieť, chvíľu to trvá…";
            Show(data);
            _scan = await Scan();
            _note = _scan is null ? "Prehľadanie sa nepodarilo." : null;
            if (_last is not null)
            {
                Show(_last);
            }
        }, primary: true));
        tools.Children.Add(Ui.Button("Zobudiť všetky vypnuté", () => Ask("*", "wake")));
        _root.Children.Add(tools);
        var auto = data.Snapshot?["auto_wake"]?.GetValue<bool>() == true;
        var schedule = data.Snapshot?["schedule"];
        _root.Children.Add(Ui.Text(auto
            ? $"Automatické budenie je zapnuté: o {schedule?["from"]} server zobudí vypnuté počítače učebne a o {schedule?["until"]} ich zasa vypne (tie, ktoré boli zapnuté, nechá tak)."
            : "Automatické budenie je vypnuté (iris-hub --wake no).", 13, brush: "TextMuted"));
        ((TextBlock)_root.Children[^1]).MaxWidth = 900;
        ((TextBlock)_root.Children[^1]).Margin = new Thickness(8, 0, 8, 6);
        if (_note is not null)
        {
            _root.Children.Add(Ui.Text(_note, 13, bold: true, brush: "Accent"));
        }

        // the computers of the classroom
        var known = new Grid();
        Columns(known, "Počítač", "Karta (MAC)", "Adresa", "Stav", "");
        var row = 1;
        foreach (var agent in data.Agents)
        {
            known.RowDefinitions.Add(new RowDefinition());
            var name = agent["name"]?.GetValue<string>() ?? "?";
            var mac = agent["mac"]?.GetValue<string>();
            var (state, back, fore) = Ui.State(agent);
            Place(known, Ui.Text(Ui.Name(agent), 14, bold: true), row, 0);
            Place(known, Ui.Text(mac ?? "zatiaľ neznáma", 13, brush: mac is null ? "TextMuted" : "Text"), row, 1);
            Place(known, Ui.Text(agent["address"]?.GetValue<string>() ?? "", 13, brush: "TextMuted"), row, 2);
            Place(known, Ui.Badge(agent["woken"]?.GetValue<bool>() == true ? state + " · zobudený serverom" : state, back, fore), row, 3);
            if (mac is not null && agent["connected"]?.GetValue<bool>() != true)
            {
                Place(known, Small("Zobudiť", () => Ask(name, "wake")), row, 4);
            }

            row++;
        }

        _root.Children.Add(Ui.Card(Section("Počítače učebne", known)));

        // what the scan found
        if (_scan?["devices"] is JsonArray devices)
        {
            var grid = new Grid();
            Columns(grid, "Adresa", "Karta (MAC)", "Názov (DNS)", "Názov (NetBIOS)", "Pracovná skupina", "Odozva", "Počítač IRIS", "");
            row = 1;
            foreach (var device in devices.OfType<JsonObject>())
            {
                grid.RowDefinitions.Add(new RowDefinition());
                var mac = device["mac"]?.GetValue<string>();
                Place(grid, Ui.Text(device["address"]?.GetValue<string>() ?? "", 13, bold: true), row, 0);
                Place(grid, Ui.Text(mac is null ? "–" : mac + (device["local_mac"]?.GetValue<bool>() == true ? " (súkromná)" : ""), 13), row, 1);
                Place(grid, Ui.Text(device["dns"]?.GetValue<string>() ?? "–", 13), row, 2);
                Place(grid, Ui.Text(device["netbios"]?.GetValue<string>() ?? "–", 13), row, 3);
                Place(grid, Ui.Text(device["workgroup"]?.GetValue<string>() ?? "–", 13, brush: "TextMuted"), row, 4);
                Place(grid, Ui.Text(Ui.Number(device["ping"], "0", " ms"), 13), row, 5);
                Place(grid, Ui.Text(device["computer"]?.GetValue<string>() ?? "", 13, bold: true, brush: "Good"), row, 6);
                if (mac is not null)
                {
                    Place(grid, Small("Zobudiť", () => Ask("mac:" + mac, "wake")), row, 7);
                }

                row++;
            }

            _root.Children.Add(Ui.Card(Section($"Nájdené v sieti ({devices.Count}) – {Ui.Ago(_scan["time"])}", grid)));
        }
        else
        {
            _root.Children.Add(Ui.Card(Ui.Text("Server prehľadá len vlastnú sieť: odozvu (ping), názov NetBIOS, kartu (MAC) a názov v DNS. Porty neskúša.", 13, brush: "TextMuted")));
        }
    }

    private static StackPanel Section(string title, UIElement content)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(title, 16, bold: true));
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(content);
        return panel;
    }

    private static void Columns(Grid grid, params string[] headers)
    {
        foreach (var _ in headers)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        grid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < headers.Length; c++)
        {
            Place(grid, Ui.Caption(headers[c]), 0, c);
        }
    }

    private static Button Small(string text, Action click)
    {
        var button = Ui.Button(text, click);
        button.MinHeight = 30;
        button.Padding = new Thickness(12, 0, 12, 0);
        button.FontSize = 12.5;
        button.Margin = new Thickness(0);
        return button;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 5, 22, 5);
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
