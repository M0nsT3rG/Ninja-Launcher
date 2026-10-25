using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Mods.Loaders;

namespace NinjaLauncher.Mods.Modrinth;

/// <summary>Creates a new instance from a .mrpack: instance record, mod loader, pack files and overrides.</summary>
public sealed class ModpackInstaller(InstanceService instances, IEnumerable<ILoaderProvider> loaders, MrpackImporter importer,
    ModrinthClient modrinth, IDownloadService downloads, IAppPaths paths)
{
    public async Task<GameInstance> InstallFromFileAsync(string mrpackPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Reading modpack", 0, 0));
        var index = await Task.Run(() => MrpackImporter.ReadIndex(mrpackPath), ct);
        var (kind, loaderVersion) = index.Loader;
        var name = $"{index.Name} {index.VersionId}".Trim();
        var instance = await instances.CreateAsync(name[..Math.Min(64, name.Length)], index.MinecraftVersion, kind, loaderVersion, ct: ct);
        try
        {
            if (kind != ModLoaderKind.None)
            {
                var provider = loaders.FirstOrDefault(l => l.Kind == kind) ?? throw new NotSupportedException($"{kind} is not supported.");
                // Forge/NeoForge installer ids are the full maven version (e.g. "1.20.1-47.3.0"); packs list only the loader part.
                var version = kind == ModLoaderKind.Forge && !loaderVersion!.StartsWith(index.MinecraftVersion + "-") ? $"{index.MinecraftVersion}-{loaderVersion}" : loaderVersion!;
                var id = await provider.InstallAsync(index.MinecraftVersion, version, progress, ct);
                instance = await instances.SetLaunchVersionAsync(instance.Id, id, version, ct);
            }
            await importer.InstallContentAsync(mrpackPath, index, instances.GetGameDirectory(instance),
                new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress($"Downloading modpack files ({p.CompletedFiles}/{p.TotalFiles})",
                    100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles), p.BytesPerSecond))), ct);
            return instance;
        }
        catch
        {
            await instances.DeleteAsync(instance.Id, CancellationToken.None); // don't leave a half-installed pack behind
            throw;
        }
    }

    /// <summary>Uses the pack's Modrinth icon for the new instance. Cosmetic: any failure keeps the default icon.</summary>
    private async Task<GameInstance> TryAdoptIconAsync(GameInstance instance, string? iconUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.modrinth.com") return instance;
        try
        {
            var temp = Path.Combine(paths.CacheDirectory, "modpacks", $"icon-{instance.Id:N}");
            await downloads.DownloadAsync([new DownloadItem(uri, temp, null, null)], null, ct);
            if (new FileInfo(temp).Length > InstanceService.MaxIconBytes) return instance;
            var bytes = await File.ReadAllBytesAsync(temp, ct);
            File.Delete(temp);
            return await instances.SetIconBytesAsync(instance.Id, bytes, Path.GetExtension(uri.AbsolutePath).ToLowerInvariant(), ct);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException) { return instance; }
    }

    public async Task<GameInstance> InstallFromModrinthAsync(ModrinthHit pack, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress($"Finding the latest {pack.Title} release", 0, 0));
        var version = (await modrinth.GetVersionsAsync(pack.ProjectId, null, ModLoaderKind.None, ct)).OrderByDescending(v => v.DatePublished).FirstOrDefault()
            ?? throw new InvalidOperationException($"'{pack.Title}' has no published versions.");
        var file = version.Files.FirstOrDefault(f => f.Primary && f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            ?? version.Files.FirstOrDefault(f => f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{pack.Title}' has no .mrpack file.");
        var url = new Uri(file.Url);
        if (url.Host != "cdn.modrinth.com") throw new InvalidOperationException($"Refusing download from unexpected host {url.Host}.");
        var local = Path.Combine(paths.CacheDirectory, "modpacks", version.Id + ".mrpack");
        await downloads.DownloadAsync([new DownloadItem(url, local, file.Hashes.GetValueOrDefault("sha1"), file.Size)], null, ct);
        var instance = await InstallFromFileAsync(local, progress, ct);
        return await TryAdoptIconAsync(instance, pack.IconUrl, ct);
    }
}
