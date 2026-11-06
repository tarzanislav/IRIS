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

    /// <summary>Whether the computers learn in the efficiency mode of their system (cooler, quieter, less power).</summary>
    public bool Eco { get; init; } = true;

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

    /// <summary>
    /// Gives the computers a newer signed program by itself (between rounds) as soon as the server has one; without
    /// it only when the console asks.
    /// </summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>How a computer is woken (tests replace it; by default the magic packet).</summary>
    public Func<string, CancellationToken, Task> Wake { get; init; } = (mac, cancel) => NetworkScanner.WakeAsync(mac, cancel);

    /// <summary>The clock (tests set their own).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>
    /// Whether a computer that is not connected is on (at its last address): then IRIS does not run there or cannot
    /// connect, and something needs to be fixed. By default a ping, and the ARP answer when pings are dropped.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>> IsOn { get; init; } = NetworkScanner.IsOnAsync;

    /// <summary>
    /// How long a connected computer may be silent before it counts as gone (it sends its state every 10 s; a computer
    /// switched off at the button leaves its connection open without a word).
    /// </summary>
    public TimeSpan AgentSilence { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Where the hub writes what happens (its log, see <see cref="HubLog"/>); null = nowhere.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Where the model of the classroom came from when the hub started (for the console), or null.</summary>
    public string? ModelSource { get; init; }
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
    private readonly ConcurrentDictionary<LabChannel, (string Name, byte[] Key)> _consoles = new();
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
    private DateTime _lastUpdateLook = DateTime.MinValue;
    private AvailableUpdate? _update;
    private readonly LabConfig _config;
    private readonly DateTime _started;
    private readonly Lazy<int> _fontFiles;
    private CancellationTokenSource? _stop;
    private TestRun? _test;
    private readonly EfficiencyLog _efficiency;
    private bool _wakeReported = true;

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
        _efficiency = new EfficiencyLog(options.Folder, options.Now);
        _config = LabConfig.Load(ConfigPath);
        _tester = new(MakeTester);
        _started = options.Now();
        _fontFiles = new(CountFonts);
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

    private string ConfigPath => Path.Combine(_options.Folder, "lab-config.json");

    /// <summary>The port it listens on (useful when 0 was asked for).</summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _options.Port;

    public int Version => _state.Step;

    public OcrWeights Weights => _weights;

    /// <summary>Whether a console asked the hub to start again (it then ends; its service starts it again).</summary>
    public bool RestartAsked { get; private set; }

    /// <summary>The file the server's updater leaves for the hub: the learning file of a release to start from.</summary>
    public static string StartModelPath(string folder) => Path.Combine(folder, "start-model.iris");

    /// <summary>What the console asks the server's updater for (systemd watches for this file and runs it as root).</summary>
    public static string UpdateRequestPath(string folder) => Path.Combine(folder, "update-request");

    /// <summary>What the updater did last time (written by it, shown by the console).</summary>
    public static string UpdateResultPath(string folder) => Path.Combine(folder, "update-result.json");

    /// <summary>The learning file the updater left (asked by the console): checked, the model before kept beside it (the
    /// three newest), then in place. Called by the hub before it loads its model.</summary>
    public static void TakeStartModel(string folder, Action<string> say)
    {
        var pending = StartModelPath(folder);
        if (!File.Exists(pending))
        {
            return;
        }

        try
        {
            OcrFiles.LoadTraining(pending);
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException or ArgumentException)
        {
            File.Move(pending, Path.ChangeExtension(pending, ".bad.iris"), overwrite: true);
            say($"the learning file from the release cannot be read ({e.Message}) – the model stays");
            return;
        }

        var model = ModelPath(folder);
        if (File.Exists(model))
        {
            var before = Path.Combine(folder, $"lab-model-before-{DateTime.Now:yyyyMMdd-HHmm}.iris");
            File.Move(model, before, overwrite: true);
            say($"the model before is kept as {Path.GetFileName(before)}");
            foreach (var old in Directory.EnumerateFiles(folder, "lab-model-before-*.iris").Order(StringComparer.Ordinal).SkipLast(3))
            {
                File.Delete(old); // the three newest stay
            }
        }

        File.Move(pending, model);
        say("the model of the classroom starts from the learning file of the release");
    }

    /// <summary>Starts listening; returns when cancelled, or when a console asked for a restart.</summary>
    public async Task RunAsync(CancellationToken outer)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _stop = stop;
        var cancel = stop.Token;
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
                    ["test"] = InTest(a, now),
                    ["connected"] = a.Channel is not null,
                    ["on"] = a.Channel is not null ? true : a.On,
                    ["state"] = a.Uninstalled && a.Channel is null ? "uninstalled" : a.State,
                    ["leaving"] = a.Leaving,
                    ["seen"] = a.Seen.ToString("o", CultureInfo.InvariantCulture),
                    ["rounds"] = a.Rounds,
                    ["lines"] = a.Lines,
                    ["version"] = a.Version,
                    ["alias"] = _config.Aliases.GetValueOrDefault(a.Name),
                    ["group"] = _config.GroupOf(a.Name, Model(a)),
                    ["model"] = Model(a),
                    ["settings"] = SettingsOf(a),
                    ["inventory"] = a.Inventory?.DeepClone(),
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
                ["hub_version"] = LabUpdates.Version,
                ["test"] = _test is { } t && now < t.Until ? t.ToJson() : null,
                ["consoles"] = new JsonArray([.. _consoles.Values.Select(c => c.Name.StartsWith('@') ? c.Name[1..] : "").Order(StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode?)n)]),
                ["config"] = ConfigJson(),
                ["update"] = _update is { } u ? new JsonObject
                {
                    ["version"] = u.Manifest.Version,
                    ["auto"] = _options.AutoUpdate,
                    ["agent"] = u.Manifest.File(UpdateManifest.Agent) is not null,
                    ["console"] = u.Manifest.File(UpdateManifest.Console) is not null,
                } : null,
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
                Note($"a step of the rounds failed: {e.Message}");
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
        List<AgentEntry> toUpdate = [];
        List<AgentEntry> testOver = [];
        List<AgentEntry> toLook = [];
        lock (_gate)
        {
            // the computers that are not connected: on (IRIS does not run) or off – a look at a few every step
            toLook.AddRange(_agents.Values.Where(a => a.Channel is null && a.Address.Length > 0 && !a.Looking && now - a.Looked > TimeSpan.FromMinutes(1))
                .OrderBy(a => a.Looked).Take(8));
            foreach (var a in toLook)
            {
                a.Looking = true;
            }

            if (_test is { } test && now >= test.Until)
            {
                // the test is over: the computers get their own settings again and wait for the time of learning
                testOver.AddRange(_agents.Values.Where(a => a.Channel is not null && test.Includes(a.Name)));
                _test = null;
                Note($"the test of {test.Console} is over");
            }

            if (now - _lastUpdateLook > TimeSpan.FromMinutes(5))
            {
                _lastUpdateLook = now;
                FindUpdate();
            }

            // a newer program for computers that are not in a round: a few at a time, each at most every 15 minutes
            if (_update is { } update && update.Manifest.File(UpdateManifest.Agent) is not null)
            {
                foreach (var a in _agents.Values.Where(a => a.Channel is not null && a.Version is { } v && LabUpdates.IsNewer(update.Manifest.Version, v) &&
                    (Switch(a, "update") || a.UpdateWanted) && a.State is "idle" or "waiting" && now - a.UpdateSent > TimeSpan.FromMinutes(15)).Take(3))
                {
                    a.UpdateSent = now;
                    a.UpdateWanted = false;
                    a.State = "updating";
                    toUpdate.Add(a);
                }
            }

            var open = _options.Schedule.IsOpen(now);
            if (_options.AutoWake)
            {
                if (open && _wasOpen == false)
                {
                    // the time of learning begins: wake every known computer that is off, and remember which
                    _openedAt = now;
                    _lastWake = now;
                    foreach (var a in _agents.Values.Where(a => a.Channel is null && a.Mac is not null && Switch(a, "wake")))
                    {
                        _woken.Add(a.Name);
                        toWake.Add(a.Mac!);
                    }

                    var noCard = _agents.Values.Where(a => a.Channel is null && a.Mac is null && Switch(a, "wake")).Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    Note($"the time of learning begins: waking {_woken.Count} computers" + (_woken.Count > 0 ? $" ({string.Join(", ", _woken.Order(StringComparer.OrdinalIgnoreCase))})" : "") +
                         (noCard.Count > 0 ? $"; without a known card (cannot be woken): {string.Join(", ", noCard)}" : ""));
                    _wakeReported = false;
                    SaveKnown();
                }
                else if (open && !_wakeReported && now - _openedAt >= TimeSpan.FromMinutes(30))
                {
                    // half an hour later: which of the woken computers never came
                    _wakeReported = true;
                    var missing = _agents.Values.Where(a => _woken.Contains(a.Name) && a.Channel is null).Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    Note(missing.Count == 0
                        ? "all woken computers came"
                        : $"woken but not here after 30 min (check Wake-on-LAN in the BIOS, Deep Sleep, fast startup of Windows): {string.Join(", ", missing)}");
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
                    Note($"the time of learning ends: {toReturn.Count} woken computers are shut down again");
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

            var idle = _agents.Values.Where(a => a.Channel is not null && a.State is "idle" or "waiting" && (Switch(a, "learn") || InTest(a, now))).ToList();
            if (_round is null && idle.Any(a => open || InTest(a, now)))
            {
                var number = (_rounds.LastOrDefault()?["round"]?.GetValue<int>() ?? 0) + 1;
                var phase = Math.Clamp((_state.Step - _state.PlanStart) / Math.Max(1, _state.PlanEnd - _state.PlanStart), 0, 1);
                var rate = _state.PlanRate * (0.02 + 0.98 * 0.5 * (1 + Math.Cos(Math.PI * phase)));
                _round = new Round(number, now.AddMinutes(_options.RoundMinutes), rate, TensorPack.Pack(_weights), new RoundAverage(_weights));
                Note($"round {number} opened (model version {_state.Step}, rate {rate:0.###e0})");
            }

            foreach (var a in idle)
            {
                // a computer joins the round (or, after a new connection, joins it again) while a third of it is left
                if ((open || InTest(a, now)) && _round is { } r && !r.Done.Contains(a.Name) && r.Deadline - now > TimeSpan.FromMinutes(_options.RoundMinutes / 3))
                {
                    r.Participants.Add(a.Name);
                    a.State = "learning";
                    toStart.Add(a);
                }
                else if (!open && !InTest(a, now) && a.State != "waiting")
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

        foreach (var a in toLook)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var on = await _options.IsOn(a.Address, cancel);
                    lock (_gate)
                    {
                        if (a.Channel is null)
                        {
                            if (on && a.On != true)
                            {
                                Note($"{a.Name} is on at {a.Address}, but IRIS does not connect (is the service IRIS-Lab running?)");
                            }

                            a.On = on;
                        }
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
                {
                }
                finally
                {
                    lock (_gate)
                    {
                        a.Looking = false;
                        a.Looked = _options.Now();
                    }
                }
            }, cancel);
        }

        foreach (var a in testOver)
        {
            await Send(a, new LabMessage(MessageKind.Settings, Settings(a)), cancel);
        }

        foreach (var a in toUpdate)
        {
            await SendUpdate(a, cancel);
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
        Note($"round {round.Number} closed: {round.Average.Parts} computers, {round.Average.Lines} lines" +
             (round.Losses.Count > 0 ? $", loss {round.Losses.Average():0.####}" : "") + (applied ? $", model version {_state.Step}" : ", nothing learned"));
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
                Note($"the backup into {backup} failed: {e.Message}");
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
            channel = await LabChannel.AcceptAsync(
                client.GetStream(),
                (role, name) => role == LabRole.Agent ? _agentKey : role == LabRole.Console ? LabKeys.ConsoleKey(_options.Folder, _consoleKey, name) : null,
                cancel);
        }
        catch (Exception e) when (e is IOException or LabProtocolException or OperationCanceledException or JsonException)
        {
            if (e is LabProtocolException && !cancel.IsCancellationRequested)
            {
                Note($"a connection from {(client.Client.RemoteEndPoint as IPEndPoint)?.Address} was refused: {e.Message}");
            }

            return;
        }

        using (channel)
        {
            if (channel.PeerRole == LabRole.Console)
            {
                _consoles[channel] = (channel.PeerName, LabKeys.ConsoleKey(_options.Folder, _consoleKey, channel.PeerName) ?? []);
                Note($"console {ConsoleName(channel)} connected from {(client.Client.RemoteEndPoint as IPEndPoint)?.Address}");
                try
                {
                    await ServeConsole(channel, cancel);
                }
                finally
                {
                    _consoles.TryRemove(channel, out var _);
                    Note($"console {ConsoleName(channel)} left");
                }

                return;
            }

            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var address = (remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote)?.ToString() ?? "";
            var agent = _agents.AddOrUpdate(channel.PeerName, name => new AgentEntry(name), (_, existing) => existing);
            lock (_gate)
            {
                agent.Channel = channel;
                agent.Address = address;
                agent.Seen = _options.Now();
                agent.State = "idle";
                agent.Uninstalled = false; // it is here again (installed anew, or it could not remove itself)
            }

            Note($"{agent.Name} connected from {address}");
            if (agent.Mac is null && NetworkScanner.ArpTable().TryGetValue(address, out var seen))
            {
                // the computer did not tell its card (an older agent): the hub sees it in its ARP table
                lock (_gate)
                {
                    agent.Mac = seen;
                    SaveKnown();
                }

                Note($"{agent.Name}: card {seen} from the ARP table of the server");
            }

            try
            {
                await Send(agent, new LabMessage(MessageKind.Settings, Settings(agent)), cancel);
                if (_sentences.Count > 0)
                {
                    var sentences = new JsonObject();
                    foreach (var (language, lines) in _sentences)
                    {
                        sentences[language] = new JsonArray([.. lines.Select(l => (JsonNode?)l)]);
                    }

                    await Send(agent, new LabMessage(MessageKind.Settings, new JsonObject { ["sentences"] = sentences }), cancel);
                }

                if (agent.Leaving)
                {
                    // asked while it was off
                    await Send(agent, new LabMessage(MessageKind.Command, new JsonObject { ["command"] = LabCommands.Uninstall }), cancel);
                }

                while (!cancel.IsCancellationRequested)
                {
                    // a computer tells how it is every few seconds; one that is silent much longer is gone, even
                    // when its connection was never closed (switched off at the button, the cable pulled)
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    silence.CancelAfter(_options.AgentSilence);
                    LabMessage message;
                    try
                    {
                        message = await channel.ReceiveAsync(silence.Token);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        Note($"{agent.Name} was silent for {_options.AgentSilence.TotalSeconds:0} s: taken as gone");
                        break;
                    }

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
                                agent.Version = message.Header["version"]?.GetValue<string>();
                                if (message.Header["state"]?.GetValue<string>() == "uninstalling" && !agent.Uninstalled)
                                {
                                    agent.Leaving = false;
                                    agent.Uninstalled = true;
                                    SaveKnown();
                                }
                                if (agent.State == "updating" && _options.Now() - agent.UpdateSent > TimeSpan.FromMinutes(2))
                                {
                                    agent.State = "idle"; // it did not take the program (it says why in its log)
                                }
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
                        case MessageKind.Inventory:
                            lock (_gate)
                            {
                                agent.Inventory = message.Header;
                                SaveKnown();
                            }

                            // its model may put it into another group
                            await Send(agent, new LabMessage(MessageKind.Settings, Settings(agent)), cancel);
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
                        agent.On = null; // looked at in a minute: a computer that shuts down is still on for a while
                        agent.Looked = _options.Now();
                        Note($"{agent.Name} left");
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
            var broken = change.Any(c => c.Any(v => !float.IsFinite(v)));
            Account(agent, round, message.Header, used: !broken);
            if (broken)
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

    // the line of the log of efficiency for this computer and round: what it says of its round (agents from 0.4.7
    // add their load and strain) with its group and hardware
    private void Account(AgentEntry agent, Round round, JsonObject header, bool used)
    {
        var receipt = header["receipt"] as JsonObject;
        double? Of(string key) => receipt?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        var model = Model(agent);
        _efficiency.Add(new JsonObject
        {
            ["round"] = round.Number,
            ["computer"] = agent.Name,
            ["name"] = _config.Aliases.GetValueOrDefault(agent.Name),
            ["group"] = _config.GroupOf(agent.Name, model),
            ["model"] = model,
            ["cpu"] = agent.Inventory?["cpu"]?["name"]?.GetValue<string>(),
            ["logical"] = Of("logical") is { } logical ? (int)logical : null,
            ["lines"] = header["lines"]?.GetValue<long>() ?? 0,
            ["seconds"] = header["seconds"]?.GetValue<double>(),
            ["asked"] = Of("asked"),
            ["stopped"] = receipt?["stopped"]?.GetValue<bool>(),
            ["used"] = used,
            ["threads"] = Of("threads"),
            ["duty"] = Of("duty"),
            ["eco"] = receipt?["eco"]?.GetValue<bool>(),
            ["moving"] = receipt?["moving"]?.GetValue<bool>(),
            ["per_thread"] = Of("per_thread"),
            ["cpu_own"] = Of("cpu_own"),
            ["cpu_total"] = Of("cpu_total"),
            ["cpu_others"] = Of("cpu_others"),
            ["strain"] = Of("strain"),
            ["reasons"] = receipt?["reasons"]?.GetValue<string>(),
            ["loss"] = header["loss"]?.GetValue<double>() is { } loss && double.IsFinite(loss) ? Math.Round(loss, 4) : null,
        });
    }

    private async Task ServeConsole(LabChannel channel, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(cancel);
                if (!_consoles.TryGetValue(channel, out var known) || LabKeys.ConsoleKey(_options.Folder, _consoleKey, channel.PeerName) is not { } current
                    || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(current, known.Key))
                {
                    // the console's own key was taken away (or made anew) on the server: it is let go at its next request
                    return;
                }

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
                        else if (command is LabCommands.Uninstall or LabCommands.Stay)
                        {
                            // one computer at a time (never "*"), also one that is off: it is kept until it connects
                            List<AgentEntry> chosen;
                            lock (_gate)
                            {
                                chosen = [.. _agents.Values.Where(a => target != "*" && string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase))];
                                foreach (var a in chosen)
                                {
                                    a.Leaving = command == LabCommands.Uninstall;
                                }

                                if (chosen.Count > 0)
                                {
                                    SaveKnown();
                                }
                            }

                            foreach (var a in chosen)
                            {
                                if (a.Channel is not null)
                                {
                                    await Send(a, new LabMessage(MessageKind.Command, new JsonObject { ["command"] = command }), cancel);
                                }

                                sent++;
                            }
                        }
                        else if (LabCommands.All.Contains(command))
                        {
                            // shutdown and restart: at once and by force only when the console asks for it
                            var power = command is LabCommands.Shutdown or LabCommands.Restart;
                            var atOnce = power && message.Header["now"]?.GetValue<bool>() == true;
                            var force = power && message.Header["force"]?.GetValue<bool>() == true;
                            foreach (var a in _agents.Values.Where(a => a.Channel is not null && (target == "*" || string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase))))
                            {
                                var order = new JsonObject { ["command"] = command };
                                if (power)
                                {
                                    order["now"] = atOnce;
                                    order["force"] = force;
                                }

                                await Send(a, new LabMessage(MessageKind.Command, order), cancel);
                                sent++;
                            }
                        }

                        Note($"console {ConsoleName(channel)}: {command} for {(target == "*" ? "all" : target)} – {sent} computers");
                        await channel.SendAsync(new LabMessage(MessageKind.Note, new JsonObject { ["sent"] = sent, ["command"] = command }), cancel);
                        break;
                    case MessageKind.Update:
                        if (message.Header["what"]?.GetValue<string>() is "agents" or "hub")
                        {
                            Note($"console {ConsoleName(channel)}: update of {(message.Header["what"]!.GetValue<string>() == "hub" ? "the server" : "the computers")}");
                        }

                        await channel.SendAsync(UpdateRequest(message.Header), cancel);
                        break;
                    case MessageKind.Configure:
                        if (message.Header.Count > 0)
                        {
                            Note($"console {ConsoleName(channel)} changed the configuration: {Shorten(message.Header.ToJsonString())}");
                        }

                        await Configure(message.Header, cancel);
                        await channel.SendAsync(new LabMessage(MessageKind.Configure, ConfigJson()), cancel);
                        break;
                    case MessageKind.Server when message.Header["what"]?.GetValue<string>() == "efficiency-table":
                        // the lines of the last days as a table (Excel), to be saved by the console
                        var days = Math.Clamp(message.Header["days"]?.GetValue<double>() ?? 7, 0.04, EfficiencyLog.Days);
                        LabMessage table;
                        try
                        {
                            var rows = _efficiency.Since(_options.Now().AddDays(-days));
                            table = new LabMessage(MessageKind.Server, new JsonObject { ["rows"] = rows.Count, ["days"] = days }, EfficiencyLog.Table(rows));
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            // an answer in any case: a failure here must not cut the console off
                            Note($"the table of efficiency failed: {e.Message}");
                            table = new LabMessage(MessageKind.Server, new JsonObject { ["rows"] = 0, ["error"] = e.Message });
                        }

                        await channel.SendAsync(table, cancel);
                        break;
                    case MessageKind.Server when message.Header["what"]?.GetValue<string>() is "test" or "test-stop":
                        await Test(channel, message.Header, cancel);
                        break;
                    case MessageKind.Server:
                        var what = message.Header["what"]?.GetValue<string>() ?? "";
                        await channel.SendAsync(new LabMessage(MessageKind.Server, ServerRequest(what, message.Header, ConsoleName(channel))), cancel);
                        if (what == "restart" && RestartAsked)
                        {
                            // the answer is out; the service starts the hub again (Restart=on-failure, the end code is 75)
                            await Task.Delay(300, CancellationToken.None);
                            _stop?.Cancel();
                        }

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

    // the newest set of programs in the folder of updates whose signature is right (the server's updater puts them there)
    private void FindUpdate()
    {
        try
        {
            var root = Path.Combine(_options.Folder, "updates");
            var previous = _update;
            _update = null;
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(root).Where(f => LabUpdates.ParseVersion(Path.GetFileName(f)) is not null)
                .OrderByDescending(f => LabUpdates.ParseVersion(Path.GetFileName(f))))
            {
                var json = Path.Combine(folder, UpdateManifest.ManifestName);
                var signature = Path.Combine(folder, UpdateManifest.SignatureName);
                if (File.Exists(json) && File.Exists(signature) && LabUpdates.Open(File.ReadAllBytes(json), File.ReadAllBytes(signature)) is { } manifest &&
                    manifest.Version == Path.GetFileName(folder))
                {
                    if (previous?.Manifest.Version != manifest.Version)
                    {
                        Note($"signed programs of {manifest.Version} are ready for the computers and the console");
                    }

                    _update = new AvailableUpdate(manifest, File.ReadAllBytes(json), File.ReadAllBytes(signature), folder);
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // a program of the set, read and checked against the signed manifest (null when missing or changed)
    private static LabMessage? Program(AvailableUpdate update, string name)
    {
        try
        {
            var path = Path.Combine(update.Folder, name);
            if (update.Manifest.File(name) is not { } file || !File.Exists(path))
            {
                return null;
            }

            var data = File.ReadAllBytes(path);
            if (!LabUpdates.Matches(file, data))
            {
                return null;
            }

            return new LabMessage(MessageKind.Update, new JsonObject
            {
                ["version"] = update.Manifest.Version,
                ["file"] = name,
                ["manifest"] = Convert.ToBase64String(update.Json),
                ["signature"] = Convert.ToBase64String(update.Signature),
            }, data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task SendUpdate(AgentEntry agent, CancellationToken cancel)
    {
        AvailableUpdate? update;
        lock (_gate)
        {
            update = _update;
        }

        if (update is not null && Program(update, UpdateManifest.Agent) is { } message)
        {
            await Send(agent, message, cancel);
        }
    }

    // what the console asks: the computers updated, the server updated (by its updater), or the console's program
    private LabMessage UpdateRequest(JsonObject header)
    {
        var what = header["what"]?.GetValue<string>() ?? "";
        var target = header["target"]?.GetValue<string>() ?? "*";
        AvailableUpdate? update;
        lock (_gate)
        {
            update = _update;
        }

        switch (what)
        {
            case "agents":
                var marked = 0;
                lock (_gate)
                {
                    foreach (var a in _agents.Values.Where(a => target == "*" || string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase)))
                    {
                        a.UpdateWanted = true;
                        a.UpdateSent = DateTime.MinValue;
                        marked++;
                    }
                }

                return new LabMessage(MessageKind.Note, new JsonObject { ["marked"] = marked, ["version"] = update?.Manifest.Version });
            case "hub":
                try
                {
                    // the updater of the server (systemd, as root) watches for this file, checks GitHub and installs
                    File.WriteAllText(UpdateRequestPath(_options.Folder), new JsonObject { ["time"] = _options.Now().ToString("o", CultureInfo.InvariantCulture) }.ToJsonString());
                    return new LabMessage(MessageKind.Note, new JsonObject { ["requested"] = true });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return new LabMessage(MessageKind.Note, new JsonObject { ["requested"] = false, ["error"] = e.Message });
                }

            case "console":
                return update is not null && Program(update, UpdateManifest.Console) is { } program
                    ? program
                    : new LabMessage(MessageKind.Note, new JsonObject { ["error"] = "the server has no newer console" });
            default:
                return new LabMessage(MessageKind.Note, new JsonObject { ["error"] = $"unknown request '{what}'" });
        }
    }

    // what the console asks of the server itself
    private JsonObject ServerRequest(string what, JsonObject header, string console)
    {
        try
        {
            switch (what)
            {
                case "status":
                    return ServerState();
                case "log":
                    var name = header["name"]?.GetValue<string>() ?? "hub";
                    var count = header["lines"]?.GetValue<int>() ?? 500;
                    return new JsonObject
                    {
                        ["name"] = name,
                        ["lines"] = new JsonArray([.. HubLog.Tail(_options.Folder, name, count).Select(l => (JsonNode?)l)]),
                    };
                case "efficiency":
                    // what every computer and group did over the last days (1–60)
                    var days = Math.Clamp(header["days"]?.GetValue<double>() ?? 7, 0.04, EfficiencyLog.Days);
                    var lines = _efficiency.Since(_options.Now().AddDays(-days));
                    Dictionary<string, string> aliases;
                    lock (_gate)
                    {
                        aliases = new Dictionary<string, string>(_config.Aliases, StringComparer.OrdinalIgnoreCase);
                    }

                    var summary = EfficiencyLog.Summary(lines, n => aliases.GetValueOrDefault(n));
                    summary["days"] = days;
                    return summary;
                case "restart":
                    RestartAsked = true;
                    Note($"console {console}: restart of the hub");
                    return new JsonObject { ["ok"] = true };
                case "model":
                    // the updater (root) takes the learning file of the newest signed release, checks it and leaves it
                    // for the hub, which starts from it (the model before is kept beside it)
                    File.WriteAllText(UpdateRequestPath(_options.Folder), new JsonObject
                    {
                        ["time"] = _options.Now().ToString("o", CultureInfo.InvariantCulture),
                        ["model"] = true,
                        ["console"] = console,
                    }.ToJsonString());
                    Note($"console {console}: the model of the classroom from the learning file of the newest release");
                    return new JsonObject { ["requested"] = true };
                default:
                    return new JsonObject { ["error"] = $"unknown request '{what}'" };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new JsonObject { ["error"] = e.Message };
        }
    }

    /// <summary>What the server is like now: program, time, model, disk, memory, fonts, the updater's last result.</summary>
    public JsonObject ServerState()
    {
        var now = _options.Now();
        var folder = Path.GetFullPath(_options.Folder);
        JsonObject? disk = null;
        try
        {
            var drive = new DriveInfo(folder);
            disk = new JsonObject { ["free_mb"] = drive.AvailableFreeSpace / (1024 * 1024), ["total_mb"] = drive.TotalSize / (1024 * 1024) };
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        JsonNode? lastUpdate = null;
        try
        {
            var result = UpdateResultPath(folder);
            lastUpdate = File.Exists(result) ? JsonNode.Parse(File.ReadAllText(result)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        var model = ModelPath(folder);
        lock (_gate)
        {
            return new JsonObject
            {
                ["hub_version"] = LabUpdates.Version,
                ["system"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["started"] = _started.ToString("o", CultureInfo.InvariantCulture),
                ["now"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                ["uptime_minutes"] = Math.Round((now - _started).TotalMinutes),
                ["model"] = new JsonObject
                {
                    ["version"] = _state.Step,
                    ["minutes"] = Math.Round(_state.Minutes),
                    ["rate"] = _state.PlanRate,
                    ["plan_rounds"] = Math.Round(_state.PlanEnd - _state.PlanStart),
                    ["plan_done"] = Math.Round(_state.Step - _state.PlanStart),
                    ["source"] = _options.ModelSource,
                    ["file_mb"] = File.Exists(model) ? Math.Round(new FileInfo(model).Length / 1048576.0, 1) : null,
                },
                ["disk"] = disk,
                ["memory_mb"] = Environment.WorkingSet / (1024 * 1024),
                ["processors"] = Environment.ProcessorCount,
                ["fonts"] = _fontFiles.Value,
                ["update_waiting"] = File.Exists(UpdateRequestPath(folder)),
                ["last_update"] = lastUpdate,
            };
        }
    }

    private int CountFonts()
    {
        var count = 0;
        foreach (var dir in _options.Fonts ?? [])
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    count += Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Count(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return count;
    }

    private bool InTest(AgentEntry agent, DateTime now) => _test is { } t && now < t.Until && t.Includes(agent.Name);

    // the test mode from the console: the chosen computers learn now (also outside the time of learning) with the
    // chosen load, for a few minutes; what they learn is kept. A new choice while it runs changes the load at once
    // (with "keep_time" the end stays). "test-stop" ends it gently: a round that runs is finished and used.
    private async Task Test(LabChannel channel, JsonObject header, CancellationToken cancel)
    {
        var console = ConsoleName(channel);
        var now = _options.Now();
        List<AgentEntry> affected;
        JsonObject answer;
        lock (_gate)
        {
            var before = _test;
            if (header["what"]!.GetValue<string>() == "test-stop")
            {
                _test = null;
                // their own settings again; a computer in a round finishes it (its part is used), then it waits
                affected = [.. _agents.Values.Where(a => a.Channel is not null && before is not null && before.Includes(a.Name))];
                Note($"console {console} stopped the test (the round that runs is finished)");
                answer = new JsonObject { ["stopped"] = before is not null };
            }
            else
            {
                var minutes = Math.Clamp(header["minutes"]?.GetValue<double>() ?? 20, 0.1, 240);
                HashSet<string>? computers = header["computers"] is JsonArray list
                    ? new HashSet<string>(list.Select(n => n?.GetValue<string>() ?? "").Where(n => _agents.ContainsKey(n)), StringComparer.OrdinalIgnoreCase)
                    : null;
                if (computers is { Count: 0 })
                {
                    answer = new JsonObject { ["error"] = "no known computer was chosen" };
                    affected = [];
                }
                else
                {
                    var keep = before is not null && header["keep_time"]?.GetValue<bool>() == true;
                    _test = new TestRun(keep ? before!.Started : now, keep ? before!.Until : now.AddMinutes(minutes), computers,
                        Math.Clamp(header["duty"]?.GetValue<double>() ?? 0.5, 0.05, 1), Math.Clamp(header["cores"]?.GetValue<double>() ?? 0.5, 0.05, 1),
                        header["eco"]?.GetValue<bool>() ?? true, console);
                    affected = [.. _agents.Values.Where(a => a.Channel is not null && ((before?.Includes(a.Name) ?? false) || _test.Includes(a.Name)))];
                    Note($"console {console} {(keep ? "changed the test (until " + _test.Until.ToString("HH:mm", CultureInfo.InvariantCulture) + ")" : before is not null ? "started the test anew for " + minutes.ToString("0", CultureInfo.InvariantCulture) + " min" : "started a test of " + minutes.ToString("0", CultureInfo.InvariantCulture) + " min")} on {(computers is null ? "all computers" : string.Join(", ", computers.Order(StringComparer.OrdinalIgnoreCase)))}: " +
                         $"{_test.Duty:P0} of the time on {_test.Cores:P0} of the cores, efficiency mode {(_test.Eco ? "on" : "off")}");
                    answer = new JsonObject { ["test"] = _test.ToJson(), ["computers"] = affected.Count(a => _test.Includes(a.Name)) };
                }
            }
        }

        await channel.SendAsync(new LabMessage(MessageKind.Server, answer), cancel);
        foreach (var a in affected)
        {
            await Send(a, new LabMessage(MessageKind.Settings, Settings(a)), cancel);
        }

    }

    private void Note(string text) => _options.Log?.Invoke(text);

    private static string ConsoleName(LabChannel channel) => channel.PeerName.StartsWith('@') ? channel.PeerName[1..] : "(main key)";

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

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
            _agents[name] = new AgentEntry(name)
            {
                Mac = item["mac"]?.GetValue<string>(),
                Address = item["address"]?.GetValue<string>() ?? "",
                State = "gone",
                Inventory = item["inventory"] as JsonObject is { } inventory ? (JsonObject)inventory.DeepClone() : null,
                Leaving = item["leaving"]?.GetValue<bool>() ?? false,
                Uninstalled = item["uninstalled"]?.GetValue<bool>() ?? false,
            };
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
                ["computers"] = new JsonArray([.. _agents.Values.Select(a => (JsonNode?)new JsonObject
                {
                    ["name"] = a.Name,
                    ["mac"] = a.Mac,
                    ["address"] = a.Address,
                    ["inventory"] = a.Inventory?.DeepClone(),
                    ["leaving"] = a.Leaving ? true : null,
                    ["uninstalled"] = a.Uninstalled ? true : null,
                })]),
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

                Note($"test of model version {version}: CER {entry["cer"]} %" + (entry["cer_unseen"] is { } u ? $", unseen font {u} %" : ""));

                File.AppendAllText(Path.Combine(_options.Folder, "tests.jsonl"), entry.ToJsonString() + "\n");
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            Note($"the test of model version {version} failed: {e.Message}");
        }
    }

    // what every computer gets unless the console set something else
    private JsonObject Defaults() => new()
    {
        ["duty"] = _options.Duty,
        ["cores"] = _options.CoreShare,
        ["moving"] = false,
        ["duty_max"] = 1.0,
        ["cores_max"] = 1.0,
        ["eco"] = _options.Eco,
        ["hot"] = 90.0,
        ["learn"] = true,
        ["wake"] = _options.AutoWake,
        ["update"] = _options.AutoUpdate,
    };

    private static string? Model(AgentEntry agent) => agent.Inventory?["model"]?.GetValue<string>();

    private JsonObject SettingsOf(AgentEntry agent) => _config.Effective(Defaults(), agent.Name, Model(agent));

    private bool Switch(AgentEntry agent, string name) => SettingsOf(agent)[name]?.GetValue<bool>() ?? true;

    // the settings message of a computer: its own settings, the batch and the schedule
    private JsonObject Settings(AgentEntry agent)
    {
        var settings = SettingsOf(agent);
        if (_test is { } test && InTest(agent, _options.Now()))
        {
            // the test mode: the load the console chose, for the time of the test
            settings["duty"] = test.Duty;
            settings["cores"] = test.Cores;
            settings["moving"] = false;
            settings["eco"] = test.Eco;
            settings["learn"] = true;
        }

        settings["batch"] = _options.BatchSize;
        settings["schedule"] = _options.Schedule.ToJson();
        return settings;
    }

    private JsonObject ConfigJson()
    {
        var json = _config.ToJson();
        json["defaults"] = Defaults();
        return json;
    }

    // a change from the console: saved, and every connected computer gets its settings again
    private async Task Configure(JsonObject change, CancellationToken cancel)
    {
        List<AgentEntry> connected;
        lock (_gate)
        {
            if (change["forget"]?["computer"]?.GetValue<string>() is { Length: > 0 } forget)
            {
                // a computer that is not connected leaves the list with its name, group and own settings (its
                // history stays); a connected one is kept
                if (!_agents.TryGetValue(forget, out var gone) || gone.Channel is not null || !_agents.TryRemove(forget, out _))
                {
                    return;
                }

                _woken.Remove(forget);
                SaveKnown();
                _config.Forget(forget);
            }
            else if (!_config.Apply(change))
            {
                return;
            }

            try
            {
                _config.Save(ConfigPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            connected = [.. _agents.Values.Where(a => a.Channel is not null)];
        }

        foreach (var a in connected)
        {
            await Send(a, new LabMessage(MessageKind.Settings, Settings(a)), cancel);
        }
    }

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

        /// <summary>While not connected: whether the computer is on (null: not looked at yet).</summary>
        public bool? On { get; set; }

        public bool Looking { get; set; }

        public DateTime Looked { get; set; } = DateTime.MinValue;

        public string Address { get; set; } = "";

        public string? Mac { get; set; }

        public DateTime Seen { get; set; }

        /// <summary>idle, learning, waiting (outside the schedule), gone.</summary>
        public string State { get; set; } = "idle";

        public JsonObject? Status { get; set; }

        public int Rounds { get; set; }

        public long Lines { get; set; }

        /// <summary>The version of its program (null: an agent too old to be updated by the hub).</summary>
        public string? Version { get; set; }

        public DateTime UpdateSent { get; set; } = DateTime.MinValue;

        public bool UpdateWanted { get; set; }

        /// <summary>What the computer is (from its firmware), as it last told.</summary>
        public JsonObject? Inventory { get; set; }

        /// <summary>Asked to uninstall itself; a computer that is off gets the command when it connects.</summary>
        public bool Leaving { get; set; }

        /// <summary>It said it removes itself (until it connects again, e.g. installed anew).</summary>
        public bool Uninstalled { get; set; }
    }

    private sealed record TestRun(DateTime Started, DateTime Until, HashSet<string>? Computers, double Duty, double Cores, bool Eco, string Console)
    {
        public bool Includes(string name) => Computers is null || Computers.Contains(name);

        public JsonObject ToJson() => new()
        {
            ["started"] = Started.ToString("o", CultureInfo.InvariantCulture),
            ["until"] = Until.ToString("o", CultureInfo.InvariantCulture),
            ["computers"] = Computers is null ? null : new JsonArray([.. Computers.Order(StringComparer.OrdinalIgnoreCase).Select(c => (JsonNode?)c)]),
            ["duty"] = Duty,
            ["cores"] = Cores,
            ["eco"] = Eco,
            ["console"] = Console,
        };
    }

    private sealed record AvailableUpdate(UpdateManifest Manifest, byte[] Json, byte[] Signature, string Folder);

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

    /// <summary>The agent finishes its round, then removes itself: the service, its program, settings and key.</summary>
    public const string Uninstall = "uninstall";

    /// <summary>An uninstall asked for is called off (while the agent is still finishing its round).</summary>
    public const string Stay = "stay";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pause, Resume, Shutdown, Restart, Uninstall, Stay };
}
