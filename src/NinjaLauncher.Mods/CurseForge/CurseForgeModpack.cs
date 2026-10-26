using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Mods.Loaders;

namespace NinjaLauncher.Mods.CurseForge;

public sealed record CurseForgePackLoader(string Id, bool Primary);
public sealed record CurseForgePackMinecraft(string Version, IReadOnlyList<CurseForgePackLoader> ModLoaders);
public sealed record CurseForgePackFile(long ProjectID, long FileID, bool Required = true);
public sealed record CurseForgeManifest(CurseForgePackMinecraft Minecraft, string? Name, string? Version, IReadOnlyList<CurseForgePackFile> Files, string? Overrides);

public sealed record CurseForgePackResult(GameInstance Instance, IReadOnlyList<BlockedFile> Blocked);

/// <summary>Installs CurseForge modpack zips (manifest.json + overrides) into a new instance (docs/research/CURSEFORGE.md C-05).</summary>
public sealed partial class CurseForgeModpackInstaller(CurseForgeClient curseForge, InstanceService instances, IEnumerable<ILoaderProvider> loaders,
    IAppPaths paths)
{
    private const int MaxFiles = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CurseForgeManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Not a CurseForge modpack: manifest.json is missing.");
        if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("manifest.json is unreasonably large.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<CurseForgeManifest>(stream, Json) ?? throw new InvalidDataException("Empty modpack manifest.");
        if (string.IsNullOrWhiteSpace(manifest.Minecraft?.Version)) throw new InvalidDataException("The modpack does not declare a Minecraft version.");
        if (manifest.Files.Count > MaxFiles) throw new InvalidDataException($"The modpack lists more than {MaxFiles} files.");
        if (manifest.Files.Any(f => f.ProjectID <= 0 || f.FileID <= 0)) throw new InvalidDataException("The modpack lists an invalid project or file id.");
        if (manifest.Overrides is { } o && !SimpleFolder().IsMatch(o)) throw new InvalidDataException("The modpack's overrides folder name is not allowed.");
        return manifest;
    }

    /// <summary>Maps a manifest loader id ("forge-47.2.0", "neoforge-21.1.65", "fabric-0.16.5", "quilt-0.26.0") to our loader version format.</summary>
    public static (ModLoaderKind Kind, string? Version) MapLoader(CurseForgeManifest manifest)
    {
        var id = (manifest.Minecraft.ModLoaders.FirstOrDefault(l => l.Primary) ?? manifest.Minecraft.ModLoaders.FirstOrDefault())?.Id;
        if (id is null) return (ModLoaderKind.None, null);
        var dash = id.IndexOf('-');
        if (dash <= 0) throw new InvalidDataException($"Unknown mod loader '{id}'.");
        var (name, version) = (id[..dash], id[(dash + 1)..]);
        return name switch
        {
            "forge" => (ModLoaderKind.Forge, $"{manifest.Minecraft.Version}-{version}"), // Forge maven ids are "<mc>-<forge>"
            "neoforge" => (ModLoaderKind.NeoForge, version),
            "fabric" => (ModLoaderKind.Fabric, version),
            "quilt" => (ModLoaderKind.Quilt, version),
            _ => throw new NotSupportedException($"Mod loader '{name}' is not supported."),
        };
    }

    public async Task<CurseForgePackResult> InstallFromFileAsync(string zipPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Reading modpack", 0, 0));
        var manifest = await Task.Run(() => ReadManifest(zipPath), ct);
        var (kind, loaderVersion) = MapLoader(manifest);
        var name = $"{manifest.Name ?? Path.GetFileNameWithoutExtension(zipPath)} {manifest.Version}".Trim();
        var instance = await instances.CreateAsync(name[..Math.Min(64, name.Length)], manifest.Minecraft.Version, kind, loaderVersion, ct: ct);
        try
        {
            if (kind != ModLoaderKind.None)
            {
                var provider = loaders.FirstOrDefault(l => l.Kind == kind) ?? throw new NotSupportedException($"{kind} is not supported.");
                var id = await provider.InstallAsync(manifest.Minecraft.Version, loaderVersion!, progress, ct);
                instance = await instances.SetLaunchVersionAsync(instance.Id, id, loaderVersion, ct);
            }
            var gameDir = instances.GetGameDirectory(instance);
            var blocked = await InstallFilesAsync(manifest, gameDir, progress, ct);
            await Task.Run(() =>
            {
                var prefix = (manifest.Overrides ?? "overrides") + "/";
                using var zip = ZipFile.OpenRead(zipPath);
                SafeArchive.Extract(zip, gameDir, rename: n => n.StartsWith(prefix, StringComparison.Ordinal) && n.Length > prefix.Length ? n[prefix.Length..] : null);
            }, ct);
            return new CurseForgePackResult(instance, blocked);
        }
        catch
        {
            await instances.DeleteAsync(instance.Id, CancellationToken.None); // never leave a half-installed pack behind
            throw;
        }
    }

    /// <summary>Installs the newest release of a CurseForge modpack project.</summary>
    public async Task<CurseForgePackResult> InstallFromCurseForgeAsync(CurseForgeMod pack, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress($"Finding the latest {pack.Name} release", 0, 0));
        var files = (await curseForge.GetFilesAsync(pack.Id, null, ModLoaderKind.None, ct)).Where(f => f.IsAvailable).OrderByDescending(f => f.FileDate).ToList();
        var file = files.FirstOrDefault(f => f.ReleaseType == 1) ?? files.FirstOrDefault() ?? throw new InvalidOperationException($"'{pack.Name}' has no files.");
        var cache = Path.Combine(paths.CacheDirectory, "modpacks");
        var (path, blocked) = await curseForge.DownloadAsync(pack, file, cache, file.Id.ToString(), ct);
        if (blocked is not null)
            throw new InvalidOperationException($"The author of '{pack.Name}' only allows downloading it on CurseForge: {blocked.PageUrl}. Download the zip there and use Library → Import modpack.");
        try { return await InstallFromFileAsync(path!, progress, ct); }
        finally { TryDelete(path!); } // the pack's files now live in the instance; nothing CurseForge stays in the cache
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task<IReadOnlyList<BlockedFile>> InstallFilesAsync(CurseForgeManifest manifest, string gameDir, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var wanted = manifest.Files.Where(f => f.Required).ToList();
        var files = new List<CurseForgeFile>();
        foreach (var chunk in wanted.Select(f => f.FileID).Distinct().Chunk(500)) files.AddRange(await curseForge.GetFilesByIdAsync(chunk, ct));
        var mods = (await curseForge.GetModsAsync(files.Select(f => f.ModId).Distinct().ToList(), ct)).ToDictionary(m => m.Id);
        var shaders = files.Any(f => mods.TryGetValue(f.ModId, out var m) && m.ClassId is not (6 or 12)) ? await curseForge.ShadersClassIdAsync(ct) : 0;

        var blocked = new List<BlockedFile>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            if (!mods.TryGetValue(file.ModId, out var mod)) throw new InvalidDataException($"CurseForge has no project {file.ModId} for file {file.FileName}.");
            progress?.Report(new InstallProgress($"Downloading modpack files ({i + 1}/{files.Count})", 100.0 * i / Math.Max(1, files.Count), 0));
            var (_, block) = await curseForge.DownloadAsync(mod, file, gameDir, CurseForgeClient.ContentFolder(mod.ClassId, shaders), ct);
            if (block is not null) blocked.Add(block);
        }
        var missing = wanted.Select(f => f.FileID).Distinct().Count() - files.Count;
        if (missing > 0) throw new InvalidDataException($"{missing} file(s) listed by the modpack no longer exist on CurseForge.");
        return blocked;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex SimpleFolder();
}
