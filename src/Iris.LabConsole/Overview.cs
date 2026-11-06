using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Iris.LabConsole;

/// <summary>
/// The classroom at a glance: the numbers that matter, the computers as tiles by their groups (colored by state, load
/// or temperature; a click opens one), and on the side what needs attention and how well the model reads.
/// </summary>
public sealed class OverviewPage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private string _colors = "state";
    private ConsoleData? _last;

    public OverviewPage() => Content = Scroll(_root);

    public override string Heading => "Prehľad";

    public override void Show(ConsoleData data)
    {
        _last = data;
        _root.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _root.Children.Add(error);
        }

        var agents = data.Agents.ToList();
        var connected = agents.Count(a => a["connected"]?.GetValue<bool>() == true);
        var learning = agents.Where(a => Ui.String(a["status"]?["state"]) == "learning").ToList();
        var speed = learning.Sum(a => Ui.Value(a["status"]?["last"]?["lines_per_second"]) is var v && double.IsFinite(v) ? v : 0);
        var snapshot = data.Snapshot;
        var round = snapshot?["round"] as JsonObject;
        var tests = snapshot?["tests"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var open = snapshot?["open"]?.GetValue<bool>() == true;
        var schedule = snapshot?["schedule"];

        var numbers = new UniformGrid { Columns = 5 };
        numbers.Children.Add(Number(Ui.Icons.Computers, "Sky", "SkyDeep", "Počítače", $"{connected} / {agents.Count}", "pripojené"));
        numbers.Children.Add(Number(Ui.Icons.Learning, "Mint", "MintDeep", "Učia sa", learning.Count.ToString(CultureInfo.InvariantCulture),
            open ? $"čas učenia do {schedule?["until"]}" : $"učenie od {schedule?["from"]}"));
        numbers.Children.Add(Number(Ui.Icons.Speed, "Peach", "PeachDeep", "Riadky/s", speed.ToString("0.0", CultureInfo.InvariantCulture), "celá učebňa"));
        numbers.Children.Add(Number(Ui.Icons.Model, "Lavender", "LavenderDeep", "Model", $"v{snapshot?["version"] ?? 0}",
            tests.LastOrDefault() is { } test ? $"chybovosť {Ui.Number(test["cer"], "0.00", " %")}" : "chybovosť zatiaľ nezmeraná",
            Sparkline([.. tests.Select(t => Ui.Value(t["cer"]))])));
        numbers.Children.Add(Number(Ui.Icons.Round, "Lilac", "LilacDeep", "Kolo", round is null ? "–" : $"{round["number"]}",
            round is null ? (open ? "čaká na počítače" : "mimo času učenia") : $"{Left(round["deadline"])} · {round["done"]}/{round["taking_part"]} hotové"));
        _root.Children.Add(numbers);

        var tools = new DockPanel { Margin = new Thickness(8, 6, 8, 4), LastChildFill = false };
        var colors = Ui.Segments("overview-colors", [("state", "Stav"), ("load", "Vyťaženie"), ("temperature", "Teplota")], _colors, key =>
        {
            _colors = key;
            if (_last is not null)
            {
                Show(_last);
            }
        });
        colors.Margin = new Thickness(0, 0, 0, 8);
        tools.Children.Add(colors);
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(Ui.Button("Pozastaviť všetky", () => Ask("*", "pause")));
        commands.Children.Add(Ui.Button("Pokračovať všetky", () => Ask("*", "resume")));
        commands.Children.Add(Ui.Button("Zobudiť vypnuté", () => Ask("*", "wake")));
        DockPanel.SetDock(commands, Dock.Right);
        tools.Children.Add(commands);
        _root.Children.Add(tools);

        // the tiles by group on the left, attention and learning on the right
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var groups = new StackPanel();
        foreach (var (group, computers) in data.Groups)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 0, 2) };
            header.Children.Add(Ui.Text(group.Length == 0 ? "Bez skupiny" : group, 15, bold: true, brush: "PageText"));
            header.Children.Add(Ui.Text($"  ·  {computers.Count} {Computers(computers.Count)}, {computers.Count(a => a["connected"]?.GetValue<bool>() == true)} pripojených", 13, brush: "PageMuted"));
            groups.Children.Add(header);
            var tiles = new WrapPanel();
            foreach (var agent in computers)
            {
                tiles.Children.Add(Tile(agent));
            }

            groups.Children.Add(tiles);
        }

        if (agents.Count == 0)
        {
            groups.Children.Add(Ui.Card(Ui.Text("Zatiaľ sa nepripojil žiadny počítač. Na počítačoch v učebni nainštalujte službu (iris-agent install).", 14, brush: "TextMuted")));
        }

        body.Children.Add(groups);
        var side = new StackPanel();
        Grid.SetColumn(side, 1);
        side.Children.Add(Attention(data, open));
        side.Children.Add(Learning(data));
        body.Children.Add(side);
        _root.Children.Add(body);
        Dispatcher.BeginInvoke(() => Ui.SizeBars(_root), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string Computers(int count) => count == 1 ? "počítač" : count is >= 2 and <= 4 ? "počítače" : "počítačov";

    private static Border Number(string icon, string tint, string deep, string caption, string value, string note, UIElement? extra = null)
    {
        var panel = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var symbol = Ui.Icon(icon, tint, deep, 34);
        DockPanel.SetDock(symbol, Dock.Right);
        head.Children.Add(symbol);
        head.Children.Add(new TextBlock { Text = caption, Style = Ui.Resource<Style>("Caption"), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        var text = new StackPanel();
        text.Children.Add(Ui.Text(value, 26, bold: true));
        if (extra is not null)
        {
            text.Children.Add(extra);
        }

        text.Children.Add(Ui.Text(note, 12, brush: "TextMuted"));
        panel.Children.Add(text);
        var card = Ui.Card(panel);
        card.Padding = new Thickness(16, 14, 16, 14);
        return card;
    }

    private static Sparkline? Sparkline(List<double> values) =>
        values.Count(double.IsFinite) >= 2 ? new Sparkline { Values = values, Height = 22, Margin = new Thickness(0, 2, 0, 2) } : null;

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
        // a computer that is not connected has no values now: its last ones would only mislead
        var connected = agent["connected"]?.GetValue<bool>() == true;
        var status = connected ? agent["status"] as JsonObject : null;
        var machine = status?["machine"] as JsonObject;
        var cpu = Ui.Value(machine?["cpu"]);
        var own = Ui.Value(machine?["cpu_own"]);
        var temperature = Ui.Value(machine?["temperature"]);
        var (stateText, stateBack, stateFore) = Ui.State(agent);
        var name = Ui.String(agent["name"]) ?? "?";
        var shown = Ui.Name(agent);

        var panel = new StackPanel { Width = 176 };
        panel.Children.Add(new TextBlock { Text = shown, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (shown != name)
        {
            panel.Children.Add(Ui.Text(name, 11.5, brush: "TextMuted"));
        }

        var badge = Ui.Badge(stateText, stateBack, stateFore);
        badge.Margin = new Thickness(0, 5, 0, 7);
        panel.Children.Add(badge);
        if (!connected)
        {
            panel.Children.Add(Ui.Text(agent["seen"] is not null ? $"naposledy {Ui.Ago(agent["seen"])}" : "ešte sa nepripojil", 12, brush: "TextMuted"));
        }
        else
        {
            panel.Children.Add(Ui.Text($"procesor {Ui.Number(machine?["cpu"], "0", " %")} · učenie {Ui.Number(machine?["cpu_own"], "0", " %")}", 12, brush: "TextMuted"));
            panel.Children.Add(Ui.Bar(double.IsFinite(cpu) ? cpu : 0, double.IsFinite(own) ? own : 0));
            panel.Children.Add(Ui.Text($"{Ui.Temperature(agent)} · {Ui.Number(status?["last"]?["lines_per_second"], "0.0")} riadkov/s", 12, brush: "TextMuted"));
        }
        var (strainText, strainWhy, strainBrush, strainLevel) = Ui.Strain(agent);
        if (strainLevel is not null)
        {
            var line = Ui.Text("● " + strainText, 12, bold: strainLevel > 0, brush: strainBrush);
            line.ToolTip = strainWhy.Length > 0 ? strainWhy : null;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.TextWrapping = TextWrapping.NoWrap;
            panel.Children.Add(line);
        }

        var stripe = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = Ui.Brush(stateFore), Margin = new Thickness(0, 2, 12, 2) };
        var row = new DockPanel();
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(panel);
        var card = Ui.Card(row, 6);
        card.Padding = new Thickness(12, 12, 14, 12);
        card.Cursor = Cursors.Hand;
        card.Background = _colors switch
        {
            "load" when double.IsFinite(cpu) => new SolidColorBrush(HeatMap.Scale(cpu / 100)),
            "temperature" when double.IsFinite(temperature) => new SolidColorBrush(HeatMap.Scale((temperature - 35) / 50)),
            _ => card.Background,
        };
        card.ContextMenu = Menu(name);
        card.ToolTip = $"{shown} ({name}) – {agent["address"]}{(Ui.String(agent["model"]) is { } model ? ", " + model : "")}; kliknutím podrobnosti, pravým tlačidlom príkazy";
        card.MouseLeftButtonUp += (_, _) => Actions?.Open(name);
        return card;
    }

    private ContextMenu Menu(string name)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Podrobnosti a nastavenia" };
        open.Click += (_, _) => Actions?.Open(name);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        foreach (var (text, command, question) in new (string, string, string?)[]
                 {
                     ("Pozastaviť učenie", "pause", null), ("Pokračovať v učení", "resume", null), ("Zobudiť", "wake", null),
                     ("Reštartovať počítač", "restart", $"Reštartovať {name}? (o minútu)"), ("Vypnúť počítač", "shutdown", $"Vypnúť {name}? (o minútu)"),
                 })
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => Ask(name, command, question);
            menu.Items.Add(item);
        }

        return menu;
    }

    // what needs attention: computers away during the time of learning, too warm, with an old program, without fonts
    private Border Attention(ConsoleData data, bool open)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Pozornosť"));
        var notes = new List<(string Text, string Tint, string Deep, string? Computer)>();
        var older = 0;
        foreach (var agent in data.Agents)
        {
            var name = Ui.String(agent["name"]) ?? "?";
            var shown = Ui.Name(agent);
            var temperature = Ui.Value(agent["status"]?["machine"]?["temperature"]);
            var hot = Ui.Value(agent["settings"]?["hot"]);
            if (agent["connected"]?.GetValue<bool>() != true)
            {
                if (agent["on"]?.GetValue<bool>() == true)
                {
                    // on, but its agent does not connect: something to fix (the service, the key, the network)
                    notes.Add(($"{shown} je zapnutý, ale IRIS sa nepripája – skontrolujte službu IRIS-Lab na ňom", "WarnSoft", "Warn", name));
                }
                else if (open && agent["settings"]?["learn"]?.GetValue<bool>() != false)
                {
                    notes.Add(($"{shown} je v čase učenia {(agent["on"]?.GetValue<bool>() == false ? "vypnutý" : "nepripojený")}", "SurfaceAlt", "TextMuted", name));
                }

                continue;
            }

            if (agent["status"]?["too_hot"]?.GetValue<bool>() == true || (double.IsFinite(temperature) && double.IsFinite(hot) && temperature >= hot - 3))
            {
                notes.Add(($"{shown}: {temperature:0} °C – učenie čaká, kým sa ochladí", "WarnSoft", "Warn", name));
            }

            var struggles = Ui.Strain(agent);
            if (struggles.Level == 2)
            {
                notes.Add(($"{shown} sa trápi: {struggles.Reasons}", "WarnSoft", "Warn", name));
            }

            if (Ui.String(agent["status"]?["state"]) == "no fonts")
            {
                notes.Add(($"{shown}: chýbajú písma", "WarnSoft", "Warn", name));
            }

            if (data.Available is { } available && Ui.String(agent["version"]) is { } version && Iris.Lab.LabUpdates.IsNewer(available, version))
            {
                older++;
            }
        }

        if (older > 0)
        {
            // one line for all of them: the update goes between rounds by itself, or at once from Settings
            notes.Add(($"{older} {Computers(older)} so starším programom – server má {data.Available}", "Lilac", "LilacDeep", null));
        }

        if (notes.Count == 0)
        {
            var fine = new StackPanel { Orientation = Orientation.Horizontal };
            fine.Children.Add(Ui.Icon("M4,10 L8,14 L16,6", "GoodSoft", "Good", 30));
            var text = Ui.Text("Všetko je v poriadku.", 13.5);
            text.Margin = new Thickness(10, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            fine.Children.Add(text);
            panel.Children.Add(fine);
        }

        foreach (var (text, tint, deep, computer) in notes.Take(12))
        {
            var line = new Border { Background = Ui.Brush(tint), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
            line.Child = Ui.Text(text, 12.5, brush: deep);
            if (computer is not null)
            {
                line.MouseLeftButtonUp += (_, _) => Actions?.Open(computer);
            }

            panel.Children.Add(line);
        }

        if (notes.Count > 12)
        {
            panel.Children.Add(Ui.Text($"… a ďalších {notes.Count - 12}", 12, brush: "TextMuted"));
        }

        return Ui.Card(panel);
    }

    // how well the model reads, and the last round
    private static Border Learning(ConsoleData data)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Učenie", "chybovosť čítania (CER) podľa verzie modelu"));
        var tests = data.Snapshot?["tests"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        if (tests.Count >= 2)
        {
            panel.Children.Add(new Sparkline { Values = [.. tests.TakeLast(40).Select(t => Ui.Value(t["cer"]))], Height = 46, Margin = new Thickness(0, 0, 0, 8) });
        }

        var last = tests.LastOrDefault();
        panel.Children.Add(Ui.Fact("známe písma", last is null ? null : Ui.Number(last["cer"], "0.00", " %")));
        panel.Children.Add(Ui.Fact("neznáme písmo", last is null ? null : Ui.Number(last["cer_unseen"], "0.00", " %")));
        if (last?["average_cer"] is { } average)
        {
            panel.Children.Add(Ui.Fact("priemer modelu", $"{Ui.Number(average, "0.00", " %")} / {Ui.Number(last["average_cer_unseen"], "0.00", " %")}"));
        }

        var round = data.Snapshot?["rounds"]?.AsArray().OfType<JsonObject>().LastOrDefault();
        panel.Children.Add(Ui.Fact("posledné kolo", round is null ? null : $"{round["round"]}: {Ui.Number(round["computers"])} počítačov, loss {Ui.Number(round["loss"], "0.000")}"));
        panel.Children.Add(Ui.Fact("model", $"verzia {data.Snapshot?["version"] ?? 0}, {Ui.Number(data.Snapshot?["minutes"], "#,0")} min učenia"));
        return Ui.Card(panel);
    }
}
