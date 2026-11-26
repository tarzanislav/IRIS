using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Iris.Learning;
using Iris.Privacy;

namespace Iris.Cli;

/// <summary>
/// The vault on the command line: put licensed sentence pairs into a vault (the original file stays as it is), look at
/// a vault, check it with its password; open a sealed adapter. The password is typed (never shown, never an argument
/// that would stay in the history of the shell); when the input is redirected, it is the first line of it.
/// </summary>
internal static class VaultCommands
{
    /// <summary>iris vault create &lt;pairs.tsv&gt; &lt;out.irisvault&gt; --tag &lt;name&gt;</summary>
    public static int Create(string pairsPath, string vaultPath, Options options)
    {
        options.NoWords();
        var name = options.Value("--tag") ?? throw new UsageException("--tag <name> is needed (e.g. --tag intercorp)");
        if (name.Length is 0 or > 40 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new UsageException("the tag is a short name of letters, digits, - and _");
        }

        if (File.Exists(vaultPath))
        {
            throw new UsageException("the vault exists already – a new one goes into a new file");
        }

        var password = Password("Heslo trezoru (aspoň 12 znakov): ", confirm: true);
        try
        {
            using var key = Vault.NewKey(password);
            var pairs = File.ReadLines(pairsPath).Select(line => line.Split('\t')).Where(p => p.Length >= 2).Select(p => (p[0], p[1]));
            var (learn, measure) = Vault.CreatePairs(vaultPath, "private:" + name, pairs, key);
            Console.WriteLine($"vault written: {learn:N0} pairs to learn from, {measure:N0} kept only for measuring; tag private:{name}");
            Console.WriteLine("the original file was not changed – keep it only on an encrypted disk, or remove it yourself when you no longer need it");
            return 0;
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>iris vault info &lt;vault&gt; – what the vault says of itself, without the password.</summary>
    public static int Info(string vaultPath)
    {
        var info = Vault.Info(vaultPath);
        Console.WriteLine($"kind: {info.Kind}, tag: {info.Tag}, created: {info.Created:yyyy-MM-dd HH:mm} UTC, key: PBKDF2-SHA-256 × {info.Iterations:N0}, AES-256-GCM");
        if (info.Header["about"] is JsonObject about)
        {
            Console.WriteLine($"about: {about.ToJsonString()}");
        }

        return 0;
    }

    /// <summary>iris vault check &lt;vault&gt; – opens every chunk (the password, the integrity) and counts the pairs.</summary>
    public static int Check(string vaultPath)
    {
        using var key = OpenKey(vaultPath);
        var info = Vault.Info(vaultPath);
        if (info.Kind == "pairs")
        {
            var learn = Vault.ReadPairs(vaultPath, key, VaultSection.Learn).Count();
            var measure = Vault.ReadPairs(vaultPath, key, VaultSection.Measure).Count();
            Console.WriteLine($"the vault is whole: {learn:N0} pairs to learn from, {measure:N0} only for measuring ({info.Tag})");
        }
        else
        {
            var bytes = Vault.Unseal(vaultPath, key);
            Console.WriteLine($"the sealed file is whole: {bytes.Length:N0} bytes ({info.Tag})");
            CryptographicOperations.ZeroMemory(bytes);
        }

        return 0;
    }

    /// <summary>The key of a vault, from the typed password.</summary>
    public static VaultKey OpenKey(string vaultPath)
    {
        var password = Password($"Heslo trezoru ({Vault.Info(vaultPath).Tag}): ", confirm: false);
        try
        {
            return Vault.Open(vaultPath, password);
        }
        catch (CryptographicException)
        {
            throw new UsageException("the password is not right");
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>A sealed adapter, opened with the password; its tag says where it was learned.</summary>
    public static (Adapter Adapter, string Tag) OpenAdapter(string path)
    {
        using var key = OpenKey(path);
        var bytes = Vault.Unseal(path, key);
        try
        {
            return (Adapter.Parse(bytes), Vault.Info(path).Tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // the password, typed without showing it (or the first line of a redirected input)
    private static char[] Password(string prompt, bool confirm)
    {
        char[] Read(string text)
        {
            Console.Error.Write(text);
            if (Console.IsInputRedirected)
            {
                var line = Console.In.ReadLine() ?? "";
                Console.Error.WriteLine();
                return line.ToCharArray();
            }

            var typed = new List<char>();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (typed.Count > 0)
                    {
                        typed.RemoveAt(typed.Count - 1);
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    typed.Add(key.KeyChar);
                }
            }

            Console.Error.WriteLine();
            var result = typed.ToArray();
            typed.Clear();
            return result;
        }

        var first = Read(prompt);
        if (!confirm)
        {
            return first;
        }

        var second = Read("Ešte raz: ");
        var same = first.AsSpan().SequenceEqual(second);
        Array.Clear(second);
        if (!same)
        {
            Array.Clear(first);
            throw new UsageException("the two passwords are not the same");
        }

        return first;
    }
}
