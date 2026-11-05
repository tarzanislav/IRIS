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
        ["update", "--check", .. var rest] => await Update(LabArguments.Parse(rest), check: true),
        ["update", .. var rest] => await Update(LabArguments.Parse(rest), check: false),
        ["signing-key", .. var rest] => SigningKey(LabArguments.Parse(rest)),
        ["console-key", "add", var name, .. var rest] => ConsoleKeyAdd(name, LabArguments.Parse(rest)),
        ["console-key", "remove", var name, .. var rest] => ConsoleKeyRemove(name, LabArguments.Parse(rest)),
        ["console-key", "list", .. var rest] => ConsoleKeyList(LabArguments.Parse(rest)),
        ["version"] => Version(),
        _ => Usage(),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"iris-hub: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
{
    Console.Error.WriteLine($"iris-hub: {ex.Message}");
    return 1;
}

static async Task<int> Run(LabArguments a)
{
    var folder = a.Required("--folder");
    var log = new HubLog(folder, "hub");
    void Say(string text)
    {
        Console.WriteLine(text);
        log.Write(text);
    }

    var model = LabHub.ModelPath(folder);
    LabHub.TakeStartModel(folder, Say);
    OcrWeights weights;
    OcrLearningState state;
    string source;
    if (File.Exists(model))
    {
        (weights, _, state) = OcrFiles.LoadTraining(model);
        source = Source(model);
    }
    else if (a.Value("--start") is { } start)
    {
        (weights, _, state) = OcrFiles.LoadTraining(start);
        source = Source(start);
    }
    else
    {
        weights = OcrWeights.Create(OcrShape.Default, seed: 1);
        state = new OcrLearningState();
        source = "random (learning from the beginning)";
    }

    var rate = a.Number("--rate");
    var planRounds = (int)(a.Number("--plan-rounds") ?? 600);
    var fromLab = source.StartsWith("IRIS lab", StringComparison.Ordinal);
    if (rate is not null || state.PlanEnd <= state.Step + 0.5 || (!fromLab && state.Step > 0))
    {
        // a new plan of the rate, counted in rounds from here; a model learned elsewhere (its plan counts steps of
        // another learning, not rounds) gets a gentler one – it can already read
        state = state with { PlanStart = state.Step, PlanEnd = state.Step + planRounds, PlanRate = rate ?? (!fromLab && state.Step > 0 ? 1e-4 : 3e-4) };
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
        Eco = (a.Value("--eco") ?? "yes") != "no",
        Rate = state.PlanRate,
        PlanRounds = planRounds,
        Schedule = schedule,
        Sentences = a.Value("--sentences"),
        AutoWake = (a.Value("--wake") ?? "yes") != "no",
        AutoUpdate = (a.Value("--auto-update") ?? "yes") != "no",
        Fonts = a.Value("--fonts")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? ["/usr/share/fonts", "/usr/local/share/fonts"],
        Log = log.Write,
        ModelSource = source,
    };
    var hub = new LabHub(options, weights, state);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cancel.Cancel();
    Say($"IRIS hub {LabUpdates.Version} on port {options.Port}, model version {state.Step} ({source}), rate {state.PlanRate:0.###e0} over {state.PlanEnd - state.PlanStart:0} rounds, " +
        $"rounds of {options.RoundMinutes} min, learning {schedule.From:HH\\:mm}–{schedule.Until:HH\\:mm}{(schedule.Weekends ? ", weekends all day" : "")}; keys in {folder}");
    await hub.RunAsync(cancel.Token);
    if (hub.RestartAsked)
    {
        // not 0, so that systemd (Restart=on-failure) starts it again
        Say("the hub ends for a restart (asked by the console); its service starts it again");
        return 75;
    }

    Say("the hub ended");
    return 0;
}

// where a model file came from (its provenance)
static string Source(string path)
{
    using var file = Iris.Model.ModelFile.Load(path);
    return file.Provenance["source"]?.GetValue<string>() ?? Path.GetFileName(path);
}


// the updater (run by systemd as root once a day, or when the console asks): the newest release on GitHub, when it
// brings newer signed classroom programs – the server's own program goes in place and the service starts again, the
// programs of the computers and the console wait in the folder of the hub for them. When the console asked for it,
// also the learning file of that release (checked against the signed manifest) is left for the hub to start from.
// Everything it says goes also into logs/update.log, and its result into update-result.json (the console shows both).
static async Task<int> Update(LabArguments a, bool check)
{
    var folder = a.Value("--folder") ?? "/var/lib/iris-hub";
    var log = new HubLog(folder, "update");
    var console = Console.Out;
    var errors = Console.Error;
    Console.SetOut(log.Tee(console));
    Console.SetError(log.Tee(errors));
    var result = new System.Text.Json.Nodes.JsonObject
    {
        ["time"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
        ["from"] = LabUpdates.Version,
    };
    try
    {
        var code = await UpdateCore(a, folder, check, result);
        result["ok"] = true;
        return code;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"iris-hub update: {e.Message}");
        result["ok"] = false;
        throw;
    }
    finally
    {
        Console.Out.Flush();
        Console.Error.Flush();
        result["message"] = log.Last;
        if (!check)
        {
            try
            {
                File.WriteAllText(LabHub.UpdateResultPath(folder), result.ToJsonString());
                HubFiles.GiveToHub(log.Folder, "iris-hub");
                HubFiles.GiveToHub(log.FilePath, "iris-hub");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        Console.SetOut(console);
        Console.SetError(errors);
    }
}

static async Task<int> UpdateCore(LabArguments a, string folder, bool check, System.Text.Json.Nodes.JsonObject result)
{
    var install = a.Value("--install") ?? Path.GetDirectoryName(Environment.ProcessPath) ?? "/opt/iris-hub";
    var service = a.Value("--service") ?? "iris-hub";
    var request = LabHub.UpdateRequestPath(folder);
    var wantModel = false;
    if (File.Exists(request))
    {
        try
        {
            wantModel = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(request))?["model"]?.GetValue<bool>() == true;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            // an older hub wrote only the time
        }

        File.Delete(request);
    }

    result["model_asked"] = wantModel;
    if (!LabUpdates.CanVerify)
    {
        Console.WriteLine("this build has no key of the IRIS releases: it never updates itself");
        return 0;
    }

    using var http = LabUpdates.Http();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    var latest = await LabUpdates.LatestAsync(http, timeout.Token);
    if (latest is not { } found)
    {
        Console.WriteLine($"{LabUpdates.Version}: the newest release has no signed classroom programs");
        return 0;
    }

    var version = found.Manifest.Version;
    result["newest"] = version;
    var newer = LabUpdates.IsNewer(version, LabUpdates.Version);
    if (!newer && !wantModel)
    {
        Console.WriteLine($"{LabUpdates.Version}: up to date");
        return 0;
    }

    if (check)
    {
        Console.WriteLine(newer ? $"{LabUpdates.Version}: version {version} is ready (iris-hub update installs it)" : $"{LabUpdates.Version}: up to date");
        return 0;
    }

    // every file checked against the signed manifest before anything is put in place
    byte[]? program = null;
    if (newer)
    {
        var target = Path.Combine(folder, "updates", version);
        Directory.CreateDirectory(target);
        foreach (var name in new[] { UpdateManifest.Hub, UpdateManifest.Agent, UpdateManifest.Console })
        {
            if (found.Manifest.File(name) is not { } file || !found.Files.TryGetValue(name, out var uri))
            {
                continue;
            }

            var data = await http.GetByteArrayAsync(uri, timeout.Token);
            if (!LabUpdates.Matches(file, data))
            {
                throw new IOException($"{name} of {version} does not match its signed manifest – nothing installed");
            }

            if (name == UpdateManifest.Hub)
            {
                program = data;
            }
            else
            {
                await File.WriteAllBytesAsync(Path.Combine(target, name), data, timeout.Token);
            }
        }

        await File.WriteAllBytesAsync(Path.Combine(target, UpdateManifest.ManifestName), found.Json, timeout.Token);
        await File.WriteAllBytesAsync(Path.Combine(target, UpdateManifest.SignatureName), found.Signature, timeout.Token);
        foreach (var old in Directory.EnumerateDirectories(Path.Combine(folder, "updates"))
            .Where(d => LabUpdates.ParseVersion(Path.GetFileName(d)) is not null)
            .OrderByDescending(d => LabUpdates.ParseVersion(Path.GetFileName(d))).Skip(2))
        {
            Directory.Delete(old, recursive: true); // the two newest sets stay
        }

        Console.WriteLine($"the programs of the computers and the console of {version} wait in {target}");
    }

    byte[]? model = null;
    if (wantModel)
    {
        if (found.Manifest.File(UpdateManifest.Training) is not { } file || !found.Files.TryGetValue(UpdateManifest.Training, out var uri))
        {
            Console.WriteLine($"the release {version} has no signed learning file ({UpdateManifest.Training}) – the model of the classroom stays");
        }
        else
        {
            model = await http.GetByteArrayAsync(uri, timeout.Token);
            if (!LabUpdates.Matches(file, model))
            {
                throw new IOException($"{UpdateManifest.Training} of {version} does not match its signed manifest – the model stays");
            }
        }
    }

    if (program is null && model is null)
    {
        return 0;
    }

    // The restart is started before anything is replaced: this program is one bundle file, and the runtime reads parts
    // of it (e.g. what starting a process needs) by its path only when they are first used – after the replacement
    // that path is the new program, and 0.4.1 failed there. So the shell waits for one line on its input and restarts
    // the hub after it; everything a process needs is loaded already.
    System.Diagnostics.Process? restart = null;
    if (OperatingSystem.IsLinux() && Environment.UserName == "root")
    {
        restart = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "read go && exec systemctl restart \"$0\"", service])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        });
    }

    try
    {
        if (model is not null)
        {
            var pending = LabHub.StartModelPath(folder);
            await File.WriteAllBytesAsync(pending + ".new", model, timeout.Token);
            HubFiles.GiveToHub(pending + ".new", "iris-hub");
            File.Move(pending + ".new", pending, overwrite: true);
            result["model"] = version;
            Console.WriteLine($"the learning file of {version} is ready: the hub starts from it (the model before is kept beside it)");
        }

        if (program is not null)
        {
            LabUpdates.ReplaceProgram(Path.Combine(install, "iris-hub"), program);
            result["installed"] = version;
            Console.WriteLine($"{LabUpdates.Version} → {version}: the server's program is in {install}");
        }
    }
    catch
    {
        restart?.Kill();
        restart?.Dispose();
        throw;
    }

    if (restart is not null)
    {
        using (restart)
        {
            restart.StandardInput.WriteLine("go");
            restart.StandardInput.Close();
            await restart.WaitForExitAsync(timeout.Token);
            Console.WriteLine(restart.ExitCode == 0 ? "the hub started again" : $"the restart of the hub ended with {restart.ExitCode}");
        }
    }

    return 0;
}

// a new key for signing the releases: the private part to a file (it goes into the secret IRIS_UPDATE_KEY of the
// repository and nowhere else), the public part printed (it goes into tools/lab/update-key.pub)
static int SigningKey(LabArguments a)
{
    var path = a.Value("--out") ?? "iris-update-key.pem";
    if (File.Exists(path))
    {
        throw new IOException($"{path} exists – a key is never overwritten");
    }

    using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem() + "\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    Console.WriteLine($"the private key is in {path}: put its whole text into the secret IRIS_UPDATE_KEY of the repository");
    Console.WriteLine("(GitHub → Settings → Secrets and variables → Actions → New repository secret), then delete the file.");
    Console.WriteLine("The public key, for tools/lab/update-key.pub:");
    Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
    return 0;
}

// a key of its own for one console (one administrator's computer): taken away alone, without the others' keys
static int ConsoleKeyAdd(string name, LabArguments a)
{
    var folder = a.Value("--folder") ?? "/var/lib/iris-hub";
    var path = LabKeys.CreateConsole(folder, name);
    HubFiles.GiveToHub(LabKeys.ConsoleFolder(folder), a.Value("--user") ?? "iris-hub");
    HubFiles.GiveToHub(path, a.Value("--user") ?? "iris-hub");
    Console.WriteLine($"the key of the console \"{name}\" is in {path}");
    if (a.Value("--out") is { } copy)
    {
        if (Directory.Exists(copy))
        {
            copy = Path.Combine(copy, name + ".key");
        }

        File.Copy(path, copy, overwrite: false);
        Console.WriteLine($"a copy for the console: {copy} – put it into the console (Settings → key), then delete the copy");
    }
    else
    {
        Console.WriteLine($"copy it to the computer of the console, e.g. --out <folder>, then delete the copy");
    }

    return 0;
}

static int ConsoleKeyRemove(string name, LabArguments a)
{
    var folder = a.Value("--folder") ?? "/var/lib/iris-hub";
    var path = LabKeys.ConsolePath(folder, LabKeys.IsConsoleName(name) ? name : throw new ArgumentException($"\"{name}\" is not a name of a console key"));
    if (!File.Exists(path))
    {
        throw new IOException($"there is no console key \"{name}\"");
    }

    File.Delete(path);
    Console.WriteLine($"the key of the console \"{name}\" is gone: that console is let go at its next request and cannot connect again");
    return 0;
}

static int ConsoleKeyList(LabArguments a)
{
    var folder = a.Value("--folder") ?? "/var/lib/iris-hub";
    Console.WriteLine("console.key (the main key)");
    foreach (var name in LabKeys.Consoles(folder))
    {
        Console.WriteLine(name);
    }

    return 0;
}

static int Version()
{
    Console.WriteLine($"iris-hub {LabUpdates.Version}{(LabUpdates.CanVerify ? "" : " (no key of the releases: no updates)")}");
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
            --eco yes|no (yes)                  the efficiency mode of Windows on the computers (cooler, quieter)
            --from HH:mm (15:00), --until HH:mm (06:00), --weekends yes|no (yes)
            --sentences <dir>                   sk.txt, cs.txt, en.txt for the lines
            --wake yes|no (yes)                 wakes the computers that are off when learning begins, and shuts them
                                                down again when it ends (Wake-on-LAN must be on in their BIOS/UEFI)
            --fonts <dir:dir>                   fonts of the server for measuring the model (/usr/share/fonts)
            --auto-update yes|no (yes)          gives the computers a newer signed program by itself (between rounds)
        iris-hub keys --folder <dir>            creates the keys (agent.key, console.key) and shows where they are
        iris-hub update [--check] [--folder <dir>] [--install <dir>] [--service <name>]
                                                the newest signed classroom programs from the IRIS releases on GitHub
                                                (as root, by the systemd timer once a day): the server's program is
                                                put in place and the service restarted; --check only tells
        iris-hub signing-key [--out <file>]     a new key for signing the releases (for the owner of the repository)
        iris-hub console-key add <name> [--out <file or dir>] [--folder <dir>]
                                                a key of its own for one console; taken away alone with
        iris-hub console-key remove <name>      (the console is let go at once)
        iris-hub console-key list
        iris-hub version
        """);
    return 2;
}

/// <summary>
/// The files of the hub belong to its user: a console key made by root is given to iris-hub, so the service (which
/// does not run as root) can read it.
/// </summary>
internal static partial class HubFiles
{
    public static void GiveToHub(string path, string user)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
        {
            return;
        }

        var entry = GetPasswordEntry(user);
        if (entry == IntPtr.Zero)
        {
            Console.WriteLine($"  (there is no user {user}: {path} stays root's)");
            return;
        }

        // struct passwd: two pointers (name, password), then uid and gid
        var uid = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(entry, 2 * IntPtr.Size);
        var gid = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(entry, (2 * IntPtr.Size) + 4);
        if (ChangeOwner(path, uid, gid) != 0)
        {
            throw new IOException($"{path} could not be given to {user}");
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "getpwnam", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial IntPtr GetPasswordEntry(string name);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "chown", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int ChangeOwner(string path, uint owner, uint group);
}
