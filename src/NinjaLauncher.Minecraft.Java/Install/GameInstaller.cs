using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Minecraft.Java.Metadata;

namespace NinjaLauncher.Minecraft.Java.Install;

public sealed record InstallProgress(string Stage, double Percent, double BytesPerSecond);

/// <summary>Everything the argument builder needs after a successful install.</summary>
public sealed record InstalledVersion(
    VersionJson Version,
    IReadOnlyList<string> Classpath,
    string ClientJar,
    string NativesDirectory,
    string LibrariesDirectory,
    string AssetsRoot,
    string AssetIndexId,
    string GameAssetsDirectory,
    string? LoggingArgument);

/// <summary>Downloads and verifies the client jar, libraries, natives, assets and logging config into the shared game directory.</summary>
public sealed class GameInstaller(IDownloadService downloads, IAppPaths paths, ILogger<GameInstaller> logger)
{
    public static readonly Uri ResourcesBase = new("https://resources.download.minecraft.net/");

    private string Root => paths.GameDirectory;
    public string LibrariesDirectory => Path.Combine(Root, "libraries");
    public string AssetsDirectory => Path.Combine(Root, "assets");

    public async Task<InstalledVersion> InstallAsync(VersionJson version, string instanceGameDirectory, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var jarId = version.Jar ?? version.Id;
        var clientJar = SafePath.Combine(Path.Combine(Root, "versions"), Path.Combine(jarId, jarId + ".jar"));
        var items = new List<DownloadItem>();
        if (version.Downloads?.Client is { } client) items.Add(new DownloadItem(new Uri(client.Url), clientJar, client.Sha1, client.Size));

        var libraries = new LibraryResolver(LibrariesDirectory, RuleEvaluator.Current).Resolve(version.Libraries);
        foreach (var lib in libraries)
        {
            if (lib.Url is not null) items.Add(new DownloadItem(lib.Url, lib.Path, lib.Sha1, lib.Size));
            else if (!File.Exists(lib.Path)) throw new FileNotFoundException($"Library {lib.Name} has no download URL and is missing; reinstall the mod loader.", lib.Path);
        }

        string assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        AssetIndexFile? index = null;
        if (version.AssetIndex is { } ai)
        {
            var indexPath = SafePath.Combine(Path.Combine(AssetsDirectory, "indexes"), ai.Id + ".json");
            progress?.Report(new InstallProgress("Fetching asset index", 0, 0));
            await downloads.DownloadAsync([new DownloadItem(new Uri(ai.Url), indexPath, ai.Sha1, ai.Size)], null, ct);
            index = MetadataJson.Parse<AssetIndexFile>(await File.ReadAllTextAsync(indexPath, ct));
            foreach (var obj in index.Objects.Values.DistinctBy(o => o.Hash))
                items.Add(new DownloadItem(new Uri(ResourcesBase, $"{obj.Hash[..2]}/{obj.Hash}"), ObjectPath(obj.Hash), obj.Hash, obj.Size));
        }

        string? loggingArgument = null;
        if (version.Logging?.Client is { } log)
        {
            var configPath = SafePath.Combine(Path.Combine(AssetsDirectory, "log_configs"), log.File.Id);
            items.Add(new DownloadItem(new Uri(log.File.Url), configPath, log.File.Sha1, log.File.Size));
            loggingArgument = log.Argument.Replace("${path}", configPath);
        }

        logger.LogInformation("Installing {Version}: {Count} files to check", version.Id, items.Count);
        var dlProgress = new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress(
            $"Downloading files ({p.CompletedFiles}/{p.TotalFiles})",
            p.TotalBytes is > 0 ? 100.0 * p.BytesDownloaded / p.TotalBytes.Value : 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles),
            p.BytesPerSecond)));
        await downloads.DownloadAsync(items, dlProgress, ct);

        progress?.Report(new InstallProgress("Preparing natives and assets", 100, 0));
        var nativesDir = SafePath.Combine(Path.Combine(Root, "versions"), Path.Combine(version.Id, "natives-" + PlatformInfo.Current.MojangArch));
        await Task.Run(() => ExtractNatives(libraries, nativesDir), ct);
        var gameAssets = await Task.Run(() => PrepareLegacyAssets(index, assetIndexId, instanceGameDirectory, ct), ct);

        var classpath = libraries.Where(l => !l.IsNativeArchive).Select(l => l.Path).Distinct().ToList();
        classpath.Add(clientJar);
        return new InstalledVersion(version, classpath, clientJar, nativesDir, LibrariesDirectory, AssetsDirectory, assetIndexId, gameAssets, loggingArgument);
    }

    private string ObjectPath(string hash) => Path.Combine(AssetsDirectory, "objects", hash[..2], hash);

    internal static void ExtractNatives(IEnumerable<ResolvedLibrary> libraries, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);
        // 26.x points java/jna/lwjgl/netty native paths at subfolders; LWJGL extracts modern classpath natives itself.
        foreach (var sub in new[] { "java", "jna", "lwjgl", "netty" }) Directory.CreateDirectory(Path.Combine(nativesDir, sub));
        foreach (var lib in libraries.Where(l => l.IsNativeArchive))
        {
            if (!File.Exists(lib.Path)) continue;
            var exclude = lib.ExtractExclude.Concat(["META-INF/"]).ToList();
            // Never overwrite: another instance of the same version may have these DLLs loaded (locked on Windows).
            SafeArchive.ExtractToDirectory(lib.Path, nativesDir, name => !exclude.Any(e => name.StartsWith(e, StringComparison.Ordinal)),
                maxTotalBytes: 512L * 1024 * 1024, overwrite: false);
        }
    }

    /// <summary>Pre-1.7 versions read assets from a virtual tree or the instance's resources folder.</summary>
    private string PrepareLegacyAssets(AssetIndexFile? index, string indexId, string instanceDir, CancellationToken ct)
    {
        if (index is null || (!index.Virtual && !index.MapToResources)) return AssetsDirectory;
        var target = index.MapToResources ? Path.Combine(instanceDir, "resources") : Path.Combine(AssetsDirectory, "virtual", indexId);
        foreach (var (name, obj) in index.Objects)
        {
            ct.ThrowIfCancellationRequested();
            var dest = SafePath.Combine(target, name);
            if (File.Exists(dest) && new FileInfo(dest).Length == obj.Size) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(ObjectPath(obj.Hash), dest, overwrite: true);
        }
        return target;
    }
}
