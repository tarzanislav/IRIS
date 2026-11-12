using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// The server itself, all from the console: its state (program, clock, model, disk, memory, fonts), the update now,
/// the model of the classroom from the learning file of the newest signed release, a restart, the updater's last
/// result, and the hub's log and its updater's log with a filter. Nothing on the server has to be typed by hand.
/// </summary>
public sealed class ServerPage : ConsolePage
{
    /// <summary>The first hub that answers about itself (older ones are only updated).</summary>
    public const string FirstVersion = "0.4.3";

    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private readonly StackPanel _top = new();
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
        FontSize = 12.5,
        Height = 440,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        AcceptsReturn = true,
    };

    private readonly TextBox _filter = new() { Width = 260, Margin = new Thickness(0, 0, 12, 8) };
    private readonly TextBlock _logNote = new() { FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _follow;
    private readonly Border _logChoice;
    private ConsoleData? _last;
    private JsonObject? _state;
    private DateTimeOffset _stateAt;
    private string _planRate = "0.0001";
    private string _planRounds = "600";
    private string? _stateError;
    private bool _loading;
    private bool _logLoading;
    private string _logName = "hub";
    private int _logLines = 500;
    private List<string> _lines = [];
    private string _loadNote = "";

    public ServerPage()
    {
        _root.Children.Add(_top);
        var logPanel = new StackPanel();
        logPanel.Children.Add(Ui.Heading("Denník", "čo server robil: počítače a konzoly, kolá, testy čítačky, chyby; denník aktualizácie: čo urobil jeho aktualizátor"));
        var tools = new WrapPanel();
        _logChoice = new Border();
        tools.Children.Add(_logChoice);
        var lines = Ui.Segments("server-log-lines", [("200", "200 riadkov"), ("500", "500"), ("2000", "2000")], "500", key =>
        {
            _logLines = int.Parse(key, CultureInfo.InvariantCulture);
            _ = LoadLog();
        });
        lines.Margin = new Thickness(0, 0, 16, 8);
        tools.Children.Add(lines);
        var caption = Ui.Caption("Hľadať");
        caption.Margin = new Thickness(0, 0, 8, 8);
        caption.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(caption);
        tools.Children.Add(_filter);
        _filter.TextChanged += (_, _) => ShowLog();
        _follow = Ui.Switch("sledovať (každých 5 s)", false);
        _follow.Margin = new Thickness(0, 0, 16, 8);
        tools.Children.Add(_follow);
        tools.Children.Add(Ui.Small("Načítať znova", () => _ = LoadLog()));
        logPanel.Children.Add(tools);
        logPanel.Children.Add(_logNote);
        _log.SetResourceReference(BackgroundProperty, "SurfaceAlt");
        _log.SetResourceReference(ForegroundProperty, "Text");
        _log.SetResourceReference(BorderBrushProperty, "Border");
        logPanel.Children.Add(_log);
        _root.Children.Add(Ui.Card(logPanel));
        Content = Scroll(_root);
        ChooseLog();
    }

    public override string Heading => "Server";

    /// <summary>The self-test shows invented answers of a server.</summary>
    public void Fake(JsonObject state, IEnumerable<string> lines)
    {
        _state = state;
        _stateAt = DateTimeOffset.Now;
        _lines = [.. lines];
        ShowLog();
    }

    public override void Show(ConsoleData data)
    {
        _last = data;
        var connected = data.Snapshot is not null && data.Error is null;
        if (connected && Supported(data) && !_loading && DateTimeOffset.Now - _stateAt > TimeSpan.FromSeconds(15))
        {
            _ = LoadState();
        }

        if (connected && Supported(data) && (_follow.IsChecked == true || (_lines.Count == 0 && !_logLoading && _loadNote.Length == 0)))
        {
            _ = LoadLog();
        }

        Render(data);
    }

    private static bool Supported(ConsoleData data) => Ui.String(data.Snapshot?["hub_version"]) is { } v && !LabUpdates.IsNewer(FirstVersion, v);

    private async Task LoadState()
    {
        if (Actions is null)
        {
            return;
        }

        _loading = true;
        try
        {
            _state = await Actions.ServerStatus();
            _stateError = _state is null ? "Server neodpovedal." : _state["error"] is { } e ? Ui.String(e) : null;
            _stateAt = DateTimeOffset.Now;
        }
        finally
        {
            _loading = false;
        }

        if (_last is not null)
        {
            Render(_last);
        }
    }

    private async Task LoadLog()
    {
        if (Actions is null || _logLoading)
        {
            return;
        }

        _logLoading = true;
        try
        {
            var lines = await Actions.ServerLog(_logName, _logLines);
            _loadNote = lines is null ? "Denník sa nepodarilo načítať (server nie je pripojený)." : lines.Count == 0 ? "Denník je zatiaľ prázdny." : "";
            _lines = lines is null ? _lines : [.. lines];
        }
        finally
        {
            _logLoading = false;
        }

        ShowLog();
    }

    private void ChooseLog()
    {
        var choice = Ui.Segments("server-log", [("hub", "Server"), ("update", "Aktualizácia")], _logName, key =>
        {
            _logName = key;
            _lines = [];
            _loadNote = "";
            ShowLog();
            ChooseLog();
            _ = LoadLog();
        });
        choice.Margin = new Thickness(0, 0, 16, 8);
        _logChoice.Child = choice;
    }

    // the lines with the filter, the newest at the bottom (the view stays at the bottom unless the user scrolled up)
    private void ShowLog()
    {
        var query = _filter.Text.Trim();
        var shown = query.Length == 0 ? _lines : _lines.Where(l => l.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var atEnd = _log.VerticalOffset + _log.ViewportHeight >= _log.ExtentHeight - 4;
        _log.Text = string.Join(Environment.NewLine, shown);
        if (atEnd || _log.ExtentHeight == 0)
        {
            _log.ScrollToEnd();
        }

        _logNote.Text = query.Length > 0 && _lines.Count > 0 ? $"{shown.Count} z {_lines.Count} riadkov obsahuje „{query}“" : _loadNote;
    }

    private void Render(ConsoleData data)
    {
        _logNote.Foreground = Ui.Brush("TextMuted");
        _top.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _top.Children.Add(error);
        }

        var hub = Ui.String(data.Snapshot?["hub_version"]);
        if (data.Snapshot is not null && !Supported(data))
        {
            var old = new StackPanel();
            old.Children.Add(Ui.Heading($"Server má verziu {hub}", $"stav, denník, reštart a model z vydania vie server až od verzie {FirstVersion}"));
            var buttons = new WrapPanel();
            buttons.Children.Add(Ui.Button("Aktualizovať server teraz", () => _ = Actions?.UpdateServer(), primary: true));
            old.Children.Add(buttons);
            old.Children.Add(Ui.Text("Po aktualizácii zo starej verzie sa server niekedy sám nereštartuje (chyba verzií 0.4.1 a 0.4.2): vtedy raz na serveri „systemctl restart iris-hub“. Od 0.4.3 to už netreba.", 12.5, brush: "TextMuted"));
            _top.Children.Add(Ui.Card(old));
            return;
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var state = Ui.Card(State());
        var actions = Ui.Card(ActionsCard(data.Snapshot is not null && data.Error is null));
        Grid.SetColumn(actions, 1);
        row.Children.Add(state);
        row.Children.Add(actions);
        _top.Children.Add(row);
        if (data.Snapshot?["plan"] is JsonObject plan)
        {
            _top.Children.Add(Ui.Card(PlanCard(plan, data.Snapshot is not null && data.Error is null)));
        }

        _top.Children.Add(Ui.Card(LastUpdate()));
    }

    // the plan of the learning rate: where it is, a warning when it is ending or has ended, and a new one
    private StackPanel PlanCard(JsonObject plan, bool connected)
    {
        var panel = new StackPanel();
        var ended = Ui.Bool(plan["ended"]) == true;
        var left = Ui.Value(plan["left"]);
        panel.Children.Add(Ui.Heading("Plán učenia", "rýchlosť učenia klesá po kosínuse od najvyššej po 2 % – potom sa model už takmer nemení"));
        panel.Children.Add(Ui.Fact("plán", $"kolo {Ui.Number(plan["done"])} z {Ui.Number(plan["rounds"])} · najvyššia rýchlosť {Ui.Rate(plan["rate"])} · teraz {Ui.Rate(plan["now"])}"));
        var state = ended ? "plán skončil – učebňa počíta, ale model sa už takmer neučí; začnite nový plán"
            : left <= 36 ? $"plán končí o {left:0} kôl – pripravte nový"
            : $"ostáva {left:0} kôl";
        var note = Ui.Text(state, 13.5, bold: ended || left <= 36, brush: ended ? "Danger" : left <= 36 ? "Warn" : "Text");
        note.Margin = new Thickness(0, 4, 0, 8);
        panel.Children.Add(note);
        panel.Children.Add(Ui.Caption("Nový plán od tejto verzie"));
        var choices = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        var rates = Ui.Segments("plan-rate", [("0.0001", "0,0001"), ("0.0002", "0,0002"), ("0.0003", "0,0003")], _planRate, key => _planRate = key);
        rates.Margin = new Thickness(0, 0, 16, 6);
        choices.Children.Add(rates);
        var rounds = Ui.Segments("plan-rounds", [("300", "300 kôl"), ("600", "600"), ("900", "900"), ("1200", "1200")], _planRounds, key => _planRounds = key);
        rounds.Margin = new Thickness(0, 0, 16, 6);
        choices.Children.Add(rounds);
        panel.Children.Add(choices);
        var buttons = new WrapPanel();
        var start = Ui.Button("Začať nový plán…", () => _ = NewPlan(), primary: ended);
        start.IsEnabled = connected;
        buttons.Children.Add(start);
        panel.Children.Add(buttons);
        panel.Children.Add(Ui.Text("Odporúčané: 0,0001 na 600 kôl (pri 10-minútových kolách asi 4 noci). Vyššia rýchlosť pohne modelom viac – najprv sa môže na pár kôl zhoršiť; najlepší model ostáva uložený.", 12, brush: "TextMuted"));
        return panel;
    }

    private async Task NewPlan()
    {
        var rate = double.Parse(_planRate, CultureInfo.InvariantCulture);
        var rounds = int.Parse(_planRounds, CultureInfo.InvariantCulture);
        if (Actions is null || MessageBox.Show($"Nový plán učenia: od tejto verzie rýchlosť {rate.ToString("0.####", CultureInfo.GetCultureInfo("sk-SK"))} klesajúca počas {rounds} kôl.\n\nZačať?",
                "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var answer = await Actions.NewPlan(rate, rounds);
        var text = answer is null ? "Server nie je pripojený." : answer["error"] is { } e ? $"Server plán neprijal: {Ui.String(e)}" : "Nový plán beží – platí od nasledujúceho kola.";
        MessageBox.Show(text, "IRIS – učebňa", MessageBoxButton.OK, answer?["ok"] is not null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        _stateAt = DateTimeOffset.MinValue;
    }

    private StackPanel State()
    {
        var panel = new StackPanel();
        if (_state is not { } s || _stateError is not null)
        {
            panel.Children.Add(Ui.Heading("Stav servera", _stateError ?? "načítavam…"));
            return panel;
        }

        panel.Children.Add(Ui.Heading("Stav servera", $"zistené o {_stateAt.LocalDateTime:HH:mm:ss}"));
        panel.Children.Add(Ui.Fact("program", $"iris-hub {Ui.String(s["hub_version"])}"));
        panel.Children.Add(Ui.Fact("systém", Ui.String(s["system"])));
        panel.Children.Add(Ui.Fact("beží", Duration(Ui.Value(s["uptime_minutes"]))));
        panel.Children.Add(Clock(s));
        var model = s["model"];
        panel.Children.Add(Ui.Fact("model učebne", $"verzia {Ui.Number(model?["version"])} · {Ui.Number(model?["file_mb"], "0.0", " MB")}"));
        panel.Children.Add(Ui.Fact("odkiaľ", Source(Ui.String(model?["source"]))));
        panel.Children.Add(Ui.Fact("plán učenia", $"kolo {Ui.Number(model?["plan_done"])} z {Ui.Number(model?["plan_rounds"])}, najvyššia rýchlosť {Ui.Rate(model?["rate"])}"));
        if (model?["best"] is JsonObject best)
        {
            var kind = Ui.String(best["kind"]) == "average" ? "priemer posledných verzií" : "model";
            panel.Children.Add(Ui.Fact("najlepší model", $"verzia {Ui.Number(best["version"])} ({kind}) · CER {Ui.Number(best["cer"], "0.00", " %")}" +
                                                       (best["cer_unseen"] is { } unseen ? $", neznáme písmo {Ui.Number(unseen, "0.00", " %")}" : "") +
                                                       (DateTimeOffset.TryParse(Ui.String(best["time"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? $" · {when.LocalDateTime:d. M. HH:mm}" : "")));
        }

        var disk = s["disk"];
        var free = Ui.Value(disk?["free_mb"]);
        panel.Children.Add(Warn(Ui.Fact("disk", double.IsFinite(free) ? $"voľných {free / 1024:0.0} GB z {Ui.Value(disk?["total_mb"]) / 1024:0} GB" : null), double.IsFinite(free) && free < 2048));
        panel.Children.Add(Ui.Fact("pamäť servera IRIS", $"{Ui.Number(s["memory_mb"])} MB · {Ui.Number(s["processors"])} vlákien procesora"));
        var fonts = Ui.Value(s["fonts"]);
        panel.Children.Add(Warn(Ui.Fact("písma", fonts > 0 ? $"{fonts:0} súborov – čítačka sa meria (CER) každých 6 kôl" : "žiadne – čítačka sa nemeria (na serveri raz: bash install.sh --fonts)"), !(fonts > 0)));
        return panel;
    }

    // the server's clock against this computer's: a wrong clock moves the time of learning
    private Grid Clock(JsonObject s)
    {
        if (!DateTimeOffset.TryParse(Ui.String(s["now"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var server))
        {
            return Ui.Fact("čas servera", null);
        }

        var difference = (server - _stateAt).TotalSeconds;
        var text = $"{server.LocalDateTime:HH:mm:ss}";
        var off = Math.Abs(difference) >= 60;
        text += off
            ? $" – o {Duration(Math.Abs(difference) / 60)} {(difference > 0 ? "napred" : "pozadu")} oproti tomuto počítaču (posúva čas učenia)"
            : " – sedí s týmto počítačom";
        return Warn(Ui.Fact("čas servera", text), off);
    }

    private StackPanel ActionsCard(bool connected)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Úkony", "len pevné úkony – konzola nemôže na serveri spustiť nič iné"));
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("Obnoviť", () => _ = LoadState()));
        if (connected)
        {
            buttons.Children.Add(Ui.Button("Aktualizovať server teraz", () => _ = Actions?.UpdateServer(), primary: true));
            buttons.Children.Add(Ui.Button("Model z vydania…", () => _ = ModelFromRelease()));
            if (_state?["model"]?["best"] is JsonObject)
            {
                buttons.Children.Add(Ui.Button("Uložiť najlepší model…", () => _ = SaveBest()));
            }

            buttons.Children.Add(Ui.Button("Reštartovať server…", () => _ = Restart(), danger: true));
        }

        panel.Children.Add(buttons);
        foreach (var note in new[]
                 {
                     "Aktualizovať: server sa pozrie na najnovšie vydanie IRIS a nainštaluje ho, ak je novšie a jeho podpis sedí (inak to robí sám raz denne).",
                     "Model z vydania: učebňa bude ďalej učiť od učiaceho súboru najnovšieho podpísaného vydania; doterajší model sa odloží vedľa (tri posledné ostávajú).",
                     "Najlepší model: server po každom kole posunie priemer posledných verzií modelu a pri meraní CER číta model aj priemer; najlepší, aký kedy nameral, si odloží – uložíte ho ako súbor čítačky IRIS.",
                     "Reštart: server sa ukončí a systém ho o pol minúty spustí znova; počítače sa pripoja samy.",
                 })
        {
            var text = Ui.Text(note, 12.5, brush: "TextMuted");
            text.Margin = new Thickness(0, 2, 0, 4);
            panel.Children.Add(text);
        }

        return panel;
    }

    private StackPanel LastUpdate()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Posledná aktualizácia", "čo naposledy urobil aktualizátor servera (raz denne, alebo keď ho požiada konzola)"));
        if (_state?["update_waiting"]?.GetValue<bool>() == true)
        {
            panel.Children.Add(Ui.Text("Žiadosť čaká na aktualizátor – spustí sa do niekoľkých sekúnd.", 13, bold: true, brush: "Accent"));
        }

        if (_state?["last_update"] is not JsonObject last)
        {
            panel.Children.Add(Ui.Text("Zatiaľ nič (zapisuje sa od verzie 0.4.3).", 13, brush: "TextMuted"));
            return panel;
        }

        var ok = last["ok"]?.GetValue<bool>() == true;
        panel.Children.Add(Ui.Fact("kedy", DateTimeOffset.TryParse(Ui.String(last["time"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? $"{time.LocalDateTime:d. M. yyyy HH:mm}" : null));
        panel.Children.Add(Warn(Ui.Fact("výsledok", ok ? "v poriadku" : "chyba – podrobnosti v denníku aktualizácie"), !ok));
        panel.Children.Add(Ui.Fact("verzie", $"{Ui.String(last["from"])} → najnovšia {Ui.String(last["newest"]) ?? "?"}" +
                                             (Ui.String(last["installed"]) is { } installed ? $", nainštalovaná {installed}" : "")));
        if (last["model_asked"]?.GetValue<bool>() == true)
        {
            panel.Children.Add(Ui.Fact("model z vydania", Ui.String(last["model"]) is { } model ? $"učiaci súbor {model} pripravený" : "nepripravený (pozri denník)"));
        }

        panel.Children.Add(Ui.Fact("posledná správa", Ui.String(last["message"])));
        return panel;
    }

    private async Task ModelFromRelease()
    {
        if (MessageBox.Show(
                "Server stiahne učiaci súbor čítačky z najnovšieho podpísaného vydania IRIS, overí jeho podpis a SHA-256 a učebňa bude ďalej učiť od neho.\n\n" +
                "Doterajší model učebne sa odloží vedľa (lab-model-before-…, tri posledné ostávajú), kolo, ktoré práve beží, sa preruší a server sa reštartuje.\n\nPokračovať?",
                "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes || Actions is null)
        {
            return;
        }

        var asked = await Actions.ModelFromRelease();
        MessageBox.Show(asked
            ? "Žiadosť je odoslaná. Aktualizátor servera to urobí do minúty a server sa reštartuje; výsledok uvidíte v „Posledná aktualizácia“ a v denníku aktualizácie."
            : "Server žiadosť neprijal.", "IRIS – učebňa", MessageBoxButton.OK, asked ? MessageBoxImage.Information : MessageBoxImage.Warning);
        _stateAt = DateTimeOffset.MinValue;
    }

    private async Task SaveBest()
    {
        if (Actions is null)
        {
            return;
        }

        var version = Ui.Number(_state?["model"]?["best"]?["version"]);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"iris-ocr-ucebna-v{version}.iris",
            Filter = "Model čítačky IRIS (*.iris)|*.iris",
            DefaultExt = ".iris",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var best = await Actions.BestModel();
        string text;
        if (best is not { } got || got.File.Length == 0)
        {
            text = "Server najlepší model neposlal (nie je pripojený, alebo ešte žiadny nemá).";
        }
        else
        {
            try
            {
                await File.WriteAllBytesAsync(dialog.FileName, got.File);
                text = $"Uložené: {dialog.FileName} ({got.File.Length / 1048576.0:0.0} MB).";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text = $"Súbor sa nepodarilo uložiť: {e.Message}";
            }
        }

        MessageBox.Show(text, "IRIS – učebňa", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task Restart()
    {
        if (MessageBox.Show("Server sa ukončí a systém ho o pol minúty spustí znova. Kolo, ktoré práve beží, sa preruší; počítače aj konzola sa pripoja samy.\n\nReštartovať server?",
                "IRIS – učebňa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes || Actions is null)
        {
            return;
        }

        if (!await Actions.RestartServer())
        {
            MessageBox.Show("Server reštart neprijal.", "IRIS – učebňa", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _stateAt = DateTimeOffset.MinValue;
    }

    private static Grid Warn(Grid fact, bool warn)
    {
        if (warn && fact.Children.Count > 1 && fact.Children[1] is TextBlock text)
        {
            text.Foreground = Ui.Brush("Danger");
            text.FontWeight = FontWeights.SemiBold;
        }

        return fact;
    }

    private static string Source(string? source) => source switch
    {
        null => "–",
        _ when source.StartsWith("IRIS lab", StringComparison.Ordinal) => "naučený v učebni",
        _ when source.StartsWith("tools/ocr/train.py", StringComparison.Ordinal) => "učiaci súbor z vydania (učenie na GitHube)",
        _ when source.StartsWith("random", StringComparison.Ordinal) => "od začiatku (náhodný)",
        _ => source,
    };

    private static string Duration(double minutes)
    {
        if (!double.IsFinite(minutes))
        {
            return "–";
        }

        var span = TimeSpan.FromMinutes(Math.Round(minutes));
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{span.Minutes} min";
    }
}
