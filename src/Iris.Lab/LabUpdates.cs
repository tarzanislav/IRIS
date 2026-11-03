using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>One file of a set of classroom programs: its name, size and SHA-256 (hexadecimal).</summary>
public sealed record UpdateFile(string Name, long Size, string Sha256);

/// <summary>
/// The classroom programs of one release of IRIS (iris-lab-manifest.json): the version and every file with its size
/// and SHA-256. The manifest is believed only with a valid signature of the key of the IRIS releases, and a file only
/// when it matches the manifest.
/// </summary>
public sealed record UpdateManifest(string Version, IReadOnlyList<UpdateFile> Files)
{
    public const string ManifestName = "iris-lab-manifest.json";
    public const string SignatureName = "iris-lab-manifest.sig";
    public const string Agent = "iris-agent-win-x64.exe";
    public const string Console = "iris-console-win-x64.exe";

    /// <summary>The hub for the processor it runs on (iris-hub-linux-x64 or iris-hub-linux-arm64).</summary>
    public static string Hub => "iris-hub-linux-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public UpdateFile? File(string name) => Files.FirstOrDefault(f => f.Name == name);

    public static UpdateManifest Parse(byte[] json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("the manifest is empty");
        var version = root["version"]?.GetValue<string>() ?? throw new FormatException("the manifest has no version");
        if (LabUpdates.ParseVersion(version) is null)
        {
            throw new FormatException($"the manifest has a strange version '{version}'");
        }

        var files = new List<UpdateFile>();
        foreach (var item in root["files"] as JsonArray ?? throw new FormatException("the manifest has no files"))
        {
            if (item is not JsonObject f || f["name"]?.GetValue<string>() is not { } name || f["size"]?.GetValue<long>() is not { } size ||
                f["sha256"]?.GetValue<string>() is not { } hash)
            {
                throw new FormatException("a file of the manifest is incomplete");
            }

            files.Add(new UpdateFile(name, size, hash.ToLowerInvariant()));
        }

        if (files.Any(f => f.Name.Contains('/', StringComparison.Ordinal) || f.Name.Contains('\\', StringComparison.Ordinal) || f.Name.StartsWith('.')))
        {
            throw new FormatException("a file of the manifest has a path in its name");
        }

        return new UpdateManifest(version, files);
    }
}

/// <summary>
/// Updates of the classroom programs, signed: the release workflow of IRIS signs the manifest of each set of programs
/// with a key whose private part exists only as a secret of that workflow (ECDSA P-256, SHA-256); the programs carry
/// the public part (tools/lab/update-key.pub, built in). The server asks GitHub once a day for the newest release;
/// the computers of the classroom get their program only from the server, never from the internet.
/// </summary>
public static class LabUpdates
{
    /// <summary>The repository whose releases bring the programs.</summary>
    public const string Repository = "tarzanislav/IRIS";

    private static readonly byte[]? s_key = LoadKey();

    /// <summary>The version of the running programs (X.Y.Z).</summary>
    public static string Version { get; } = (typeof(LabUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Whether this build knows the key of the releases (without it nothing is ever installed).</summary>
    public static bool CanVerify => (KeyOverride ?? s_key) is not null;

    /// <summary>Another public key instead of the built-in one (only the tests sign their own programs).</summary>
    internal static byte[]? KeyOverride { get; set; }

    /// <summary>The manifest, when its signature is the one of the releases; otherwise null.</summary>
    public static UpdateManifest? Open(byte[] manifest, byte[] signature, byte[]? publicKey = null)
    {
        var key = publicKey ?? KeyOverride ?? s_key;
        if (key is null)
        {
            return null;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            return ecdsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                ? UpdateManifest.Parse(manifest)
                : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether data is exactly the file the manifest describes.</summary>
    public static bool Matches(UpdateFile file, ReadOnlySpan<byte> data) =>
        data.Length == file.Size && Convert.ToHexStringLower(SHA256.HashData(data)) == file.Sha256;

    /// <summary>X.Y.Z as three numbers, or null.</summary>
    public static (int Major, int Minor, int Patch)? ParseVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 3 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && int.TryParse(parts[2], out var c) && a >= 0 && b >= 0 && c >= 0
            ? (a, b, c)
            : null;
    }

    /// <summary>Whether candidate is a later version than current.</summary>
    public static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) is { } a && ParseVersion(current) is { } b && a.CompareTo(b) > 0;

    /// <summary>
    /// Puts a new program in the place of a running one: the new file beside it, the running one renamed to .old
    /// (Windows lets a running program be renamed, not overwritten), the new one into its place. The program starts
    /// as the new one next time; <see cref="RemoveOld"/> clears the .old then.
    /// </summary>
    public static void ReplaceProgram(string path, byte[] data)
    {
        var fresh = path + ".new";
        var old = path + ".old";
        File.WriteAllBytes(fresh, data);
        if (OperatingSystem.IsWindows())
        {
            File.Delete(old);
            File.Move(path, old);
            File.Move(fresh, path);
        }
        else
        {
            File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(fresh, path, overwrite: true); // a running program keeps its old file until it ends
        }
    }

    /// <summary>Removes what an earlier update left (the program before it).</summary>
    public static void RemoveOld(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path + ".old");
            File.Delete(path + ".new");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The signed classroom programs of the newest release on GitHub: the manifest, its signature and where every file
    /// lies; null when the release has none or its signature is wrong. One request for the release, two small downloads.
    /// </summary>
    public static async Task<(UpdateManifest Manifest, byte[] Json, byte[] Signature, IReadOnlyDictionary<string, Uri> Files)?> LatestAsync(HttpClient http, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{Repository}/releases/latest"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no release yet
        }

        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel))?.AsObject();
        var files = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var asset in release?["assets"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (asset["name"]?.GetValue<string>() is { } name && asset["browser_download_url"]?.GetValue<string>() is { } url &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                files[name] = uri;
            }
        }

        if (!files.TryGetValue(UpdateManifest.ManifestName, out var manifestUri) || !files.TryGetValue(UpdateManifest.SignatureName, out var signatureUri))
        {
            return null;
        }

        var json = await http.GetByteArrayAsync(manifestUri, cancel);
        var signature = await http.GetByteArrayAsync(signatureUri, cancel);
        return Open(json, signature) is { } manifest ? (manifest, json, signature, files) : null;
    }

    /// <summary>An HTTP client for GitHub (it asks every client to name itself; nothing about the computer is sent).</summary>
    public static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"IRIS-lab/{Version}");
        return http;
    }

    // the public key of the releases, built in from tools/lab/update-key.pub (PEM)
    private static byte[]? LoadKey()
    {
        using var stream = typeof(LabUpdates).Assembly.GetManifestResourceStream("update-key.pub");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            return null;
        }
    }
}
