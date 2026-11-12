using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// Efficiency: what every computer and every group brought to the learning over a day, a week or a month – its share
/// of all lines, hours, speed, the load it had, how often it struggled, was busy or stepped aside – and the log as a
/// table (Excel) to send for analysis.
/// </summary>
public sealed class EfficiencyPage : ConsolePage
{
    /// <summary>The first hub with the log of efficiency.</summary>
    public const string FirstVersion = "0.4.7";

    private readonly StackPanel _root = new() { Margin = new Thickness(4) };
    private string _days = "7";
    private JsonObject? _summary;
    private DateTime _loaded = DateTime.MinValue;
    private bool _loading;
    private string? _note;
    private ConsoleData? _last;

    public EfficiencyPage() => Content = Scroll(_root);

    public override string Heading => "Efektivita";

    /// <summary>The self-test shows an invented summary.</summary>
    public void Fake(JsonObject summary)
    {
        _summary = summary;
        _loaded = DateTime.Now;
    }

    public override void Show(ConsoleData data)
    {
        _last = data;
        var hub = Ui.String(data.Snapshot?["hub_version"]);
        var supported = hub is not null && !LabUpdates.IsNewer(FirstVersion, hub);
        if (supported && data.Error is null && !_loading && DateTime.Now - _loaded > TimeSpan.FromSeconds(60))
        {
            _ = Load();
        }

        _root.Children.Clear();
        if (ErrorNote(data) is { } error)
        {
            _root.Children.Add(error);
        }

        if (data.Snapshot is not null && !supported)
        {
            var old = new StackPanel();
            old.Children.Add(Ui.Heading($"Server má verziu {hub}", $"denník efektivity vedie server od verzie {FirstVersion}"));
            var buttons = new WrapPanel();
            buttons.Children.Add(Ui.Button("Aktualizovať server teraz", () => _ = Actions?.UpdateServer(), primary: true));
            old.Children.Add(buttons);
            _root.Children.Add(Ui.Card(old));
            return;
        }

        var head = new StackPanel();
        head.Children.Add(Ui.Heading("Prínos počítačov k učeniu", Period()));
        var tools = new WrapPanel();
        var days = Ui.Segments("efficiency-days", [("1", "1 deň"), ("7", "7 dní"), ("30", "30 dní"), ("60", "60 dní")], _days, key =>
        {
            _days = key;
            _loaded = DateTime.MinValue;
            _summary = null;
            Show(data);
        });
        days.Margin = new Thickness(0, 0, 16, 8);
        tools.Children.Add(days);
        tools.Children.Add(Ui.Button("Obnoviť", () =>
        {
            _loaded = DateTime.MinValue;
            Show(data);
        }));
        tools.Children.Add(Ui.Button("Uložiť denník efektivity…", () => _ = Save(), primary: true));
        head.Children.Add(tools);
        if (_note is not null)
        {
            head.Children.Add(Ui.Text(_note, 13, bold: true, brush: "Accent"));
        }

        head.Children.Add(Ui.Text("Podiel = koľko zo všetkých naučených riadkov urobil počítač. Rýchlosť na vlákno porovnáva počítače bez ohľadu na nastavenú záťaž. " +
                                  "Denník (súbor pre Excel) obsahuje jeden riadok na počítač a kolo: mená z konzoly, typ hardvéru a čísla – žiadne adresy.", 12.5, brush: "TextMuted"));
        _root.Children.Add(Ui.Card(head));
        if (_summary is null)
        {
            _root.Children.Add(Ui.Card(Ui.Text(_loading ? "Načítavam…" : "Zatiaľ nič – denník sa plní po každom kole (od verzie 0.4.7).", 13, brush: "TextMuted")));
            return;
        }

        _root.Children.Add(Ui.Card(Table("Skupiny", _summary["groups"]?.AsArray(), groups: true)));
        _root.Children.Add(Ui.Card(Table("Počítače", _summary["computers"]?.AsArray(), groups: false)));
        _root.Children.Add(Ui.Card(Moving(_summary)));
    }

    // the moving load against the fixed one: where it got, what it used, how much faster the nights learned, what it cost
    private static StackPanel Moving(JsonObject summary)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("Pohyblivá záťaž", "oproti pevnej záťaži tých istých počítačov v tom istom období"));
        if (summary["moving"] is not JsonObject all)
        {
            panel.Children.Add(Ui.Text("V tomto období pohyblivá záťaž nebežala – zapína sa v Nastaveniach (pre všetky počítače, skupinu alebo jeden).", 13, brush: "TextMuted"));
            return panel;
        }

        var gain = Gain(all);
        panel.Children.Add(Ui.Text($"Spolu: {Ui.Number(all["rounds"])} kôl s pohyblivou záťažou, {Ui.Number(all["fixed_rounds"])} s pevnou · " +
                                   $"riadky za sekundu kola {Ui.Number(all["per_second"], "0.0")} oproti {Ui.Number(all["per_second_fixed"], "0.0")}" +
                                   (gain is { } g ? $" ({g:+0;−0} %)" : "") + $" · priemerná úroveň {Ui.Percent(Ui.Value(all["level"]))} cesty k najvyššej", 13, bold: true));
        var note = Ui.Text("Riadky/s kola = čo sa za noc naučí (aj s oddychom). Na vlákno = rýchlosť jedného pracujúceho vlákna. Úroveň 0 % = nastavená záťaž, 100 % = najvyššia. " +
                           "Uberania = koľkokrát za kolo záťaž ubrala; prečo držala = čo ju brzdilo (námaha, iné programy, človek, teplo, alebo že vyššia záťaž naučí menej riadkov).", 12, brush: "TextMuted");
        note.Margin = new Thickness(0, 4, 0, 8);
        panel.Children.Add(note);
        string[] headers = ["Počítač", "Kolá", "Úroveň", "Vlákna · čas", "Riadky/s", "Pevná", "Zisk", "Na vlákno", "Učenie % CPU", "Trápil sa", "Uberania na kolo", "Prečo držala"];
        var grid = new Grid();
        foreach (var _ in headers)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        grid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < headers.Length; c++)
        {
            Place(grid, Ui.Caption(headers[c]), 0, c);
        }

        var row = 1;
        foreach (var item in summary["computers"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (item["moving"] is not JsonObject m)
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition());
            var itemGain = Gain(m);
            var held = string.Join(", ", m["held"]?.AsArray().OfType<JsonObject>().Select(h => $"{Held(Ui.String(h["code"]))} {Ui.Number(h["looks"])}×") ?? []);
            var cells = new FrameworkElement[]
            {
                Ui.Text(Ui.String(item["name"]) ?? Ui.String(item["key"]) ?? "?", 13.5, bold: true),
                Ui.Text($"{Ui.Number(m["rounds"])} / {Ui.Number(m["fixed_rounds"])}", 13),
                Ui.Text(Ui.Percent(Ui.Value(m["level"])), 13),
                Ui.Text($"{Ui.Number(m["threads"], "0.#")} · {Ui.Percent(Ui.Value(m["duty"]))}", 13),
                Ui.Text(Ui.Number(m["per_second"], "0.0"), 13, bold: true),
                Ui.Text(Ui.Number(m["per_second_fixed"], "0.0"), 13, brush: "TextMuted"),
                Ui.Text(itemGain is { } ig ? $"{ig:+0;−0} %" : "–", 13, bold: true, brush: itemGain is > 0 ? "Text" : "Warn"),
                Ui.Text($"{Ui.Number(m["per_thread"], "0.00")} / {Ui.Number(m["per_thread_fixed"], "0.00")}", 13),
                Ui.Text($"{Ui.Number(m["cpu_own"], "0", " %")} / {Ui.Number(m["cpu_own_fixed"], "0", " %")}", 13),
                Ui.Text($"{Ui.Number(m["struggled"])} / {Ui.Number(m["struggled_fixed"])}", 13, brush: Ui.Value(m["struggled"]) > 0 ? "Danger" : "Text"),
                Ui.Text(Ui.Number(m["backs"], "0.0"), 13),
                Ui.Text(held.Length == 0 ? "–" : held, 12, brush: "TextMuted"),
            };
            for (var c = 0; c < cells.Length; c++)
            {
                Place(grid, cells[c], row, c);
            }

            row++;
        }

        panel.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var pairs = Ui.Text("Pri dvojiciach čísel: pohyblivá / pevná záťaž.", 11.5, brush: "TextMuted");
        pairs.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(pairs);
        return panel;
    }

    // how much more a round learned with the moving load than with the fixed one (%), when both ran
    private static double? Gain(JsonObject moving) =>
        Ui.Value(moving["per_second"]) is var a && Ui.Value(moving["per_second_fixed"]) is var b && double.IsFinite(a) && double.IsFinite(b) && b > 0
            ? Math.Round(100 * (a / b - 1))
            : null;

    private static string Held(string? code) => code switch
    {
        "strain" => "námaha",
        "others" => "iné programy",
        "user" => "človek pri PC",
        "warm" => "teplo",
        "slower" => "vyššie sa naučí menej",
        _ => code ?? "?",
    };

    private string Period()
    {
        if (_summary is null)
        {
            return "za zvolené obdobie";
        }

        static string When(JsonNode? node) => DateTime.TryParse(Ui.String(node), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("d. M. HH:mm", CultureInfo.InvariantCulture) : "–";
        return $"{When(_summary["from"])} – {When(_summary["until"])} · {Ui.Number(_summary["rounds"])} kôl · {Ui.Number(_summary["lines"], "#,0")} riadkov spolu";
    }

    private async Task Load()
    {
        if (Actions is null)
        {
            return;
        }

        _loading = true;
        try
        {
            _summary = await Actions.Efficiency(double.Parse(_days, CultureInfo.InvariantCulture)) ?? _summary;
            _loaded = DateTime.Now;
        }
        finally
        {
            _loading = false;
        }

        if (_last is not null)
        {
            Show(_last);
        }
    }

    private async Task Save()
    {
        if (Actions is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"iris-efektivita-{DateTime.Now:yyyy-MM-dd}-{_days}d.csv",
            Filter = "Tabuľka CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var table = await Actions.EfficiencyTable(double.Parse(_days, CultureInfo.InvariantCulture));
        if (table is not { } got)
        {
            _note = "Server denník neposlal (nie je pripojený).";
        }
        else
        {
            try
            {
                await File.WriteAllBytesAsync(dialog.FileName, got.Table);
                _note = $"Uložené: {dialog.FileName} ({got.Rows} riadkov). Tento súbor môžete poslať na analýzu.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _note = $"Súbor sa nepodarilo uložiť: {e.Message}";
            }
        }

        if (_last is not null)
        {
            Show(_last);
        }
    }

    private static StackPanel Table(string title, JsonArray? items, bool groups)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading(title, groups ? "súčty po skupinách" : "najväčší prínos navrchu"));
        string[] headers = groups
            ? ["Skupina", "Počítače", "Podiel", "Riadky", "Hodiny", "Riadky/s", "Na vlákno", "Trápili sa", "Ustúpili"]
            : ["Počítač", "Procesor", "Podiel", "Riadky", "Hodiny", "Riadky/s", "Na vlákno", "Vlákna · čas", "Učenie % CPU", "Iné programy", "Trápil sa", "Vyťažený", "Ustúpil", "Dôvody"];
        var grid = new Grid();
        foreach (var _ in headers)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        grid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < headers.Length; c++)
        {
            Place(grid, Ui.Caption(headers[c]), 0, c);
        }

        var row = 1;
        foreach (var item in items?.OfType<JsonObject>() ?? [])
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var rounds = Ui.Value(item["rounds"]);
            string Rounds(string key) => Ui.Value(item[key]) is > 0 and var n ? $"{n:0} ({100 * n / rounds:0} %)" : "0";
            var share = Ui.Value(item["share"]);
            var cells = new List<FrameworkElement>
            {
                Ui.Text(Ui.String(item["name"]) ?? (groups ? "bez skupiny" : Ui.String(item["key"]) ?? "?"), 13.5, bold: true),
            };
            if (groups)
            {
                cells.Add(Ui.Text(Ui.Number(item["computers"]), 13));
            }
            else
            {
                var cpu = Ui.String(item["cpu"]);
                cells.Add(Ui.Text($"{(cpu is null ? Ui.String(item["model"]) ?? "–" : cpu)}{(item["logical"] is { } l ? $" · {Ui.Number(l)} vl." : "")}", 12, brush: "TextMuted"));
            }

            cells.Add(Share(share));
            cells.Add(Ui.Text(Ui.Number(item["lines"], "#,0"), 13));
            cells.Add(Ui.Text(Ui.Number(item["hours"], "0.0", " h"), 13));
            cells.Add(Ui.Text(Ui.Number(item["per_second"], "0.0"), 13));
            cells.Add(Ui.Text(Ui.Number(item["per_thread"], "0.00"), 13, bold: true));
            if (!groups)
            {
                cells.Add(Ui.Text($"{Ui.Number(item["threads"], "0.#")} · {Ui.Percent(Ui.Value(item["duty"]))}", 13));
                cells.Add(Ui.Text(Ui.Number(item["cpu_own"], "0", " %"), 13));
                cells.Add(Ui.Text(Ui.Number(item["cpu_others"], "0", " %"), 13, brush: Ui.Value(item["cpu_others"]) >= 20 ? "Warn" : "Text"));
            }

            cells.Add(Ui.Text(Rounds("struggled"), 13, bold: Ui.Value(item["struggled"]) > 0, brush: Ui.Value(item["struggled"]) > 0 ? "Danger" : "Text"));
            if (!groups)
            {
                cells.Add(Ui.Text(Rounds("busy"), 13, brush: Ui.Value(item["busy"]) > 0 ? "Warn" : "Text"));
            }

            cells.Add(Ui.Text(Rounds("stopped"), 13));
            if (!groups)
            {
                var reasons = string.Join(", ", item["reasons"]?.AsArray().OfType<JsonObject>()
                    .Select(r => $"{Ui.ReasonName(Ui.String(r["code"]))} {Ui.Number(r["rounds"])}×") ?? []);
                var text = Ui.Text(reasons.Length == 0 ? "–" : reasons, 12, brush: "TextMuted");
                text.MaxWidth = 300;
                cells.Add(text);
            }

            for (var c = 0; c < cells.Count; c++)
            {
                Place(grid, cells[c], row, c);
            }

            row++;
        }

        panel.Children.Add(row == 1 ? Ui.Text("Žiadne kolá v tomto období.", 13, brush: "TextMuted")
            : new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return panel;
    }

    // the share as a number with a small bar
    private static StackPanel Share(double share)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Ui.Text($"{share:0.0} %", 13.5, bold: true));
        var bar = new Border
        {
            Width = Math.Max(2, Math.Min(100, share) * 1.2),
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Ui.Brush("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(bar);
        return panel;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 5, 22, 5);
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
