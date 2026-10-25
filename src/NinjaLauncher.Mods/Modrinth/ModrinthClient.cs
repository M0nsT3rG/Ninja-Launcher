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
    [property: JsonPropertyName("date_published")] DateTimeOffset DatePublished);

public sealed record InstalledContent(string FileName, string ProjectTitle, string VersionNumber);

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
        await InstallRecursiveAsync(project.ProjectId, project.Title, kind, loader, instance.MinecraftVersion, gameDirectory, visited, installed, ct);
        return installed;
    }

    private async Task InstallRecursiveAsync(string projectId, string title, ProjectKind kind, ModLoaderKind loader, string mc, string gameDir,
        HashSet<string> visited, List<InstalledContent> installed, CancellationToken ct)
    {
        if (!visited.Add(projectId) || visited.Count > 64) return;
        var versions = await GetVersionsAsync(projectId, mc, loader, ct);
        var version = versions.OrderByDescending(v => v.DatePublished).FirstOrDefault()
            ?? throw new InvalidOperationException($"'{title}' has no release for Minecraft {mc}{(loader == ModLoaderKind.None ? "" : " / " + loader)}.");
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.First();
        var url = new Uri(file.Url);
        if (!AllowedHosts.Contains(url.Host)) throw new InvalidOperationException($"Refusing download from unexpected host {url.Host}.");

        var folder = Path.Combine(gameDir, ContentFolder(kind));
        var target = SafePath.Combine(folder, SafePath.SanitizeName(file.Filename));
        await downloads.DownloadAsync([new DownloadItem(url, target, file.Hashes.GetValueOrDefault("sha1"), file.Size)], null, ct);
        installed.Add(new InstalledContent(Path.GetFileName(target), title, version.VersionNumber));

        foreach (var dep in version.Dependencies.Where(d => d.DependencyType == "required" && d.ProjectId is not null))
            await InstallRecursiveAsync(dep.ProjectId!, dep.ProjectId!, kind, loader, mc, gameDir, visited, installed, ct);
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
