using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;

namespace NinjaLauncher.Minecraft.Java.Metadata;

/// <summary>Version discovery and metadata resolution (manifest cache, per-version JSON, inheritance).</summary>
public sealed class VersionCatalog(IDownloadService downloads, IAppPaths paths, ILogger<VersionCatalog> logger)
{
    public static readonly Uri ManifestUrl = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly TimeSpan ManifestMaxAge = TimeSpan.FromMinutes(30);
    private VersionManifest? _manifest;
    private DateTimeOffset _manifestFetched;

    public string VersionsDirectory => Path.Combine(paths.GameDirectory, "versions");

    public string VersionJsonPath(string id) => SafePath.Combine(VersionsDirectory, Path.Combine(id, id + ".json"));

    public async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _manifest is not null && DateTimeOffset.UtcNow - _manifestFetched < ManifestMaxAge) return _manifest;
        var cache = Path.Combine(paths.CacheDirectory, "version_manifest_v2.json");
        try
        {
            var json = await downloads.GetStringAsync(ManifestUrl, ct);
            _manifest = MetadataJson.Parse<VersionManifest>(json);
            await File.WriteAllTextAsync(cache, json, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException && File.Exists(cache))
        {
            logger.LogWarning("Version manifest unavailable ({Message}); using cached copy.", e.Message);
            _manifest = MetadataJson.Parse<VersionManifest>(await File.ReadAllTextAsync(cache, ct));
        }
        _manifestFetched = DateTimeOffset.UtcNow;
        return _manifest;
    }

    /// <summary>Ids of versions already present locally (vanilla and loader profiles).</summary>
    public IReadOnlyList<string> InstalledVersionIds() => Directory.Exists(VersionsDirectory)
        ? Directory.GetDirectories(VersionsDirectory).Select(Path.GetFileName).OfType<string>().Where(id => File.Exists(VersionJsonPath(id))).ToList()
        : [];

    /// <summary>Loads a version JSON, fetching vanilla ones from Mojang (SHA-1 checked) when missing.</summary>
    public async Task<VersionJson> GetVersionAsync(string id, CancellationToken ct = default)
    {
        var path = VersionJsonPath(id);
        var manifest = File.Exists(path) ? null : await GetManifestAsync(ct: ct);
        var entry = manifest?.Versions.FirstOrDefault(v => v.Id == id);
        if (entry is not null)
            await downloads.DownloadAsync([new DownloadItem(new Uri(entry.Url), path, entry.Sha1, null)], null, ct);
        if (!File.Exists(path)) throw new KeyNotFoundException($"Minecraft version '{id}' is not installed and is not in Mojang's manifest.");
        return MetadataJson.Parse<VersionJson>(await File.ReadAllTextAsync(path, ct));
    }

    /// <summary>Resolves the full <c>inheritsFrom</c> chain into one effective version.</summary>
    public async Task<VersionJson> ResolveAsync(string id, CancellationToken ct = default)
    {
        var version = await GetVersionAsync(id, ct);
        var seen = new HashSet<string> { id };
        while (version.InheritsFrom is { } parentId)
        {
            if (!seen.Add(parentId)) throw new InvalidDataException($"Circular inheritsFrom chain at '{parentId}'.");
            version = VersionMerger.Merge(version, await GetVersionAsync(parentId, ct));
        }
        version.Jar ??= version.Id;
        return version;
    }

    public async Task SaveProfileAsync(string id, string json, CancellationToken ct = default)
    {
        MetadataJson.Parse<VersionJson>(json); // reject anything that is not a version document
        var path = VersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, ct);
    }
}
