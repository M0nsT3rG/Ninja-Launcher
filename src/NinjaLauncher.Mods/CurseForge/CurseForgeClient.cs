using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.Mods.CurseForge;

public sealed record CurseForgeHash(string Value, int Algo);
public sealed record CurseForgeDependency(long ModId, int RelationType);
public sealed record CurseForgeLinks(string? WebsiteUrl);
public sealed record CurseForgeAuthor(string Name);

public sealed record CurseForgeFile(
    long Id, long ModId, string DisplayName, string FileName, int ReleaseType, DateTimeOffset FileDate, long FileLength,
    string? DownloadUrl, bool IsAvailable, IReadOnlyList<CurseForgeHash> Hashes, IReadOnlyList<string> GameVersions,
    IReadOnlyList<CurseForgeDependency> Dependencies)
{
    public string? Sha1 => Hashes.FirstOrDefault(h => h.Algo == 1)?.Value;
}

public sealed record CurseForgeMod(
    long Id, string Name, string Slug, string Summary, long DownloadCount, int ClassId, bool? AllowModDistribution,
    CurseForgeLinks? Links, IReadOnlyList<CurseForgeAuthor> Authors)
{
    public string PageUrl => Links?.WebsiteUrl ?? $"https://www.curseforge.com/minecraft/mc-mods/{Slug}";
}

public sealed record CurseForgeSearchResult(IReadOnlyList<CurseForgeMod> Mods, int TotalCount);

/// <summary>A file the author does not allow third-party apps to download; the user fetches it from <see cref="PageUrl"/>.</summary>
public sealed record BlockedFile(string ModName, string FileName, string PageUrl, string TargetFolder);

public sealed record CurseForgeInstallResult(IReadOnlyList<InstalledContent> Installed, IReadOnlyList<BlockedFile> Blocked);

public sealed class CurseForgeKeyMissingException()
    : InvalidOperationException("CurseForge needs your own API key. Add it in Settings → CurseForge (see docs/research/CURSEFORGE.md).");

/// <summary>
/// CurseForge API client (https://docs.curseforge.com). Follows the 3rd Party API Terms (docs/research/CURSEFORGE.md):
/// the key comes from the user's secret store and is never logged or shared, no API data is cached, and files whose
/// author disallows third-party distribution are never downloaded (no CDN URL construction).
/// </summary>
public sealed class CurseForgeClient(HttpClient http, Func<CancellationToken, Task<string?>> apiKey, IDownloadService downloads)
{
    public const string ApiBase = "https://api.curseforge.com";
    public const int MinecraftGameId = 432;
    private const int ClassMods = 6, ClassModpacks = 4471, ClassResourcePacks = 12;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public async Task<bool> HasKeyAsync(CancellationToken ct) => !string.IsNullOrWhiteSpace(await apiKey(ct));

    public static int LoaderType(ModLoaderKind loader) => loader switch
    {
        ModLoaderKind.Forge => 1, ModLoaderKind.Fabric => 4, ModLoaderKind.Quilt => 5, ModLoaderKind.NeoForge => 6, _ => 0,
    };

    public static string ContentFolder(int classId, int shadersClassId) =>
        classId == ClassResourcePacks ? "resourcepacks" : classId == shadersClassId && shadersClassId != 0 ? "shaderpacks" : "mods";

    public async Task<CurseForgeSearchResult> SearchAsync(string query, ProjectKind kind, string? gameVersion, ModLoaderKind loader, int index, int pageSize, CancellationToken ct)
    {
        var classId = await ClassIdAsync(kind, ct);
        var url = $"{ApiBase}/v1/mods/search?gameId={MinecraftGameId}&classId={classId}&searchFilter={Uri.EscapeDataString(query)}" +
                  $"&sortField=2&sortOrder=desc&index={index}&pageSize={Math.Clamp(pageSize, 1, 50)}";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        if (kind == ProjectKind.Mod && LoaderType(loader) is > 0 and var type && !string.IsNullOrWhiteSpace(gameVersion)) url += $"&modLoaderType={type}";
        using var doc = await GetAsync(url, ct);
        var mods = doc.RootElement.GetProperty("data").Deserialize<List<CurseForgeMod>>(Json)!;
        var total = doc.RootElement.TryGetProperty("pagination", out var p) ? p.GetProperty("totalCount").GetInt32() : mods.Count;
        return new CurseForgeSearchResult(mods, Math.Min(total, 10_000 - pageSize));
    }

    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(long modId, string? gameVersion, ModLoaderKind loader, CancellationToken ct)
    {
        var url = $"{ApiBase}/v1/mods/{modId}/files?pageSize=50";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        if (LoaderType(loader) is > 0 and var type) url += $"&modLoaderType={type}";
        using var doc = await GetAsync(url, ct);
        return doc.RootElement.GetProperty("data").Deserialize<List<CurseForgeFile>>(Json)!;
    }

    public async Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyCollection<long> modIds, CancellationToken ct)
    {
        if (modIds.Count == 0) return [];
        using var doc = await PostAsync($"{ApiBase}/v1/mods", new { modIds }, ct);
        return doc.RootElement.GetProperty("data").Deserialize<List<CurseForgeMod>>(Json)!;
    }

    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesByIdAsync(IReadOnlyCollection<long> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return [];
        using var doc = await PostAsync($"{ApiBase}/v1/mods/files", new { fileIds }, ct);
        return doc.RootElement.GetProperty("data").Deserialize<List<CurseForgeFile>>(Json)!;
    }

    /// <summary>The shaders class has no confirmed fixed id, so it is looked up by slug (0 if absent).</summary>
    public async Task<int> ShadersClassIdAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{ApiBase}/v1/categories?gameId={MinecraftGameId}&classesOnly=true", ct);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(c => c.TryGetProperty("slug", out var s) && s.GetString() == "shaders")
            .Select(c => c.GetProperty("id").GetInt32()).FirstOrDefault();
    }

    private async Task<int> ClassIdAsync(ProjectKind kind, CancellationToken ct) => kind switch
    {
        ProjectKind.Mod => ClassMods,
        ProjectKind.Modpack => ClassModpacks,
        ProjectKind.ResourcePack => ClassResourcePacks,
        _ => await ShadersClassIdAsync(ct) is > 0 and var id ? id : throw new InvalidOperationException("CurseForge does not list a shaders category."),
    };

    /// <summary>Picks the newest release (falling back to beta/alpha) and installs it plus required dependencies.</summary>
    public async Task<CurseForgeInstallResult> InstallAsync(CurseForgeMod mod, ProjectKind kind, GameInstance instance, string gameDirectory, CancellationToken ct)
    {
        if (kind == ProjectKind.Mod && instance.Loader == ModLoaderKind.None)
            throw new InvalidOperationException("This instance has no mod loader. Create a Fabric, Quilt, Forge or NeoForge instance to install mods.");
        var shaders = kind == ProjectKind.Shader ? await ShadersClassIdAsync(ct) : 0;
        var installed = new List<InstalledContent>();
        var blocked = new List<BlockedFile>();
        var visited = new HashSet<long>();
        await InstallRecursiveAsync(mod, kind, instance, gameDirectory, shaders, visited, installed, blocked, ct);
        return new CurseForgeInstallResult(installed, blocked);
    }

    private async Task InstallRecursiveAsync(CurseForgeMod mod, ProjectKind kind, GameInstance instance, string gameDir, int shadersClass,
        HashSet<long> visited, List<InstalledContent> installed, List<BlockedFile> blocked, CancellationToken ct)
    {
        if (!visited.Add(mod.Id) || visited.Count > 64) return;
        var loader = kind == ProjectKind.Mod ? instance.Loader : ModLoaderKind.None;
        var files = (await GetFilesAsync(mod.Id, instance.MinecraftVersion, loader, ct)).Where(f => f.IsAvailable).OrderByDescending(f => f.FileDate).ToList();
        var file = files.FirstOrDefault(f => f.ReleaseType == 1) ?? files.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{mod.Name}' has no file for Minecraft {instance.MinecraftVersion}{(loader == ModLoaderKind.None ? "" : " / " + loader)}.");
        var folder = ContentFolder(mod.ClassId, shadersClass);
        var (path, block) = await DownloadAsync(mod, file, gameDir, folder, ct);
        if (block is not null) blocked.Add(block);
        else installed.Add(new InstalledContent(Path.GetFileName(path!), mod.Name, file.DisplayName));

        var required = file.Dependencies.Where(d => d.RelationType == 3).Select(d => d.ModId).Where(id => !visited.Contains(id)).ToList();
        foreach (var dep in await GetModsAsync(required, ct))
            await InstallRecursiveAsync(dep, ProjectKind.Mod, instance, gameDir, shadersClass, visited, installed, blocked, ct);
    }

    /// <summary>Downloads one file if the author allows it; otherwise returns it as blocked (never builds a CDN URL).</summary>
    internal async Task<(string? Path, BlockedFile? Blocked)> DownloadAsync(CurseForgeMod mod, CurseForgeFile file, string gameDir, string folder, CancellationToken ct)
    {
        var target = SafePath.Combine(gameDir, Path.Combine(folder, SafePath.SanitizeName(file.FileName)));
        if (mod.AllowModDistribution == false || string.IsNullOrWhiteSpace(file.DownloadUrl))
            return (null, new BlockedFile(mod.Name, file.FileName, $"{mod.PageUrl.TrimEnd('/')}/files/{file.Id}", folder));
        var url = new Uri(file.DownloadUrl);
        if (url.Scheme != Uri.UriSchemeHttps || !IsCurseForgeCdn(url.Host))
            throw new InvalidOperationException($"Refusing CurseForge download from unexpected host {url.Host}.");
        await downloads.DownloadAsync([new DownloadItem(url, target, file.Sha1, file.FileLength)], null, ct);
        return (target, null);
    }

    internal static bool IsCurseForgeCdn(string host) => host == "forgecdn.net" || host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);

    // Per the API terms nothing here is cached: every call goes to the API.
    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, ct);
    }

    private async Task<JsonDocument> PostAsync(string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        return await SendAsync(request, ct);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = await apiKey(ct);
        if (string.IsNullOrWhiteSpace(key)) throw new CurseForgeKeyMissingException();
        request.Headers.Add("x-api-key", key);
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("CurseForge rejected the API key (403). Check the key in Settings → CurseForge.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"CurseForge returned {(int)response.StatusCode}.", null, response.StatusCode);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
    }
}
