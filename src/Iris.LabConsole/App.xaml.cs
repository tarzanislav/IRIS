using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Iris.LabConsole;

/// <summary>The console of the classroom learning.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--selftest", var folder])
        {
            Shutdown(SelfTest(folder));
            return;
        }

        new MainWindow(ConsoleSettings.Load()).Show();
    }

    /// <summary>
    /// Builds every page with invented data (a classroom of 18 computers, a day of history, rounds, tests) and saves
    /// a picture of each into the folder – CI checks that all of it builds and keeps the pictures to look at.
    /// </summary>
    private static int SelfTest(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var window = new MainWindow(new ConsoleSettings(), offline: true) { Width = 1320, Height = 860 };
            Fill(window.Data);
            window.Network.Found = new JsonObject
            {
                ["time"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                ["devices"] = new JsonArray([.. Enumerable.Range(1, 22).Select(i => (JsonNode?)new JsonObject
                {
                    ["address"] = $"10.0.0.{(i <= 18 ? 100 + i : 200 + i)}",
                    ["mac"] = $"00-11-22-33-44-{i:X2}",
                    ["dns"] = i <= 18 ? $"pc-{i:00}.ucebna.local" : null,
                    ["netbios"] = i <= 18 ? $"PC-{i:00}" : null,
                    ["workgroup"] = i <= 18 ? "UCEBNA" : null,
                    ["ping"] = i % 5 == 0 ? null : 0.4 + i * 0.03,
                    ["local_mac"] = false,
                    ["computer"] = i <= 18 ? $"PC-{i:00}" : null,
                })]),
            };
            window.Show();
            foreach (var page in window.PageNames.ToList())
            {
                window.Navigate(page);
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Ui.SizeBars(window);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, $"console-{page}.png"));
                encoder.Save(file);
            }

            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    // a made-up classroom: names PC-01 … PC-18, nothing real
    private static void Fill(ConsoleData data)
    {
        var random = new Random(3);
        var now = DateTime.Now;
        var agents = new JsonArray();
        var series = new JsonObject();
        for (var i = 1; i <= 18; i++)
        {
            var name = $"PC-{i:00}";
            var state = i switch { 4 => "paused", 9 => "gone", 13 => "waiting", _ => "learning" };
            var cpu = 38 + random.NextDouble() * 20;
            agents.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["address"] = $"10.0.0.{100 + i}",
                ["mac"] = $"00-11-22-33-44-{i:X2}",
                ["woken"] = i % 3 == 0,
                ["connected"] = state != "gone",
                ["state"] = state,
                ["seen"] = now.AddSeconds(-random.Next(2, 30)).ToString("o", CultureInfo.InvariantCulture),
                ["rounds"] = 40 + i,
                ["lines"] = 120_000 + i * 3_100,
                ["status"] = new JsonObject
                {
                    ["state"] = state,
                    ["machine"] = new JsonObject { ["cpu"] = Math.Round(cpu), ["cpu_own"] = Math.Round(cpu - 4), ["memory"] = 41, ["temperature"] = 46 + i % 7 * 3, ["cores"] = 20 },
                    ["last"] = new JsonObject { ["lines_per_second"] = Math.Round(11 + random.NextDouble() * 4, 1) },
                },
            });
            var points = new JsonArray();
            for (var m = 12 * 60; m >= 0; m -= 5)
            {
                var t = now.AddMinutes(-m);
                var night = t.Hour >= 15 || t.Hour < 6;
                points.Add((JsonNode)new JsonObject
                {
                    ["t"] = t.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                    ["cpu"] = night ? 40 + random.NextDouble() * 15 : 8 + random.NextDouble() * 10,
                    ["own"] = night ? 36 + random.NextDouble() * 12 : 0,
                    ["temperature"] = night ? 52 + i % 7 * 2 + random.NextDouble() * 4 : 38 + random.NextDouble() * 3,
                    ["lps"] = night ? 11 + random.NextDouble() * 4 : 0,
                });
            }

            series[name] = points;
        }

        var rounds = new JsonArray();
        var tests = new JsonArray();
        for (var r = 1; r <= 60; r++)
        {
            rounds.Add((JsonNode)new JsonObject
            {
                ["round"] = r,
                ["version"] = 1000 + r,
                ["computers"] = 16 + r % 3,
                ["lines"] = 160_000 + r * 900,
                ["loss"] = Math.Round(0.12 * Math.Exp(-r / 40.0) + 0.06 + random.NextDouble() * 0.01, 4),
                ["rate"] = 3e-4 * (0.5 + 0.5 * Math.Cos(Math.PI * r / 120)),
                ["closed"] = now.AddMinutes(-(60 - r) * 11).ToString("o", CultureInfo.InvariantCulture),
            });
            if (r % 6 == 0)
            {
                tests.Add((JsonNode)new JsonObject { ["version"] = 1000 + r, ["cer"] = Math.Round(1.4 - r * 0.006, 2), ["cer_unseen"] = Math.Round(1.1 - r * 0.004, 2) });
            }
        }

        data.Snapshot = new JsonObject
        {
            ["version"] = 1060,
            ["open"] = true,
            ["auto_wake"] = true,
            ["schedule"] = new JsonObject { ["from"] = "15:00", ["until"] = "06:00", ["weekends"] = true },
            ["round"] = new JsonObject { ["number"] = 61, ["deadline"] = now.AddMinutes(7).ToString("o", CultureInfo.InvariantCulture), ["taking_part"] = 16, ["done"] = 3 },
            ["agents"] = agents,
            ["rounds"] = rounds,
            ["tests"] = tests,
        };
        data.History = new JsonObject { ["series"] = series, ["bucket"] = 5 };
    }
}
