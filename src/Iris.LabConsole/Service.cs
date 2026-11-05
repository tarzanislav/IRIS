using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// The service of the classroom: switch computers on (Wake-on-LAN by the hub) and off (or restart them) – one, the
/// ones ticked (a whole group with one click), or all. Shutting down always asks first; Windows warns the user of
/// the computer and waits a minute. And the removal: IRIS uninstalled from one computer, or a computer that left the
/// classroom taken off the list.
/// </summary>
public sealed class ServicePage : ConsolePage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private readonly Border _offHost = new();
    private readonly Border _onHost = new();
    private readonly Border _removeHost = new();
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

    public override void Show(ConsoleData data)
    {
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

        foreach (var host in new[] { _offHost, _onHost, _removeHost })
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
