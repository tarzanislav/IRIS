using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Iris.Vision.Training;

namespace Iris.Lab;

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

    /// <summary>What shutdown and restart do (tests replace it; by default the system's shutdown command).</summary>
    public Action<string> Power { get; init; } = DefaultPower;

    /// <summary>Lines of progress (the service writes them to its log).</summary>
    public Action<string>? Log { get; init; }

    private static void DefaultPower(string command)
    {
        var restart = command == LabCommands.Restart;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("shutdown.exe", restart ? "/r /t 60 /c \"IRIS\"" : "/s /t 60 /c \"IRIS\"")
            : new ProcessStartInfo("systemctl", restart ? "reboot" : "poweroff");
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
    private readonly Lock _gate = new();
    private Dictionary<string, string[]> _sentences = [];
    private LabLearner? _learner;
    private CancellationTokenSource? _learning;
    private double _duty = 0.5;
    private double _cores = 0.5;
    private int _batch = 32;
    private bool _eco = true;
    private bool? _ecoSet;
    private bool _paused;
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

        StopLearning();
        _learner?.Dispose();
    }

    private async Task Session(LabChannel channel, CancellationToken cancel)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var status = Task.Run(async () =>
        {
            while (!session.IsCancellationRequested)
            {
                _sample = _machine.Sample();
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
                        StartRound(channel, message, session.Token);
                        break;
                    case MessageKind.Command:
                        await Command(message.Header["command"]?.GetValue<string>() ?? "");
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
            ["state"] = _paused ? "paused" : _state,
            ["round"] = _round,
            ["machine"] = _sample?.ToJson(),
            ["threads"] = _learner?.Threads,
            ["duty"] = _duty,
            ["eco"] = _ecoSet,
            ["mac"] = _mac,
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

    private int Threads() => Math.Max(1, (int)Math.Floor(Environment.ProcessorCount * _cores));

    // the efficiency mode as the hub wants it (null in the status: the system has no such mode)
    private void ApplyEfficiency() => _ecoSet = Machine.SetEfficiency(_eco) ? _eco : null;

    private void StartRound(LabChannel channel, LabMessage message, CancellationToken cancel)
    {
        StopLearning();
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
        _ = Task.Run(async () =>
        {
            // learning runs below normal priority: whatever the computer is asked to do comes first
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var work = learner.Learn(TimeSpan.FromSeconds(seconds), rate, MayGoOn, learning.Token);
            _last = work;
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
        }, CancellationToken.None);
    }

    // goes on while not paused and nobody else needs the processor
    private bool MayGoOn() => !_paused && (_sample is not { } s || s.CpuOthers < _options.OthersLimit);

    private void StopLearning()
    {
        _learning?.Cancel();
        _learning = null;
    }

    private Task Command(string command)
    {
        _options.Log?.Invoke($"command: {command}");
        switch (command)
        {
            case LabCommands.Pause:
                _paused = true;
                StopLearning();
                break;
            case LabCommands.Resume:
                _paused = false;
                break;
            case LabCommands.Shutdown or LabCommands.Restart:
                StopLearning();
                _options.Power(command);
                break;
            default:
                break; // anything else is not a command of IRIS
        }

        return Task.CompletedTask;
    }
}
