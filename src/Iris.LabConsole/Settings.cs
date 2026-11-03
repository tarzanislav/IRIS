using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// The settings: where the console connects; how all computers learn and how each group does (a group is a hardware
/// model unless set by hand); the versions of the programs with the buttons for updating; the look.
/// </summary>
public sealed class SettingsPage : ConsolePage
{
    private readonly ConsoleSettings _settings;
    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private readonly TextBox _hub = new() { MinWidth = 260 };
    private readonly TextBox _port = new() { MinWidth = 100 };
    private readonly TextBox _key = new() { MinWidth = 420 };
    private readonly Border _connection;
    private string? _group;

    public SettingsPage(ConsoleSettings settings)
    {
        _settings = settings;
        _hub.Text = settings.Hub;
        _port.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        _key.Text = settings.KeyFile;
        var form = new StackPanel();
        form.Children.Add(Ui.Heading("Pripojenie", "server učebne a kľúč konzoly"));
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
        form.Children.Add(Ui.Text("Kľúč konzoly patrí len na tento počítač. Komu ho dáte, ten môže počítače v učebni pozastaviť, reštartovať, vypnúť a meniť ich nastavenia.", 12, brush: "TextMuted"));
        _connection = Ui.Card(form);
        Content = Scroll(_root);
    }

    public override string Heading => "Nastavenia";

    public Action? Saved { get; set; }

    /// <summary>The look was changed (light or dark).</summary>
    public Action<string>? Theme { get; set; }

    public override void Show(ConsoleData data)
    {
        _root.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _root.Children.Add(error);
        }

        var connected = data.Snapshot is not null && data.Error is null;
        if (connected)
        {
            // how all computers learn
            var all = new StackPanel();
            all.Children.Add(Ui.Heading("Učenie – všetky počítače", "platí pre každý počítač, ak jeho skupina alebo on sám nemá vlastné"));
            all.Children.Add(new SettingsEditor("all", "", data.Own("all", ""), data.Defaults, Configure));
            _root.Children.Add(Ui.Card(all));

            // each group
            var groups = data.Groups.Where(g => g.Group.Length > 0).ToList();
            var panel = new StackPanel();
            panel.Children.Add(Ui.Heading("Skupiny", "skupina je model počítača (z jeho firmvéru), ak ho nedáte do inej ručne na stránke Počítače"));
            if (groups.Count == 0)
            {
                panel.Children.Add(Ui.Text("Zatiaľ žiadne skupiny – počítače ešte nepovedali, čo sú zač.", 13, brush: "TextMuted"));
            }
            else
            {
                _group = groups.Any(g => g.Group == _group) ? _group : groups[0].Group;
                var choose = Ui.Segments("settings-groups", groups.Select(g => (g.Group, $"{g.Group} ({g.Computers.Count})")), _group!, key =>
                {
                    _group = key;
                    Show(data);
                });
                choose.Margin = new Thickness(0, 0, 0, 12);
                panel.Children.Add(choose);
                panel.Children.Add(new SettingsEditor("group", _group!, data.Own("group", _group!), data.Inherited("group", _group), Configure));
            }

            _root.Children.Add(Ui.Card(panel));
        }

        _root.Children.Add(Updates(data, connected));
        _root.Children.Add(_connection);
        _root.Children.Add(Look());
    }

    private async Task<bool> Configure(JsonObject change) => Actions is not null && await Actions.Configure(change);

    private Border Updates(ConsoleData data, bool connected)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Aktualizácie", "programy podpísané kľúčom vydaní IRIS; počítače ich dostávajú len od servera"));
        var available = data.Available;
        var hub = Ui.String(data.Snapshot?["hub_version"]);
        var agents = data.Agents.Select(a => Ui.String(a["version"])).Where(v => v is not null).ToList();
        var older = available is null ? 0 : agents.Count(v => LabUpdates.IsNewer(available, v!));
        panel.Children.Add(Ui.Fact("táto konzola", LabUpdates.Version + (available is not null && LabUpdates.IsNewer(available, LabUpdates.Version) ? $" → {available} je pripravená" : "")));
        panel.Children.Add(Ui.Fact("server", hub ?? "–"));
        panel.Children.Add(Ui.Fact("pripravená na serveri", available ?? "žiadna novšia"));
        panel.Children.Add(Ui.Fact("počítače", agents.Count == 0 ? "–" : older == 0 ? $"{agents.Count} aktuálnych" : $"{older} z {agents.Count} majú starší program"));
        panel.Children.Add(Ui.Fact("samé od seba", data.Snapshot?["update"]?["auto"]?.GetValue<bool>() == false ? "nie – len tlačidlom" : "áno, medzi kolami"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (available is not null && LabUpdates.IsNewer(available, LabUpdates.Version))
        {
            buttons.Children.Add(Ui.Button("Aktualizovať konzolu", () => _ = Actions?.UpdateConsole(), primary: true));
        }

        if (connected)
        {
            buttons.Children.Add(Ui.Button("Aktualizovať všetky počítače teraz", () => _ = Actions?.UpdateComputers("*")));
            buttons.Children.Add(Ui.Button("Aktualizovať server teraz", () => _ = Actions?.UpdateServer()));
        }

        panel.Children.Add(buttons);
        panel.Children.Add(Ui.Text("Server sa na novú verziu pozrie raz denne okolo poludnia; „teraz“ to urobí hneď. Nová verzia vzniká len vydaním, ktoré schvaľuje vlastník.", 12, brush: "TextMuted"));
        return Ui.Card(panel);
    }

    private Border Look()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Vzhľad"));
        panel.Children.Add(Ui.Segments("settings-theme", [("light", "Svetlý"), ("dark", "Tmavý")], _settings.Theme, key => Theme?.Invoke(key)));
        return Ui.Card(panel);
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
