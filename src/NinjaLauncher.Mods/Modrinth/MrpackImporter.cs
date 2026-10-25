using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Mods.Modrinth;

public sealed record MrpackFile(string Path, Dictionary<string, string> Hashes, Dictionary<string, string>? Env, IReadOnlyList<string> Downloads, long FileSize);

public sealed record MrpackIndex(
    int FormatVersion, string Game, string VersionId, string Name, string? Summary,
    IReadOnlyList<MrpackFile> Files, Dictionary<string, string> Dependencies)
{
    public string MinecraftVersion => Dependencies.GetValueOrDefault("minecraft") ?? throw new InvalidDataException("Modpack does not declare a Minecraft version.");

    public (ModLoaderKind Kind, string? Version) Loader =>
        Dependencies.TryGetValue("fabric-loader", out var f) ? (ModLoaderKind.Fabric, f)
        : Dependencies.TryGetValue("quilt-loader", out var q) ? (ModLoaderKind.Quilt, q)
        : Dependencies.TryGetValue("neoforge", out var n) ? (ModLoaderKind.NeoForge, n)
        : Dependencies.TryGetValue("forge", out var fo) ? (ModLoaderKind.Forge, fo)
        : (ModLoaderKind.None, null);
}

/// <summary>
/// Modrinth modpack (.mrpack, formatVersion 1) reader. Enforces the download host allow-list, mandatory hashes,
/// path containment for files and overrides (docs/research/MOD_LOADERS.md L-11).
/// </summary>
public sealed class MrpackImporter(IDownloadService downloads)
{
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public static MrpackIndex ReadIndex(string mrpackPath)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var entry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Not a Modrinth modpack: modrinth.index.json is missing.");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("modrinth.index.json is unreasonably large.");
        using var stream = entry.Open();
        var index = JsonSerializer.Deserialize<MrpackIndex>(stream, Json) ?? throw new InvalidDataException("Empty modpack index.");
        Validate(index);
        return index;
    }

    internal static void Validate(MrpackIndex index)
    {
        if (index.FormatVersion != 1) throw new InvalidDataException($"Unsupported modpack format version {index.FormatVersion}.");
        if (index.Game != "minecraft") throw new InvalidDataException($"Unsupported modpack game '{index.Game}'.");
        _ = index.MinecraftVersion;
        foreach (var file in index.Files)
        {
            SafePath.Combine(Path.GetTempPath(), file.Path); // throws on traversal / absolute paths
            if (!file.Hashes.ContainsKey("sha1") || !file.Hashes.ContainsKey("sha512")) throw new InvalidDataException($"'{file.Path}' is missing required hashes.");
            if (file.Downloads.Count == 0) throw new InvalidDataException($"'{file.Path}' has no download URL.");
            foreach (var url in file.Downloads)
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(u.Host))
                    throw new InvalidDataException($"'{file.Path}' uses a download host that is not allowed: {url}");
        }
    }

    /// <summary>Downloads pack files and applies overrides into <paramref name="gameDirectory"/>.</summary>
    public async Task InstallContentAsync(string mrpackPath, MrpackIndex index, string gameDirectory, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // shortcut: client-optional files are installed without asking; add a picker when the modpack UI grows.
        var files = index.Files.Where(f => f.Env?.GetValueOrDefault("client") != "unsupported").ToList();
        var items = files.Select(f => new DownloadItem(new Uri(f.Downloads[0]), SafePath.Combine(gameDirectory, f.Path), f.Hashes["sha1"], f.FileSize)).ToList();
        if (items.Count > 0) await downloads.DownloadAsync(items, progress, ct);
        await Task.Run(() =>
        {
            foreach (var (file, item) in files.Zip(items))
            {
                ct.ThrowIfCancellationRequested();
                using var s = File.OpenRead(item.DestinationPath);
                if (!Convert.ToHexString(SHA512.HashData(s)).Equals(file.Hashes["sha512"], StringComparison.OrdinalIgnoreCase))
                {
                    s.Dispose();
                    File.Delete(item.DestinationPath);
                    throw new InvalidDataException($"SHA-512 mismatch for {file.Path}.");
                }
            }
            using var zip = ZipFile.OpenRead(mrpackPath);
            foreach (var prefix in new[] { "overrides/", "client-overrides/" })
                SafeArchive.Extract(zip, gameDirectory, rename: name => name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : null);
        }, ct);
    }
}
