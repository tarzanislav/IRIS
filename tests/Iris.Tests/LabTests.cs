using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Iris.Lab;
using Iris.Vision.Training;

namespace Iris.Tests;

/// <summary>The classroom learning: the signed channel, the averaging, the schedule, and a hub with two computers.</summary>
public static class LabTests
{
    [Test]
    public static async Task ChannelSignsEveryMessage()
    {
        var agentKey = new byte[32];
        new Random(1).NextBytes(agentKey);
        var consoleKey = new byte[32];
        new Random(2).NextBytes(consoleKey);

        // a good connection: both ways, in order
        var (server, client) = await Pair(agentKey, agentKey, consoleKey);
        Assert.Equal(LabRole.Agent, server.PeerRole, "role");
        Assert.Equal("PC-07", server.PeerName, "name");
        await client.SendAsync(new LabMessage(MessageKind.Status, new JsonObject { ["cpu"] = 41.5 }, [1, 2, 3]), default);
        var received = await server.ReceiveAsync(default);
        Assert.Equal(MessageKind.Status, received.Kind, "kind");
        Assert.Equal(41.5, received.Header["cpu"]!.GetValue<double>(), "header");
        Assert.Sequence(new byte[] { 1, 2, 3 }, received.Data, "data");
        await server.SendAsync(new LabMessage(MessageKind.Command, new JsonObject { ["command"] = "pause" }), default);
        Assert.Equal("pause", (await client.ReceiveAsync(default)).Header["command"]!.GetValue<string>(), "command");
        server.Dispose();
        client.Dispose();

        // a wrong key never gets in; the console key does not open the agent's door
        await Assert.ThrowsAsync<LabProtocolException>(() => Pair(agentKey, consoleKey, consoleKey), "a wrong key");

        // a changed byte on the way ends the connection
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            var socket = await listener.AcceptTcpClientAsync();
            return await LabChannel.AcceptAsync(socket.GetStream(), _ => agentKey, default);
        });
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var tamper = new TamperStream(raw.GetStream());
        using var sender = await LabChannel.ConnectAsync(tamper, LabRole.Agent, "PC-07", agentKey, default);
        using var hub = await accept;
        tamper.FlipNextWrite = true;
        await sender.SendAsync(new LabMessage(MessageKind.Command, new JsonObject { ["command"] = "resume" }), default);
        await Assert.ThrowsAsync<LabProtocolException>(() => hub.ReceiveAsync(default), "a changed message");
        listener.Stop();
    }

    [Test]
    public static void AverageWeighsByLines()
    {
        var shape = OcrTrainingTests.Tiny();
        var weights = OcrWeights.Create(shape, 1);
        var before = weights.Copy();
        var tensors = TensorPack.Tensors(weights).ToList();
        var average = new RoundAverage(weights);
        average.Add([.. tensors.Select(t => Enumerable.Repeat(1f, t.Values.Length).ToArray())], lines: 300);
        average.Add([.. tensors.Select(t => Enumerable.Repeat(-1f, t.Values.Length).ToArray())], lines: 100);
        average.Add([.. tensors.Select(t => Enumerable.Repeat(99f, t.Values.Length).ToArray())], lines: 0); // learned nothing
        Assert.True(average.ApplyTo(weights), "applied");
        foreach (var (after, old) in TensorPack.Tensors(weights).Zip(TensorPack.Tensors(before)))
        {
            for (var i = 0; i < after.Values.Length; i++)
            {
                Assert.Near(old.Values[i] + 0.5, after.Values[i], 1e-5, after.Name);
            }
        }

        var (layout, data) = TensorPack.Pack(weights);
        var copy = new OcrWeights(shape);
        TensorPack.Unpack(layout, data, copy);
        Assert.Sequence(weights.OutWeight, copy.OutWeight, "packed and unpacked");
    }

    [Test]
    public static void ScheduleOpensAfternoonsNightsAndWeekends()
    {
        var s = LabSchedule.Default;
        var monday = new DateTime(2026, 11, 2);
        Assert.True(!s.IsOpen(monday.AddHours(10)), "Monday 10:00 – lessons");
        Assert.True(s.IsOpen(monday.AddHours(15)), "Monday 15:00");
        Assert.True(s.IsOpen(monday.AddHours(23.5)), "Monday 23:30");
        Assert.True(s.IsOpen(monday.AddDays(1).AddHours(5.9)), "Tuesday 5:54");
        Assert.True(!s.IsOpen(monday.AddDays(1).AddHours(6)), "Tuesday 6:00");
        Assert.True(s.IsOpen(monday.AddDays(5).AddHours(11)), "Saturday 11:00");
        Assert.True(!s.IsOpen(monday.AddDays(7).AddHours(6.5)), "next Monday 6:30");
        Assert.Equal(TimeSpan.FromHours(5), s.UntilChange(monday.AddHours(10)), "from 10:00 to the start at 15:00");
        Assert.Equal(TimeSpan.FromHours(3), s.UntilChange(monday.AddDays(7).AddHours(3)), "Monday 3:00 → 6:00");
        Assert.True(s.UntilChange(monday.AddDays(4).AddHours(15)).TotalHours > 62, "Friday afternoon → Monday morning");
    }

    [Test]
    public static async Task HubAndTwoComputersLearnTogether()
    {
        var fonts = new[] { "/usr/share/fonts/truetype/dejavu", @"C:\Windows\Fonts" }.Where(Directory.Exists).ToArray();
        if (fonts.Length == 0)
        {
            throw new SkipException("no fonts on this computer");
        }

        var folder = Path.Combine(Path.GetTempPath(), $"iris-lab-test-{Environment.ProcessId}");
        try
        {
            var shape = OcrTrainingTests.Tiny(8, 8);
            var weights = OcrWeights.Create(shape, 5);
            var start = weights.Copy();
            var options = new LabHubOptions
            {
                Folder = folder,
                Port = 0,
                RoundMinutes = 0.06, // 3.6 s
                GraceMinutes = 0.1,
                Duty = 1,
                CoreShare = 0.5,
                BatchSize = 8,
                Schedule = new LabSchedule(new TimeOnly(0, 0), new TimeOnly(0, 0), true), // always
                Fonts = fonts,
                TestEvery = 1,
                TestLines = 16,
            };
            var hub = new LabHub(options, weights, new OcrLearningState { PlanStart = 0, PlanEnd = 50, PlanRate = 1e-3 });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var hubTask = hub.RunAsync(stop.Token);
            while (hub.Port == 0)
            {
                await Task.Delay(50);
            }

            var key = LabKeys.Read(Path.Combine(folder, "agent.key"));
            var powered = new List<string>();
            var agents = new[] { "PC-01", "PC-02" }.Select(name => new LabAgent(new LabAgentOptions
            {
                Hub = "127.0.0.1",
                Port = hub.Port,
                Name = name,
                Key = key,
                Fonts = fonts,
                StatusSeconds = 1,
                OthersLimit = 101,
                Power = command => { lock (powered) { powered.Add(name + " " + command); } },
            }).RunAsync(stop.Token)).ToArray();

            while (hub.Version < 2 && !stop.IsCancellationRequested)
            {
                await Task.Delay(200);
            }

            Assert.True(hub.Version >= 2, $"two rounds were averaged (version {hub.Version})");
            var rounds = File.ReadAllLines(Path.Combine(folder, "rounds.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.True(rounds.Any(r => r["computers"]!.GetValue<int>() == 2), "a round with both computers");
            Assert.True(!hub.Weights.OutWeight.SequenceEqual(start.OutWeight), "the model changed");
            Assert.True(File.Exists(Path.Combine(folder, "iris-ocr.iris")) && File.Exists(LabHub.ModelPath(folder)), "the model is saved");

            // the console: what the hub knows, the history, and a command to one computer
            using var console = await LabConsoleClient.ConnectAsync("127.0.0.1", hub.Port, LabKeys.Read(Path.Combine(folder, "console.key")), default);
            var snapshot = await console.SnapshotAsync(default);
            Assert.Equal(2, snapshot["agents"]!.AsArray().Count, "the hub knows both computers");
            Assert.True(snapshot["agents"]!.AsArray().All(a => a!["status"]?["machine"]?["cores"] is not null), "with their measurements");
            for (var i = 0; i < 100 && (await console.SnapshotAsync(default))["tests"]!.AsArray().Count == 0; i++)
            {
                await Task.Delay(100);
            }

            var test = (await console.SnapshotAsync(default))["tests"]!.AsArray();
            Assert.True(test.Count > 0 && test[0]!["cer"]!.GetValue<double>() >= 0, "the hub measured how well the model reads");
            var history = await console.HistoryAsync(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(1), null, 1, default);
            Assert.True(history["series"]!.AsObject().Count == 2, "a history of both computers");
            Assert.Equal(1, await console.CommandAsync("PC-02", LabCommands.Shutdown, default), "the command went to one computer");
            Assert.Equal(0, await console.CommandAsync("*", "format c:", default), "anything else is not a command");
            for (var i = 0; i < 50 && powered.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            Assert.Sequence(["PC-02 shutdown"], powered, "the computer was told to shut down");
            stop.Cancel();
            await Task.WhenAll(agents.Append(hubTask));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public static async Task HubWakesTheComputersThatAreOffAndReturnsThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"iris-lab-wake-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "computers.json"), """
                {"computers":[{"name":"PC-01","mac":"00-11-22-33-44-01","address":"10.0.0.101"},{"name":"PC-02","mac":"00-11-22-33-44-02","address":"10.0.0.102"},{"name":"PC-03","address":"10.0.0.103"}],"woken":[]}
                """);
            var clock = new DateTime(2026, 11, 2, 14, 50, 0); // a Monday, before the time of learning
            var woken = new List<string>();
            var hub = new LabHub(new LabHubOptions
            {
                Folder = folder,
                Now = () => clock,
                Wake = (mac, _) =>
                {
                    woken.Add(mac);
                    return Task.CompletedTask;
                },
            }, OcrWeights.Create(OcrTrainingTests.Tiny(), 1), new OcrLearningState());
            await hub.Step(default);
            Assert.Equal(0, woken.Count, "nothing is woken during lessons");
            clock = clock.AddMinutes(11); // 15:01
            await hub.Step(default);
            Assert.Sequence(["00-11-22-33-44-01", "00-11-22-33-44-02"], woken.Order(), "both computers with a known card are woken");
            Assert.True(hub.Snapshot()["agents"]!.AsArray().Count(a => a!["woken"]!.GetValue<bool>()) == 2, "the hub remembers which it woke");
            clock = clock.AddMinutes(6);
            await hub.Step(default);
            Assert.Equal(4, woken.Count, "once more after five minutes for those still off");
            clock = new DateTime(2026, 11, 3, 6, 1, 0);
            await hub.Step(default);
            Assert.True(hub.Snapshot()["agents"]!.AsArray().All(a => !a!["woken"]!.GetValue<bool>()), "at the end of the time it forgets them");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public static void UpdatesAreBelievedOnlyWithTheSignature()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var program = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var (json, signature) = Manifest(key, "1.2.3", (UpdateManifest.Agent, program));
        var manifest = LabUpdates.Open(json, signature, publicKey);
        Assert.True(manifest is { Version: "1.2.3" }, "a signed manifest is believed");
        Assert.True(LabUpdates.Matches(manifest!.File(UpdateManifest.Agent)!, program), "and the file that matches it");
        var changed = (byte[])program.Clone();
        changed[100] ^= 1;
        Assert.True(!LabUpdates.Matches(manifest.File(UpdateManifest.Agent)!, changed), "a changed file is not");
        var forged = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(json).Replace("1.2.3", "9.2.3", StringComparison.Ordinal));
        Assert.True(LabUpdates.Open(forged, signature, publicKey) is null, "a changed manifest is not");
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.True(LabUpdates.Open(json, signature, other.ExportSubjectPublicKeyInfo()) is null, "another key is not");
        var (badJson, badSignature) = Manifest(key, "1.2.4", ("../evil.exe", program));
        Assert.True(LabUpdates.Open(badJson, badSignature, publicKey) is null, "a file name with a path is refused even when signed");
        Assert.True(LabUpdates.IsNewer("0.10.0", "0.9.9") && !LabUpdates.IsNewer("0.4.0", "0.4.0") && !LabUpdates.IsNewer("x", "0.1.0"), "versions compare as numbers");
    }

    [Test]
    public static async Task HubGivesTheComputersASignedProgram()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"iris-lab-update-{Environment.ProcessId}");
        var program = Path.Combine(folder, "agent", "iris-agent.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, "the old program");
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        LabUpdates.KeyOverride = key.ExportSubjectPublicKeyInfo();
        try
        {
            var agentProgram = Enumerable.Range(0, 70000).Select(i => (byte)(i % 251)).ToArray();
            var consoleProgram = Enumerable.Range(0, 30000).Select(i => (byte)(i % 13)).ToArray();
            Put(folder, key, "9.9.9", (UpdateManifest.Agent, agentProgram), (UpdateManifest.Console, consoleProgram));
            using var stranger = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            Put(folder, stranger, "10.0.0", (UpdateManifest.Agent, [1, 2, 3])); // newer, but not signed by the key
            var hub = new LabHub(new LabHubOptions
            {
                Folder = folder,
                Port = 0,
                Schedule = new LabSchedule(new TimeOnly(10, 0), new TimeOnly(10, 0), false), // never learning
                AutoWake = false,
            }, OcrWeights.Create(OcrTrainingTests.Tiny(), 1), new OcrLearningState());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var hubTask = hub.RunAsync(stop.Token);
            while (hub.Port == 0)
            {
                await Task.Delay(50);
            }

            var restarted = new TaskCompletionSource();
            var agent = new LabAgent(new LabAgentOptions
            {
                Hub = "127.0.0.1",
                Port = hub.Port,
                Name = "PC-07",
                Key = LabKeys.Read(Path.Combine(folder, "agent.key")),
                Fonts = [],
                StatusSeconds = 1,
                Program = program,
                Restart = () => restarted.TrySetResult(),
            }).RunAsync(stop.Token);
            await restarted.Task.WaitAsync(stop.Token);
            Assert.Sequence(agentProgram, File.ReadAllBytes(program), "the program of the computer is the signed one now");

            using var console = await LabConsoleClient.ConnectAsync("127.0.0.1", hub.Port, LabKeys.Read(Path.Combine(folder, "console.key")), default);
            var snapshot = await console.SnapshotAsync(default);
            Assert.Equal("9.9.9", snapshot["update"]?["version"]?.GetValue<string>(), "the console sees the signed version, not the stranger's");
            var (consoleManifest, data) = await console.ConsoleProgramAsync(default) ?? throw new InvalidOperationException("no console program");
            Assert.True(consoleManifest.Version == "9.9.9" && data.SequenceEqual(consoleProgram), "the console gets its program, checked");

            // the computer told what it is; the console names it and gives it its own settings
            for (var i = 0; i < 50 && (await console.SnapshotAsync(default))["agents"]![0]!["inventory"] is null; i++)
            {
                await Task.Delay(100);
            }

            Assert.True((await console.SnapshotAsync(default))["agents"]![0]!["inventory"]?["cpu"]?["logical"] is not null, "the inventory arrived");
            var config = await console.ConfigureAsync(JsonNode.Parse("""{"settings":{"scope":"computer","name":"PC-07","values":{"duty":0.25,"learn":false}}}""")!.AsObject(), default);
            Assert.True(config["settings"]?["computers"]?["PC-07"]?["duty"]?.GetValue<double>() == 0.25, "the hub keeps the setting");
            await console.ConfigureAsync(JsonNode.Parse("""{"alias":{"computer":"PC-07","name":"Okno 3"}}""")!.AsObject(), default);
            var agentInSnapshot = (await console.SnapshotAsync(default))["agents"]![0]!;
            Assert.True(agentInSnapshot["alias"]?.GetValue<string>() == "Okno 3" && agentInSnapshot["settings"]?["learn"]?.GetValue<bool>() == false, "the snapshot shows the name and the settings");
            for (var i = 0; i < 50 && (await console.SnapshotAsync(default))["agents"]![0]!["status"]?["duty"]?.GetValue<double>() != 0.25; i++)
            {
                await Task.Delay(100);
            }

            Assert.True((await console.SnapshotAsync(default))["agents"]![0]!["status"]?["duty"]?.GetValue<double>() == 0.25, "the computer works with its new share of time");
            stop.Cancel();
            await Task.WhenAll(hubTask, agent);
        }
        finally
        {
            LabUpdates.KeyOverride = null;
            Directory.Delete(folder, recursive: true);
        }
    }

    // a signed set of programs in the hub's folder of updates
    private static void Put(string folder, System.Security.Cryptography.ECDsa key, string version, params (string Name, byte[] Data)[] files)
    {
        var target = Path.Combine(folder, "updates", version);
        Directory.CreateDirectory(target);
        var (json, signature) = Manifest(key, version, files);
        File.WriteAllBytes(Path.Combine(target, UpdateManifest.ManifestName), json);
        File.WriteAllBytes(Path.Combine(target, UpdateManifest.SignatureName), signature);
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(target, name), data);
        }
    }

    private static (byte[] Json, byte[] Signature) Manifest(System.Security.Cryptography.ECDsa key, string version, params (string Name, byte[] Data)[] files)
    {
        var json = System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["version"] = version,
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode?)new JsonObject
            {
                ["name"] = f.Name,
                ["size"] = f.Data.Length,
                ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Data)),
            })]),
        }.ToJsonString());
        return (json, key.SignData(json, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
    }

    [Test]
    public static void FirmwareTablesAreRead()
    {
        // an invented computer: maker, model, service tag, BIOS, case, processor, two memory modules and an empty slot
        var system = new byte[23];
        system[0] = 1; // maker
        system[1] = 2; // model
        system[2] = 3; // version: a placeholder
        system[3] = 4; // serial
        for (var i = 0; i < 16; i++)
        {
            system[4 + i] = (byte)(i + 1);
        }

        system[22] = 5; // family
        var processor = new byte[0x2A - 4];
        processor[0x04 - 4] = 1;
        processor[0x07 - 4] = 2;
        processor[0x10 - 4] = 3;
        BitConverter.GetBytes((ushort)4800).CopyTo(processor, 0x14 - 4);
        processor[0x18 - 4] = 0x41; // populated, enabled
        processor[0x23 - 4] = 14;
        processor[0x25 - 4] = 20;
        byte[] Module(ushort megabytes, byte slot)
        {
            var m = new byte[0x22 - 4];
            BitConverter.GetBytes(megabytes).CopyTo(m, 0x0C - 4);
            m[0x10 - 4] = slot;
            m[0x12 - 4] = 0x1A; // DDR4
            BitConverter.GetBytes((ushort)3200).CopyTo(m, 0x15 - 4);
            m[0x1A - 4] = 2;
            BitConverter.GetBytes((ushort)3200).CopyTo(m, 0x20 - 4);
            return m;
        }

        var table = Concat(
            Structure(0, [1, 2, 0, 0, 3], "Example BIOS Ltd.", "1.23.0", "05/06/2026"),
            Structure(1, system, "Example Corp", "Desk 3000", "To Be Filled By O.E.M.", "TEST0001", "Desk"),
            Structure(3, [1, 6, 0, 2, 0], "Example Corp", "TEST0001"),
            Structure(4, processor, "SOCKET 0", "Example Chips", "Example Core 9 900"),
            Structure(17, Module(8192, 1), "DIMM 1", "PART-8G"),
            Structure(17, Module(0, 1), "DIMM 2"),
            Structure(17, Module(8192, 1), "DIMM 3", "PART-8G"),
            Structure(127, []));
        var json = SmBios.Parse(table);
        Assert.Equal("Example Corp", json["maker"]?.GetValue<string>(), "maker");
        Assert.Equal("Desk 3000", json["model"]?.GetValue<string>(), "model");
        Assert.Equal("TEST0001", json["serial"]?.GetValue<string>(), "service tag");
        Assert.True(json["version"] is null, "a placeholder of the firmware counts as missing");
        Assert.Equal("1.23.0", json["bios"]?["version"]?.GetValue<string>(), "BIOS");
        Assert.Equal("mini tower", json["chassis"]?["type"]?.GetValue<string>(), "case");
        Assert.Equal("Example Core 9 900", json["cpu"]?["name"]?.GetValue<string>(), "processor");
        Assert.Equal(14, json["cpu"]?["cores"]?.GetValue<int>() ?? 0, "cores");
        Assert.Equal(20, json["cpu"]?["threads"]?.GetValue<int>() ?? 0, "threads");
        var modules = json["memory_modules"]!.AsArray();
        Assert.Equal(2, modules.Count, "two modules, the empty slot left out");
        Assert.True(modules.All(m => m!["size_gb"]!.GetValue<double>() == 8 && m["type"]!.GetValue<string>() == "DDR4" && m["speed"]!.GetValue<int>() == 3200), "8 GB DDR4-3200 each");
        Assert.True(json["uuid"]?.GetValue<string>() is { Length: 36 }, "the UUID");
        Assert.True(SmBios.Parse([1, 2, 3]).Count == 0 && SmBios.Parse(Structure(1, [9, 9], "x")).Count == 0, "broken tables give nothing, no exception");
        var here = Inventory.Collect();
        Assert.True(here["cpu"]?["logical"]?.GetValue<int>() == Environment.ProcessorCount, "the inventory of this computer has its processor");

        static byte[] Structure(byte type, byte[] fields, params string[] strings)
        {
            var bytes = new List<byte> { type, (byte)(4 + fields.Length), 0, 0 };
            bytes.AddRange(fields);
            foreach (var text in strings)
            {
                bytes.AddRange(System.Text.Encoding.Latin1.GetBytes(text));
                bytes.Add(0);
            }

            bytes.Add(0);
            if (strings.Length == 0)
            {
                bytes.Add(0);
            }

            return [.. bytes];
        }

        static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
    }

    [Test]
    public static void SettingsGoFromAllToTheGroupToTheComputer()
    {
        var config = new LabConfig();
        var defaults = new JsonObject { ["duty"] = 0.5, ["cores"] = 0.5, ["eco"] = true, ["hot"] = 90.0, ["learn"] = true };
        Assert.True(config.Apply(JsonNode.Parse("""{"settings":{"scope":"all","values":{"duty":0.7,"cores":3,"evil":"rm -rf"}}}""")!.AsObject()), "all");
        Assert.True(config.Apply(JsonNode.Parse("""{"settings":{"scope":"group","name":"Desk 3000","values":{"cores":0.25,"eco":false}}}""")!.AsObject()), "a hardware group");
        Assert.True(config.Apply(JsonNode.Parse("""{"settings":{"scope":"computer","name":"PC-02","values":{"duty":0.3}}}""")!.AsObject()), "one computer");
        Assert.True(config.Apply(JsonNode.Parse("""{"alias":{"computer":"PC-02","name":"Miesto 2"}}""")!.AsObject()), "a name");
        Assert.True(!config.Apply(JsonNode.Parse("""{"command":"format"}""")!.AsObject()), "nothing else");
        var first = config.Effective(defaults, "PC-01", "Desk 3000");
        Assert.True(first["duty"]!.GetValue<double>() == 0.7 && first["cores"]!.GetValue<double>() == 0.25 && !first["eco"]!.GetValue<bool>(), "PC-01: all, then its model's group");
        Assert.True(first["evil"] is null, "unknown settings are dropped");
        var second = config.Effective(defaults, "PC-02", "Desk 3000");
        Assert.True(second["duty"]!.GetValue<double>() == 0.3 && second["cores"]!.GetValue<double>() == 0.25, "PC-02: its own share of time wins");
        Assert.True(config.Effective(defaults, "PC-09", "Other 1")["cores"]!.GetValue<double>() == 1, "cores are held at most 1 (100 %)");
        config.Apply(JsonNode.Parse("""{"group":{"computer":"PC-01","name":"Rad A"}}""")!.AsObject());
        Assert.Equal("Rad A", config.GroupOf("PC-01", "Desk 3000"), "a group set by hand wins over the model");
        Assert.True(config.Effective(defaults, "PC-01", "Desk 3000")["eco"]!.GetValue<bool>(), "and the model's settings no longer apply");
        var path = Path.Combine(Path.GetTempPath(), $"iris-lab-config-{Environment.ProcessId}.json");
        try
        {
            config.Save(path);
            var again = LabConfig.Load(path);
            Assert.Equal("Miesto 2", again.Aliases["PC-02"], "names are kept");
            Assert.True(again.Effective(defaults, "PC-02", "Desk 3000").ToJsonString() == second.ToJsonString(), "settings are kept");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void NetworkNamesAndCardsAreRead()
    {
        Assert.Sequence(new byte[] { 0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E }, NetworkScanner.ParseMac("00-1a-2b-3c-4d-5e")!, "a MAC with dashes");
        Assert.Sequence(new byte[] { 0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E }, NetworkScanner.ParseMac("00:1A:2B:3C:4D:5E")!, "a MAC with colons");
        Assert.True(NetworkScanner.ParseMac("00-1a-2b") is null, "too short");
        Assert.True(new NetworkDevice("10.0.0.5", "DA-A1-19-00-00-01", null, null, null, null).LocalMac, "a private (software) MAC");
        Assert.True(!new NetworkDevice("10.0.0.5", "00-1A-2B-3C-4D-5E", null, null, null, null).LocalMac, "a maker's MAC");

        // a NetBIOS node status answer with the computer name and the workgroup
        var answer = new byte[12 + 34 + 10 + 1 + 3 * 18 + 6];
        answer[56] = 3;
        void Name(int i, string text, byte type, bool group)
        {
            var at = 57 + 18 * i;
            System.Text.Encoding.ASCII.GetBytes(text.PadRight(15)).CopyTo(answer, at);
            answer[at + 15] = type;
            answer[at + 16] = group ? (byte)0x80 : (byte)0;
        }

        Name(0, "UCEBNA-PC07", 0x00, false);
        Name(1, "SKOLA", 0x00, true);
        Name(2, "UCEBNA-PC07", 0x20, false);
        Assert.Equal(("UCEBNA-PC07", (string?)"SKOLA"), NetworkScanner.ParseNodeStatus(answer)!.Value, "name and workgroup");
    }

    private static async Task<(LabChannel Server, LabChannel Client)> Pair(byte[] serverAgentKey, byte[] clientKey, byte[] serverConsoleKey)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = Task.Run(async () =>
            {
                var socket = await listener.AcceptTcpClientAsync();
                return await LabChannel.AcceptAsync(socket.GetStream(), role => role == LabRole.Agent ? serverAgentKey : serverConsoleKey, default);
            });
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var connect = LabChannel.ConnectAsync(tcp.GetStream(), LabRole.Agent, "PC-07", clientKey, default);
            try
            {
                var server = await accept;
                return (server, await connect);
            }
            catch (LabProtocolException)
            {
                tcp.Dispose();
                throw;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    // flips a bit of the next write, as a fault or an attacker on the way would
    private sealed class TamperStream(Stream inner) : Stream
    {
        public bool FlipNextWrite { get; set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FlipNextWrite && buffer.Length > 20)
            {
                FlipNextWrite = false;
                var copy = buffer.ToArray();
                copy[20] ^= 0x01;
                return inner.WriteAsync(copy, cancellationToken);
            }

            return inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
