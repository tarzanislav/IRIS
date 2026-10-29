using System.Text.Json;

namespace Iris.Assistant.Packs;

/// <summary>A language pack of a release: one model file of a pair, its size, SHA-256 and where the weights came from.</summary>
public sealed record PackInfo(LanguagePair Pair, string File, long Size, string Sha256, string License, string Source);

/// <summary>
/// The language packs a release offers (packs.json, published with every release next to the model files). The
/// application reads the catalog of its own version, so a pack is always one its core was checked with.
/// </summary>
public sealed class PackCatalog
{
    private PackCatalog(IReadOnlyList<PackInfo> packs) => Packs = packs;

    public IReadOnlyList<PackInfo> Packs { get; }

    public PackInfo? Find(LanguagePair pair) => Packs.FirstOrDefault(p => p.Pair == pair);

    /// <summary>
    /// Reads packs.json: {"format":1,"packs":[{"pair":"en-sk","file":"opus-mt-en-sk-int8.iris","size":…,"sha256":"…",
    /// "license":"apache-2.0","source":"…"}]}. Anything else is refused, so a damaged catalog never installs a file.
    /// </summary>
    public static PackCatalog Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != 1)
            {
                throw new FormatException("packs.json: unknown format");
            }

            var packs = new List<PackInfo>();
            foreach (var item in root.GetProperty("packs").EnumerateArray())
            {
                var pair = LanguagePair.Parse(item.GetProperty("pair").GetString() ?? "");
                var file = item.GetProperty("file").GetString() ?? "";
                var sha = item.GetProperty("sha256").GetString() ?? "";
                var size = item.GetProperty("size").GetInt64();
                if (file.Length == 0 || file != Path.GetFileName(file) || !file.EndsWith(".iris", StringComparison.Ordinal) ||
                    sha.Length != 64 || !sha.All(char.IsAsciiHexDigit) || size <= 0)
                {
                    throw new FormatException($"packs.json: damaged entry for {pair}");
                }

                packs.Add(new PackInfo(pair, file, size, sha.ToLowerInvariant(), item.GetProperty("license").GetString() ?? "",
                    item.GetProperty("source").GetString() ?? ""));
            }

            return new PackCatalog(packs);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"packs.json is damaged ({ex.Message})", ex);
        }
    }
}
