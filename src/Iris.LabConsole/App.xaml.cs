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

        Iris.Lab.LabUpdates.RemoveOld(Environment.ProcessPath); // what an update of the console left behind
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
            var window = new MainWindow(new ConsoleSettings(), offline: true) { Width = 1320, Height = 860, WindowState = WindowState.Normal };
            Fill(window.Data);
            window.Server.Fake(new JsonObject
            {
                ["hub_version"] = Iris.Lab.LabUpdates.Version,
                ["system"] = "Debian GNU/Linux 13",
                ["started"] = DateTime.Now.AddHours(-26).ToString("o", CultureInfo.InvariantCulture),
                ["now"] = DateTimeOffset.Now.AddMinutes(129).ToString("o", CultureInfo.InvariantCulture),
                ["uptime_minutes"] = 26 * 60 + 14,
                ["model"] = new JsonObject { ["version"] = 83142, ["minutes"] = 26300, ["rate"] = 1e-4, ["plan_rounds"] = 600, ["plan_done"] = 62, ["source"] = "IRIS lab: learned on the computers of a classroom", ["file_mb"] = 29.7 },
                ["disk"] = new JsonObject { ["free_mb"] = 81234, ["total_mb"] = 117000 },
                ["memory_mb"] = 412,
                ["processors"] = 4,
                ["fonts"] = 0,
                ["update_waiting"] = false,
                ["last_update"] = new JsonObject
                {
                    ["time"] = DateTimeOffset.Now.AddHours(-3).ToString("o", CultureInfo.InvariantCulture),
                    ["from"] = "0.4.2",
                    ["newest"] = "0.4.3",
                    ["installed"] = "0.4.3",
                    ["model_asked"] = true,
                    ["model"] = "0.4.3",
                    ["ok"] = true,
                    ["message"] = "the hub started again",
                },
            }, Enumerable.Range(0, 60).Select(i => $"{DateTime.Now.AddMinutes(i - 60):yyyy-MM-dd HH:mm:ss}  " + (i % 10 == 0
                ? $"round {i / 10 + 40} closed: 8 computers, 190000 lines, loss 0.2{i % 7}31, model version {83100 + i / 10}"
                : i % 10 == 1 ? $"round {i / 10 + 41} opened (model version {83100 + i / 10}, rate 9.8e-5)" : $"PC-{i % 18 + 1:00} connected from 10.0.0.{100 + i % 18}")));
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
                Picture(window, folder, page);
            }

            // a computer opened beside the list, and the dark look
            window.Open("PC-03");
            Picture(window, folder, "computers-detail");
            window.SetTheme("dark");
            window.Navigate("overview");
            Picture(window, folder, "overview-dark");
            window.Open("PC-03");
            Picture(window, folder, "computers-dark");
            window.Navigate("server");
            Picture(window, folder, "server-dark");
            window.Navigate("test");
            Picture(window, folder, "test-dark");
            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    // a picture of the window's content at the size of a usual screen (the runner's screen is smaller than the window)
    private static void Picture(Window window, string folder, string name)
    {
        const int Width = 1360, Height = 880;
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        for (var pass = 0; pass < 2; pass++)
        {
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            Ui.SizeBars(root);
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, Width, Height));
        }

        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, $"console-{name}.png"));
        encoder.Save(file);
    }

    // a made-up classroom: names PC-01 … PC-18, two invented models, nothing real
    private static void Fill(ConsoleData data)
    {
        var random = new Random(3);
        var now = DateTime.Now;
        var agents = new JsonArray();
        var series = new JsonObject();
        for (var i = 1; i <= 18; i++)
        {
            var name = $"PC-{i:00}";
            var state = i switch { 4 => "paused", 9 => "gone", 10 => "uninstalled", 13 => "waiting", 15 => "leaving", _ => "learning" };
            var cpu = 38 + random.NextDouble() * 20;
            var model = i <= 12 ? "Example Desk 3000" : "Example Desk 5000";
            agents.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["alias"] = i == 3 ? "Miesto pri okne" : null,
                ["group"] = model,
                ["model"] = model,
                ["version"] = i == 7 ? "0.3.9" : "0.4.0",
                ["settings"] = new JsonObject { ["duty"] = 0.5, ["cores"] = i <= 12 ? 0.5 : 0.25, ["eco"] = true, ["hot"] = 90.0, ["learn"] = true, ["wake"] = true, ["update"] = true },
                ["inventory"] = Inventory(i, model),
                ["address"] = $"10.0.0.{100 + i}",
                ["mac"] = $"00-11-22-33-44-{i:X2}",
                ["woken"] = i % 3 == 0,
                ["test"] = i <= 6,
                ["on"] = i == 9 ? true : null,
                ["connected"] = state is not "gone" and not "uninstalled",
                ["state"] = state,
                ["leaving"] = state == "leaving",
                ["seen"] = now.AddSeconds(-random.Next(2, 30)).ToString("o", CultureInfo.InvariantCulture),
                ["rounds"] = 40 + i,
                ["lines"] = 120_000 + i * 3_100,
                ["status"] = new JsonObject
                {
                    ["state"] = state,
                    ["eco"] = true,
                    ["threads"] = i <= 12 ? 10 : 6,
                    ["too_hot"] = i == 16,
                    ["machine"] = new JsonObject { ["cpu"] = Math.Round(cpu), ["cpu_own"] = Math.Round(cpu - 4), ["memory"] = 41, ["temperature"] = i == 16 ? 91 : 46 + i % 7 * 3, ["cores"] = 20 },
                    ["last"] = new JsonObject { ["lines_per_second"] = Math.Round(11 + random.NextDouble() * 4, 1) },
                    ["strain"] = new JsonObject
                    {
                        ["level"] = i is 5 or 16 ? 2 : i is 2 or 11 ? 1 : 0,
                        ["reasons"] = i switch
                        {
                            5 => new JsonArray((JsonNode)new JsonObject { ["code"] = "slow", ["value"] = 64 }, new JsonObject { ["code"] = "learning", ["value"] = 58 }),
                            16 => new JsonArray((JsonNode)new JsonObject { ["code"] = "limit", ["value"] = 71 }),
                            2 => new JsonArray((JsonNode)new JsonObject { ["code"] = "others", ["value"] = 42 }),
                            11 => new JsonArray((JsonNode)new JsonObject { ["code"] = "queue", ["value"] = 26 }),
                            _ => new JsonArray(),
                        },
                    },
                    ["signals"] = new JsonObject { ["performance"] = i == 5 ? 68 : 104, ["usual_performance"] = 106, ["limit"] = i == 16 ? 71 : 100, ["queue"] = i == 11 ? 26 : 1, ["free_mb"] = 9200, ["disk"] = 3, ["temperature_fixed"] = i <= 4 },
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
                    ["strain"] = night && i == 5 && t.Hour is >= 1 and < 4 ? 2 : night && i % 4 == 0 ? random.NextDouble() : 0,
                    ["performance"] = night ? (i == 5 && t.Hour is >= 1 and < 4 ? 66 : 100 + random.NextDouble() * 6) : 99,
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
            ["test"] = new JsonObject
            {
                ["started"] = now.AddMinutes(-4).ToString("o", CultureInfo.InvariantCulture),
                ["until"] = now.AddMinutes(16).ToString("o", CultureInfo.InvariantCulture),
                ["computers"] = new JsonArray([.. Enumerable.Range(1, 6).Select(i => (JsonNode?)$"PC-{i:00}")]),
                ["duty"] = 1.0,
                ["cores"] = 1.0,
                ["eco"] = false,
                ["console"] = "notebook-skola",
            },
            ["auto_wake"] = true,
            ["schedule"] = new JsonObject { ["from"] = "15:00", ["until"] = "06:00", ["weekends"] = true },
            ["round"] = new JsonObject { ["number"] = 61, ["deadline"] = now.AddMinutes(7).ToString("o", CultureInfo.InvariantCulture), ["taking_part"] = 16, ["done"] = 3 },
            ["agents"] = agents,
            ["rounds"] = rounds,
            ["tests"] = tests,
            ["minutes"] = 6600,
            ["hub_version"] = Iris.Lab.LabUpdates.Version,
            ["consoles"] = new JsonArray("notebook-skola", "notebook-praca"),
            ["update"] = new JsonObject { ["version"] = "0.4.1", ["auto"] = true, ["agent"] = true, ["console"] = true },
            ["config"] = new JsonObject
            {
                ["aliases"] = new JsonObject { ["PC-03"] = "Miesto pri okne" },
                ["groups"] = new JsonObject(),
                ["settings"] = new JsonObject
                {
                    ["all"] = new JsonObject { ["duty"] = 0.5 },
                    ["groups"] = new JsonObject { ["Example Desk 5000"] = new JsonObject { ["cores"] = 0.25 } },
                    ["computers"] = new JsonObject { ["PC-03"] = new JsonObject { ["hot"] = 85.0 } },
                },
                ["defaults"] = new JsonObject { ["duty"] = 0.5, ["cores"] = 0.5, ["eco"] = true, ["hot"] = 90.0, ["learn"] = true, ["wake"] = true, ["update"] = true },
            },
        };
        data.History = new JsonObject { ["series"] = series, ["bucket"] = 5 };
    }

    // what an invented computer is (the shape the agents send)
    private static JsonObject Inventory(int i, string model) => new()
    {
        ["maker"] = "Example Corp",
        ["model"] = model,
        ["serial"] = $"EXAMPLE{i:00}",
        ["chassis"] = new JsonObject { ["type"] = "mini tower" },
        ["bios"] = new JsonObject { ["version"] = "1.20.0", ["date"] = "05/06/2026" },
        ["cpu"] = new JsonObject
        {
            ["name"] = i <= 12 ? "Example Core 5 500 (14 cores)" : "Example Core 3 300",
            ["cores"] = i <= 12 ? 14 : 6,
            ["performance_cores"] = i <= 12 ? 6 : null,
            ["efficient_cores"] = i <= 12 ? 8 : null,
            ["logical"] = i <= 12 ? 20 : 12,
            ["avx2"] = true,
        },
        ["memory_gb"] = 16,
        ["memory_modules"] = new JsonArray(
            new JsonObject { ["size_gb"] = 8, ["type"] = "DDR5", ["speed"] = 4800 },
            new JsonObject { ["size_gb"] = 8, ["type"] = "DDR5", ["speed"] = 4800 }),
        ["disks"] = new JsonArray(new JsonObject { ["model"] = "Example NVMe 512", ["bus"] = "NVMe", ["size_gb"] = 512 }),
        ["gpu"] = new JsonArray("Example Graphics 770"),
        ["os"] = new JsonObject { ["name"] = "Windows 11 Education", ["version"] = "24H2", ["build"] = "26100.2314" },
        ["network"] = new JsonArray(new JsonObject { ["name"] = "Example Ethernet Connection", ["mbps"] = 1000, ["mac"] = $"00-11-22-33-44-{i:X2}" }),
    };
}
