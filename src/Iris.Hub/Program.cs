using System.Globalization;
using System.Text;
using Iris.Lab;
using Iris.Vision.Training;

// iris-hub – the hub of the IRIS learning in a classroom. Exit codes: 0 done, 1 error, 2 wrong use.
Console.OutputEncoding = Encoding.UTF8;
try
{
    return args switch
    {
        ["run", .. var rest] => await Run(LabArguments.Parse(rest)),
        ["keys", .. var rest] => Keys(LabArguments.Parse(rest)),
        _ => Usage(),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"iris-hub: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"iris-hub: {ex.Message}");
    return 1;
}

static async Task<int> Run(LabArguments a)
{
    var folder = a.Required("--folder");
    var model = LabHub.ModelPath(folder);
    OcrWeights weights;
    OcrLearningState state;
    if (File.Exists(model))
    {
        (weights, _, state) = OcrFiles.LoadTraining(model);
    }
    else if (a.Value("--start") is { } start)
    {
        (weights, _, state) = OcrFiles.LoadTraining(start);
    }
    else
    {
        weights = OcrWeights.Create(OcrShape.Default, seed: 1);
        state = new OcrLearningState();
    }

    var rate = a.Number("--rate");
    var planRounds = (int)(a.Number("--plan-rounds") ?? 600);
    if (rate is not null || state.PlanEnd <= state.Step + 0.5)
    {
        // a new plan of the rate, counted in rounds from here
        state = state with { PlanStart = state.Step, PlanEnd = state.Step + planRounds, PlanRate = rate ?? 3e-4 };
    }

    var schedule = new LabSchedule(
        TimeOnly.ParseExact(a.Value("--from") ?? "15:00", "HH:mm", CultureInfo.InvariantCulture),
        TimeOnly.ParseExact(a.Value("--until") ?? "06:00", "HH:mm", CultureInfo.InvariantCulture),
        (a.Value("--weekends") ?? "yes") != "no");
    var options = new LabHubOptions
    {
        Folder = folder,
        Port = (int)(a.Number("--port") ?? 47620),
        Backup = a.Value("--backup"),
        RoundMinutes = a.Number("--round") ?? 10,
        Duty = a.Number("--duty") ?? 0.5,
        CoreShare = a.Number("--cores") ?? 0.5,
        Rate = state.PlanRate,
        PlanRounds = planRounds,
        Schedule = schedule,
        Sentences = a.Value("--sentences"),
        AutoWake = (a.Value("--wake") ?? "yes") != "no",
        Fonts = a.Value("--fonts")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? ["/usr/share/fonts", "/usr/local/share/fonts"],
    };
    var hub = new LabHub(options, weights, state);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cancel.Cancel();
    Console.WriteLine($"IRIS hub on port {options.Port}, model version {state.Step}, rounds of {options.RoundMinutes} min, " +
                      $"learning {schedule.From:HH\\:mm}–{schedule.Until:HH\\:mm}{(schedule.Weekends ? ", weekends all day" : "")}; keys in {folder}");
    await hub.RunAsync(cancel.Token);
    return 0;
}

static int Keys(LabArguments a)
{
    var folder = a.Required("--folder");
    LabKeys.ReadOrCreate(Path.Combine(folder, "agent.key"));
    LabKeys.ReadOrCreate(Path.Combine(folder, "console.key"));
    Console.WriteLine($"the key of the computers: {Path.Combine(folder, "agent.key")}");
    Console.WriteLine($"the key of the console:   {Path.Combine(folder, "console.key")}");
    Console.WriteLine("copy agent.key to the computers when installing them, console.key only to the console – nowhere else");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        iris-hub run --folder <dir> [options]   the hub of the classroom learning (Ctrl+C ends it)
            --start <iris-ocr-train.iris>       the model to begin with (when the folder has none yet)
            --port N (47620), --backup <dir>    a copy of the model after each round (e.g. the shared folder)
            --round M (10)                      minutes of a round
            --rate R (0.0003), --plan-rounds N (600)   a new plan of the learning rate
            --duty D (0.5), --cores F (0.5)     share of time and of cores the computers use
            --from HH:mm (15:00), --until HH:mm (06:00), --weekends yes|no (yes)
            --sentences <dir>                   sk.txt, cs.txt, en.txt for the lines
            --wake yes|no (yes)                 wakes the computers that are off when learning begins, and shuts them
                                                down again when it ends (Wake-on-LAN must be on in their BIOS/UEFI)
            --fonts <dir:dir>                   fonts of the server for measuring the model (/usr/share/fonts)
        iris-hub keys --folder <dir>            creates the keys (agent.key, console.key) and shows where they are
        """);
    return 2;
}
