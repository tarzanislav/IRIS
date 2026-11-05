using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>Where the console connects and how it looks (kept in the user's application data).</summary>
public sealed class ConsoleSettings
{
    public string Hub { get; set; } = "";

    public int Port { get; set; } = 47620;

    public string KeyFile { get; set; } = "";

    /// <summary>The folder the key is looked for in when no key is chosen by hand (see <see cref="KeyFolder"/>).</summary>
    public string KeyFolder { get; set; } = "";

    /// <summary>light or dark.</summary>
    public string Theme { get; set; } = "light";

    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IRIS Lab", "console.json");

    public static ConsoleSettings Load()
    {
        try
        {
            if (File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject json)
            {
                return new ConsoleSettings
                {
                    Hub = json["hub"]?.GetValue<string>() ?? "",
                    Port = json["port"]?.GetValue<int>() ?? 47620,
                    KeyFile = json["key"]?.GetValue<string>() ?? "",
                    KeyFolder = json["key_folder"]?.GetValue<string>() ?? "",
                    Theme = json["theme"]?.GetValue<string>() == "dark" ? "dark" : "light",
                };
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
        }

        return new ConsoleSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, new JsonObject { ["hub"] = Hub, ["port"] = Port, ["key"] = KeyFile, ["key_folder"] = KeyFolder, ["theme"] = Theme }.ToJsonString());
    }
}

/// <summary>
/// The console window: the menu on the left (the look of the IRIS application), a header with the page's title and
/// the state of the classroom, the page, and a connection to the hub that asks for news every five seconds and for
/// the history every minute.
/// </summary>
public sealed class MainWindow : Window, IConsoleActions
{
    private readonly ConsoleSettings _settings;
    private readonly ConsoleData _data = new();
    private readonly ContentControl _page = new() { Margin = new Thickness(6, 0, 14, 14) };
    private readonly TextBlock _heading = new();
    private readonly WrapPanel _chips = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connection = new() { FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, ConsolePage> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _menu = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LabConsoleClient? _client;
    private DateTime _historyAt = DateTime.MinValue;
    private bool _busy;
    private bool _syncing;
    private string _current = "overview";

    public MainWindow(ConsoleSettings settings, bool offline = false)
    {
        _settings = settings;
        if (!offline)
        {
            KeyFolder.EnsureDefault(settings);
        }

        Title = "IRIS – učebňa";
        // never larger than the screen (with a scaling of 150 % a notebook has about 830 points of height): the title
        // bar with minimize, maximize and close must stay on the screen; a small screen starts maximized
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1360, area.Width);
        Height = Math.Min(880, area.Height);
        MinWidth = Math.Min(1080, area.Width);
        MinHeight = Math.Min(640, area.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (area.Width < 1360 || area.Height < 880)
        {
            WindowState = WindowState.Maximized;
        }
        ApplyTheme(settings.Theme);

        _pages["overview"] = new OverviewPage();
        _pages["computers"] = new ComputersPage();
        var charts = new ChartsPage();
        charts.Span = hours =>
        {
            _data.HistoryHours = hours;
            _historyAt = DateTime.MinValue;
            _ = Refresh();
        };
        _pages["charts"] = charts;
        _pages["rounds"] = new RoundsPage();
        var network = new NetworkPage();
        network.Scan = ScanNetwork;
        _pages["network"] = network;
        _pages["service"] = new ServicePage();
        _pages["test"] = new TestPage();
        _pages["server"] = new ServerPage();
        var settingsPage = new SettingsPage(settings);
        settingsPage.Saved = () =>
        {
            Disconnect();
            _historyAt = DateTime.MinValue;
            _ = Refresh();
        };
        settingsPage.Theme = theme =>
        {
            _settings.Theme = theme;
            _settings.Save();
            SetTheme(theme);
        };
        _pages["settings"] = settingsPage;
        foreach (var page in _pages.Values)
        {
            page.Actions = this;
        }

        Build();
        if (!offline)
        {
            _timer.Tick += async (_, _) => await Refresh();
            Loaded += async (_, _) =>
            {
                _timer.Start();
                await Refresh();
            };
        }

        Closed += (_, _) => Disconnect();
    }

    /// <summary>The data the pages show (the self-test fills it without a hub).</summary>
    public ConsoleData Data => _data;

    public IEnumerable<string> PageNames => _pages.Keys;

    /// <summary>The network page (the self-test gives it a scan to show).</summary>
    public NetworkPage Network => (NetworkPage)_pages["network"];

    /// <summary>The page of the server (the self-test gives it invented answers).</summary>
    public ServerPage Server => (ServerPage)_pages["server"];

    public void Navigate(string name)
    {
        _current = name;
        if (_menu.TryGetValue(name, out var item) && item.IsChecked != true)
        {
            _syncing = true; // the menu shows the page without opening it again
            item.IsChecked = true;
            _syncing = false;
        }

        _page.Content = _pages[name];
        _heading.Text = _pages[name].Heading;
        _pages[name].Show(_data);
        Header();
    }

    /// <summary>The window in the light or the dark look.</summary>
    public void SetTheme(string theme)
    {
        ApplyTheme(theme);
        Build();
    }

    /// <summary>The light or the dark colors of IRIS (the styles stay).</summary>
    public static void ApplyTheme(string theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = new ResourceDictionary { Source = new Uri(theme == "dark" ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        }
    }

    // the window anew (after a change of the look: everything built in code takes the new colors)
    private void Build()
    {
        SetResourceReference(BackgroundProperty, "WindowBackground");
        FontFamily = (FontFamily)FindResource("UiFont");
        _menu.Clear();
        _heading.Style = (Style)FindResource("PageTitle");
        _heading.Margin = new Thickness(0);
        _connection.Foreground = (Brush)FindResource("TextMuted");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (_connection.Parent is Panel old)
        {
            old.Children.Remove(_connection);
        }

        grid.Children.Add(Menu());
        var header = new DockPanel { Margin = new Thickness(14, 22, 14, 10) };
        if (_chips.Parent is Panel oldChips)
        {
            oldChips.Children.Remove(_chips);
        }

        if (_heading.Parent is Panel oldHeading)
        {
            oldHeading.Children.Remove(_heading);
        }

        DockPanel.SetDock(_chips, Dock.Right);
        header.Children.Add(_chips);
        header.Children.Add(_heading);
        var right = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        if (_page.Parent is Panel oldPage)
        {
            oldPage.Children.Remove(_page);
        }

        right.Children.Add(_page);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;
        Navigate(_current);
    }

    private Border Menu()
    {
        var items = new StackPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 22) };
        var logo = new Grid { Width = 34, Height = 34 };
        logo.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = (Brush)FindResource("BrandGradient"), StrokeThickness = 3 });
        logo.Children.Add(new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, Fill = (Brush)FindResource("BrandGradient") });
        brand.Children.Add(logo);
        brand.Children.Add(new TextBlock
        {
            Text = "Učebňa",
            FontFamily = (FontFamily)FindResource("DisplayFont"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("BrandGradient"),
        });
        items.Children.Add(brand);
        foreach (var (name, text, tint, deep, icon) in new[]
                 {
                     ("overview", "Prehľad", "Lavender", "LavenderDeep", "M3,3 H9 V9 H3 Z M11,3 H17 V9 H11 Z M3,11 H9 V17 H3 Z M11,11 H17 V17 H11 Z"),
                     ("computers", "Počítače", "Sky", "SkyDeep", Ui.Icons.Computers),
                     ("charts", "Grafy", "Mint", "MintDeep", "M3,17 V3 M3,17 H17 M5,13 L9,8 L12,11 L17,5"),
                     ("rounds", "Kolá a učenie", "Peach", "PeachDeep", Ui.Icons.Round),
                     ("network", "Sieť", "Pink", "PinkDeep", "M10,3 V7 M4,17 V13 H16 V17 M10,7 V13 M7,3 H13 V7 H7 Z M2,17 H6 V20 H2 Z M14,17 H18 V20 H14 Z"),
                     ("service", "Servis", "Sky", "SkyDeep", "M10,2 V9 M5.5,5 A7,7 0 1 0 14.5,5"),
                     ("test", "Testovací režim", "Peach", "PeachDeep", "M10,2 L12,8 L18,8 L13,12 L15,18 L10,14 L5,18 L7,12 L2,8 L8,8 Z"),
                     ("server", "Server", "Mint", "MintDeep", "M3,3 H17 V8 H3 Z M3,12 H17 V17 H3 Z M6,5.5 H7 M6,14.5 H7 M11,5.5 H14 M11,14.5 H14"),
                     ("settings", "Nastavenia", "Lilac", "LilacDeep", "M10,6 A4,4 0 1 1 10,14 A4,4 0 1 1 10,6 Z M10,1 V4 M10,16 V19 M1,10 H4 M16,10 H19"),
                 })
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                GroupName = "nav",
                IsChecked = name == _current,
                Background = (Brush)FindResource(tint),
                BorderBrush = (Brush)FindResource(deep),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Style = (Style)FindResource("NavIcon"),
                Background = (Brush)FindResource(tint),
                Child = new System.Windows.Shapes.Path { Style = (Style)FindResource("IconPath"), Stroke = (Brush)FindResource(deep), Data = Geometry.Parse(icon) },
            });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            button.Checked += (_, _) =>
            {
                if (!_syncing)
                {
                    Navigate(name);
                }
            };
            _menu[name] = button;
            items.Children.Add(button);
        }

        var dock = new DockPanel { Margin = new Thickness(14, 20, 14, 20), LastChildFill = false };
        DockPanel.SetDock(items, Dock.Top);
        dock.Children.Add(items);
        DockPanel.SetDock(_connection, Dock.Bottom);
        dock.Children.Add(_connection);
        return new Border
        {
            Margin = new Thickness(14, 14, 0, 14),
            Background = (Brush)FindResource("Sidebar"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(26),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("CardShadow"),
            Child = dock,
        };
    }

    // the chips of the header: the connection, the time of learning, the round, a newer version
    private void Header()
    {
        _chips.Children.Clear();
        var snapshot = _data.Snapshot;
        if (snapshot is null || _data.Error is not null)
        {
            _chips.Children.Add(Ui.Chip("nepripojené", "Danger", _data.Error));
            return;
        }

        _chips.Children.Add(Ui.Chip(_settings.Hub.Length > 0 ? $"pripojené · {_settings.Hub}" : "pripojené", "Good"));
        var schedule = snapshot["schedule"];
        _chips.Children.Add(snapshot["open"]?.GetValue<bool>() == true
            ? Ui.Chip($"čas učenia do {schedule?["until"]}", "Accent")
            : Ui.Chip($"učenie od {schedule?["from"]}", "SkyDeep"));
        if (snapshot["round"] is JsonObject round)
        {
            _chips.Children.Add(Ui.Chip($"kolo {round["number"]} · {round["done"]}/{round["taking_part"]}", "PeachDeep"));
        }

        if (_data.Available is { } available && LabUpdates.IsNewer(available, LabUpdates.Version))
        {
            var chip = Ui.Chip($"nová verzia {available}", "LilacDeep", "Aktualizácie sú v Nastaveniach");
            chip.Cursor = Cursors.Hand;
            chip.MouseLeftButtonUp += (_, _) => Navigate("settings");
            _chips.Children.Add(chip);
        }
    }

    private async Task Refresh()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_client is null)
            {
                var (keyFile, problem) = KeyFolder.Find(_settings);
                if (_settings.Hub.Length == 0 || keyFile is null)
                {
                    _data.Error = _settings.Hub.Length == 0
                        ? "Konzola ešte nevie, kde je server: v Nastaveniach zadajte server a kľúč konzoly."
                        : problem;
                    _connection.Text = "nepripojené";
                    ShowData();
                    return;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var (key, keyName) = LabKeys.ReadNamed(keyFile);
                _client = await LabConsoleClient.ConnectAsync(_settings.Hub, _settings.Port, key, LabKeys.HelloName(keyName), timeout.Token);
            }

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                _data.Snapshot = await _client.SnapshotAsync(timeout.Token);
                if (DateTime.Now - _historyAt > TimeSpan.FromMinutes(1))
                {
                    var hours = _data.HistoryHours;
                    var bucket = hours <= 12 ? 5 : hours <= 72 ? 15 : 60;
                    _data.History = await _client.HistoryAsync(DateTime.Now.AddHours(-hours), DateTime.Now, null, bucket, timeout.Token);
                    _historyAt = DateTime.Now;
                }
            }

            _data.Error = null;
            _connection.Text = $"pripojené k {_settings.Hub}\n{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException or FormatException or ObjectDisposedException)
        {
            Disconnect();
            _data.Error = $"Server nie je dostupný ({e.Message}). Skúsim to znova o chvíľu.";
            _connection.Text = "nepripojené";
        }
        finally
        {
            _busy = false;
        }

        ShowData();
    }

    // the page anew with the news; the settings only when asked (their editors must not move under the hand)
    private void ShowData(bool settingsToo = false)
    {
        Header();
        if (_current != "settings" || settingsToo)
        {
            _pages[_current].Show(_data);
        }
    }

    private async Task<JsonObject?> ScanNetwork()
    {
        if (_client is null)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            return await _client.ScanAsync(timeout.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
            return null;
        }
    }

    public async Task Command(string target, string command, bool now = false, bool force = false)
    {
        if (_client is null)
        {
            MessageBox.Show("Konzola nie je pripojená k serveru.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sent = command == "wake" ? await _client.WakeAsync(target, timeout.Token) : await _client.CommandAsync(target, command, timeout.Token, now, force);
            _connection.Text = $"príkaz „{command}“ poslaný na {sent} {(sent == 1 ? "počítač" : "počítačov")}";
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
            MessageBox.Show($"Príkaz sa nepodarilo poslať: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task<bool> Configure(JsonObject change)
    {
        if (_client is null)
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _client.ConfigureAsync(change, timeout.Token);
            _data.Snapshot = await _client.SnapshotAsync(timeout.Token);
            ShowData(settingsToo: true);
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
            return false;
        }
    }

    public async Task UpdateComputers(string target)
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var marked = await _client.UpdateComputersAsync(target, timeout.Token);
            _connection.Text = _data.Available is null
                ? "server nemá novší program pre počítače"
                : $"{marked} {(marked == 1 ? "počítač dostane" : "počítačov dostane")} verziu {_data.Available} medzi kolami";
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
        }
    }

    public async Task UpdateServer()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var asked = await _client.UpdateServerAsync(timeout.Token);
            MessageBox.Show(asked
                ? "Server sa teraz pozrie na novú verziu IRIS; ak ju nájde a jej podpis sedí, nainštaluje ju a na chvíľu sa reštartuje."
                : "Server žiadosť neprijal (jeho priečinok nie je zapisovateľný).", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
        }
    }

    // the console's own new program: from the server, checked (signature and SHA-256), put in place, started anew
    public async Task UpdateConsole()
    {
        if (_client is null || Environment.ProcessPath is not { } path)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            if (await _client.ConsoleProgramAsync(timeout.Token) is not { } program || !LabUpdates.IsNewer(program.Manifest.Version, LabUpdates.Version))
            {
                MessageBox.Show("Server nemá novšiu konzolu.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LabUpdates.ReplaceProgram(path, program.Data);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Konzolu sa nepodarilo nahradiť ({e.Message}). Je v priečinku, kam sa nedá zapisovať? Presuňte ju napr. na Plochu.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception e) when (e is SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
        }
    }

    public async Task<JsonObject?> ServerStatus() => await AskServer<JsonObject>(c => c.ServerStatusAsync, 20);

    public async Task<IReadOnlyList<string>?> ServerLog(string name, int lines) => await AskServer<IReadOnlyList<string>>(c => cancel => c.ServerLogAsync(name, lines, cancel), 30);

    public async Task<bool> RestartServer()
    {
        var done = await AskServer<bool>(c => c.RestartServerAsync, 10);
        _connection.Text = done ? "server sa reštartuje – o pol minúty sa konzola pripojí znova" : _connection.Text;
        return done;
    }

    public async Task<JsonObject?> StartTest(IReadOnlyList<string>? computers, double minutes, double duty, double cores, bool eco, bool keepTime = false)
    {
        var answer = await AskServer<JsonObject>(c => cancel => c.StartTestAsync(computers, minutes, duty, cores, eco, cancel, keepTime), 15);
        await Refresh();
        return answer;
    }

    public async Task<bool> StopTest()
    {
        var answer = await AskServer<JsonObject>(c => c.StopTestAsync, 15);
        await Refresh();
        return answer?["stopped"] is not null;
    }

    public async Task<bool> ModelFromRelease() => await AskServer<bool>(c => c.ServerModelFromReleaseAsync, 10);

    // one question to the server itself; on a broken connection the console connects again with the next refresh
    private async Task<T?> AskServer<T>(Func<LabConsoleClient, Func<CancellationToken, Task<T>>> ask, int seconds)
    {
        if (_client is null)
        {
            return default;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            return await ask(_client)(timeout.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            Disconnect();
            return default;
        }
    }

    public void Open(string computer)
    {
        ((ComputersPage)_pages["computers"]).Select(computer);
        Navigate("computers");
    }

    private void Disconnect()
    {
        _client?.Dispose();
        _client = null;
    }
}
