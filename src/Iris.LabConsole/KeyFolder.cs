using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Iris.Lab;

namespace Iris.LabConsole;

/// <summary>
/// Where the console's key lies. By default the folder "kluc" next to the console's program: the console makes it
/// empty on its first start and lets only the signed-in user (and the system) into it; the user copies the key there.
/// A key chosen by hand wins; otherwise the only *.key in the folder is used.
/// </summary>
internal static class KeyFolder
{
    public const string DefaultName = "kluc";

    /// <summary>The folder next to the program, or in the user's data when the program's folder cannot be written.</summary>
    public static string Default
    {
        get
        {
            var nextToProgram = Path.Combine(AppContext.BaseDirectory, DefaultName);
            return Directory.Exists(nextToProgram) || CanWrite(AppContext.BaseDirectory)
                ? nextToProgram
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IRIS Lab", DefaultName);
        }
    }

    /// <summary>Makes the default folder (once), open only for the signed-in user and the system.</summary>
    public static void EnsureDefault(ConsoleSettings settings)
    {
        if (settings.KeyFolder.Length > 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Default;
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                new DirectoryInfo(folder).Create(OnlyMe());
            }

            settings.KeyFolder = folder;
            settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // no folder: the key can still be chosen by hand
        }
    }

    /// <summary>The key file to connect with: the one chosen by hand, else the only *.key in the folder.</summary>
    public static (string? File, string Problem) Find(ConsoleSettings settings)
    {
        if (settings.KeyFile.Length > 0 && File.Exists(settings.KeyFile))
        {
            return (settings.KeyFile, "");
        }

        var keys = Keys(settings.KeyFolder);
        return keys.Count switch
        {
            1 => (keys[0], ""),
            0 when settings.KeyFolder.Length > 0 => (null, $"V priečinku s kľúčom ({settings.KeyFolder}) nie je žiadny kľúč: skopírujte doň kľúč konzoly zo servera."),
            0 => (null, "Konzola ešte nevie, kde je kľúč: v Nastaveniach vyberte kľúč konzoly alebo priečinok s ním."),
            _ => (null, $"V priečinku s kľúčom je {keys.Count} kľúčov: v Nastaveniach vyberte, ktorý patrí tomuto počítaču."),
        };
    }

    public static List<string> Keys(string folder)
    {
        try
        {
            return folder.Length > 0 && Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.key").Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A warning when people other than the signed-in user may read the folder (empty when it is fine).</summary>
    public static string Openness(string folder)
    {
        if (!OperatingSystem.IsWindows() || folder.Length == 0 || !Directory.Exists(folder))
        {
            return "";
        }

        try
        {
            var others = new[] { WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid }
                .Select(t => new SecurityIdentifier(t, null)).ToHashSet();
            var rules = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            var open = rules.OfType<FileSystemAccessRule>().Any(r =>
                r.AccessControlType == AccessControlType.Allow
                && r.IdentityReference is SecurityIdentifier sid && others.Contains(sid)
                && (r.FileSystemRights & FileSystemRights.ReadData) != 0);
            return open ? "Pozor: tento priečinok môžu čítať aj iní používatelia počítača. Kľúč patrí do priečinka, kam smiete len vy." : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
        {
            return "";
        }
    }

    // only the signed-in user and the system, nothing inherited from the folder above
    private static DirectorySecurity OnlyMe()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var who in new IdentityReference[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        return security;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".iris-write-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
