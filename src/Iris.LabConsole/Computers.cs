using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Iris.LabConsole;

/// <summary>
/// Every computer in a list (search, its group, state, load, temperature, speed, version); a chosen one opens beside it:
/// its name in the console and its group, how it is doing, the commands, its own settings and what it is (from its
/// firmware: model, service tag, processor with its cores, memory modules, disks, graphics, system, network).
/// </summary>
public sealed class ComputersPage : ConsolePage
{
    private readonly Grid _root = new();
    private readonly StackPanel _list = new();
    private readonly ContentControl _detail = new();
    private readonly TextBox _search = new() { MinWidth = 220, Margin = new Thickness(0, 0, 12, 0) };
    private StackPanel? _live;
    private string? _selected;
    private string? _detailFor;
    private ConsoleData? _last;

    public ComputersPage()
    {
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new DockPanel();
        var tools = new DockPanel { Margin = new Thickness(8, 0, 8, 6) };
        tools.Children.Add(Ui.Caption("Hľadať"));
        ((TextBlock)tools.Children[0]).Margin = new Thickness(0, 0, 8, 0);
        ((TextBlock)tools.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(_search);
        _search.TextChanged += (_, _) =>
        {
            if (_last is not null)
            {
                List(_last);
            }
        };
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(Scroll(_list));
        _root.Children.Add(left);
        var detail = Scroll(_detail);
        detail.Width = 470;
        Grid.SetColumn(detail, 1);
        _root.Children.Add(detail);
        Content = _root;
    }

    public override string Heading => "Počítače";

    /// <summary>Opens a computer (from the overview or the self-test).</summary>
    public void Select(string name)
    {
        _selected = name;
        _detailFor = null;
    }

    public override void Show(ConsoleData data)
    {
        _last = data;
        List(data);
        Detail(data);
    }

    private void List(ConsoleData data)
    {
        _list.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _list.Children.Add(error);
        }

        var query = _search.Text.Trim();
        var table = new StackPanel();
        table.Children.Add(Row(null, ["Počítač", "Skupina", "Stav", "Procesor", "Teplota", "Riadky/s", "Verzia"], header: true));
        foreach (var agent in data.Agents.OrderBy(a => Ui.String(a["group"]) ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(Ui.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = Ui.String(agent["name"]) ?? "?";
            if (query.Length > 0 && !new[] { name, Ui.Name(agent), Ui.String(agent["group"]), Ui.String(agent["address"]), Ui.String(agent["model"]) }
                    .Any(t => t?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }

            var machine = agent["status"]?["machine"] as JsonObject;
            var (state, back, fore) = Ui.State(agent);
            var version = Ui.String(agent["version"]);
            var old = data.Available is { } available && version is not null && Iris.Lab.LabUpdates.IsNewer(available, version);
            var radio = new RadioButton { Style = Ui.Resource<Style>("Row"), GroupName = "computers", IsChecked = string.Equals(name, _selected, StringComparison.OrdinalIgnoreCase) };
            radio.Content = Row(agent, [
                null,
                Ui.String(agent["group"]) is { Length: > 0 } g ? g : "–",
                null,
                Ui.Number(machine?["cpu"], "0", " %"),
                Ui.Number(machine?["temperature"], "0", " °C"),
                Ui.Number(agent["status"]?["last"]?["lines_per_second"], "0.0"),
                version is null ? "–" : old ? version + " ↑" : version
            ], header: false, badge: Ui.Badge(state, back, fore));
            radio.Checked += (_, _) =>
            {
                _selected = name;
                Detail(data);
            };
            table.Children.Add(radio);
        }

        _list.Children.Add(Ui.Card(table));
    }

    // one line of the list: the name (and the computer's own name under it), the group, the state, the numbers
    private static Grid Row(JsonObject? agent, string?[] cells, bool header, Border? badge = null)
    {
        var grid = new Grid { Margin = header ? new Thickness(10, 0, 10, 6) : new Thickness(0) };
        foreach (var width in new[] { 2.2, 1.6, 1.5, 0.9, 0.9, 0.9, 0.9 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        }

        for (var c = 0; c < cells.Length; c++)
        {
            FrameworkElement element;
            if (header)
            {
                element = Ui.Caption(cells[c] ?? "");
            }
            else if (c == 0 && agent is not null)
            {
                var names = new StackPanel();
                names.Children.Add(new TextBlock { Text = Ui.Name(agent), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
                var own = Ui.String(agent["name"]) ?? "";
                names.Children.Add(new TextBlock
                {
                    Text = own == Ui.Name(agent) ? Ui.String(agent["address"]) ?? "" : $"{own} · {agent["address"]}",
                    FontSize = 11.5,
                    Foreground = Ui.Brush("TextMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                element = names;
            }
            else if (c == 2 && badge is not null)
            {
                element = badge;
            }
            else
            {
                element = new TextBlock { Text = cells[c] ?? "", FontSize = 13, Foreground = Ui.Brush(c == 1 ? "TextMuted" : "Text"), TextTrimming = TextTrimming.CharacterEllipsis };
            }

            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(element, c);
            grid.Children.Add(element);
        }

        return grid;
    }

    private void Detail(ConsoleData data)
    {
        if (_selected is null || data.Agent(_selected) is not { } agent)
        {
            _detail.Content = Ui.Card(Ui.Text("Vyberte počítač v zozname: ukáže sa, ako sa mu darí, jeho príkazy, vlastné nastavenia a čo je zač.", 13.5, brush: "TextMuted"));
            _detailFor = null;
            return;
        }

        // the editors are built once per computer (a refresh must not throw away what is being typed); the measurements every time
        if (_detailFor != _selected)
        {
            _detailFor = _selected;
            _live = new StackPanel();
            _detail.Content = Build(data, agent, _live);
        }

        Live(agent, data);
    }

    private StackPanel Build(ConsoleData data, JsonObject agent, StackPanel live)
    {
        var name = Ui.String(agent["name"]) ?? "?";
        var panel = new StackPanel();

        // who it is: the name in the console, the group
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = Ui.Name(agent), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Ui.Resource<Brush>("TitleGradient"), FontFamily = Ui.Resource<FontFamily>("DisplayFont") });
        head.Children.Add(Ui.Text($"{name} · {agent["address"]} · {Ui.String(agent["mac"]) ?? "karta neznáma"}", 12.5, brush: "TextMuted"));
        head.Children.Add(new Border { Height = 12 });
        var alias = new TextBox { Text = Ui.String(agent["alias"]) ?? "", MinWidth = 200 };
        head.Children.Add(Ui.Caption("Meno v konzole (počítač si ponechá svoje)"));
        head.Children.Add(Line(alias, Ui.Small("Uložiť meno", async () =>
            await Configure(new JsonObject { ["alias"] = new JsonObject { ["computer"] = name, ["name"] = alias.Text.Trim() } }))));
        var groups = data.Groups.Select(g => g.Group).Where(g => g.Length > 0).ToList();
        var group = new ComboBox { IsEditable = true, ItemsSource = groups, Text = Ui.String(data.Config["groups"]?[name]) ?? "", MinWidth = 200 };
        head.Children.Add(Ui.Caption($"Skupina (prázdna = podľa modelu{(Ui.String(agent["model"]) is { } model ? ": " + model : "")})"));
        head.Children.Add(Line(group, Ui.Small("Uložiť skupinu", async () =>
            await Configure(new JsonObject { ["group"] = new JsonObject { ["computer"] = name, ["name"] = group.Text.Trim() } }))));
        panel.Children.Add(Ui.Card(head));

        // how it is doing (refreshed), and the commands
        var doing = new StackPanel();
        doing.Children.Add(Ui.Heading("Teraz"));
        doing.Children.Add(live);
        var commands = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        commands.Children.Add(Ui.Small("Pozastaviť", () => Ask(name, "pause")));
        commands.Children.Add(Ui.Small("Pokračovať", () => Ask(name, "resume")));
        commands.Children.Add(Ui.Small("Zobudiť", () => Ask(name, "wake")));
        commands.Children.Add(Ui.Small("Aktualizovať", () => _ = Actions?.UpdateComputers(name)));
        commands.Children.Add(Ui.Small("Reštartovať", () => Ask(name, "restart", $"Reštartovať {Ui.Name(agent)}? (o minútu)")));
        commands.Children.Add(Ui.Small("Vypnúť", () => Ask(name, "shutdown", $"Vypnúť {Ui.Name(agent)}? (o minútu)"), danger: true));
        doing.Children.Add(commands);
        panel.Children.Add(Ui.Card(doing));

        // its own settings (what it does not set comes from its group and from all)
        var settings = new StackPanel();
        settings.Children.Add(Ui.Heading("Nastavenia tohto počítača", "čo nie je vlastné, berie zo skupiny a zo „všetkých“"));
        settings.Children.Add(new SettingsEditor("computer", name, data.Own("computer", name), data.Inherited("computer", Ui.String(agent["group"])), Configure));
        panel.Children.Add(Ui.Card(settings));

        panel.Children.Add(Ui.Card(Hardware(agent)));
        return panel;
    }

    private static DockPanel Line(FrameworkElement field, Button button)
    {
        var line = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        button.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(button, Dock.Right);
        line.Children.Add(button);
        line.Children.Add(field);
        return line;
    }

    private async Task<bool> Configure(JsonObject change)
    {
        if (Actions is null)
        {
            return false;
        }

        var done = await Actions.Configure(change);
        _detailFor = null; // the next refresh builds it again with what the hub keeps now
        return done;
    }

    private void Live(JsonObject agent, ConsoleData data)
    {
        if (_live is null)
        {
            return;
        }

        _live.Children.Clear();
        var status = agent["status"] as JsonObject;
        var machine = status?["machine"] as JsonObject;
        var (state, back, fore) = Ui.State(agent);
        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        badges.Children.Add(Ui.Badge(state, back, fore));
        if (Ui.String(agent["version"]) is { } version)
        {
            var old = data.Available is { } available && Iris.Lab.LabUpdates.IsNewer(available, version);
            var badge = Ui.Badge($"program {version}{(old ? " – je novší" : "")}", old ? "Lilac" : "SurfaceAlt", old ? "LilacDeep" : "TextMuted");
            badge.Margin = new Thickness(6, 0, 0, 0);
            badges.Children.Add(badge);
        }

        if (Ui.Bool(status?["eco"]) == true)
        {
            var eco = Ui.Badge("úsporný režim", "Mint", "MintDeep");
            eco.Margin = new Thickness(6, 0, 0, 0);
            badges.Children.Add(eco);
        }

        _live.Children.Add(badges);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var (label, value) in new[]
                 {
                     ("procesor", Ui.Number(machine?["cpu"], "0", " %")), ("učenie", Ui.Number(machine?["cpu_own"], "0", " %")),
                     ("pamäť", Ui.Number(machine?["memory"], "0", " %")), ("teplota", Ui.Number(machine?["temperature"], "0", " °C")),
                     ("riadky/s", Ui.Number(status?["last"]?["lines_per_second"], "0.0")), ("vlákna", Ui.Number(status?["threads"])),
                     ("kolá", Ui.Number(agent["rounds"])), ("naposledy", Ui.Ago(agent["seen"])),
                 })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            cell.Children.Add(Ui.Text(value, 17, bold: true));
            cell.Children.Add(Ui.Text(label, 11.5, brush: "TextMuted"));
            grid.Children.Add(cell);
        }

        _live.Children.Add(grid);
    }

    // what the computer is, from its firmware and system
    private static StackPanel Hardware(JsonObject agent)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Hardvér a systém", "z tabuliek firmvéru (SMBIOS) a zo systému, keď sa počítač pripojil"));
        if (agent["inventory"] is not JsonObject inventory)
        {
            panel.Children.Add(Ui.Text("Počítač ešte nepovedal, čo je zač (pripojí sa s novším programom).", 13, brush: "TextMuted"));
            return panel;
        }

        string? S(JsonNode? node) => Ui.String(node);
        var cpu = inventory["cpu"] as JsonObject;
        panel.Children.Add(Ui.Fact("výrobca", S(inventory["maker"])));
        panel.Children.Add(Ui.Fact("model", string.Join(" · ", new[] { S(inventory["model"]), S(inventory["family"]) }.Where(t => t is not null))));
        panel.Children.Add(Ui.Fact("servisný štítok", S(inventory["serial"])));
        panel.Children.Add(Ui.Fact("skriňa", Chassis(S(inventory["chassis"]?["type"]))));
        panel.Children.Add(Ui.Fact("BIOS", string.Join(" · ", new[] { S(inventory["bios"]?["version"]), S(inventory["bios"]?["date"]) }.Where(t => t is not null))));
        var cores = Ui.Value(cpu?["performance_cores"]) is var p && double.IsFinite(p)
            ? $"{p:0} výkonných + {Ui.Value(cpu?["efficient_cores"]):0} úsporných jadier"
            : Ui.Value(cpu?["cores"]) is var c && double.IsFinite(c) ? $"{c:0} jadier" : null;
        panel.Children.Add(Ui.Fact("procesor", S(cpu?["name"])));
        panel.Children.Add(Ui.Fact("jadrá", string.Join(", ", new[] { cores, $"{Ui.Number(cpu?["logical"])} vlákien", Ui.Bool(cpu?["avx2"]) == true ? "AVX2" : null }.Where(t => t is not null))));
        var modules = inventory["memory_modules"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        panel.Children.Add(Ui.Fact("pamäť", $"{Ui.Number(inventory["memory_gb"], "0")} GB" + (modules.Count > 0
            ? " · " + string.Join(", ", modules.Select(m => $"{Ui.Number(m["size_gb"], "0")} GB {S(m["type"])} {Ui.Number(m["speed"], "0")}".Replace(" –", "", StringComparison.Ordinal)))
            : "")));
        foreach (var disk in inventory["disks"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            panel.Children.Add(Ui.Fact("disk", string.Join(" · ", new[] { S(disk["model"]), S(disk["bus"]), Ui.Value(disk["size_gb"]) is var g && double.IsFinite(g) ? $"{g:0} GB" : null }.Where(t => t is not null))));
        }

        panel.Children.Add(Ui.Fact("grafika", string.Join(", ", inventory["gpu"]?.AsArray().Select(S).Where(t => t is not null) ?? [])));
        panel.Children.Add(Ui.Fact("systém", string.Join(" · ", new[] { S(inventory["os"]?["name"]), S(inventory["os"]?["version"]), S(inventory["os"]?["build"]) }.Where(t => t is not null))));
        foreach (var card in inventory["network"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            panel.Children.Add(Ui.Fact(Ui.Bool(card["wireless"]) == true ? "wi-fi" : "sieť", string.Join(" · ", new[] { S(card["name"]), Ui.Value(card["mbps"]) is var m && double.IsFinite(m) ? $"{m:0} Mb/s" : null, S(card["mac"]) }.Where(t => t is not null))));
        }

        return panel;
    }

    private static string? Chassis(string? type) => type switch
    {
        null => null,
        "desktop" => "stolový",
        "low profile desktop" => "nízky stolový",
        "mini tower" => "minitower",
        "tower" => "tower",
        "laptop" or "notebook" or "portable" => "notebook",
        "all in one" => "všetko v jednom",
        "space-saving" => "úsporný (SFF)",
        "mini PC" => "mini PC",
        _ => type,
    };
}
