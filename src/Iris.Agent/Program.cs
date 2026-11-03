using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Lab;
using Iris.Vision.Training;

// iris-agent – a computer of the classroom that learns the IRIS OCR. Exit codes: 0 done, 1 error, 2 wrong use.
Console.OutputEncoding = Encoding.UTF8;
try
{
    return args switch
    {
        ["run", .. var rest] => await RunConsole(LabArguments.Parse(rest)),
        ["test", .. var rest] => Test(LabArguments.Parse(rest)),
        ["service"] when OperatingSystem.IsWindows() => Service(),
        ["install", .. var rest] when OperatingSystem.IsWindows() => Install(LabArguments.Parse(rest)),
        ["uninstall"] when OperatingSystem.IsWindows() => Installer.Uninstall(),
        _ => Usage(),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"iris-agent: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"iris-agent: {ex.Message}");
    return 1;
}

// in a console window (for trying it out): learns with the hub until Ctrl+C
static async Task<int> RunConsole(LabArguments a)
{
    var options = new LabAgentOptions
    {
        Hub = a.Required("--hub"),
        Port = (int)(a.Number("--port") ?? 47620),
        Key = LabKeys.Read(a.Required("--key")),
        Name = a.Value("--name") ?? Environment.MachineName,
        Fonts = Fonts(a.Value("--fonts")),
        Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
    };
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    Lower();
    await new LabAgent(options).RunAsync(cancel.Token);
    return 0;
}

// the service: settings and key from the protected folder of ProgramData, a log beside them
static int Service()
{
    if (!OperatingSystem.IsWindows())
    {
        return 2;
    }

    var data = Installer.DataFolder;
    var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "agent.json")))!.AsObject();
    var log = Path.Combine(data, "agent.log");
    var options = new LabAgentOptions
    {
        Hub = settings["hub"]!.GetValue<string>(),
        Port = settings["port"]?.GetValue<int>() ?? 47620,
        Name = settings["name"]?.GetValue<string>() ?? Environment.MachineName,
        Key = LabKeys.Read(Path.Combine(data, "agent.key")),
        Fonts = Fonts(null),
        Log = line => Log(log, line),
    };
    Lower();
    var started = WindowsService.Run(Installer.ServiceName, token => new LabAgent(options).RunAsync(token));
    return started ? 0 : 1;
}

static int Install(LabArguments a)
{
    if (!OperatingSystem.IsWindows())
    {
        return 2;
    }

    return Installer.Install(a.Required("--hub"), (int)(a.Number("--port") ?? 47620), a.Required("--key"), a.Value("--name"));
}

// a trial without the hub: learns here for some minutes and shows every 10 s how busy and warm the computer is
static int Test(LabArguments a)
{
    var minutes = a.Number("--minutes") ?? 10;
    var duty = Math.Clamp(a.Number("--duty") ?? 0.5, 0.05, 1);
    var cores = Math.Clamp(a.Number("--cores") ?? 0.5, 0.05, 1);
    var eco = (a.Value("--eco") ?? "yes") != "no";
    var shape = OcrShape.Default;
    var fonts = LineArtist.FindFonts(Fonts(a.Value("--fonts")), shape.Charset, ["Math", "Display", "Unifont", "Symbol"]);
    if (fonts.Count == 0)
    {
        throw new ArgumentException("no font with all the characters was found (--fonts)");
    }

    var threads = Math.Max(1, (int)(Environment.ProcessorCount * cores));
    var efficient = Machine.SetEfficiency(eco) && eco;
    Console.WriteLine($"IRIS trial: {minutes} min, {threads} of {Environment.ProcessorCount} threads, works {duty:P0} of the time, {fonts.Count} fonts{(efficient ? ", efficiency mode" : "")}");
    Console.WriteLine("time      processor  learning  memory  temperature  lines/s");
    using var learner = new LabLearner(OcrWeights.Create(shape, 1), new LineArtist(fonts), new Dictionary<string, string[]>(), seed: 1, drawingThreads: 2)
    {
        Threads = threads,
        Duty = duty,
    };
    var machine = new Machine();
    machine.Sample();
    var watch = Stopwatch.StartNew();
    var samples = new List<MachineSample>();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    var reporter = Task.Run(async () =>
    {
        var last = 0L;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            var s = machine.Sample();
            samples.Add(s);
            var done = learner.LinesTotal;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{watch.Elapsed:mm\\:ss}     {s.CpuTotal,5:F0} %    {s.CpuOwn,5:F0} %   {s.MemoryUsed,4:F0} %   {(s.Temperature is { } t ? $"{t,6:F0} °C" : "     –  ")}    {(done - last) / 10.0,5:F1}"));
            last = done;
        }
    });
    while (watch.Elapsed.TotalMinutes < minutes && !stop.IsCancellationRequested)
    {
        learner.Learn(TimeSpan.FromSeconds(30), 1e-4, () => true, stop.Token);
    }

    stop.Cancel();
    reporter.Wait();
    var temperatures = samples.Where(s => s.Temperature is not null).Select(s => s.Temperature!.Value).ToList();
    var processor = samples.Count > 0 ? samples.Average(s => s.CpuTotal) : 0;
    var own = samples.Count > 0 ? samples.Average(s => s.CpuOwn) : 0;
    var warmth = temperatures.Count > 0 ? FormattableString.Invariant($", temperature {temperatures.Min():F0}–{temperatures.Max():F0} °C") : ", no temperature sensor offered";
    Console.WriteLine(FormattableString.Invariant($"done: {learner.LinesTotal / watch.Elapsed.TotalSeconds:F1} lines/s, processor on average {processor:F0} % (learning {own:F0} %){warmth}"));
    return 0;
}

// the free fonts beside the program and the fonts of the system
static string[] Fonts(string? given)
{
    if (given is not null)
    {
        return given.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    var beside = Path.Combine(AppContext.BaseDirectory, "fonts");
    return OperatingSystem.IsWindows()
        ? [beside, Environment.GetFolderPath(Environment.SpecialFolder.Fonts)]
        : [beside, "/usr/share/fonts", "/usr/local/share/fonts"];
}

// the learning gives way to everything else on the computer
static void Lower()
{
    try
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
    {
    }
}

static void Log(string path, string line)
{
    try
    {
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
        {
            File.Move(path, path + ".1", overwrite: true);
        }

        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
    }
    catch (IOException)
    {
    }
}

static int Usage()
{
    Console.Error.WriteLine("""
        iris-agent test [--minutes M] [--duty D] [--cores F] [--eco yes|no] [--fonts <dir;dir>]
                                              a trial on this computer without the hub: learns M minutes (10) with a
                                              share of time D (0.5) and of cores F (0.5), in the efficiency mode of
                                              Windows (yes), shows load and temperature
        iris-agent run --hub <server> --key <agent.key> [--port N] [--name N] [--fonts <dir;dir>]
                                              learns with the hub in this window (Ctrl+C ends it)
        iris-agent install --hub <server> --key <agent.key> [--port N] [--name N]
                                              (Windows, as administrator) installs the service "IRIS-Lab"
        iris-agent uninstall                  (Windows, as administrator) removes the service and its files
        """);
    return 2;
}
