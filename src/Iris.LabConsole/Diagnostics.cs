using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Iris.LabConsole;

/// <summary>
/// The diagnosis of computers (agents and hubs from 0.5.3): asked for from the console, answered by the computer
/// through the server, kept on the server. Shown for one computer, as a table for many, and written as a text report
/// to save and send – what one would otherwise read from the Task Manager at every computer. The memory of the agent
/// over the last days comes from the server's history.
/// </summary>
public static class DiagnosisView
{
    /// <summary>How long the console waits for the computers to answer.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Asks the computers for their diagnosis and waits until each answered (or the time is over): the server's
    /// diagnoses then, the new ones and the old ones of those that did not answer (an answer is new when the time the
    /// server got it changed – the clocks of the console and the server need not agree).
    /// </summary>
    public static async Task<(List<JsonObject> Entries, List<string> Silent)> AskAsync(IConsoleActions actions, IReadOnlyList<string> names)
    {
        var before = Received(await actions.Diagnoses(null));
        foreach (var name in names)
        {
            await actions.Command(name, Iris.Lab.LabCommands.Diagnose);
        }

        var waiting = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        JsonObject? last = null;
        while (waiting.Count > 0 && watch.Elapsed < Wait)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            last = await actions.Diagnoses(null);
            foreach (var (name, received) in Received(last))
            {
                if (!before.TryGetValue(name, out var old) || old != received)
                {
                    waiting.Remove(name);
                }
            }
        }

        var entries = Entries(last ?? await actions.Diagnoses(null));
        return (entries, [.. waiting]);
    }

    /// <summary>The diagnoses of a server's answer.</summary>
    public static List<JsonObject> Entries(JsonObject? answer) => answer?["diagnoses"]?.AsArray().OfType<JsonObject>().ToList() ?? [];

    private static Dictionary<string, string> Received(JsonObject? answer) =>
        Entries(answer).Where(e => Ui.String(e["computer"]) is not null)
            .GroupBy(e => Ui.String(e["computer"])!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Ui.String(g.First()["received"]) ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a diagnosis says in a few words, and its color: the agent holding much memory, the computer short of
    /// memory (and which program holds most), or fine.
    /// </summary>
    public static (string Text, string Background, string Foreground) Verdict(JsonObject report)
    {
        var agent = report["agent"] as JsonObject;
        var system = report["system"] as JsonObject;
        var total = Ui.Value(system?["total_mb"]);
        var available = Ui.Value(system?["available_mb"]);
        var own = Ui.Value(agent?["private_mb"]);
        if (double.IsFinite(own) && own > Math.Max(3000, double.IsFinite(total) ? total * 0.3 : 0))
        {
            return ($"agent drží veľa pamäte ({Gb(own)})", "WarnSoft", "Danger");
        }

        if (double.IsFinite(total) && double.IsFinite(available) && total > 0 && available / total < 0.10)
        {
            var biggest = report["programs"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            var who = biggest is null ? "" : $" – najviac {Ui.String(biggest["name"])} ({Gb(Ui.Value(biggest["private_mb"]))})";
            return ($"počítaču dochádza pamäť{who}", "WarnSoft", "Warn");
        }

        return ("pamäť v poriadku", "GoodSoft", "Good");
    }

    /// <summary>One computer's diagnosis: the verdict, the agent, the computer, the programs that hold most memory.</summary>
    public static StackPanel Details(JsonObject entry)
    {
        var panel = new StackPanel();
        var report = entry["report"] as JsonObject ?? [];
        var (text, back, fore) = Verdict(report);
        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        badges.Children.Add(Ui.Badge(text, back, fore));
        var when = Ui.Text($"zistené {Ui.Ago(entry["received"])} · program {Ui.String(report["version"]) ?? "?"}", 12, brush: "TextMuted");
        when.Margin = new Thickness(8, 0, 0, 0);
        when.VerticalAlignment = VerticalAlignment.Center;
        badges.Children.Add(when);
        panel.Children.Add(badges);
        var agent = report["agent"] as JsonObject;
        var system = report["system"] as JsonObject;
        panel.Children.Add(Ui.Caption("Agent IRIS"));
        panel.Children.Add(Ui.Fact("pamäť", $"{Mb(agent?["private_mb"])} súkromná · {Mb(agent?["working_set_mb"])} pracovná množina (najviac {Mb(agent?["peak_working_set_mb"])})"));
        panel.Children.Add(Ui.Fact("z toho", $"{Mb(agent?["heap_mb"])} halda .NET ({Mb(agent?["heap_committed_mb"])} vyhradená) · {Mb(agent?["native_mb"])} pamäť učenia"));
        panel.Children.Add(Ui.Fact("vlákna", $"{Ui.Number(agent?["threads"])} ({Ui.Number(agent?["pool_threads"])} vo fonde) · {Ui.Number(agent?["handles"])} popisovačov"));
        panel.Children.Add(Ui.Fact("beží", Hours(agent?["up_hours"])));
        panel.Children.Add(new Border { Height = 6 });
        panel.Children.Add(Ui.Caption("Počítač"));
        panel.Children.Add(Ui.Fact("pamäť", $"{Mb(system?["total_mb"])} · voľná {Mb(system?["available_mb"])} (použitá {Ui.Number(system?["used_percent"], "0", " %")})"));
        panel.Children.Add(Ui.Fact("potvrdená", $"{Mb(system?["committed_mb"])} z {Mb(system?["commit_limit_mb"])} (pamäť + stránkovací súbor)"));
        panel.Children.Add(Ui.Fact("Windows beží", Hours(report["system_up_hours"])));
        if (report["programs"] is JsonArray programs && programs.Count > 0)
        {
            panel.Children.Add(new Border { Height = 6 });
            panel.Children.Add(Ui.Caption("Najviac pamäte (súkromná · pracovná množina)"));
            foreach (var program in programs.OfType<JsonObject>().Take(8))
            {
                var count = Ui.Value(program["count"]) is > 1 and var c ? $" ×{c:0}" : "";
                panel.Children.Add(Ui.Fact(Ui.String(program["name"]) + count, $"{Mb(program["private_mb"])} · {Mb(program["working_set_mb"])}"));
            }
        }

        return panel;
    }

    /// <summary>Many computers in one table, the agents that hold most memory first; a click opens the computer.</summary>
    public static UIElement Table(IReadOnlyList<JsonObject> entries, ConsoleData data, Action<string> open)
    {
        var table = new StackPanel();
        double[] widths = [1.6, 1.0, 1.0, 1.0, 1.0, 0.7, 2.6, 1.0];
        table.Children.Add(Row(widths, [Ui.Caption("Počítač"), Ui.Caption("Agent"), Ui.Caption("Halda / učenie"), Ui.Caption("Počítač voľná"), Ui.Caption("Agent beží"), Ui.Caption("Vlákna"), Ui.Caption("Stav"), Ui.Caption("Zistené")]));
        foreach (var entry in entries.OrderByDescending(e => Ui.Value(e["report"]?["agent"]?["private_mb"]) is var v && double.IsFinite(v) ? v : -1))
        {
            var report = entry["report"] as JsonObject ?? [];
            var agent = report["agent"] as JsonObject;
            var system = report["system"] as JsonObject;
            var name = Ui.String(entry["computer"]) ?? "?";
            var (text, back, fore) = Verdict(report);
            var shown = data.Agent(name) is { } a ? Ui.Name(a) : name;
            var free = Ui.Value(system?["total_mb"]) is > 0 and var total ? $"{Mb(system?["available_mb"])} ({100 * Ui.Value(system?["available_mb"]) / total:0} %)" : "–";
            var link = new TextBlock { Text = shown, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Accent"), Cursor = System.Windows.Input.Cursors.Hand, TextTrimming = TextTrimming.CharacterEllipsis };
            link.MouseLeftButtonUp += (_, _) => open(name);
            table.Children.Add(Row(widths, [
                link,
                Ui.Text(Mb(agent?["private_mb"]), 13, bold: true),
                Ui.Text($"{Mb(agent?["heap_mb"])} / {Mb(agent?["native_mb"])}", 13),
                Ui.Text(free, 13),
                Ui.Text(Hours(agent?["up_hours"]), 13),
                Ui.Text(Ui.Number(agent?["threads"]), 13),
                Ui.Badge(text, back, fore),
                Ui.Text(Ui.Ago(entry["received"]), 12, brush: "TextMuted"),
            ]));
        }

        return table;
    }

    private static Grid Row(double[] widths, FrameworkElement[] cells)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        }

        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            cell.Margin = new Thickness(0, 0, 10, 0);
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.HorizontalAlignment = cell is Border ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }

        return grid;
    }

    /// <summary>The diagnoses as a text report (to save and send instead of screenshots of the Task Manager).</summary>
    public static string Report(IEnumerable<JsonObject> entries, ConsoleData data)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"IRIS – diagnostika počítačov ({DateTime.Now:d. M. yyyy HH:mm})");
        text.AppendLine();
        foreach (var entry in entries.OrderBy(e => Ui.String(e["computer"]), StringComparer.OrdinalIgnoreCase))
        {
            var report = entry["report"] as JsonObject ?? [];
            var name = Ui.String(entry["computer"]) ?? "?";
            var shown = data.Agent(name) is { } a ? Ui.Name(a) : name;
            var agent = report["agent"] as JsonObject;
            var system = report["system"] as JsonObject;
            var status = report["status"] as JsonObject;
            text.AppendLine(CultureInfo.InvariantCulture, $"== {shown}{(shown == name ? "" : $" ({name})")} – zistené {When(entry["received"])} · program {Ui.String(report["version"]) ?? "?"}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Hodnotenie: {Verdict(report).Text}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Agent: súkromná {Mb(agent?["private_mb"])} · pracovná množina {Mb(agent?["working_set_mb"])} (najviac {Mb(agent?["peak_working_set_mb"])}) · halda .NET {Mb(agent?["heap_mb"])} (vyhradená {Mb(agent?["heap_committed_mb"])}) · pamäť učenia {Mb(agent?["native_mb"])}");
            text.AppendLine(CultureInfo.InvariantCulture, $"       vlákna {Ui.Number(agent?["threads"])} (fond {Ui.Number(agent?["pool_threads"])}) · popisovače {Ui.Number(agent?["handles"])} · zberov haldy {Collections(agent?["collections"])} · beží {Hours(agent?["up_hours"])}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Počítač: pamäť {Mb(system?["total_mb"])} · voľná {Mb(system?["available_mb"])} · použitá {Ui.Number(system?["used_percent"], "0", " %")} · potvrdená {Mb(system?["committed_mb"])} z {Mb(system?["commit_limit_mb"])} · Windows beží {Hours(report["system_up_hours"])}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Učenie: {Ui.String(status?["state"]) ?? "?"} · vlákna {Ui.Number(status?["threads"])} · čas {Ui.Number(status?["duty"], "0.00")} · riadkov od štartu {Ui.Number(report["lines_total"], "#,0")}");
            if (report["programs"] is JsonArray programs)
            {
                text.AppendLine("Najviac pamäte (súkromná / pracovná množina):");
                foreach (var program in programs.OfType<JsonObject>())
                {
                    var count = Ui.Value(program["count"]) is > 1 and var c ? $" ×{c:0}" : "";
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {Ui.String(program["name"])}{count}: {Mb(program["private_mb"])} / {Mb(program["working_set_mb"])}");
                }
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Saves a report where the user chooses; returns the file or null.</summary>
    public static string? Save(string report, string name)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"iris-diagnostika-{name}-{DateTime.Now:yyyy-MM-dd-HHmm}.txt",
            Filter = "Text (*.txt)|*.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        File.WriteAllText(dialog.FileName, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return dialog.FileName;
    }

    /// <summary>The memory of the agent and of the computer over the last days (from the server's history).</summary>
    public static async Task<UIElement> MemoryOverDays(IConsoleActions actions, string computer, double days)
    {
        var history = await actions.History(computer, days * 24, 60);
        var rows = history?["series"]?[computer]?.AsArray().OfType<JsonObject>().ToList()
            ?? history?["series"]?.AsObject().FirstOrDefault(p => string.Equals(p.Key, computer, StringComparison.OrdinalIgnoreCase)).Value?.AsArray().OfType<JsonObject>().ToList()
            ?? [];
        List<(DateTime Time, double Value)> Points(string key) =>
            [.. rows.Select(r => (Time: DateTime.TryParseExact(Ui.String(r["t"]), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue, Value: Ui.Value(r[key])))
                .Where(p => p.Time != DateTime.MinValue && double.IsFinite(p.Value))];
        var panel = new StackPanel();
        var agent = new LineChart { Title = $"Pamäť agenta za {days:0} dní (MB)", Unit = "MB", Height = 220, Series = [new ChartSeries("agent", Palette.For(0), Points("agent_mb"))] };
        var memory = new LineChart { Title = "Použitá pamäť počítača (%)", Unit = "%", Minimum = 0, Maximum = 100, Height = 200, Series = [new ChartSeries("počítač", Palette.For(2), Points("memory"))] };
        panel.Children.Add(agent);
        panel.Children.Add(memory);
        if (Points("agent_mb").Count == 0)
        {
            panel.Children.Add(Ui.Text("Pamäť agenta posielajú počítače od verzie 0.5.3 – krivka pribudne po aktualizácii.", 12, brush: "TextMuted"));
        }

        return panel;
    }

    private static string Mb(JsonNode? node)
    {
        var mb = Ui.Value(node);
        return double.IsFinite(mb) ? mb >= 1024 ? Gb(mb) : $"{mb:0} MB" : "–";
    }

    private static string Gb(double mb) => (mb / 1024).ToString(mb >= 10240 ? "0" : "0.0", CultureInfo.GetCultureInfo("sk-SK")) + " GB";

    private static string Hours(JsonNode? node)
    {
        var hours = Ui.Value(node);
        return !double.IsFinite(hours) ? "–" : hours < 48 ? $"{hours:0} h" : $"{hours / 24:0.#} dní".Replace('.', ',');
    }

    private static string Collections(JsonNode? node) =>
        node is JsonArray counts ? string.Join(" / ", counts.Select(c => Ui.Number(c))) : "–";

    private static string When(JsonNode? time) =>
        Ui.String(time) is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("d. M. yyyy HH:mm", CultureInfo.InvariantCulture)
            : "–";
}
