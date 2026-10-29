using System.Security.Cryptography;

namespace Iris.Assistant.Packs;

/// <summary>How far an installation is: bytes received of all.</summary>
public readonly record struct InstallProgress(long Done, long Total);

/// <summary>
/// The installed language packs: one file per pair in the models folder (models/en-sk.iris). A pack is installed only
/// after its size and SHA-256 match the catalog – a download goes to a temporary file first, so an interrupted or
/// damaged download never replaces a working model. Downloading is the only time IRIS goes to the internet, and only
/// when the user asks for a pack.
/// </summary>
public sealed class PackStore(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>The model file of a pair (it need not exist).</summary>
    public string PathOf(LanguagePair pair) => Path.Combine(Folder, $"{pair}.iris");

    public bool IsInstalled(LanguagePair pair) => File.Exists(PathOf(pair));

    /// <summary>The installed pairs, in a stable order.</summary>
    public IReadOnlyList<LanguagePair> Installed() =>
        !Directory.Exists(Folder) ? [] :
        [.. Directory.EnumerateFiles(Folder, "*.iris")
            .Select(f => LanguagePair.TryParse(Path.GetFileNameWithoutExtension(f), out var pair) ? pair : (LanguagePair?)null)
            .OfType<LanguagePair>()
            .OrderBy(p => p.ToString(), StringComparer.Ordinal)];

    /// <summary>
    /// The packs that translate from one language to another: the direct pack, or two packs through English
    /// (e.g. de-en and en-sk for de → sk); null when the installed packs cannot do it.
    /// </summary>
    public IReadOnlyList<LanguagePair>? Route(string source, string target)
    {
        var direct = new LanguagePair(source, target);
        if (IsInstalled(direct))
        {
            return [direct];
        }

        if (source == LanguagePair.Pivot || target == LanguagePair.Pivot)
        {
            return null;
        }

        var first = new LanguagePair(source, LanguagePair.Pivot);
        var second = new LanguagePair(LanguagePair.Pivot, target);
        return IsInstalled(first) && IsInstalled(second) ? [first, second] : null;
    }

    /// <summary>Downloads a pack from the release folder and installs it when its size and SHA-256 are right.</summary>
    public async Task InstallAsync(PackInfo pack, HttpClient http, Uri releaseFolder, IProgress<InstallProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var url = new Uri(releaseFolder, Uri.EscapeDataString(pack.File));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await InstallAsync(pack, stream, progress, cancellation).ConfigureAwait(false);
    }

    /// <summary>Installs a pack from a stream (a download or a file the user has); checked like a download.</summary>
    public async Task InstallAsync(PackInfo pack, Stream source, IProgress<InstallProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Folder);
        var target = PathOf(pack.Pair);
        var temporary = target + ".part";
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        done += read;
                        if (done > pack.Size)
                        {
                            throw new InvalidDataException($"{pack.File} is larger than the catalog says");
                        }

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        progress?.Report(new InstallProgress(done, pack.Size));
                    }

                    if (done != pack.Size)
                    {
                        throw new InvalidDataException($"{pack.File} has {done} bytes, the catalog says {pack.Size}");
                    }
                }

                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (sha != pack.Sha256)
                {
                    throw new InvalidDataException($"{pack.File}: SHA-256 {sha} does not match the catalog");
                }
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Removes an installed pack; false when it was not installed.</summary>
    public bool Remove(LanguagePair pair)
    {
        var path = PathOf(pair);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
