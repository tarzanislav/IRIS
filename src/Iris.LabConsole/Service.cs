using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// The service of the classroom: switch computers on (Wake-on-LAN by the hub) and off (or restart them) – one, the
/// ones ticked (a whole group with one click), or all. Shutting down always asks first; Windows warns the user of
/// the computer and waits a minute. The moving load at a glance – where it is on, with which bounds, set where – and
/// set for a whole group or the ticked computers. The diagnosis of one, the ticked or all computers. And the removal:
/// IRIS uninstalled from one computer, or a computer that left the classroom taken off the list.
/// </summary>
public sealed class ServicePage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private readonly Border _offHost = new();
    private readonly Border _onHost = new();
    private readonly Border _removeHost = new();
    private readonly Border _movingHost = new();
    private readonly Border _diagnosisHost = new();
    private readonly HashSet<string> _movingPicked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _diagnosisPicked = new(StringComparer.OrdinalIgnoreCase);
    private double _movingDuty = 0.75;
    private double _movingCores = 0.7;
    private string _diagnosisMode = "all";
    private List<JsonObject>? _diagnoses;
    private string? _diagnosisNote;
    private ConsoleData? _data;
    private string? _removeChoice;
    private bool _now;
    private bool _force;
    private readonly HashSet<string> _offPicked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _onPicked = new(StringComparer.OrdinalIgnoreCase);
    private string _offMode = "picked";
    private string _onMode = "picked";
    private string? _note;

    public ServicePage() => Content = Scroll(_root);

    public override string Heading => "Servis";

    /// <summary>Invented diagnoses for the self-test (pictures of the page).</summary>
    public void Fake(List<JsonObject> diagnoses)
    {
        _diagnoses = diagnoses;
        _diagnosisNote = $"Odpovedali všetky ({diagnoses.Count}).";
    }

    public override void Show(ConsoleData data)
    {
        _data = data;
        _root.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _root.Children.Add(error);
        }

        if (_note is not null)
        {
            _root.Children.Add(Ui.Card(Ui.Text(_note, 13.5, bold: true, brush: "Accent")));
        }

        // a card is built anew only while the mouse and the keyboard are elsewhere (a ticked list stays in the hand)
        if (_offHost.Child is null || !(_offHost.IsMouseOver || _offHost.IsKeyboardFocusWithin))
        {
            _offHost.Child = Ui.Card(Off(data));
        }

        if (_onHost.Child is null || !(_onHost.IsMouseOver || _onHost.IsKeyboardFocusWithin))
        {
            _onHost.Child = Ui.Card(On(data));
        }

        if (_removeHost.Child is null || !(_removeHost.IsMouseOver || _removeHost.IsKeyboardFocusWithin))
        {
            _removeHost.Child = Ui.Card(Remove(data));
        }

        if (_movingHost.Child is null || !(_movingHost.IsMouseOver || _movingHost.IsKeyboardFocusWithin))
        {
            _movingHost.Child = Ui.Card(Moving(data));
        }

        if (_diagnosisHost.Child is null || !(_diagnosisHost.IsMouseOver || _diagnosisHost.IsKeyboardFocusWithin))
        {
            _diagnosisHost.Child = Ui.Card(Diagnosis(data));
        }

        foreach (var host in new[] { _offHost, _onHost, _movingHost, _diagnosisHost, _removeHost })
        {
            if (host.Parent is Panel parent)
            {
                parent.Children.Remove(host);
            }

            _root.Children.Add(host);
        }
    }

    private StackPanel Off(ConsoleData data)
    {
        var connected = data.Agents.Where(a => a["connected"]?.GetValue<bool>() == true).ToList();
        _offPicked.RemoveWhere(n => !connected.Any(a => Same(a, n)));
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Vypnúť alebo reštartovať", $"{connected.Count} pripojených počítačov; bez voľieb Windows na počítači upozorní a vypne ho o minútu"));
        panel.Children.Add(Mode("service-off", _offMode, mode => { _offMode = mode; Redraw(data, _offHost); }));
        var chosen = Choose(data, connected, _offMode, _offPicked, () => Redraw(data, _offHost));
        panel.Children.Add(chosen.Panel);
        var now = new CheckBox { Content = "ihneď (bez minútovej lehoty)", IsChecked = _now, Margin = new Thickness(0, 8, 24, 0), Foreground = Ui.Brush("Text") };
        now.Checked += (_, _) => _now = true;
        now.Unchecked += (_, _) => _now = false;
        var force = new CheckBox { Content = "nasilu (zavrie aj programy, ktoré vypnutie brzdia – neuložená práca sa stratí)", IsChecked = _force, Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.Brush("Text") };
        force.Checked += (_, _) => _force = true;
        force.Unchecked += (_, _) => _force = false;
        var options = new WrapPanel();
        options.Children.Add(now);
        options.Children.Add(force);
        panel.Children.Add(options);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Button($"Vypnúť ({chosen.Names().Count})", () => _ = Power(data, chosen.Names(), LabCommands.Shutdown), danger: true));
        buttons.Children.Add(Ui.Button($"Reštartovať ({chosen.Names().Count})", () => _ = Power(data, chosen.Names(), LabCommands.Restart)));
        panel.Children.Add(buttons);
        return panel;
    }

    private StackPanel On(ConsoleData data)
    {
        var off = data.Agents.Where(a => a["connected"]?.GetValue<bool>() != true && Ui.String(a["state"]) != "uninstalled").ToList();
        var wakeable = off.Where(a => Ui.String(a["mac"]) is not null).ToList();
        var noCard = off.Where(a => Ui.String(a["mac"]) is null).ToList();
        _onPicked.RemoveWhere(n => !wakeable.Any(a => Same(a, n)));
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Zapnúť", $"{off.Count} vypnutých alebo odpojených; server ich zobudí cez sieť (Wake-on-LAN)"));
        panel.Children.Add(Mode("service-on", _onMode, mode => { _onMode = mode; Redraw(data, _onHost); }));
        var chosen = Choose(data, wakeable, _onMode, _onPicked, () => Redraw(data, _onHost));
        panel.Children.Add(chosen.Panel);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Button($"Zapnúť ({chosen.Names().Count})", () => _ = Wake(data, chosen.Names()), primary: true));
        panel.Children.Add(buttons);
        if (noCard.Count > 0)
        {
            panel.Children.Add(Ui.Text($"Nedajú sa zobudiť (server ešte nepozná ich sieťovú kartu): {string.Join(", ", noCard.Select(Ui.Name))}. Kartu pošle počítač sám, keď je zapnutý a pripojený.", 12.5, brush: "TextMuted"));
        }

        panel.Children.Add(Ui.Text("Zobudenie potrebuje v BIOSe Wake on LAN (LAN Only) a vypnutý Deep Sleep, vo Windows vypnuté rýchle spustenie. Počítač sa ozve do pár minút.", 12.5, brush: "TextMuted"));
        return panel;
    }

    // one computer at a time (on purpose: a removal is never sent to many by one click)
    private StackPanel Remove(ConsoleData data)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Odinštalovať alebo odstrániť", "vždy jeden počítač; odinštalovanie sa dá zrušiť, kým ho počítač neurobí"));
        var agents = data.Agents.OrderBy(Ui.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (agents.Count == 0)
        {
            panel.Children.Add(Ui.Text("Žiadny počítač.", 13, brush: "TextMuted"));
            return panel;
        }

        var list = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var agent in agents)
        {
            var (state, _, _) = Ui.State(agent);
            list.Items.Add(new ComboBoxItem { Content = $"{Ui.Name(agent)} – {state}", Tag = Ui.String(agent["name"]) });
        }

        list.SelectedIndex = Math.Max(0, agents.FindIndex(a => Same(a, _removeChoice ?? "")));
        var buttons = new WrapPanel();
        void Buttons()
        {
            buttons.Children.Clear();
            if (list.SelectedItem is not ComboBoxItem { Tag: string name } || data.Agent(name) is not { } agent)
            {
                return;
            }

            _removeChoice = name;
            var shown = Ui.Name(agent);
            var connected = agent["connected"]?.GetValue<bool>() == true;
            if (agent["leaving"]?.GetValue<bool>() == true)
            {
                buttons.Children.Add(Ui.Button("Zrušiť odinštalovanie", () => Ask(name, LabCommands.Stay)));
            }
            else if (Ui.String(agent["state"]) != "uninstalled")
            {
                buttons.Children.Add(Ui.Button("Odinštalovať…", () => Ask(name, LabCommands.Uninstall,
                    $"Odinštalovať IRIS z počítača {shown}?\n\nPočítač dokončí rozrobené kolo a potom odstráni službu, program aj kľúč. "
                    + "Ak je vypnutý, urobí to pri najbližšom pripojení. Vrátiť sa to dá len novou inštaláciou."), danger: true));
            }

            if (!connected)
            {
                buttons.Children.Add(Ui.Button("Odstrániť zo zoznamu…", async () =>
                {
                    if (MessageBox.Show($"Odstrániť {shown} zo zoznamu?\n\nZmizne meno v konzole, skupina aj vlastné nastavenia tohto počítača; história ostane. "
                            + "Ak sa počítač znova pripojí, objaví sa ako nový.", "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes && Actions is not null)
                    {
                        await Actions.Configure(new JsonObject { ["forget"] = new JsonObject { ["computer"] = name } });
                        _removeChoice = null;
                        Redraw(data, _removeHost);
                    }
                }, danger: true));
            }
        }

        list.SelectionChanged += (_, _) => Buttons();
        Buttons();
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        panel.Children.Add(Ui.Text("Odstrániť zo zoznamu sa dá len nepripojený počítač (napr. ktorý z učebne odišiel). Pri preinštalovaní Windows ho neodstraňujte – s rovnakým názvom nadviaže na všetko.", 12.5, brush: "TextMuted"));
        return panel;
    }

    // the moving load of every computer: on or off, its bounds and where that is set; on or off for a whole group or
    // for the ticked computers, with the upper bounds chosen here (the lower ones stay as they are)
    private StackPanel Moving(ConsoleData data)
    {
        var agents = data.Agents.Where(a => Ui.String(a["state"]) != "uninstalled").ToList();
        _movingPicked.RemoveWhere(n => !agents.Any(a => Same(a, n)));
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Pohyblivá záťaž", $"zapnutá na {agents.Count(MovingOn)} z {agents.Count} počítačov; zapnite ju celej skupine alebo vybraným počítačom"));
        var bounds = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bounds.Children.Add(Bound("Najviac času", _movingDuty, v => _movingDuty = v));
        bounds.Children.Add(Bound("Najviac jadier", _movingCores, v => _movingCores = v));
        panel.Children.Add(bounds);
        double[] widths = [0.35, 2.0, 0.9, 2.2, 1.1, 2.3];
        foreach (var (group, all) in data.Groups)
        {
            var members = all.Where(a => Ui.String(a["state"]) != "uninstalled").ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var names = members.Select(m => Ui.String(m["name"]) ?? "").ToList();
            var head = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
            var title = Ui.Text($"{(group.Length == 0 ? "bez skupiny" : group)} · zapnutá na {members.Count(MovingOn)} z {members.Count}", 14, bold: true);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0, 0, 12, 0);
            head.Children.Add(title);
            var picked = names.All(_movingPicked.Contains);
            head.Children.Add(Ui.Small(picked ? "− zrušiť výber" : "+ vybrať všetky", () =>
            {
                foreach (var name in names)
                {
                    if (picked)
                    {
                        _movingPicked.Remove(name);
                    }
                    else
                    {
                        _movingPicked.Add(name);
                    }
                }

                Redraw(data, _movingHost);
            }));
            if (group.Length > 0)
            {
                head.Children.Add(Ui.Small($"Celej skupine zapnúť ({Ui.Percent(_movingDuty)} / {Ui.Percent(_movingCores)})", () => _ = MovingForGroup(data, group, members, on: true), primary: true));
                head.Children.Add(Ui.Small("Celej skupine vypnúť", () => _ = MovingForGroup(data, group, members, on: false)));
            }

            panel.Children.Add(head);
            panel.Children.Add(MovingRow(widths, [null, Ui.Caption("Počítač"), Ui.Caption("Zapnutá"), Ui.Caption("Hranice (čas · jadrá)"), Ui.Caption("Nastavená"), Ui.Caption("Teraz")]));
            foreach (var agent in members)
            {
                var name = Ui.String(agent["name"]) ?? "?";
                var settings = agent["settings"] as JsonObject;
                var on = MovingOn(agent);
                var box = new CheckBox { IsChecked = _movingPicked.Contains(name), VerticalAlignment = VerticalAlignment.Center };
                box.Checked += (_, _) => _movingPicked.Add(name);
                box.Unchecked += (_, _) => _movingPicked.Remove(name);
                var limits = $"{Ui.Percent(Share(settings?["duty"], 0.5))}–{Ui.Percent(Share(settings?["duty_max"], 1))} · {Ui.Percent(Share(settings?["cores"], 0.5))}–{Ui.Percent(Share(settings?["cores_max"], 1))}";
                var moving = agent["connected"]?.GetValue<bool>() == true ? agent["status"]?["moving"] as JsonObject : null;
                var now = moving is null
                    ? (agent["connected"]?.GetValue<bool>() == true ? (on ? "čaká na kolo" : "pevná záťaž") : "nepripojený")
                    : $"{Ui.Number(agent["status"]?["threads"])} vl. · {Ui.Percent(Ui.Value(agent["status"]?["duty"]))} času · úroveň {Ui.Percent(Ui.Value(moving["level"]))}";
                panel.Children.Add(MovingRow(widths, [
                    box,
                    Ui.Text(Ui.Name(agent), 13.5, bold: true),
                    Ui.Badge(on ? "áno" : "nie", on ? "GoodSoft" : "SurfaceAlt", on ? "Good" : "TextMuted"),
                    Ui.Text(on ? limits : "–", 13),
                    Ui.Text(Source(data, agent, "moving"), 12.5, brush: "TextMuted"),
                    Ui.Text(now, 12.5, brush: moving is null ? "TextMuted" : "Text"),
                ]));
            }
        }

        var count = _movingPicked.Count;
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Ui.Button($"Zapnúť vybraným ({count})", () => _ = MovingForPicked(data, "on"), primary: true));
        buttons.Children.Add(Ui.Button($"Vypnúť vybraným ({count})", () => _ = MovingForPicked(data, "off")));
        buttons.Children.Add(Ui.Button($"Podľa skupiny ({count})", () => _ = MovingForPicked(data, "group")));
        panel.Children.Add(buttons);
        panel.Children.Add(Ui.Text("„Nastavená“ hovorí, odkiaľ počítač nastavenie berie: vlastné (len on), skupina, všetky alebo predvolené. Vlastné vyhráva nad skupinou – "
            + "„Celej skupine“ preto vlastné nastavenia pohyblivej záťaže jej počítačov zruší (najprv sa opýta), „Podľa skupiny“ ich zruší len vybraným. "
            + "Spodnú hranicu (podiel času a jadier) to nemení; nastavíte ju v Nastaveniach.", 12, brush: "TextMuted"));
        return panel;
    }

    private static bool MovingOn(JsonObject agent) => Ui.Bool(agent["settings"]?["moving"]) == true;

    private static double Share(JsonNode? node, double otherwise) => Ui.Value(node) is var v && double.IsFinite(v) ? v : otherwise;

    // where a computer's setting comes from: itself, its group, all, or the hub's defaults
    private static string Source(ConsoleData data, JsonObject agent, string key) =>
        data.Own("computer", Ui.String(agent["name"]) ?? "").ContainsKey(key) ? "vlastné"
        : data.Own("group", Ui.String(agent["group"]) ?? "").ContainsKey(key) ? "skupina"
        : data.Own("all", "").ContainsKey(key) ? "všetky"
        : "predvolené";

    private static readonly string[] MovingKeys = ["moving", "duty_max", "cores_max"];

    private JsonObject MovingValues(bool on) => on
        ? new JsonObject { ["moving"] = true, ["duty_max"] = Math.Round(_movingDuty, 2), ["cores_max"] = Math.Round(_movingCores, 2) }
        : new JsonObject { ["moving"] = false };

    private async Task MovingForGroup(ConsoleData data, string group, List<JsonObject> members, bool on)
    {
        if (Actions is null)
        {
            return;
        }

        // the computers of the group that set the moving load themselves would not follow the group
        var own = members.Where(m => MovingKeys.Any(data.Own("computer", Ui.String(m["name"]) ?? "").ContainsKey)).ToList();
        var what = on ? $"zapnúť (najviac {Ui.Percent(_movingDuty)} času a {Ui.Percent(_movingCores)} jadier)" : "vypnúť";
        var question = $"Pohyblivú záťaž {what} celej skupine {group} ({Count(members.Count)})?"
            + (own.Count > 0 ? $"\n\nVlastné nastavenie pohyblivej záťaže sa zruší, aby sa riadili skupinou: {string.Join(", ", own.Select(Ui.Name))}." : "");
        if (MessageBox.Show(question, "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var operations = new JsonArray { new JsonObject { ["scope"] = "group", ["names"] = new JsonArray(group), ["set"] = MovingValues(on) } };
        if (own.Count > 0)
        {
            operations.Add(new JsonObject
            {
                ["scope"] = "computer",
                ["names"] = new JsonArray([.. own.Select(m => (JsonNode?)Ui.String(m["name"]))]),
                ["remove"] = new JsonArray([.. MovingKeys.Select(k => (JsonNode?)k)]),
            });
        }

        var done = await Actions.Configure(new JsonObject { ["merge"] = operations });
        _note = done ? $"Skupina {group}: pohyblivá záťaž {(on ? "zapnutá" : "vypnutá")} – počítače dostali nové nastavenie." : "Nepodarilo sa uložiť (server nie je pripojený).";
        Redraw(_data ?? data, _movingHost);
    }

    private async Task MovingForPicked(ConsoleData data, string how)
    {
        var names = _movingPicked.ToList();
        if (names.Count == 0 || Actions is null)
        {
            _note = "Vyberte aspoň jeden počítač.";
            Redraw(data, _movingHost);
            return;
        }

        var what = how switch
        {
            "on" => $"zapnúť (najviac {Ui.Percent(_movingDuty)} času a {Ui.Percent(_movingCores)} jadier)",
            "off" => "vypnúť",
            _ => "nastaviť podľa skupiny (ich vlastné nastavenie sa zruší)",
        };
        if (MessageBox.Show($"Pohyblivú záťaž {what} – {Count(names.Count)}?\n\n{string.Join(", ", names.Select(n => Display(data, n)))}",
                "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var operation = new JsonObject { ["scope"] = "computer", ["names"] = new JsonArray([.. names.Select(n => (JsonNode?)n)]) };
        if (how == "group")
        {
            operation["remove"] = new JsonArray([.. MovingKeys.Select(k => (JsonNode?)k)]);
        }
        else
        {
            operation["set"] = MovingValues(how == "on");
        }

        var done = await Actions.Configure(new JsonObject { ["merge"] = new JsonArray(operation) });
        _note = done ? $"Pohyblivá záťaž: {what} – {Count(names.Count)}." : "Nepodarilo sa uložiť (server nie je pripojený).";
        Redraw(_data ?? data, _movingHost);
    }

    private static StackPanel Bound(string text, double value, Action<double> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 28, 0) };
        var label = Ui.Text(text, 13.5, bold: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 0);
        var shown = Ui.Text(Ui.Percent(value), 13.5, bold: true);
        shown.VerticalAlignment = VerticalAlignment.Center;
        shown.Width = 48;
        var slider = new Slider { Minimum = 0.05, Maximum = 1, SmallChange = 0.05, LargeChange = 0.1, TickFrequency = 0.05, IsSnapToTickEnabled = true, Value = value, Width = 180, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            shown.Text = Ui.Percent(e.NewValue);
        };
        panel.Children.Add(label);
        panel.Children.Add(slider);
        panel.Children.Add(shown);
        return panel;
    }

    private static Grid MovingRow(double[] widths, FrameworkElement?[] cells)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        }

        for (var c = 0; c < cells.Length; c++)
        {
            if (cells[c] is not { } cell)
            {
                continue;
            }

            cell.Margin = new Thickness(0, 0, 10, 0);
            cell.VerticalAlignment = VerticalAlignment.Center;
            if (cell is Border)
            {
                cell.HorizontalAlignment = HorizontalAlignment.Left;
            }

            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }

        return grid;
    }

    // the diagnosis of one, the ticked or all connected computers: asked for, waited for, shown as a table and saved
    private StackPanel Diagnosis(ConsoleData data)
    {
        var connected = data.Agents.Where(a => a["connected"]?.GetValue<bool>() == true).ToList();
        _diagnosisPicked.RemoveWhere(n => !connected.Any(a => Same(a, n)));
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Diagnostika", "pamäť agenta a počítača a programy s najviac pamäťou – zo všetkých počítačov naraz, bez Správcu úloh"));
        panel.Children.Add(Mode("service-diagnosis", _diagnosisMode, mode => { _diagnosisMode = mode; Redraw(data, _diagnosisHost); }));
        var chosen = Choose(data, connected, _diagnosisMode, _diagnosisPicked, () => Redraw(data, _diagnosisHost));
        panel.Children.Add(chosen.Panel);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Button($"Zistiť ({chosen.Names().Count})", () => _ = Diagnose(data, chosen.Names()), primary: true));
        buttons.Children.Add(Ui.Button("Ukázať uložené na serveri", () => _ = Kept(data)));
        if (_diagnoses is { Count: > 0 } shown)
        {
            buttons.Children.Add(Ui.Button("Uložiť správu…", () =>
            {
                var file = DiagnosisView.Save(DiagnosisView.Report(shown, _data ?? data), shown.Count == 1 ? Ui.String(shown[0]["computer"]) ?? "pc" : "ucebna");
                _diagnosisNote = file is null ? _diagnosisNote : $"Uložené: {file}";
                Redraw(_data ?? data, _diagnosisHost);
            }));
        }

        panel.Children.Add(buttons);
        if (_diagnosisNote is not null)
        {
            var note = Ui.Text(_diagnosisNote, 12.5, brush: "TextMuted");
            note.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(note);
        }

        if (_diagnoses is { Count: > 0 } entries)
        {
            var table = DiagnosisView.Table(entries, data, name => Actions?.Open(name));
            ((FrameworkElement)table).Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(table);
        }

        panel.Children.Add(Ui.Text("Počítač pošle len čísla o pamäti a názvy programov s ich veľkosťou – nič z toho, čo programy robia alebo ukazujú. Posledná diagnostika každého počítača ostáva na serveri.", 12, brush: "TextMuted"));
        return panel;
    }

    private async Task Diagnose(ConsoleData data, List<string> names)
    {
        if (names.Count == 0 || Actions is null)
        {
            _diagnosisNote = "Vyberte aspoň jeden počítač.";
            Redraw(data, _diagnosisHost);
            return;
        }

        _diagnosisNote = $"Pýtam sa {Count(names.Count)}…";
        Redraw(data, _diagnosisHost);
        var (entries, silent) = await DiagnosisView.AskAsync(Actions, names);
        var asked = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        _diagnoses = [.. entries.Where(e => asked.Contains(Ui.String(e["computer"]) ?? ""))];
        _diagnosisNote = silent.Count == 0
            ? $"Odpovedali všetky ({names.Count})."
            : $"Neodpovedali ({silent.Count}): {string.Join(", ", silent.Select(n => Display(_data ?? data, n)))} – majú starší program (aktualizujte ich) alebo sa práve odpojili.";
        Redraw(_data ?? data, _diagnosisHost);
    }

    private async Task Kept(ConsoleData data)
    {
        if (Actions is null)
        {
            return;
        }

        _diagnoses = DiagnosisView.Entries(await Actions.Diagnoses(null));
        _diagnosisNote = _diagnoses.Count == 0 ? "Na serveri zatiaľ nie je žiadna diagnostika." : $"Uložené na serveri: {_diagnoses.Count}.";
        Redraw(_data ?? data, _diagnosisHost);
    }

    private static Border Mode(string group, string mode, Action<string> choose)
    {
        var segments = Ui.Segments(group, [("one", "Jeden počítač"), ("picked", "Zvolené"), ("all", "Všetky")], mode, choose);
        segments.Margin = new Thickness(0, 0, 0, 10);
        return segments;
    }

    // the computers a card works with: one from a list, the ticked ones (a group with one click), or all of them
    private static (UIElement Panel, Func<List<string>> Names) Choose(ConsoleData data, List<JsonObject> computers, string mode, HashSet<string> picked, Action redraw)
    {
        var panel = new StackPanel();
        if (computers.Count == 0)
        {
            panel.Children.Add(Ui.Text("Žiadny počítač.", 13, brush: "TextMuted"));
            return (panel, () => []);
        }

        switch (mode)
        {
            case "one":
                var list = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var agent in computers.OrderBy(Ui.Name, StringComparer.OrdinalIgnoreCase))
                {
                    list.Items.Add(new ComboBoxItem { Content = Ui.Name(agent), Tag = Ui.String(agent["name"]) });
                }

                list.SelectedIndex = 0;
                panel.Children.Add(list);
                return (panel, () => list.SelectedItem is ComboBoxItem { Tag: string name } ? [name] : []);
            case "all":
                panel.Children.Add(Ui.Text($"Všetky: {string.Join(", ", computers.Select(Ui.Name).Order(StringComparer.OrdinalIgnoreCase))}", 13));
                return (panel, () => [.. computers.Select(a => Ui.String(a["name"]) ?? "")]);
            default:
                var groups = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (var (group, members) in data.Groups)
                {
                    var inside = members.Where(m => computers.Contains(m)).Select(m => Ui.String(m["name"]) ?? "").ToList();
                    if (inside.Count == 0)
                    {
                        continue;
                    }

                    var all = inside.All(picked.Contains);
                    groups.Children.Add(Ui.Small($"{(all ? "− " : "+ ")}{(group.Length == 0 ? "bez skupiny" : group)} ({inside.Count})", () =>
                    {
                        foreach (var name in inside)
                        {
                            if (all)
                            {
                                picked.Remove(name);
                            }
                            else
                            {
                                picked.Add(name);
                            }
                        }

                        redraw();
                    }));
                }

                groups.Children.Add(Ui.Small("Zrušiť výber", () =>
                {
                    picked.Clear();
                    redraw();
                }));
                panel.Children.Add(groups);
                var boxes = new WrapPanel();
                foreach (var agent in computers.OrderBy(Ui.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Ui.String(agent["name"]) ?? "?";
                    var box = new CheckBox { Content = Ui.Name(agent), IsChecked = picked.Contains(name), Margin = new Thickness(0, 4, 18, 4), Foreground = Ui.Brush("Text") };
                    box.Checked += (_, _) => picked.Add(name);
                    box.Unchecked += (_, _) => picked.Remove(name);
                    boxes.Children.Add(box);
                }

                panel.Children.Add(boxes);
                return (panel, () => [.. picked]);
        }
    }

    private async Task Power(ConsoleData data, List<string> names, string command)
    {
        if (names.Count == 0 || Actions is null)
        {
            _note = "Vyberte aspoň jeden počítač.";
            Redraw(data, _offHost);
            return;
        }

        var what = command == LabCommands.Restart ? "reštartovať" : "vypnúť";
        var how = (_now ? "Hneď, bez lehoty." : "Windows používateľa upozorní a urobí to o 60 sekúnd.") +
                  (_force ? " Nasilu: programy sa zavrú bez pýtania, neuložená práca sa stratí." : "");
        if (MessageBox.Show($"Naozaj {what} {Count(names.Count)}?\n\n{string.Join(", ", names.Select(n => Display(data, n)))}\n\n{how}",
                "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var name in names)
        {
            await Actions.Command(name, command, _now, _force);
        }

        _note = $"Príkaz „{what}“{(_now ? " ihneď" : "")}{(_force ? " nasilu" : "")} odoslaný na {Count(names.Count)}.";
        Redraw(data, _offHost);
    }

    private async Task Wake(ConsoleData data, List<string> names)
    {
        if (names.Count == 0 || Actions is null)
        {
            _note = "Vyberte aspoň jeden počítač.";
            Redraw(data, _onHost);
            return;
        }

        foreach (var name in names)
        {
            await Actions.Command(name, "wake");
        }

        _note = $"Zobudenie poslané na {Count(names.Count)} – ozvú sa do pár minút (stav uvidíte na Prehľade).";
        Redraw(data, _onHost);
    }

    private void Redraw(ConsoleData data, Border host)
    {
        host.Child = null;
        Show(data);
    }

    private static bool Same(JsonObject agent, string name) => string.Equals(Ui.String(agent["name"]), name, StringComparison.OrdinalIgnoreCase);

    private static string Display(ConsoleData data, string name) => data.Agent(name) is { } agent ? Ui.Name(agent) : name;

    private static string Count(int n) => n == 1 ? "1 počítač" : n is >= 2 and <= 4 ? $"{n} počítače" : $"{n} počítačov";
}
