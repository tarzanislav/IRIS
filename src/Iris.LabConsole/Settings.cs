using System.Globalization;
using System.IO;
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
    private readonly TextBox _keyFolder = new() { MinWidth = 420 };
    private readonly TextBlock _keyFolderNote = Ui.Text("", 12, brush: "TextMuted");
    private readonly TextBlock _keyName = Ui.Text("", 12, brush: "TextMuted");
    private readonly Border _connection;
    private string? _group;

    public SettingsPage(ConsoleSettings settings)
    {
        _settings = settings;
        _hub.Text = settings.Hub;
        _port.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        _key.Text = settings.KeyFile;
        _keyFolder.Text = settings.KeyFolder;
        var form = new StackPanel();
        form.Children.Add(Ui.Heading("Pripojenie", "server učebne a kľúč konzoly"));
        form.Children.Add(Ui.Caption("Server (adresa alebo názov)"));
        form.Children.Add(_hub);
        form.Children.Add(new Border { Height = 12 });
        form.Children.Add(Ui.Caption("Port"));
        form.Children.Add(_port);
        form.Children.Add(new Border { Height = 12 });
        form.Children.Add(Ui.Caption("Priečinok s kľúčom (skopírujte doň kľúč konzoly zo servera; konzola si ho nájde sama)"));
        var folderRow = new DockPanel();
        var open = Ui.Button("Otvoriť priečinok", OpenFolder);
        var change = Ui.Button("Zmeniť…", ChooseFolder);
        open.Margin = change.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(change, Dock.Right);
        folderRow.Children.Add(open);
        folderRow.Children.Add(change);
        folderRow.Children.Add(_keyFolder);
        form.Children.Add(folderRow);
        _keyFolderNote.Margin = new Thickness(0, 4, 0, 0);
        form.Children.Add(_keyFolderNote);
        form.Children.Add(new Border { Height = 12 });
        form.Children.Add(Ui.Caption("Kľúč konzoly (nepovinné: len ak je v priečinku viac kľúčov alebo leží inde)"));
        var keyRow = new DockPanel();
        var browse = Ui.Button("Vybrať…", Browse);
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        keyRow.Children.Add(browse);
        keyRow.Children.Add(_key);
        form.Children.Add(keyRow);
        _keyName.Margin = new Thickness(0, 4, 0, 0);
        form.Children.Add(_keyName);
        form.Children.Add(new Border { Height = 16 });
        form.Children.Add(Ui.Button("Uložiť a pripojiť", () =>
        {
            settings.Hub = _hub.Text.Trim();
            settings.Port = int.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 47620;
            settings.KeyFile = _key.Text.Trim();
            settings.KeyFolder = _keyFolder.Text.Trim();
            settings.Save();
            Saved?.Invoke();
        }, primary: true));
        form.Children.Add(Ui.Text("Kľúč konzoly patrí len na tento počítač a nechajte ho v priečinku natrvalo (konzola ho číta pri každom pripojení). Komu ho dáte, ten môže počítače v učebni pozastaviť, reštartovať, vypnúť a meniť ich nastavenia. Každý počítač so svojou konzolou môže mať vlastný kľúč, ktorý sa na serveri dá zrušiť sám (iris-hub console-key).", 12, brush: "TextMuted"));
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
        var (keyFile, problem) = KeyFolder.Find(_settings);
        _keyName.Text = keyFile is null ? problem : (keyFile == _settings.KeyFile ? "" : $"používa sa {Path.GetFileName(keyFile)} – ") + KeyName(keyFile);
        _keyFolderNote.Text = KeyFolder.Openness(_settings.KeyFolder);
        _root.Children.Add(_connection);
        if (connected && data.Snapshot?["consoles"] is JsonArray consoles)
        {
            _root.Children.Add(Consoles(consoles));
        }

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

    private static string KeyName(string file)
    {
        try
        {
            return File.Exists(file) && LabKeys.ReadNamed(file).Name is { } name ? $"vlastný kľúč konzoly „{name}“" : File.Exists(file) ? "hlavný kľúč konzoly servera" : "";
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            return "súbor nie je kľúč konzoly";
        }
    }

    private static Border Consoles(JsonArray consoles)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Pripojené konzoly", "kto má práve konzolu pripojenú k serveru (aj táto)"));
        var names = consoles.Select(c => Ui.String(c) ?? "").ToList();
        foreach (var group in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            panel.Children.Add(Ui.Fact(group.Key.Length == 0 ? "hlavný kľúč" : group.Key, group.Count() == 1 ? "pripojená" : $"pripojená {group.Count()}×"));
        }

        panel.Children.Add(Ui.Text("Vlastný kľúč pre ďalší počítač: na serveri sudo /opt/iris-hub/iris-hub console-key add <meno>; zrušenie: … console-key remove <meno> (konzola s ním sa hneď odpojí).", 12, brush: "TextMuted"));
        return Ui.Card(panel);
    }

    private Border Look()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Vzhľad"));
        panel.Children.Add(Ui.Segments("settings-theme", [("light", "Svetlý"), ("dark", "Tmavý")], _settings.Theme, key => Theme?.Invoke(key)));
        return Ui.Card(panel);
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Priečinok s kľúčom konzoly" };
        if (_keyFolder.Text.Trim() is { Length: > 0 } now && Directory.Exists(now))
        {
            dialog.InitialDirectory = now;
        }

        if (dialog.ShowDialog() == true)
        {
            _keyFolder.Text = dialog.FolderName;
            _keyFolderNote.Text = KeyFolder.Openness(dialog.FolderName);
        }
    }

    private void OpenFolder()
    {
        var folder = _keyFolder.Text.Trim();
        if (folder.Length > 0 && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        }
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Kľúč (*.key)|*.key|Všetky súbory|*.*" };
        if (_keyFolder.Text.Trim() is { Length: > 0 } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog() == true)
        {
            _key.Text = dialog.FileName;
        }
    }
}
