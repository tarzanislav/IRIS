using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace Iris.Agent;

/// <summary>
/// Puts the agent on a computer of the classroom as a Windows service (run as administrator): the program to
/// Program Files, its settings and the key to a folder of ProgramData that only the system and administrators may
/// open (students cannot read the key), the service set to start with Windows and to start again after a failure.
/// Everything visible in Services as "IRIS – učenie v noci"; uninstall removes all of it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Installer
{
    public const string ServiceName = "IRIS-Lab";

    public static string ProgramFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IRIS Lab");

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IRIS Lab");

    public static int Install(string hub, int port, string keyFile, string? name)
    {
        var key = Iris.Lab.LabKeys.Read(keyFile); // checks the key before anything is changed
        Directory.CreateDirectory(ProgramFolder);
        var source = Path.GetDirectoryName(Environment.ProcessPath)!;
        var exe = Path.Combine(ProgramFolder, "iris-agent.exe");
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(ProgramFolder), StringComparison.OrdinalIgnoreCase))
        {
            Run("sc.exe", $"stop {ServiceName}", allowFailure: true);
            File.Copy(Environment.ProcessPath!, exe, overwrite: true);
            var fonts = Path.Combine(source, "fonts");
            if (Directory.Exists(fonts))
            {
                foreach (var file in Directory.EnumerateFiles(fonts, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(ProgramFolder, "fonts", Path.GetRelativePath(fonts, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
        }

        Directory.CreateDirectory(DataFolder);
        // only SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544): the key stays away from students
        Run("icacls.exe", $"\"{DataFolder}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F");
        File.WriteAllText(Path.Combine(DataFolder, "agent.key"), Convert.ToBase64String(key) + "\n");
        var settings = new JsonObject { ["hub"] = hub, ["port"] = port, ["name"] = name };
        File.WriteAllText(Path.Combine(DataFolder, "agent.json"), settings.ToJsonString());

        Run("sc.exe", $"delete {ServiceName}", allowFailure: true);
        Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{exe}\\\" service\" start= delayed-auto DisplayName= \"IRIS – učenie v noci\"");
        Run("sc.exe", $"description {ServiceName} \"IRIS: učenie čítania textu (OCR) mimo vyučovania, pri polovičnom výkone. Ovláda ho správca učebne.\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/10000/restart/60000/restart/300000");
        Run("sc.exe", $"start {ServiceName}");
        Console.WriteLine($"installed: {exe} as the service {ServiceName}, settings and key in {DataFolder} (system and administrators only)");
        return 0;
    }

    public static int Uninstall()
    {
        Run("sc.exe", $"stop {ServiceName}", allowFailure: true);
        Run("sc.exe", $"delete {ServiceName}", allowFailure: true);
        foreach (var folder in new[] { DataFolder, ProgramFolder })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"could not remove {folder}: {e.Message} (the running program is removed after a restart)");
            }
        }

        Console.WriteLine($"removed the service {ServiceName}");
        return 0;
    }

    /// <summary>
    /// The uninstall the console asked for, started by the running service itself: a small command of Windows, left
    /// running on its own, stops the service (so the program can be deleted), deletes it, then the program's folder
    /// and the folder with the settings and the key. The agent has already sent its round.
    /// </summary>
    public static void RemoveLater()
    {
        var steps = string.Join(" & ",
            "ping -n 6 127.0.0.1 >nul",
            $"sc.exe stop {ServiceName} >nul",
            "ping -n 16 127.0.0.1 >nul",
            $"sc.exe delete {ServiceName} >nul",
            $"rmdir /s /q \"{ProgramFolder}\"",
            $"rmdir /s /q \"{DataFolder}\"",
            // once more a little later, should the program still have been ending a moment ago
            "ping -n 31 127.0.0.1 >nul",
            $"if exist \"{ProgramFolder}\" rmdir /s /q \"{ProgramFolder}\"",
            $"if exist \"{DataFolder}\" rmdir /s /q \"{DataFolder}\"");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"{steps}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory, // not inside a folder that is about to be deleted
        };
        using var _ = Process.Start(start);
    }

    private static void Run(string program, string arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"{program} {arguments}: {output.Trim()}");
        }
    }
}
