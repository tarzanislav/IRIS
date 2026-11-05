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
        Eco = (a.Value("--eco") ?? "yes") != "no",
        Rate = state.PlanRate,
        PlanRounds = planRounds,
        Schedule = schedule,
        Sentences = a.Value("--sentences"),
        AutoWake = (a.Value("--wake") ?? "yes") != "no",
        AutoUpdate = (a.Value("--auto-update") ?? "yes") != "no",
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

// the updater (run by systemd as root once a day, or when the console asks): the newest release on GitHub, when it
// brings newer signed classroom programs – the server's own program goes in place and the service starts again, the
// programs of the computers and the console wait in the folder of the hub for them
static async Task<int> Update(LabArguments a, bool check)
{
    var folder = a.Value("--folder") ?? "/var/lib/iris-hub";
    var install = a.Value("--install") ?? Path.GetDirectoryName(Environment.ProcessPath) ?? "/opt/iris-hub";
    var service = a.Value("--service") ?? "iris-hub";
    var request = Path.Combine(folder, "update-request");
    if (File.Exists(request))
    {
        File.Delete(request);
    }

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
    if (!LabUpdates.IsNewer(version, LabUpdates.Version))
    {
        Console.WriteLine($"{LabUpdates.Version}: up to date");
        return 0;
    }

    if (check)
    {
        Console.WriteLine($"{LabUpdates.Version}: version {version} is ready (iris-hub update installs it)");
        return 0;
    }

    // every file checked against the signed manifest before anything is put in place
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

        await File.WriteAllBytesAsync(Path.Combine(target, name), data, timeout.Token);
    }

    await File.WriteAllBytesAsync(Path.Combine(target, UpdateManifest.ManifestName), found.Json, timeout.Token);
    await File.WriteAllBytesAsync(Path.Combine(target, UpdateManifest.SignatureName), found.Signature, timeout.Token);
    foreach (var old in Directory.EnumerateDirectories(Path.Combine(folder, "updates"))
        .Where(d => LabUpdates.ParseVersion(Path.GetFileName(d)) is not null)
        .OrderByDescending(d => LabUpdates.ParseVersion(Path.GetFileName(d))).Skip(2))
    {
        Directory.Delete(old, recursive: true); // the two newest sets stay
    }

    var hub = Path.Combine(target, UpdateManifest.Hub);
    if (File.Exists(hub))
    {
        var program = await File.ReadAllBytesAsync(hub, timeout.Token);
        // The restart is started before the program is replaced: this program is one bundle file, and the runtime
        // reads parts of it (e.g. what starting a process needs) by its path only when they are first used – after
        // the replacement that path is the new program, and 0.4.1 failed there. So the shell waits for one line on
        // its input and restarts the hub after it; everything a process needs is loaded already.
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
            LabUpdates.ReplaceProgram(Path.Combine(install, "iris-hub"), program);
        }
        catch
        {
            restart?.Kill();
            restart?.Dispose();
            throw;
        }

        File.Delete(hub);
        Console.WriteLine($"{LabUpdates.Version} → {version}: the server's program is in {install}");
        if (restart is not null)
        {
            using (restart)
            {
                restart.StandardInput.WriteLine("go");
                restart.StandardInput.Close();
                await restart.WaitForExitAsync(timeout.Token);
            }
        }
    }

    Console.WriteLine($"the programs of the computers and the console of {version} wait in {target}");
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
