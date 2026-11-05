using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Iris.Vision.Training;

namespace Iris.Lab;

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
    private readonly Lock _gate = new();
    private Dictionary<string, string[]> _sentences = [];
    private LabLearner? _learner;
    private CancellationTokenSource? _learning;
    private Task _learningTask = Task.CompletedTask;
    private double _duty = 0.5;
    private double _cores = 0.5;
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
                _strain = _judge.Judge(_sample, _signals, _ecoSet ?? _eco, _state == "learning");
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
            },
            ["threads"] = _learner?.Threads,
            ["duty"] = _duty,
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
        _duty = Math.Clamp(settings["duty"]?.GetValue<double>() ?? _duty, 0.05, 1);
        _cores = Math.Clamp(settings["cores"]?.GetValue<double>() ?? _cores, 0.05, 1);
        _batch = Math.Clamp(settings["batch"]?.GetValue<int>() ?? _batch, 1, 256);
        _eco = settings["eco"]?.GetValue<bool>() ?? _eco;
        _hot = Math.Clamp(settings["hot"]?.GetValue<double>() ?? _hot, 50, 100);
        ApplyEfficiency();
        if (settings["sentences"] is JsonObject sentences)
        {
            _sentences = sentences.ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(s => s!.GetValue<string>()).ToArray(), StringComparer.Ordinal);
        }

        if (_learner is { } learner)
        {
            learner.Duty = _duty;
            learner.BatchSize = _batch;
            learner.Threads = Threads();
        }
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;

    private int Threads() => Math.Max(1, (int)Math.Floor(Environment.ProcessorCount * _cores));

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
            _learner.Threads = Threads();
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
        _state = "learning";
        _learningTask = Task.Run(async () =>
        {
            // learning runs below normal priority: whatever the computer is asked to do comes first
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var work = learner.Learn(TimeSpan.FromSeconds(seconds), rate, MayGoOn, learning.Token);
            _last = work;
            if (!work.Stopped && work.Seconds > 0)
            {
                _judge.Round(work.Lines / (work.Seconds * Math.Max(0.05, _duty) * Math.Max(1, learner.Threads)), _ecoSet ?? _eco);
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
