using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Platform;
using Iris.Assistant;
using Iris.Assistant.Packs;
using Microsoft.Win32;

namespace Iris.App;

/// <summary>
/// The IRIS application: one instance per user, a window that hides to the notification area, a shortcut that
/// translates the selected text in any application. All data stays in the IRIS folder of this computer.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\IRIS.App.Instance";
    private const string ShowName = @"Local\IRIS.App.Show";

    private Mutex? _instance;
    private EventWaitHandle? _show;
    private MessageWindow? _messages;
    private TrayIcon? _tray;
    private HotKey? _hotKey;
    private MainWindow? _window;
    private bool _exiting;

    public static new App Current => (App)Application.Current;

    public Settings Settings { get; private set; } = new();

    public TranslationService Service { get; } = TranslationService.ForThisComputer();

    public History History { get; } = History.Open(UserData.HistoryPath);

    /// <summary>The packs of this version's release (built into release builds), or null in a development build.</summary>
    public PackCatalog? Catalog { get; } = ReadCatalog();

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(30) };

    public string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    /// <summary>The folder of this version's release, where the language packs are downloaded from.</summary>
    public Uri ReleaseFolder => new($"https://github.com/tarzanislav/IRIS/releases/download/v{Version}/");

    /// <summary>Whether the dark colors are in use now.</summary>
    public bool IsDark { get; private set; }

    public event Action? SettingsChanged;

    /// <summary>Changes the settings, saves them and tells the windows.</summary>
    public void Update(Func<Settings, Settings> change)
    {
        Settings = change(Settings);
        Settings.Save(UserData.SettingsPath);
        Localizer.Instance.Use(Settings.InterfaceLanguage);
        ApplyTheme();
        SettingsChanged?.Invoke();
    }

    /// <summary>Shows the main window (from the tray, the second start or the quick window).</summary>
    public MainWindow ShowMain()
    {
        _window ??= new MainWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        return _window;
    }

    /// <summary>Ends IRIS (the tray menu).</summary>
    public void Quit()
    {
        _exiting = true;
        Shutdown();
    }

    /// <summary>Whether a closing window may close (otherwise it hides to the notification area).</summary>
    public bool IsExiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            // IRIS already runs: ask it to show its window, then end
            if (EventWaitHandle.TryOpenExisting(ShowName, out var other))
            {
                other.Set();
                other.Dispose();
            }

            _instance.Dispose();
            _instance = null;
            _exiting = true;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        Settings = Settings.Load(UserData.SettingsPath);
        Localizer.Instance.Use(Settings.InterfaceLanguage);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.Invoke(ApplyTheme);

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        var show = _show;
        var listener = new Thread(() =>
        {
            while (show.WaitOne() && !_exiting)
            {
                Dispatcher.Invoke(() => ShowMain());
            }
        });
        listener.IsBackground = true;
        listener.Name = "IRIS show";
        listener.Start();

        _messages = new MessageWindow();
        _tray = new TrayIcon(_messages, "IRIS");
        _tray.Open += () => ShowMain();
        _tray.Menu += ShowTrayMenu;
        RegisterShortcut();

        ShowMain();
        PreloadModel();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotKey?.Dispose();
        _tray?.Dispose();
        _messages?.Dispose();
        _show?.Dispose();
        if (_instance is not null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
        }

        Http.Dispose();
        base.OnExit(e);
    }

    /// <summary>Registers the translate shortcut again (after it was changed); false when another program has it.</summary>
    public bool RegisterShortcut()
    {
        _hotKey?.Dispose();
        _hotKey = _messages is null ? null : HotKey.Register(_messages, Settings.TranslateShortcut);
        if (_hotKey is null)
        {
            return false;
        }

        _hotKey.Pressed += async () =>
        {
            var text = await Selection.CopyAsync();
            new MiniWindow(text).ShowNearCursor();
        };
        return true;
    }

    private void ApplyTheme()
    {
        IsDark = Settings.Theme switch
        {
            Theme.Dark => true,
            Theme.Light => false,
            _ => WindowsTheme.SystemIsDark(),
        };
        var colors = new ResourceDictionary { Source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        Resources.MergedDictionaries[0] = colors;
        foreach (Window window in Windows)
        {
            WindowsTheme.TitleBar(window, IsDark);
        }
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = Localizer.Instance["tray.open"] };
        open.Click += (_, _) => ShowMain();
        var exit = new MenuItem { Header = Localizer.Instance["tray.exit"] };
        exit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    // loads the model of the usual direction in the background, so the first translation does not wait
    private void PreloadModel()
    {
        var pair = new LanguagePair(Settings.SourceLanguage, Settings.TargetLanguage);
        if (Service.Packs.IsInstalled(pair))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Service.Preload(pair);
                }
                catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
                {
                    // shown when the user translates
                }
            });
        }
    }

    private static PackCatalog? ReadCatalog()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("Iris.App.packs.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return PackCatalog.Parse(reader.ReadToEnd());
    }
}
