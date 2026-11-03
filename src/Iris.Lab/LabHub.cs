using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Vision.Training;

namespace Iris.Lab;

/// <summary>How the hub runs a classroom.</summary>
public sealed record LabHubOptions
{
    public int Port { get; init; } = 47620;

    /// <summary>The folder of the hub: the model, the rounds, the keys.</summary>
    public required string Folder { get; init; }

    /// <summary>A second place for a copy of the model after each round (e.g. the shared folder), or null.</summary>
    public string? Backup { get; init; }

    /// <summary>Minutes of one round (every computer learns this long, then the changes are averaged).</summary>
    public double RoundMinutes { get; init; } = 10;

    /// <summary>Minutes after the deadline that the hub waits for late computers.</summary>
    public double GraceMinutes { get; init; } = 3;

    public LabSchedule Schedule { get; init; } = LabSchedule.Default;

    /// <summary>What the computers get: share of time working, share of cores, batch size.</summary>
    public double Duty { get; init; } = 0.5;

    public double CoreShare { get; init; } = 0.5;

    public int BatchSize { get; init; } = 32;

    /// <summary>The highest rate of a new plan, and the plan in rounds (the rate falls along them by a cosine).</summary>
    public double Rate { get; init; } = 3e-4;

    public int PlanRounds { get; init; } = 600;

    /// <summary>Sentences for the lines (sk.txt, cs.txt, en.txt), sent to every computer; may be null.</summary>
    public string? Sentences { get; init; }

    /// <summary>Folders with fonts on the hub: with them it measures how well the model reads every few rounds (CER).</summary>
    public IReadOnlyList<string>? Fonts { get; init; }

    /// <summary>Every how many rounds the hub tests the model, and on how many lines.</summary>
    public int TestEvery { get; init; } = 6;

    public int TestLines { get; init; } = 300;

    /// <summary>
    /// Wakes the known computers that are off when the time of learning begins (Wake-on-LAN), and at its end shuts
    /// down those it woke – a computer that was on stays on.
    /// </summary>
    public bool AutoWake { get; init; } = true;

    /// <summary>How a computer is woken (tests replace it; by default the magic packet).</summary>
    public Func<string, CancellationToken, Task> Wake { get; init; } = (mac, cancel) => NetworkScanner.WakeAsync(mac, cancel);

    /// <summary>The clock (tests set their own).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
}

/// <summary>
/// The hub of the classroom learning: keeps the model, opens rounds while the schedule allows it, sends every
/// computer the model of the round with the rate and the deadline, collects what each learned and averages it into
/// the next model (weighted by lines), saves the model after each round (also to the backup folder), and tells the
/// console everything it knows. Computers come and go as they like: a round closes when all who took part have sent
/// their change, or a few minutes after the deadline with those who did.
/// </summary>
public sealed class LabHub
{
    private readonly LabHubOptions _options;
    private readonly byte[] _agentKey;
    private readonly byte[] _consoleKey;
    private readonly ConcurrentDictionary<string, AgentEntry> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly List<JsonObject> _rounds = [];
    private readonly Dictionary<string, string[]> _sentences;
    private readonly LabHistory _history;
    private readonly List<JsonObject> _tests = [];
    private readonly Lazy<(OcrNetwork Network, List<TrainingLine> Known, List<TrainingLine> Unseen)?> _tester;
    private OcrWeights _weights;
    private OcrLearningState _state;
    private readonly HashSet<string> _woken = new(StringComparer.OrdinalIgnoreCase);
    private Round? _round;
    private TcpListener? _listener;
    private bool? _wasOpen;
    private DateTime _openedAt;
    private DateTime _lastWake;

    public LabHub(LabHubOptions options, OcrWeights weights, OcrLearningState state)
    {
        _options = options;
        Directory.CreateDirectory(options.Folder);
        _agentKey = LabKeys.ReadOrCreate(Path.Combine(options.Folder, "agent.key"));
        _consoleKey = LabKeys.ReadOrCreate(Path.Combine(options.Folder, "console.key"));
        _weights = weights;
        _state = state;
        _sentences = LineTexts.ReadSentences(options.Sentences);
        _history = new LabHistory(Path.Combine(options.Folder, "history"));
        _tester = new(MakeTester);
        var tests = Path.Combine(options.Folder, "tests.jsonl");
        if (File.Exists(tests))
        {
            _tests.AddRange(File.ReadLines(tests).TakeLast(200).Select(l => JsonNode.Parse(l) as JsonObject).OfType<JsonObject>());
        }

        LoadKnown();
        var log = Path.Combine(options.Folder, "rounds.jsonl");
        if (File.Exists(log))
        {
            _rounds.AddRange(File.ReadLines(log).TakeLast(200).Select(l => JsonNode.Parse(l) as JsonObject).OfType<JsonObject>());
        }
    }

    /// <summary>The model file the hub keeps (iris-ocr-train) in its folder.</summary>
    public static string ModelPath(string folder) => Path.Combine(folder, "lab-model.iris");

    /// <summary>The port it listens on (useful when 0 was asked for).</summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _options.Port;

    public int Version => _state.Step;

    public OcrWeights Weights => _weights;

    /// <summary>Starts listening; returns when cancelled.</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Start();
        var timer = Task.Run(() => Tick(cancel), cancel);
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancel);
                _ = Task.Run(() => Serve(client, cancel), cancel);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
            try
            {
                await timer;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Everything the hub knows now (for the console).</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var now = _options.Now();
            var agents = new JsonArray();
            foreach (var a in _agents.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                agents.Add((JsonNode)new JsonObject
                {
                    ["name"] = a.Name,
                    ["address"] = a.Address,
                    ["mac"] = a.Mac,
                    ["woken"] = _woken.Contains(a.Name),
                    ["connected"] = a.Channel is not null,
                    ["state"] = a.State,
                    ["seen"] = a.Seen.ToString("o", CultureInfo.InvariantCulture),
                    ["rounds"] = a.Rounds,
                    ["lines"] = a.Lines,
                    ["status"] = a.Status?.DeepClone(),
                });
            }

            return new JsonObject
            {
                ["version"] = _state.Step,
                ["minutes"] = Math.Round(_state.Minutes),
                ["open"] = _options.Schedule.IsOpen(now),
                ["auto_wake"] = _options.AutoWake,
                ["schedule"] = _options.Schedule.ToJson(),
                ["round"] = _round is null ? null : new JsonObject
                {
                    ["number"] = _round.Number,
                    ["deadline"] = _round.Deadline.ToString("o", CultureInfo.InvariantCulture),
                    ["rate"] = _round.Rate,
                    ["taking_part"] = _round.Participants.Count,
                    ["done"] = _round.Done.Count,
                },
                ["agents"] = agents,
                ["rounds"] = new JsonArray([.. _rounds.TakeLast(200).Select(r => (JsonNode?)r.DeepClone())]),
                ["tests"] = new JsonArray([.. _tests.TakeLast(200).Select(r => (JsonNode?)r.DeepClone())]),
            };
        }
    }

    // the clock of rounds: opens a round when computers wait, closes it when all are done or the time is over
    private async Task Tick(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await Step(cancel);
            }
            catch (Exception e) when (e is IOException or LabProtocolException or ObjectDisposedException)
            {
                // one computer failing does not stop the hub
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, _options.RoundMinutes * 60 / 10)), cancel);
        }
    }

    internal async Task Step(CancellationToken cancel)
    {
        var now = _options.Now();
        List<AgentEntry> toStart = [];
        List<AgentEntry> toWait = [];
        List<string> toWake = [];
        List<AgentEntry> toReturn = [];
        lock (_gate)
        {
            var open = _options.Schedule.IsOpen(now);
            if (_options.AutoWake)
            {
                if (open && _wasOpen == false)
                {
                    // the time of learning begins: wake every known computer that is off, and remember which
                    _openedAt = now;
                    _lastWake = now;
                    foreach (var a in _agents.Values.Where(a => a.Channel is null && a.Mac is not null))
                    {
                        _woken.Add(a.Name);
                        toWake.Add(a.Mac!);
                    }

                    SaveKnown();
                }
                else if (open && now - _openedAt < TimeSpan.FromMinutes(30) && now - _lastWake > TimeSpan.FromMinutes(5))
                {
                    // once more every five minutes for those that did not wake yet
                    _lastWake = now;
                    toWake.AddRange(_agents.Values.Where(a => _woken.Contains(a.Name) && a.Channel is null && a.Mac is not null).Select(a => a.Mac!));
                }
                else if (!open && _wasOpen == true)
                {
                    // the time of learning ends: the computers that were off go off again
                    toReturn.AddRange(_agents.Values.Where(a => _woken.Contains(a.Name) && a.Channel is not null));
                    _woken.Clear();
                    SaveKnown();
                }
            }

            _wasOpen = open;
            // a round is over when no connected computer learns for it any more, or a few minutes after its deadline
            if (_round is { } round && (!_agents.Values.Any(a => a.Channel is not null && a.State == "learning") ||
                                        now > round.Deadline.AddMinutes(_options.GraceMinutes)))
            {
                Close(round, now);
            }

            var idle = _agents.Values.Where(a => a.Channel is not null && a.State is "idle" or "waiting").ToList();
            if (open && _round is null && idle.Count > 0)
            {
                var number = (_rounds.LastOrDefault()?["round"]?.GetValue<int>() ?? 0) + 1;
                var phase = Math.Clamp((_state.Step - _state.PlanStart) / Math.Max(1, _state.PlanEnd - _state.PlanStart), 0, 1);
                var rate = _state.PlanRate * (0.02 + 0.98 * 0.5 * (1 + Math.Cos(Math.PI * phase)));
                _round = new Round(number, now.AddMinutes(_options.RoundMinutes), rate, TensorPack.Pack(_weights), new RoundAverage(_weights));
            }

            foreach (var a in idle)
            {
                // a computer joins the round (or, after a new connection, joins it again) while a third of it is left
                if (open && _round is { } r && !r.Done.Contains(a.Name) && r.Deadline - now > TimeSpan.FromMinutes(_options.RoundMinutes / 3))
                {
                    r.Participants.Add(a.Name);
                    a.State = "learning";
                    toStart.Add(a);
                }
                else if (!open && a.State != "waiting")
                {
                    a.State = "waiting";
                    toWait.Add(a);
                }
            }
        }

        foreach (var mac in toWake)
        {
            await WakeQuietly(mac, cancel);
        }

        foreach (var a in toReturn)
        {
            await Send(a, new LabMessage(MessageKind.Command, new JsonObject { ["command"] = LabCommands.Shutdown }), cancel);
        }

        foreach (var a in toStart)
        {
            await SendModel(a, cancel);
        }

        foreach (var a in toWait)
        {
            await Send(a, new LabMessage(MessageKind.Wait, new JsonObject { ["seconds"] = Math.Round(_options.Schedule.UntilChange(now).TotalSeconds) }), cancel);
        }
    }

    private async Task SendModel(AgentEntry agent, CancellationToken cancel)
    {
        Round round;
        lock (_gate)
        {
            round = _round!;
        }

        var seconds = Math.Max(Math.Min(30, _options.RoundMinutes * 60 / 3), (round.Deadline - _options.Now()).TotalSeconds);
        var header = new JsonObject
        {
            ["round"] = round.Number,
            ["version"] = _state.Step,
            ["rate"] = round.Rate,
            ["seconds"] = Math.Round(seconds),
            ["shape"] = _weights.Shape.ToJson(),
            ["layout"] = round.Pack.Layout.DeepClone(),
        };
        await Send(agent, new LabMessage(MessageKind.Model, header, round.Pack.Data), cancel);
    }

    // the mean of the changes into the model, saved; the next round starts with the next tick
    private void Close(Round round, DateTime now)
    {
        var applied = round.Average.ApplyTo(_weights);
        if (applied)
        {
            _state = _state with { Step = _state.Step + 1, Minutes = _state.Minutes + _options.RoundMinutes };
            Save();
            if (_options.Fonts is { Count: > 0 } && _state.Step % Math.Max(1, _options.TestEvery) == 0)
            {
                var copy = _weights.Copy();
                var version = _state.Step;
                _ = Task.Run(() => Test(copy, version));
            }
        }

        var entry = new JsonObject
        {
            ["round"] = round.Number,
            ["version"] = _state.Step,
            ["closed"] = now.ToString("o", CultureInfo.InvariantCulture),
            ["rate"] = round.Rate,
            ["computers"] = round.Average.Parts,
            ["lines"] = round.Average.Lines,
            ["loss"] = round.Losses.Count > 0 ? Math.Round(round.Losses.Average(), 4) : null,
            ["applied"] = applied,
        };
        _rounds.Add(entry);
        File.AppendAllText(Path.Combine(_options.Folder, "rounds.jsonl"), entry.ToJsonString() + "\n");
        foreach (var name in round.Participants)
        {
            if (_agents.TryGetValue(name, out var a) && a.State == "learning")
            {
                a.State = "idle";
            }
        }

        _round = null;
    }

    private void Save()
    {
        var provenance = new JsonObject { ["source"] = "IRIS lab: learned on the computers of a classroom", ["version"] = _state.Step };
        OcrFiles.SaveTraining(ModelPath(_options.Folder), _weights, new AdamW(), _state, provenance);
        OcrFiles.SaveReader(Path.Combine(_options.Folder, "iris-ocr.iris"), _weights, provenance);
        if (_options.Backup is { } backup)
        {
            try
            {
                Directory.CreateDirectory(backup);
                File.Copy(ModelPath(_options.Folder), Path.Combine(backup, "lab-model.iris"), overwrite: true);
                File.Copy(Path.Combine(_options.Folder, "iris-ocr.iris"), Path.Combine(backup, "iris-ocr.iris"), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the backup is a copy; the model is safe in the hub's folder
            }
        }
    }

    private async Task Serve(TcpClient client, CancellationToken cancel)
    {
        using var _ = client;
        client.NoDelay = true;
        LabChannel channel;
        try
        {
            channel = await LabChannel.AcceptAsync(client.GetStream(), role => role == LabRole.Agent ? _agentKey : role == LabRole.Console ? _consoleKey : null, cancel);
        }
        catch (Exception e) when (e is IOException or LabProtocolException or OperationCanceledException or JsonException)
        {
            return;
        }

        using (channel)
        {
            if (channel.PeerRole == LabRole.Console)
            {
                await ServeConsole(channel, cancel);
                return;
            }

            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            var agent = _agents.AddOrUpdate(channel.PeerName, name => new AgentEntry(name), (_, existing) => existing);
            lock (_gate)
            {
                agent.Channel = channel;
                agent.Address = address;
                agent.Seen = _options.Now();
                agent.State = "idle";
            }

            try
            {
                await Send(agent, new LabMessage(MessageKind.Settings, Settings()), cancel);
                if (_sentences.Count > 0)
                {
                    var sentences = new JsonObject();
                    foreach (var (language, lines) in _sentences)
                    {
                        sentences[language] = new JsonArray([.. lines.Select(l => (JsonNode?)l)]);
                    }

                    await Send(agent, new LabMessage(MessageKind.Settings, new JsonObject { ["sentences"] = sentences }), cancel);
                }

                while (!cancel.IsCancellationRequested)
                {
                    var message = await channel.ReceiveAsync(cancel);
                    lock (_gate)
                    {
                        agent.Seen = _options.Now();
                    }

                    switch (message.Kind)
                    {
                        case MessageKind.Status:
                            lock (_gate)
                            {
                                agent.Status = message.Header;
                                if (message.Header["mac"]?.GetValue<string>() is { } mac && mac != agent.Mac)
                                {
                                    agent.Mac = mac;
                                    SaveKnown();
                                }
                            }

                            _history.Record(agent.Name, message.Header, _options.Now());
                            break;
                        case MessageKind.Delta:
                            Receive(agent, message);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or LabProtocolException or OperationCanceledException or JsonException or SocketException)
            {
            }
            finally
            {
                lock (_gate)
                {
                    if (agent.Channel == channel)
                    {
                        agent.Channel = null;
                        agent.State = "gone";
                    }
                }
            }
        }
    }

    private void Receive(AgentEntry agent, LabMessage message)
    {
        lock (_gate)
        {
            var number = message.Header["round"]?.GetValue<int>() ?? -1;
            if (_round is not { } round || round.Number != number || !round.Participants.Contains(agent.Name) || round.Done.Contains(agent.Name))
            {
                return; // a change of a closed round is too old to use
            }

            var lines = message.Header["lines"]?.GetValue<long>() ?? 0;
            var change = TensorPack.Tensors(_weights).Select(t => new float[t.Values.Length]).ToList();
            TensorPack.Unpack(message.Header["layout"]!.AsArray(), message.Data,
                TensorPack.Tensors(_weights).Select((t, i) => (t.Name, change[i])));
            if (change.Any(c => c.Any(v => !float.IsFinite(v))))
            {
                round.Done.Add(agent.Name);
                return; // a broken change (a fault on that computer) is left out
            }

            round.Average.Add(change, lines);
            round.Done.Add(agent.Name);
            if (message.Header["loss"]?.GetValue<double>() is { } loss && double.IsFinite(loss))
            {
                round.Losses.Add(loss);
            }

            agent.Rounds++;
            agent.Lines += lines;
            agent.State = "idle";
        }
    }

    private async Task ServeConsole(LabChannel channel, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(cancel);
                switch (message.Kind)
                {
                    case MessageKind.Snapshot:
                        await channel.SendAsync(new LabMessage(MessageKind.Snapshot, Snapshot()), cancel);
                        break;
                    case MessageKind.History:
                        var now = _options.Now();
                        var from = message.Header["from"]?.GetValue<DateTime>() ?? now.AddHours(-12);
                        var to = message.Header["to"]?.GetValue<DateTime>() ?? now;
                        var history = _history.Query(from, to, message.Header["agent"]?.GetValue<string>(), message.Header["bucket"]?.GetValue<int>() ?? 5);
                        await channel.SendAsync(new LabMessage(MessageKind.History, history), cancel);
                        break;
                    case MessageKind.Scan:
                        var devices = await NetworkScanner.ScanAsync(cancel);
                        var found = new JsonArray();
                        foreach (var d in devices)
                        {
                            var json = d.ToJson();
                            json["computer"] = _agents.Values.FirstOrDefault(a => (d.Mac is not null && string.Equals(a.Mac, d.Mac, StringComparison.OrdinalIgnoreCase)) || a.Address == d.Address)?.Name;
                            found.Add((JsonNode)json);
                        }

                        await channel.SendAsync(new LabMessage(MessageKind.Scan, new JsonObject { ["devices"] = found, ["time"] = _options.Now().ToString("o", CultureInfo.InvariantCulture) }), cancel);
                        break;
                    case MessageKind.Command:
                        var target = message.Header["target"]?.GetValue<string>() ?? "";
                        var command = message.Header["command"]?.GetValue<string>() ?? "";
                        var sent = 0;
                        if (command == "wake")
                        {
                            // a computer of the classroom by its name, all that are off ("*"), or any card by its MAC
                            var macs = target.StartsWith("mac:", StringComparison.Ordinal) ? [target[4..]]
                                : _agents.Values.Where(a => a.Mac is not null && a.Channel is null && (target == "*" || string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase))).Select(a => a.Mac!).ToList();
                            foreach (var mac in macs.Where(m => NetworkScanner.ParseMac(m) is not null))
                            {
                                await WakeQuietly(mac, cancel);
                                sent++;
                            }
                        }
                        else if (LabCommands.All.Contains(command))
                        {
                            foreach (var a in _agents.Values.Where(a => a.Channel is not null && (target == "*" || string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase))))
                            {
                                await Send(a, new LabMessage(MessageKind.Command, new JsonObject { ["command"] = command }), cancel);
                                sent++;
                            }
                        }

                        await channel.SendAsync(new LabMessage(MessageKind.Note, new JsonObject { ["sent"] = sent, ["command"] = command }), cancel);
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or LabProtocolException or OperationCanceledException or JsonException or SocketException)
        {
        }
    }

    private async Task WakeQuietly(string mac, CancellationToken cancel)
    {
        try
        {
            await _options.Wake(mac, cancel);
        }
        catch (Exception e) when (e is SocketException or ArgumentException or IOException)
        {
        }
    }

    // the computers the hub has met (name, MAC, address) and the ones it woke, kept across restarts of the hub
    private void LoadKnown()
    {
        var path = Path.Combine(_options.Folder, "computers.json");
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
        {
            return;
        }

        foreach (var item in json["computers"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var name = item["name"]!.GetValue<string>();
            _agents[name] = new AgentEntry(name) { Mac = item["mac"]?.GetValue<string>(), Address = item["address"]?.GetValue<string>() ?? "", State = "gone" };
        }

        foreach (var name in json["woken"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])
        {
            _woken.Add(name);
        }
    }

    private void SaveKnown()
    {
        try
        {
            var json = new JsonObject
            {
                ["computers"] = new JsonArray([.. _agents.Values.Select(a => (JsonNode?)new JsonObject { ["name"] = a.Name, ["mac"] = a.Mac, ["address"] = a.Address })]),
                ["woken"] = new JsonArray([.. _woken.Select(n => (JsonNode?)n)]),
            };
            File.WriteAllText(Path.Combine(_options.Folder, "computers.json"), json.ToJsonString());
        }
        catch (IOException)
        {
        }
    }

    // the test sets of the hub, drawn once (fonts it learns from, and Caladea when the server has it, never learned)
    private (OcrNetwork Network, List<TrainingLine> Known, List<TrainingLine> Unseen)? MakeTester()
    {
        var faces = LineArtist.FindFonts(_options.Fonts ?? [], _weights.Shape.Charset, ["Math", "Display", "Unifont", "Symbol"]);
        if (faces.Count == 0)
        {
            return null;
        }

        var unseenFaces = faces.Where(f => f.Name.Contains("Caladea", StringComparison.OrdinalIgnoreCase)).ToList();
        var knownFaces = faces.Except(unseenFaces).ToList();
        List<TrainingLine> Draw(List<Iris.Vision.Fonts.FontFace> list, int seed, int count)
        {
            var lines = new List<TrainingLine>();
            if (list.Count == 0)
            {
                return lines;
            }

            var artist = new LineArtist(list);
            var texts = new LineTexts(_weights.Shape, _sentences, seed);
            var random = new Random(seed);
            while (lines.Count < count)
            {
                if (artist.Line(texts.Next(), _weights.Shape, random) is { } line)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        return (new OcrNetwork(new OcrWeights(_weights.Shape)) { Threads = Math.Max(1, Environment.ProcessorCount / 2) },
            Draw(knownFaces.Count > 0 ? knownFaces : faces, 7777, _options.TestLines), Draw(unseenFaces, 8888, _options.TestLines / 2));
    }

    private void Test(OcrWeights weights, int version)
    {
        try
        {
            if (_tester.Value is not { } tester)
            {
                return;
            }

            lock (tester.Network)
            {
                foreach (var (target, source) in TensorPack.Tensors(tester.Network.Weights).Zip(TensorPack.Tensors(weights)))
                {
                    source.Values.CopyTo(target.Values, 0);
                }

                var entry = new JsonObject
                {
                    ["version"] = version,
                    ["time"] = _options.Now().ToString("o", CultureInfo.InvariantCulture),
                    ["cer"] = Math.Round(OcrTrainer.Cer(tester.Network, tester.Known), 2),
                    ["cer_unseen"] = tester.Unseen.Count > 0 ? Math.Round(OcrTrainer.Cer(tester.Network, tester.Unseen), 2) : null,
                };
                lock (_gate)
                {
                    _tests.Add(entry);
                }

                File.AppendAllText(Path.Combine(_options.Folder, "tests.jsonl"), entry.ToJsonString() + "\n");
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
        }
    }

    private JsonObject Settings() => new()
    {
        ["duty"] = _options.Duty,
        ["cores"] = _options.CoreShare,
        ["batch"] = _options.BatchSize,
        ["schedule"] = _options.Schedule.ToJson(),
    };

    private static async Task Send(AgentEntry agent, LabMessage message, CancellationToken cancel)
    {
        var channel = agent.Channel;
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendAsync(message, cancel);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    private sealed class AgentEntry(string name)
    {
        public string Name { get; } = name;

        public LabChannel? Channel { get; set; }

        public string Address { get; set; } = "";

        public string? Mac { get; set; }

        public DateTime Seen { get; set; }

        /// <summary>idle, learning, waiting (outside the schedule), gone.</summary>
        public string State { get; set; } = "idle";

        public JsonObject? Status { get; set; }

        public int Rounds { get; set; }

        public long Lines { get; set; }
    }

    private sealed class Round(int number, DateTime deadline, double rate, (JsonArray Layout, byte[] Data) pack, RoundAverage average)
    {
        public int Number { get; } = number;

        public DateTime Deadline { get; } = deadline;

        public double Rate { get; } = rate;

        public (JsonArray Layout, byte[] Data) Pack { get; } = pack;

        public RoundAverage Average { get; } = average;

        public HashSet<string> Participants { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Done { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<double> Losses { get; } = [];
    }
}

/// <summary>The only commands an agent carries out – nothing else, never a program or a script.</summary>
public static class LabCommands
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pause, Resume, Shutdown, Restart };
}
