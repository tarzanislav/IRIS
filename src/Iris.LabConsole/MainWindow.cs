using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>Where the console connects (kept in the user's application data).</summary>
public sealed class ConsoleSettings
{
    public string Hub { get; set; } = "";

    public int Port { get; set; } = 47620;

    public string KeyFile { get; set; } = "";

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
        File.WriteAllText(Path, new JsonObject { ["hub"] = Hub, ["port"] = Port, ["key"] = KeyFile }.ToJsonString());
    }
}

/// <summary>
/// The console window: the menu on the left (the look of the IRIS application), the page on the right, and a
/// connection to the hub that asks for news every five seconds and for the history every minute.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ConsoleSettings _settings;
    private readonly ConsoleData _data = new();
    private readonly ContentControl _page = new() { Margin = new Thickness(14, 18, 14, 14) };
    private readonly TextBlock _connection = new() { FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, ConsolePage> _pages = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LabConsoleClient? _client;
    private DateTime _historyAt = DateTime.MinValue;
    private bool _busy;
    private string _current = "overview";

    public MainWindow(ConsoleSettings settings, bool offline = false)
    {
        _settings = settings;
        Title = "IRIS – učebňa";
        Width = 1320;
        Height = 860;
        MinWidth = 960;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        FontFamily = (FontFamily)FindResource("UiFont");

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
        var settingsPage = new SettingsPage(settings);
        settingsPage.Saved = () =>
        {
            Disconnect();
            _historyAt = DateTime.MinValue;
            _ = Refresh();
        };
        _pages["settings"] = settingsPage;
        foreach (var page in _pages.Values)
        {
            page.Command = SendCommand;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Menu());
        Grid.SetColumn(_page, 1);
        grid.Children.Add(_page);
        Content = grid;
        Navigate("overview");
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

    public void Navigate(string name)
    {
        _current = name;
        _page.Content = _pages[name];
        _pages[name].Show(_data);
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
                     ("computers", "Počítače", "Sky", "SkyDeep", "M2,4 H18 V13 H2 Z M7,17 H13 M10,13 V17"),
                     ("charts", "Grafy", "Mint", "MintDeep", "M3,17 V3 M3,17 H17 M5,13 L9,8 L12,11 L17,5"),
                     ("rounds", "Kolá a učenie", "Peach", "PeachDeep", "M10,3 A7,7 0 1 1 3,10 M3,4 V10 H9"),
                     ("network", "Sieť", "Pink", "PinkDeep", "M10,3 V7 M4,17 V13 H16 V17 M10,7 V13 M7,3 H13 V7 H7 Z M2,17 H6 V20 H2 Z M14,17 H18 V20 H14 Z"),
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
            button.Checked += (_, _) => Navigate(name);
            items.Children.Add(button);
        }

        var dock = new DockPanel { Margin = new Thickness(14, 20, 14, 20), LastChildFill = false };
        DockPanel.SetDock(items, Dock.Top);
        dock.Children.Add(items);
        _connection.Foreground = (Brush)FindResource("TextMuted");
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
                if (_settings.Hub.Length == 0 || !File.Exists(_settings.KeyFile))
                {
                    _data.Error = "Konzola ešte nevie, kde je server: v Nastaveniach zadajte server a kľúč konzoly (console.key).";
                    _connection.Text = "nepripojené";
                    ShowData();
                    return;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                _client = await LabConsoleClient.ConnectAsync(_settings.Hub, _settings.Port, LabKeys.Read(_settings.KeyFile), timeout.Token);
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

    private void ShowData()
    {
        if (_current != "settings")
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

    /// <summary>The network page (the self-test gives it a scan to show).</summary>
    public NetworkPage Network => (NetworkPage)_pages["network"];

    private async Task SendCommand(string target, string command)
    {
        if (_client is null)
        {
            MessageBox.Show("Konzola nie je pripojená k serveru.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sent = await _client.CommandAsync(target, command, timeout.Token);
            _connection.Text = $"príkaz „{command}“ poslaný na {sent} {(sent == 1 ? "počítač" : "počítačov")}";
        }
        catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException)
        {
            Disconnect();
            MessageBox.Show($"Príkaz sa nepodarilo poslať: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Disconnect()
    {
        _client?.Dispose();
        _client = null;
    }
}
