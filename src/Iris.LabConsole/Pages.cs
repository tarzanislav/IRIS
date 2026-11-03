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
}

/// <summary>A page of the console.</summary>
public abstract class ConsolePage : UserControl
{
    /// <summary>Sends a command (target "*" = all); set by the window.</summary>
    public Func<string, string, Task>? Command { get; set; }

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

        if (Command is { } send)
        {
            await send(target, command);
        }
    }
}

/// <summary>The classroom at a glance: the numbers that matter and a tile for every computer.</summary>
public sealed class OverviewPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private string _colors = "state";
    private ConsoleData? _last;

    public OverviewPage() => Content = Scroll(_root);

    public override void Show(ConsoleData data)
    {
        _last = data;
        _root.Children.Clear();
        _root.Children.Add(Ui.Title("Učebňa"));
        if (data.Error is { } error)
        {
            _root.Children.Add(Ui.Card(Ui.Text(error, 14, brush: "Danger")));
        }

        var agents = data.Agents.ToList();
        var connected = agents.Count(a => a["connected"]?.GetValue<bool>() == true);
        var learning = agents.Count(a => a["status"]?["state"]?.GetValue<string>() == "learning");
        var speed = agents.Where(a => a["status"]?["state"]?.GetValue<string>() == "learning").Sum(a => Ui.Value(a["status"]?["last"]?["lines_per_second"]) is var v && double.IsFinite(v) ? v : 0);
        var snapshot = data.Snapshot;
        var round = snapshot?["round"] as JsonObject;
        var test = snapshot?["tests"]?.AsArray().LastOrDefault();
        var open = snapshot?["open"]?.GetValue<bool>() == true;
        var schedule = snapshot?["schedule"];

        var numbers = new UniformGrid { Columns = 5 };
        numbers.Children.Add(Number("Počítače", $"{connected} / {agents.Count}", "pripojené"));
        numbers.Children.Add(Number("Učia sa", learning.ToString(CultureInfo.InvariantCulture), open ? "čas učenia" : $"učenie od {schedule?["from"]}"));
        numbers.Children.Add(Number("Riadky/s", speed.ToString("0.0", CultureInfo.InvariantCulture), "celá učebňa"));
        numbers.Children.Add(Number("Model", $"v{snapshot?["version"] ?? 0}", test is null ? "chybovosť zatiaľ nezmeraná" : $"chybovosť {Ui.Number(test["cer"], "0.00", " %")}"));
        numbers.Children.Add(Number("Kolo", round is null ? "–" : $"{round["number"]}", round is null ? (open ? "čaká na počítače" : "mimo času učenia") : Left(round["deadline"])));
        _root.Children.Add(numbers);

        var tools = new WrapPanel { Margin = new Thickness(8, 8, 8, 0) };
        tools.Children.Add(Ui.Caption("Farby podľa:"));
        foreach (var (key, text) in new[] { ("state", "stavu"), ("load", "vyťaženia"), ("temperature", "teploty") })
        {
            var toggle = new RadioButton { Content = text, GroupName = "colors", IsChecked = _colors == key, Margin = new Thickness(10, 0, 4, 8), VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += (_, _) =>
            {
                _colors = key;
                if (_last is not null)
                {
                    Show(_last);
                }
            };
            tools.Children.Add(toggle);
        }

        tools.Children.Add(new Border { Width = 24 });
        tools.Children.Add(Ui.Button("Pozastaviť všetky", () => Ask("*", "pause")));
        tools.Children.Add(Ui.Button("Pokračovať všetky", () => Ask("*", "resume")));
        _root.Children.Add(tools);

        var tiles = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var agent in agents)
        {
            tiles.Children.Add(Tile(agent));
        }

        if (agents.Count == 0)
        {
            tiles.Children.Add(Ui.Card(Ui.Text("Zatiaľ sa nepripojil žiadny počítač. Na počítačoch v učebni nainštalujte službu (iris-agent install).", 14, brush: "TextMuted")));
        }

        _root.Children.Add(tiles);
        Dispatcher.BeginInvoke(() => Ui.SizeBars(_root), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static Border Number(string caption, string value, string note)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Caption(caption));
        panel.Children.Add(Ui.Text(value, 26, bold: true));
        panel.Children.Add(Ui.Text(note, 12, brush: "TextMuted"));
        return Ui.Card(panel);
    }

    private static string Left(JsonNode? deadline)
    {
        if (deadline is not JsonValue v || !v.TryGetValue(out string? text) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
        {
            return "";
        }

        var left = t - DateTime.Now;
        return left > TimeSpan.Zero ? $"koniec o {left.TotalMinutes:0} min" : "zbiera výsledky";
    }

    private Border Tile(JsonObject agent)
    {
        var status = agent["status"] as JsonObject;
        var machine = status?["machine"] as JsonObject;
        var cpu = Ui.Value(machine?["cpu"]);
        var own = Ui.Value(machine?["cpu_own"]);
        var temperature = Ui.Value(machine?["temperature"]);
        var (stateText, stateBack, stateFore) = Ui.State(agent);
        var name = agent["name"]?.GetValue<string>() ?? "?";

        var panel = new StackPanel { Width = 190 };
        var head = new DockPanel();
        head.Children.Add(Ui.Text(name, 16, bold: true));
        panel.Children.Add(head);
        panel.Children.Add(Ui.Badge(stateText, stateBack, stateFore));
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(Ui.Text($"procesor {Ui.Number(machine?["cpu"], "0", " %")} · učenie {Ui.Number(machine?["cpu_own"], "0", " %")}", 12, brush: "TextMuted"));
        panel.Children.Add(Ui.Bar(double.IsFinite(cpu) ? cpu : 0, double.IsFinite(own) ? own : 0));
        panel.Children.Add(Ui.Text($"teplota {Ui.Number(machine?["temperature"], "0", " °C")} · {Ui.Number(status?["last"]?["lines_per_second"], "0.0")} riadkov/s", 12, brush: "TextMuted"));
        panel.Children.Add(Ui.Text($"naposledy {Ui.Ago(agent["seen"])}", 11, brush: "TextMuted"));

        var card = Ui.Card(panel, 6);
        card.Padding = new Thickness(16, 14, 16, 14);
        card.Background = _colors switch
        {
            "load" when double.IsFinite(cpu) => new SolidColorBrush(HeatMap.Scale(cpu / 100)),
            "temperature" when double.IsFinite(temperature) => new SolidColorBrush(HeatMap.Scale((temperature - 35) / 50)),
            _ => card.Background,
        };
        card.ContextMenu = Menu(name);
        card.ToolTip = $"{name} – {agent["address"]}; pravým tlačidlom príkazy";
        return card;
    }

    private ContextMenu Menu(string name)
    {
        var menu = new ContextMenu();
        foreach (var (text, command, question) in new (string, string, string?)[]
                 {
                     ("Pozastaviť učenie", "pause", null), ("Pokračovať v učení", "resume", null),
                     ("Reštartovať počítač", "restart", $"Reštartovať {name}? (o minútu)"), ("Vypnúť počítač", "shutdown", $"Vypnúť {name}? (o minútu)"),
                 })
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => Ask(name, command, question);
            menu.Items.Add(item);
        }

        return menu;
    }
}

/// <summary>Every computer in a row: all its measurements and the commands.</summary>
public sealed class ComputersPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };

    public ComputersPage()
    {
        var scroll = Scroll(_root);
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = scroll;
    }

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        _root.Children.Add(Ui.Title("Počítače"));
        var grid = new Grid();
        string[] headers = ["Počítač", "Adresa", "Stav", "Procesor", "Učenie", "Pamäť", "Teplota", "Riadky/s", "Kolá", "Riadky spolu", "Naposledy", ""];
        foreach (var _ in headers)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        grid.ColumnDefinitions[^1].Width = new GridLength(1, GridUnitType.Star);
        grid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < headers.Length; c++)
        {
            Place(grid, Ui.Caption(headers[c]), 0, c);
        }

        var row = 1;
        foreach (var agent in data.Agents)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var status = agent["status"] as JsonObject;
            var machine = status?["machine"] as JsonObject;
            var (stateText, back, fore) = Ui.State(agent);
            var name = agent["name"]?.GetValue<string>() ?? "?";
            Place(grid, Ui.Text(name, 14, bold: true), row, 0);
            Place(grid, Ui.Text(agent["address"]?.GetValue<string>() ?? "", 13, brush: "TextMuted"), row, 1);
            Place(grid, Ui.Badge(stateText, back, fore), row, 2);
            Place(grid, Ui.Text(Ui.Number(machine?["cpu"], "0", " %"), 13), row, 3);
            Place(grid, Ui.Text(Ui.Number(machine?["cpu_own"], "0", " %"), 13), row, 4);
            Place(grid, Ui.Text(Ui.Number(machine?["memory"], "0", " %"), 13), row, 5);
            Place(grid, Ui.Text(Ui.Number(machine?["temperature"], "0", " °C"), 13), row, 6);
            Place(grid, Ui.Text(Ui.Number(status?["last"]?["lines_per_second"], "0.0"), 13), row, 7);
            Place(grid, Ui.Text(Ui.Number(agent["rounds"]), 13), row, 8);
            Place(grid, Ui.Text(Ui.Number(agent["lines"], "#,0"), 13), row, 9);
            Place(grid, Ui.Text(Ui.Ago(agent["seen"]), 13, brush: "TextMuted"), row, 10);
            var actions = new WrapPanel();
            actions.Children.Add(Small("Pozastaviť", () => Ask(name, "pause")));
            actions.Children.Add(Small("Pokračovať", () => Ask(name, "resume")));
            actions.Children.Add(Small("Reštart", () => Ask(name, "restart", $"Reštartovať {name}? (o minútu)")));
            actions.Children.Add(Small("Vypnúť", () => Ask(name, "shutdown", $"Vypnúť {name}? (o minútu)")));
            Place(grid, actions, row, 11);
            row++;
        }

        _root.Children.Add(Ui.Card(grid));
    }

    private static Button Small(string text, Action click)
    {
        var button = Ui.Button(text, click);
        button.MinHeight = 30;
        button.Padding = new Thickness(12, 0, 12, 0);
        button.FontSize = 12.5;
        button.Margin = new Thickness(0, 0, 6, 0);
        return button;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 6, 18, 6);
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}

/// <summary>The history in charts: load, temperature, speed of the whole classroom, and a map of hours.</summary>
public sealed class ChartsPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private bool _each;

    public ChartsPage() => Content = Scroll(_root);

    /// <summary>The span the user chose (hours); the window fetches the history.</summary>
    public Action<int>? Span { get; set; }

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        _root.Children.Add(Ui.Title("Grafy"));
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

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        _root.Children.Add(Ui.Title("Kolá a učenie"));
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

/// <summary>Where the console connects: the hub's address and port and the console key.</summary>
public sealed class SettingsPage : ConsolePage
{
    private readonly TextBox _hub = new() { MinWidth = 260 };
    private readonly TextBox _port = new() { MinWidth = 100 };
    private readonly TextBox _key = new() { MinWidth = 420 };

    public SettingsPage(ConsoleSettings settings)
    {
        _hub.Text = settings.Hub;
        _port.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        _key.Text = settings.KeyFile;
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(Ui.Title("Nastavenia"));
        var form = new StackPanel();
        form.Children.Add(Ui.Caption("Server (adresa alebo názov)"));
        form.Children.Add(_hub);
        form.Children.Add(new Border { Height = 12 });
        form.Children.Add(Ui.Caption("Port"));
        form.Children.Add(_port);
        form.Children.Add(new Border { Height = 12 });
        form.Children.Add(Ui.Caption("Kľúč konzoly (console.key zo servera)"));
        var keyRow = new DockPanel();
        var browse = Ui.Button("Vybrať…", Browse);
        DockPanel.SetDock(browse, Dock.Right);
        keyRow.Children.Add(browse);
        keyRow.Children.Add(_key);
        form.Children.Add(keyRow);
        form.Children.Add(new Border { Height = 16 });
        form.Children.Add(Ui.Button("Uložiť a pripojiť", () =>
        {
            settings.Hub = _hub.Text.Trim();
            settings.Port = int.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 47620;
            settings.KeyFile = _key.Text.Trim();
            settings.Save();
            Saved?.Invoke();
        }, primary: true));
        form.Children.Add(Ui.Text("Kľúč konzoly patrí len na tento počítač. Komu ho dáte, ten môže počítače v učebni pozastaviť, reštartovať a vypnúť.", 12, brush: "TextMuted"));
        panel.Children.Add(Ui.Card(form));
        Content = Scroll(panel);
    }

    public Action? Saved { get; set; }

    public override void Show(ConsoleData data)
    {
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Kľúč (*.key)|*.key|Všetky súbory|*.*" };
        if (dialog.ShowDialog() == true)
        {
            _key.Text = dialog.FileName;
        }
    }
}
