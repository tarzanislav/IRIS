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

            // the console: what the hub knows, and a command to one computer
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, hub.Port);
            using var console = await LabChannel.ConnectAsync(client.GetStream(), LabRole.Console, "console", LabKeys.Read(Path.Combine(folder, "console.key")), default);
            await console.SendAsync(new LabMessage(MessageKind.Snapshot, []), default);
            var snapshot = (await console.ReceiveAsync(default)).Header;
            Assert.Equal(2, snapshot["agents"]!.AsArray().Count, "the hub knows both computers");
            await console.SendAsync(new LabMessage(MessageKind.Command, new JsonObject { ["target"] = "PC-02", ["command"] = "shutdown" }), default);
            Assert.Equal(1, (await console.ReceiveAsync(default)).Header["sent"]!.GetValue<int>(), "the command went to one computer");
            await console.SendAsync(new LabMessage(MessageKind.Command, new JsonObject { ["target"] = "*", ["command"] = "format c:" }), default);
            Assert.Equal(0, (await console.ReceiveAsync(default)).Header["sent"]!.GetValue<int>(), "anything else is not a command");
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
