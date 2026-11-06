using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Iris.Vision.Training;

namespace Iris.Lab;

/// <summary>
/// What a computer measured while it learned one round (every few seconds): the mean load, its own and of the
/// others, and the highest strain with its reasons – sent with the round's change for the log of efficiency.
/// </summary>
internal sealed class RoundLoad
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _reasons = new(StringComparer.Ordinal);
    private int _count;
    private double _own;
    private double _total;
    private double _others;
    private int _strain;
    private int _movingLooks;
    private double _movingLevel;
    private int _movingUps;
    private int _movingBacks;
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);

    public void Add(MachineSample sample, Strain? strain)
    {
        lock (_gate)
        {
            AddLocked(sample, strain);
        }
    }

    private void AddLocked(MachineSample sample, Strain? strain)
    {
        _count++;
        _own += sample.CpuOwn;
        _total += sample.CpuTotal;
        _others += sample.CpuOthers;
        if (strain is { Level: > 0 })
        {
            _strain = Math.Max(_strain, strain.Level);
            foreach (var (code, _) in strain.Reasons)
            {
                _reasons.Add(code);
            }
        }
    }

    /// <summary>One look of the moving load during the round: its level, why it held (or null) and whether it added (+1) or gave back (−1).</summary>
    public void Moving(double level, string? holding, int change)
    {
        lock (_gate)
        {
            _movingLooks++;
            _movingLevel += level;
            _movingUps += change > 0 ? 1 : 0;
            _movingBacks += change < 0 ? 1 : 0;
            if (holding is not null)
            {
                _held[holding] = _held.GetValueOrDefault(holding) + 1;
            }
        }
    }

    public JsonObject Receipt(RoundWork work, double asked, bool eco, bool moving)
    {
        lock (_gate)
        {
            return ReceiptLocked(work, asked, eco, moving);
        }
    }

    // the threads and the share of time as they really were (the moving load changes them during a round)
    private JsonObject ReceiptLocked(RoundWork work, double asked, bool eco, bool moving) => new()
    {
        ["logical"] = Environment.ProcessorCount,
        ["asked"] = Math.Round(asked, 1),
        ["stopped"] = work.Stopped,
        ["threads"] = Math.Round(work.Threads, 1),
        ["duty"] = Math.Round(work.Duty, 2),
        ["eco"] = eco,
        ["moving"] = moving,
        ["moving_level"] = _movingLooks > 0 ? Math.Round(_movingLevel / _movingLooks, 2) : null,
        ["moving_ups"] = _movingLooks > 0 ? _movingUps : null,
        ["moving_backs"] = _movingLooks > 0 ? _movingBacks : null,
        ["moving_held"] = _held.Count > 0 ? string.Join(',', _held.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}:{h.Value}")) : null,
        ["per_thread"] = work.ThreadSeconds > 0 ? Math.Round(work.PerThread, 3) : null,
        ["cpu_own"] = _count > 0 ? Math.Round(_own / _count, 1) : null,
        ["cpu_total"] = _count > 0 ? Math.Round(_total / _count, 1) : null,
        ["cpu_others"] = _count > 0 ? Math.Round(_others / _count, 1) : null,
        ["strain"] = _strain,
        ["reasons"] = string.Join(',', _reasons.Order(StringComparer.Ordinal)),
    };
}

/// <summary>A shutdown or restart: at once or after a minute's warning, by force or not.</summary>
public sealed record PowerRequest(string Command, bool Now = false, bool Force = false)
{
    public override string ToString() => Command + (Now ? " now" : "") + (Force ? " force" : "");
}

/// <summary>How a computer of the classroom takes part.</summary>
public sealed record LabAgentOptions
{
    public required string Hub { get; init; }

    public int Port { get; init; } = 47620;

    /// <summary>The name of this computer as the console shows it.</summary>
    public string Name { get; init; } = Environment.MachineName;

    public required byte[] Key { get; init; }

    /// <summary>Folders with fonts (the free fonts beside the agent, the fonts of Windows).</summary>
    public required IReadOnlyList<string> Fonts { get; init; }

    public int Seed { get; init; } = Environment.MachineName.GetHashCode(StringComparison.Ordinal) & 0x7fffffff;

    /// <summary>Above this share of the processor used by others (%), the learning steps aside – someone works here.</summary>
    public double OthersLimit { get; init; } = 35;

    /// <summary>Seconds between two status messages.</summary>
    public double StatusSeconds { get; init; } = 10;

    /// <summary>
    /// How the agent removes itself when the console asks (after its round): the service sets it to take away the
    /// service, its program, settings and key once the agent has stopped. By default nothing is installed to remove.
    /// </summary>
    public Action Leave { get; init; } = () => { };

    /// <summary>What shutdown and restart do (tests replace it; by default the system's shutdown command).</summary>
    public Action<PowerRequest> Power { get; init; } = DefaultPower;

    /// <summary>Lines of progress (the service writes them to its log).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// The program file of the agent, replaced by a newer signed one from the hub (null: never updated, e.g. in tests).
    /// </summary>
    public string? Program { get; init; }

    /// <summary>
    /// What happens after the program was replaced: by default the process ends with code 3, and the service manager
    /// starts the service again (its recovery), now with the new program.
    /// </summary>
    public Action Restart { get; init; } = () => Environment.Exit(3);

    // Windows: a minute's warning to the user (with it Windows closes the programs itself), or at once; by force the
    // programs that hold the shutdown up are closed without asking (unsaved work is lost)
    private static void DefaultPower(PowerRequest request)
    {
        var restart = request.Command == LabCommands.Restart;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("shutdown.exe", $"{(restart ? "/r" : "/s")} /t {(request.Now ? 0 : 60)}{(request.Force ? " /f" : "")} /c \"IRIS\"")
            : new ProcessStartInfo("systemctl", (restart ? "reboot" : "poweroff") + (request.Force ? " --force" : ""));
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var _ = Process.Start(start);
    }
}

/// <summary>
/// One computer of the classroom: connects to the hub (it never listens itself), sends how it is doing every few
/// seconds, takes the model of each round, learns until the deadline – stepping aside while someone uses the
/// computer – and sends back what changed. It carries out only the fixed commands (pause, resume, shutdown,
/// restart); everything it receives is signed with the key of the classroom.
/// </summary>
public sealed class LabAgent
{
    private readonly LabAgentOptions _options;
    private readonly Machine _machine = new();
    private readonly StrainCounters _counters = new();
    private readonly StrainJudge _judge = new();
    private StrainSignals _signals = StrainSignals.None;
    private Strain? _strain;
    private List<(string Name, double Share)> _others = [];
    private RoundLoad _load = new();
    private bool _lookingAtOthers;
    private readonly Lock _gate = new();
    private Dictionary<string, string[]> _sentences = [];
    private LabLearner? _learner;
    private CancellationTokenSource? _learning;
    private Task _learningTask = Task.CompletedTask;
    private LoadSettings _loadSettings = new(0.5, 0.5);
    private readonly LoadControl _control = new();
    private readonly int? _physicalCores = Inventory.PhysicalCores();
    private double _duty = 0.5;
    private int _threads = LoadControl.Threads(0.5, Environment.ProcessorCount);
    private int _batch = 32;
    private bool _eco = true;
    private bool? _ecoSet;
    private double _hot = 90;
    private bool _tooHot;
    private bool _paused;
    private bool _leaving;
    private bool _left;
    private string _state = "starting";
    private MachineSample? _sample;
    private RoundWork? _last;
    private int _round;
    private string? _mac;

    public LabAgent(LabAgentOptions options) => _options = options;

    /// <summary>Runs until cancelled: connects, and after a lost connection connects again (waiting longer each time).</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        ApplyEfficiency();
        var delay = TimeSpan.FromSeconds(5);
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_options.Hub, _options.Port, cancel);
                using var channel = await LabChannel.ConnectAsync(client.GetStream(), LabRole.Agent, _options.Name, _options.Key, cancel);
                if (client.Client.LocalEndPoint is System.Net.IPEndPoint local)
                {
                    // the card that reaches the hub: its MAC lets the hub wake this computer when it is off
                    _mac = NetworkScanner.MacOf(local.Address);
                }

                _options.Log?.Invoke($"connected to {_options.Hub}:{_options.Port}");
                delay = TimeSpan.FromSeconds(5);
                await Session(channel, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is IOException or SocketException or LabProtocolException or OperationCanceledException or ObjectDisposedException)
            {
                _options.Log?.Invoke($"no connection: {e.Message}");
            }

            StopLearning();
            try
            {
                await Task.Delay(delay, cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
        }

        await StopLearningAsync();
        _learner?.Dispose();
    }

    private async Task Session(LabChannel channel, CancellationToken cancel)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _ = Task.Run(async () =>
        {
            // what this computer is, once per connection (the firmware's tables, the cores, disks, the system)
            try
            {
                await channel.SendAsync(new LabMessage(MessageKind.Inventory, Inventory.Collect()), session.Token);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
            }
        }, session.Token);
        var status = Task.Run(async () =>
        {
            while (!session.IsCancellationRequested)
            {
                _sample = _machine.Sample();
                _signals = _counters.Read();
                // who the others are, when they take much (the reading of all processes is kept for then); the
                // first look only prepares the next one
                _others = _sample.CpuOthers >= 15 || _lookingAtOthers ? _machine.Others() : [];
                _lookingAtOthers = _sample.CpuOthers >= 15;
                if (_state == "learning")
                {
                    _load.Add(_sample, _strain);
                }
                _strain = _judge.Judge(_sample, _signals, _ecoSet ?? _eco, _state == "learning", _learner?.Threads ?? _threads, _duty);
                if (_loadSettings.Moving && _state == "learning" && _sample is { } now)
                {
                    var before = _control.Level;
                    if (_control.Look(DateTime.UtcNow, _strain.Level, now.CpuOthers, now.Temperature, _hot, now.IdleSeconds))
                    {
                        ApplyLoad();
                    }

                    _load.Moving(_control.Level, _control.Holding, Math.Sign(_control.Level - before));
                }

                await channel.SendAsync(new LabMessage(MessageKind.Status, Status()), session.Token);
                await Task.Delay(TimeSpan.FromSeconds(_options.StatusSeconds), session.Token);
            }
        }, session.Token);
        try
        {
            while (!session.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(session.Token);
                switch (message.Kind)
                {
                    case MessageKind.Settings:
                        ApplySettings(message.Header);
                        break;
                    case MessageKind.Wait:
                        StopLearning();
                        _state = "waiting";
                        break;
                    case MessageKind.Model:
                        await StartRound(channel, message, session.Token);
                        break;
                    case MessageKind.Command:
                        await Command(channel, message.Header["command"]?.GetValue<string>() ?? "", session.Token,
                            message.Header["now"]?.GetValue<bool>() == true, message.Header["force"]?.GetValue<bool>() == true);
                        break;
                    case MessageKind.Update:
                        Update(message);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            session.Cancel();
            StopLearning();
            try
            {
                await status;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private JsonObject Status()
    {
        var json = new JsonObject
        {
            ["state"] = _left ? "uninstalling" : _leaving ? "leaving" : _paused ? "paused" : _state,
            ["round"] = _round,
            ["machine"] = _sample?.ToJson(),
            ["strain"] = _strain?.ToJson(),
            ["signals"] = new JsonObject
            {
                ["performance"] = Round(_signals.Performance),
                ["limit"] = Round(_signals.Limit),
                ["queue"] = Round(_signals.Queue),
                ["free_mb"] = Round(_signals.FreeMb),
                ["pages_in"] = Round(_signals.PagesIn),
                ["disk"] = Round(_signals.DiskBusy),
                ["usual_performance"] = Round(_judge.UsualPerformance(_ecoSet ?? _eco)),
                ["temperature_fixed"] = _machine.FixedTemperature,
                ["others"] = new JsonArray([.. _others.Select(o => (JsonNode?)new JsonObject { ["name"] = o.Name, ["cpu"] = Math.Round(o.Share, 1) })]),
            },
            ["threads"] = _learner?.Threads,
            ["duty"] = _duty,
            ["moving"] = _loadSettings.Moving ? _control.ToJson() : null,
            ["eco"] = _ecoSet,
            ["too_hot"] = _tooHot,
            ["mac"] = _mac,
            ["version"] = LabUpdates.Version,
        };
        if (_last is { } last)
        {
            json["last"] = new JsonObject
            {
                ["steps"] = last.Steps,
                ["lines"] = last.Lines,
                ["loss"] = double.IsFinite(last.Loss) ? Math.Round(last.Loss, 4) : null,
                ["seconds"] = Math.Round(last.Seconds),
                ["lines_per_second"] = last.Seconds > 0 ? Math.Round(last.Lines / last.Seconds, 2) : 0,
            };
        }

        return json;
    }

    private void ApplySettings(JsonObject settings)
    {
        var load = LoadSettings.From(settings, _loadSettings);
        if (!load.Moving)
        {
            _control.Reset();
        }

        _loadSettings = load;
        _batch = Math.Clamp(settings["batch"]?.GetValue<int>() ?? _batch, 1, 256);
        _eco = settings["eco"]?.GetValue<bool>() ?? _eco;
        _hot = Math.Clamp(settings["hot"]?.GetValue<double>() ?? _hot, 50, 100);
        ApplyEfficiency();
        if (settings["sentences"] is JsonObject sentences)
        {
            _sentences = sentences.ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(s => s!.GetValue<string>()).ToArray(), StringComparer.Ordinal);
        }

        ApplyLoad();
    }

    // the share of time and the threads now: the set load, or with the moving load where it has got to
    private void ApplyLoad()
    {
        (_duty, _threads) = _loadSettings.Moving
            ? _control.Load(_loadSettings, Environment.ProcessorCount, _physicalCores)
            : (_loadSettings.Duty, LoadControl.Threads(_loadSettings.Cores, Environment.ProcessorCount));
        if (_learner is { } learner)
        {
            learner.Duty = _duty;
            learner.BatchSize = _batch;
            learner.Threads = _threads;
        }
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;

    // the efficiency mode as the hub wants it (null in the status: the system has no such mode)
    private void ApplyEfficiency() => _ecoSet = Machine.SetEfficiency(_eco) ? _eco : null;

    private async Task StartRound(LabChannel channel, LabMessage message, CancellationToken cancel)
    {
        if (_leaving || _left)
        {
            return; // it is going away: no new round
        }

        // the learning of an earlier round ends first (its step finishes): one network never learns twice at once
        await StopLearningAsync();
        var shape = OcrShape.FromJson(message.Header["shape"]!.AsObject());
        lock (_gate)
        {
            if (_learner is null || !_learner.Weights.Shape.Charset.Equals(shape.Charset, StringComparison.Ordinal))
            {
                _learner?.Dispose();
                var fonts = LineArtist.FindFonts(_options.Fonts, shape.Charset, ["Math", "Display", "Unifont", "Symbol"]);
                if (fonts.Count == 0)
                {
                    _state = "no fonts";
                    return;
                }

                // two drawing threads: the faster learning must not wait for lines (they rest when the queue is full)
                _learner = new LabLearner(new OcrWeights(shape), new LineArtist(fonts), _sentences, _options.Seed, drawingThreads: 2);
            }

            _learner.Duty = _duty;
            _learner.BatchSize = _batch;
            _learner.Threads = _threads;
        }

        var learner = _learner;
        TensorPack.Unpack(message.Header["layout"]!.AsArray(), message.Data, learner.Weights);
        var start = learner.Weights.Copy();
        var round = message.Header["round"]!.GetValue<int>();
        var rate = message.Header["rate"]!.GetValue<double>();
        var seconds = message.Header["seconds"]!.GetValue<double>();
        var learning = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _learning = learning;
        _round = round;
        _load = new RoundLoad();
        _state = "learning";
        _learningTask = Task.Run(async () =>
        {
            // learning runs below normal priority: whatever the computer is asked to do comes first
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var work = learner.Learn(TimeSpan.FromSeconds(seconds), rate, MayGoOn, learning.Token);
            _last = work;
            if (!work.Stopped && work.ThreadSeconds > 0)
            {
                _judge.Round(work.PerThread, _ecoSet ?? _eco, (int)Math.Round(work.Threads), Math.Round(work.Duty, 2));
            }
            _options.Log?.Invoke($"round {round}: {work.Steps} steps, {work.Lines} lines, loss {work.Loss:F3}, {work.Seconds:F0} s");
            var (layout, data) = TensorPack.Pack(TensorPack.Difference(learner.Weights, start));
            var header = new JsonObject
            {
                ["round"] = round,
                ["lines"] = work.Lines,
                ["steps"] = work.Steps,
                ["seconds"] = Math.Round(work.Seconds, 1),
                ["loss"] = double.IsFinite(work.Loss) ? work.Loss : null,
                ["layout"] = layout,
                ["receipt"] = _load.Receipt(work, seconds, _ecoSet ?? _eco, _loadSettings.Moving),
            };
            _state = "idle";
            try
            {
                await channel.SendAsync(new LabMessage(MessageKind.Delta, header, data), cancel);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
            }

            if (_leaving)
            {
                await LeaveAsync(channel, cancel); // the round is done and sent: now it goes
            }
        }, CancellationToken.None);
    }

    // goes on while not paused, nobody else needs the processor and the computer is not too warm (it goes on again
    // five degrees below the limit)
    private bool MayGoOn()
    {
        if (_sample?.Temperature is { } temperature)
        {
            _tooHot = temperature >= _hot || (_tooHot && temperature > _hot - 5);
        }

        return !_paused && !_tooHot && (_sample is not { } s || s.CpuOthers < _options.OthersLimit);
    }

    private void StopLearning()
    {
        _learning?.Cancel();
        _learning = null;
    }

    // stops the learning and waits until its step is over (before the network is used again or disposed)
    private async Task StopLearningAsync()
    {
        StopLearning();
        await _learningTask.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // a newer program from the hub: believed only with the signature of the IRIS releases and the right SHA-256
    private void Update(LabMessage message)
    {
        try
        {
            var manifest = LabUpdates.Open(Convert.FromBase64String(message.Header["manifest"]?.GetValue<string>() ?? ""),
                Convert.FromBase64String(message.Header["signature"]?.GetValue<string>() ?? ""));
            if (manifest is null || manifest.File(UpdateManifest.Agent) is not { } file || !LabUpdates.Matches(file, message.Data))
            {
                _options.Log?.Invoke("update refused: the signature or the file is not right");
                return;
            }

            if (!LabUpdates.IsNewer(manifest.Version, LabUpdates.Version) || _options.Program is not { } program)
            {
                return;
            }

            StopLearning();
            LabUpdates.ReplaceProgram(program, message.Data);
            _options.Log?.Invoke($"updated to {manifest.Version}; starting again");
            _options.Restart();
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _options.Log?.Invoke($"update failed: {e.Message}");
        }
    }

    private async Task Command(LabChannel channel, string command, CancellationToken cancel, bool now = false, bool force = false)
    {
        _options.Log?.Invoke($"command: {command}{(now ? " now" : "")}{(force ? " by force" : "")}");
        switch (command)
        {
            case LabCommands.Uninstall when !_left:
                _leaving = true;
                if (_learningTask.IsCompleted || _state != "learning")
                {
                    await LeaveAsync(channel, cancel); // nothing to finish
                }

                break; // else it goes when the round is sent
            case LabCommands.Stay when !_left:
                _leaving = false;
                break;
            case LabCommands.Pause:
                _paused = true;
                StopLearning();
                break;
            case LabCommands.Resume:
                _paused = false;
                break;
            case LabCommands.Shutdown or LabCommands.Restart:
                StopLearning();
                _options.Power(new PowerRequest(command, now, force));
                break;
            default:
                break; // anything else is not a command of IRIS
        }
    }

    // tells the hub it removes itself, then hands over to the removal (which stops the service)
    private async Task LeaveAsync(LabChannel channel, CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_left || !_leaving)
            {
                return;
            }

            _left = true;
        }

        StopLearning();
        _options.Log?.Invoke("uninstalling as the console asked");
        try
        {
            await channel.SendAsync(new LabMessage(MessageKind.Status, Status()), cancel);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }

        _options.Leave();
    }
}
