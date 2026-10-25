using System.Text.Json;
using System.Text.Json.Serialization;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Mods.Modrinth;

public enum ProjectKind { Mod, Modpack, Shader, ResourcePack }

public sealed record ModrinthHit(
    [property: JsonPropertyName("project_id")] string ProjectId,
    string Slug, string Title, string Description, string Author, long Downloads,
    [property: JsonPropertyName("icon_url")] string? IconUrl,
    [property: JsonPropertyName("project_type")] string ProjectType,
    IReadOnlyList<string> Categories);

public sealed record ModrinthSearchResult(IReadOnlyList<ModrinthHit> Hits, int Offset, int Limit, [property: JsonPropertyName("total_hits")] int TotalHits);

public sealed record ModrinthFile(Dictionary<string, string> Hashes, string Url, string Filename, bool Primary, long Size);

public sealed record ModrinthDependency(
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("dependency_type")] string DependencyType);

public sealed record ModrinthVersion(
    string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    string Name,
    [property: JsonPropertyName("version_number")] string VersionNumber,
    [property: JsonPropertyName("game_versions")] IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependency> Dependencies,
    [property: JsonPropertyName("date_published")] DateTimeOffset DatePublished,
    [property: JsonPropertyName("version_type")] string? VersionType = null);

/// <summary>Which Modrinth project owns which installed file, so updates replace instead of duplicating.</summary>
internal sealed record LedgerEntry(string FileName, string Folder, string VersionId);

public sealed record InstalledContent(string FileName, string ProjectTitle, string VersionNumber);

/// <param name="FileName">Installed file name in <c>mods/</c> (may end in <c>.disabled</c>).</param>
/// <param name="Sha1">Hash of the installed file at check time; the update is skipped if the file changed since.</param>
public sealed record ModUpdate(string FileName, string Sha1, string ProjectId, string? CurrentVersion, ModrinthVersion Latest);

/// <summary>Modrinth API v2 client (https://docs.modrinth.com). Downloads only from Modrinth's CDN, hash-verified.</summary>
public sealed class ModrinthClient(IDownloadService downloads)
{
    public const string ApiBase = "https://api.modrinth.com/v2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedHosts = ["cdn.modrinth.com"];
    private readonly Dictionary<string, (DateTimeOffset At, string Body)> _cache = new();

    public static string ProjectTypeFacet(ProjectKind kind) => kind switch
    {
        ProjectKind.Mod => "mod", ProjectKind.Modpack => "modpack", ProjectKind.Shader => "shader", _ => "resourcepack",
    };

    public static string? LoaderName(ModLoaderKind loader) => loader switch
    {
        ModLoaderKind.Fabric => "fabric", ModLoaderKind.Quilt => "quilt", ModLoaderKind.Forge => "forge", ModLoaderKind.NeoForge => "neoforge", _ => null,
    };

    public async Task<ModrinthSearchResult> SearchAsync(string query, ProjectKind kind, string? gameVersion, ModLoaderKind loader,
        int offset, int limit, CancellationToken ct)
    {
        var facets = new List<string[]> { new[] { $"project_type:{ProjectTypeFacet(kind)}" } };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add([$"versions:{gameVersion}"]);
        if (kind == ProjectKind.Mod && LoaderName(loader) is { } l) facets.Add([$"categories:{l}"]);
        var url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&index=relevance&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        return JsonSerializer.Deserialize<ModrinthSearchResult>(await GetCachedAsync(url, ct), Json)!;
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, string? gameVersion, ModLoaderKind loader, CancellationToken ct)
    {
        var url = $"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version";
        var q = new List<string>();
        if (LoaderName(loader) is { } l) q.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { l })));
        if (!string.IsNullOrWhiteSpace(gameVersion)) q.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        if (q.Count > 0) url += "?" + string.Join('&', q);
        return JsonSerializer.Deserialize<List<ModrinthVersion>>(await GetCachedAsync(url, ct), Json)!;
    }

    /// <summary>Installs the newest compatible version plus required dependencies into the instance's content folder.</summary>
    public async Task<IReadOnlyList<InstalledContent>> InstallAsync(ModrinthHit project, ProjectKind kind, GameInstance instance, string gameDirectory, CancellationToken ct)
    {
        var installed = new List<InstalledContent>();
        var visited = new HashSet<string>();
        var loader = kind == ProjectKind.Mod ? instance.Loader : ModLoaderKind.None;
        if (kind == ProjectKind.Mod && loader == ModLoaderKind.None)
            throw new InvalidOperationException("This instance has no mod loader. Create a Fabric, Quilt, Forge or NeoForge instance to install mods.");
        var ledger = await LoadLedgerAsync(gameDirectory, ct);
        try
        {
            await InstallRecursiveAsync(project.ProjectId, project.Title, null, isRoot: true, kind, loader, instance.MinecraftVersion, gameDirectory, ledger, visited, installed, ct);
        }
        finally { await SaveLedgerAsync(gameDirectory, ledger, CancellationToken.None); }
        return installed;
    }

    private async Task InstallRecursiveAsync(string projectId, string title, string? pinnedVersionId, bool isRoot, ProjectKind kind, ModLoaderKind loader,
        string mc, string gameDir, Dictionary<string, LedgerEntry> ledger, HashSet<string> visited, List<InstalledContent> installed, CancellationToken ct)
    {
        if (!visited.Add(projectId) || visited.Count > 64) return;
        // Dependencies already installed by an earlier install are left alone; the project the user picked is updated in place.
        if (!isRoot && ledger.TryGetValue(projectId, out var existing) && File.Exists(Path.Combine(gameDir, existing.Folder, existing.FileName))) return;

        ModrinthVersion version;
        if (pinnedVersionId is not null)
            version = JsonSerializer.Deserialize<ModrinthVersion>(await GetCachedAsync($"{ApiBase}/version/{Uri.EscapeDataString(pinnedVersionId)}", ct), Json)!;
        else
        {
            var versions = await GetVersionsAsync(projectId, mc, loader, ct);
            var ordered = versions.OrderByDescending(v => v.DatePublished).ToList();
            version = ordered.FirstOrDefault(v => v.VersionType is null or "release") ?? ordered.FirstOrDefault()
                ?? throw new InvalidOperationException($"'{title}' has no release for Minecraft {mc}{(loader == ModLoaderKind.None ? "" : " / " + loader)}.");
        }
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.First();
        var url = new Uri(file.Url);
        if (!AllowedHosts.Contains(url.Host)) throw new InvalidOperationException($"Refusing download from unexpected host {url.Host}.");

        var folderName = ContentFolder(kind);
        var folder = Path.Combine(gameDir, folderName);
        var target = SafePath.Combine(folder, SafePath.SanitizeName(file.Filename));
        await downloads.DownloadAsync([new DownloadItem(url, target, file.Hashes.GetValueOrDefault("sha1"), file.Size)], null, ct);
        if (ledger.TryGetValue(projectId, out var previous) && previous.FileName != Path.GetFileName(target))
        {
            // Replace the older version (enabled or disabled copy) so the loader never sees two copies of one mod.
            foreach (var old in new[] { previous.FileName, previous.FileName + ".disabled" })
            {
                var oldPath = SafePath.Combine(gameDir, Path.Combine(previous.Folder, old)); // ledger is on-disk data: keep it inside the instance
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
        }
        ledger[projectId] = new LedgerEntry(Path.GetFileName(target), folderName, version.Id);
        installed.Add(new InstalledContent(Path.GetFileName(target), title, version.VersionNumber));

        foreach (var dep in version.Dependencies.Where(d => d.DependencyType == "required" && d.ProjectId is not null))
            await InstallRecursiveAsync(dep.ProjectId!, dep.ProjectId!, dep.VersionId, isRoot: false, kind, loader, mc, gameDir, ledger, visited, installed, ct);
    }

    private static string LedgerPath(string gameDir) => Path.Combine(gameDir, ".ninja", "modrinth.json");

    private static async Task<Dictionary<string, LedgerEntry>> LoadLedgerAsync(string gameDir, CancellationToken ct)
    {
        var path = LedgerPath(gameDir);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, LedgerEntry>>(await File.ReadAllTextAsync(path, ct), Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static async Task SaveLedgerAsync(string gameDir, Dictionary<string, LedgerEntry> ledger, CancellationToken ct)
    {
        var path = LedgerPath(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, Json), ct);
    }

    public static string ContentFolder(ProjectKind kind) => kind switch
    {
        ProjectKind.Shader => "shaderpacks", ProjectKind.ResourcePack => "resourcepacks", _ => "mods",
    };

    /// <summary>Lists mods in the instance; disabled ones carry a <c>.disabled</c> suffix (the de-facto convention).</summary>
    public static IReadOnlyList<(string FileName, bool Enabled)> ListMods(string gameDirectory)
    {
        var dir = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>()
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .Order().Select(f => (f, !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static void SetModEnabled(string gameDirectory, string fileName, bool enabled)
    {
        var dir = Path.Combine(gameDirectory, "mods");
        var source = SafePath.Combine(dir, fileName);
        var target = enabled ? source[..^".disabled".Length] : source + ".disabled";
        if (enabled && !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return;
        if (!enabled && fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return;
        File.Move(source, target);
    }

    /// <summary>
    /// Identifies installed mods by SHA-1 (works for hand-added jars too) and asks Modrinth for the newest version
    /// compatible with the instance. Pre-release updates are only offered when the installed file is itself a pre-release.
    /// </summary>
    public async Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(GameInstance instance, string gameDirectory, CancellationToken ct)
    {
        if (LoaderName(instance.Loader) is not { } loader) return [];
        var dir = Path.Combine(gameDirectory, "mods");
        var hashes = await Task.Run(() => ListMods(gameDirectory)
            .GroupBy(m => Sha1Of(Path.Combine(dir, m.FileName)), StringComparer.OrdinalIgnoreCase) // identical copies count once
            .ToDictionary(g => g.Key, g => g.First().FileName, StringComparer.OrdinalIgnoreCase), ct);
        if (hashes.Count == 0) return [];

        var current = await PostAsync<Dictionary<string, ModrinthVersion>>("/version_files", new { hashes = hashes.Keys, algorithm = "sha1" }, ct);
        var latest = await PostAsync<Dictionary<string, ModrinthVersion>>("/version_files/update",
            new { hashes = hashes.Keys, algorithm = "sha1", loaders = new[] { loader }, game_versions = new[] { instance.MinecraftVersion } }, ct);

        var updates = new List<ModUpdate>();
        foreach (var (hash, latestAny) in latest)
        {
            if (!hashes.TryGetValue(hash, out var fileName)) continue;
            // Without the installed version we cannot tell newer from older, so never guess (no downgrades).
            if (current.GetValueOrDefault(hash) is not { } installed) continue;
            var newest = latestAny;
            if (IsRelease(installed) && !IsRelease(newest))
            {
                // A newer pre-release must not hide a newer release: look for the newest release explicitly.
                var releases = await GetVersionsAsync(installed.ProjectId, instance.MinecraftVersion, instance.Loader, ct);
                var release = releases.Where(IsRelease).OrderByDescending(v => v.DatePublished).FirstOrDefault();
                if (release is null) continue;
                newest = release;
            }
            if (newest.DatePublished <= installed.DatePublished) continue;
            var file = newest.Files.FirstOrDefault(f => f.Primary) ?? newest.Files.FirstOrDefault();
            if (file is null || file.Hashes.GetValueOrDefault("sha1")?.Equals(hash, StringComparison.OrdinalIgnoreCase) == true) continue;
            updates.Add(new ModUpdate(fileName, hash, newest.ProjectId, installed.VersionNumber, newest));
        }
        return updates.OrderBy(u => u.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsRelease(ModrinthVersion v) => v.VersionType is null or "release";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Downloads the new file, keeps the enabled/disabled state, removes the old file and installs any newly
    /// required dependencies. Returns null (skipped) if the old file changed since the update check.
    /// </summary>
    public async Task<string?> ApplyUpdateAsync(ModUpdate update, GameInstance instance, string gameDirectory, CancellationToken ct)
    {
        var file = update.Latest.Files.FirstOrDefault(f => f.Primary) ?? update.Latest.Files.First();
        var url = new Uri(file.Url);
        if (!AllowedHosts.Contains(url.Host)) throw new InvalidOperationException($"Refusing download from unexpected host {url.Host}.");
        var dir = Path.Combine(gameDirectory, "mods");
        var oldPath = SafePath.Combine(dir, update.FileName);
        // The user may have toggled, deleted or replaced the jar since checking; never act on a stale result.
        if (!File.Exists(oldPath) || !Sha1Of(oldPath).Equals(update.Sha1, StringComparison.OrdinalIgnoreCase)) return null;
        var disabled = update.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var newName = SafePath.SanitizeName(file.Filename) + (disabled ? ".disabled" : "");
        var newPath = SafePath.Combine(dir, newName);
        await downloads.DownloadAsync([new DownloadItem(url, newPath, file.Hashes.GetValueOrDefault("sha1"), file.Size)], null, ct);
        if (!string.Equals(oldPath, newPath, PathComparison)) File.Delete(oldPath);

        var ledger = await LoadLedgerAsync(gameDirectory, ct);
        ledger[update.ProjectId] = new LedgerEntry(SafePath.SanitizeName(file.Filename), "mods", update.Latest.Id);
        try
        {
            var visited = new HashSet<string> { update.ProjectId };
            var added = new List<InstalledContent>();
            foreach (var dep in update.Latest.Dependencies.Where(d => d.DependencyType == "required" && d.ProjectId is not null))
                await InstallRecursiveAsync(dep.ProjectId!, dep.ProjectId!, dep.VersionId, isRoot: false, ProjectKind.Mod, instance.Loader,
                    instance.MinecraftVersion, gameDirectory, ledger, visited, added, ct);
        }
        finally { await SaveLedgerAsync(gameDirectory, ledger, CancellationToken.None); }
        return newName;
    }

    /// <summary>Maps SHA-1 hashes of local files to the Modrinth versions they belong to (unknown files are absent).</summary>
    public async Task<Dictionary<string, ModrinthVersion>> IdentifyFilesAsync(IReadOnlyCollection<string> sha1s, CancellationToken ct) =>
        sha1s.Count == 0 ? new() : new(await PostAsync<Dictionary<string, ModrinthVersion>>("/version_files", new { hashes = sha1s, algorithm = "sha1" }, ct),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Project id → display title (one batched request).</summary>
    public async Task<Dictionary<string, string>> GetProjectTitlesAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct) =>
        (await GetProjectInfoAsync(projectIds, ct)).ToDictionary(p => p.Key, p => p.Value.Title);

    /// <summary>Project id → (title, icon URL) in one batched request.</summary>
    public async Task<Dictionary<string, (string Title, string? IconUrl)>> GetProjectInfoAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return new();
        var result = new Dictionary<string, (string, string?)>();
        foreach (var chunk in projectIds.Chunk(100)) // keep URLs a sane length
        {
            var url = $"{ApiBase}/projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(chunk))}";
            using var doc = JsonDocument.Parse(await GetCachedAsync(url, ct));
            foreach (var p in doc.RootElement.EnumerateArray())
                result[p.GetProperty("id").GetString()!] = (p.GetProperty("title").GetString()!,
                    p.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null);
        }
        return result;
    }

    /// <summary>Version id → version (one batched request); unknown ids are absent.</summary>
    public async Task<Dictionary<string, ModrinthVersion>> GetVersionsByIdsAsync(IReadOnlyCollection<string> versionIds, CancellationToken ct)
    {
        if (versionIds.Count == 0) return new();
        var url = $"{ApiBase}/versions?ids={Uri.EscapeDataString(JsonSerializer.Serialize(versionIds))}";
        return JsonSerializer.Deserialize<List<ModrinthVersion>>(await GetCachedAsync(url, ct), Json)!.ToDictionary(v => v.Id);
    }

    /// <summary>Installs projects (e.g. missing dependencies), using the pinned version when one is given; already installed ones are skipped.</summary>
    public async Task<IReadOnlyList<InstalledContent>> InstallProjectsAsync(IEnumerable<(string ProjectId, string? VersionId)> projects, GameInstance instance,
        string gameDirectory, CancellationToken ct)
    {
        var ledger = await LoadLedgerAsync(gameDirectory, ct);
        var visited = new HashSet<string>();
        var installed = new List<InstalledContent>();
        try
        {
            foreach (var (id, version) in projects.DistinctBy(p => p.ProjectId))
                await InstallRecursiveAsync(id, id, version, isRoot: false, ProjectKind.Mod, instance.Loader, instance.MinecraftVersion, gameDirectory, ledger, visited, installed, ct);
        }
        finally { await SaveLedgerAsync(gameDirectory, ledger, CancellationToken.None); }
        return installed;
    }

    public static async Task<string> Sha1OfAsync(string path, CancellationToken ct)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        return Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(s, ct)).ToLowerInvariant();
    }

    internal static string Sha1Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(s)).ToLowerInvariant();
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await downloads.PostJsonAsync(new Uri(ApiBase + path), JsonSerializer.Serialize(body, Json), ct), Json)!;

    // shortcut: in-memory 5-minute cache per URL; move to SQLite if metadata reuse across sessions matters.
    private async Task<string> GetCachedAsync(string url, CancellationToken ct)
    {
        lock (_cache)
            if (_cache.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Body;
        var body = await downloads.GetStringAsync(new Uri(url), ct);
        lock (_cache) _cache[url] = (DateTimeOffset.UtcNow, body);
        return body;
    }
}
