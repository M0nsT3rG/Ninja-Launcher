using System.IO.Compression;
using System.Net;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.CurseForge;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.UnitTests;

public sealed class CurseForgeTests
{
    private sealed class FakeApi(Func<HttpRequestMessage, string?> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = respond(request);
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeDownloads : IDownloadService
    {
        public readonly List<DownloadItem> Items = [];
        public Task DownloadAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct) { Items.AddRange(items); return Task.CompletedTask; }
        public Task<string> GetStringAsync(Uri url, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> PostJsonAsync(Uri url, string json, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly GameInstance Fabric = new() { Id = Guid.NewGuid(), Name = "t", MinecraftVersion = "1.21.1", Loader = ModLoaderKind.Fabric, LoaderVersion = "x" };

    private static string Mod(long id, string name, bool allow = true, int classId = 6) =>
        $$"""{"id":{{id}},"name":"{{name}}","slug":"{{name.ToLowerInvariant()}}","summary":"s","downloadCount":5,"classId":{{classId}},"allowModDistribution":{{(allow ? "true" : "false")}},"links":{"websiteUrl":"https://www.curseforge.com/minecraft/mc-mods/{{name.ToLowerInvariant()}}"},"authors":[{"name":"a"}]}""";

    private static string File(long id, long modId, string? url, params long[] requiredDeps)
    {
        var deps = string.Join(",", requiredDeps.Select(d => "{\"modId\":" + d + ",\"relationType\":3}"));
        var download = url is null ? "null" : "\"" + url + "\"";
        return $$$"""{"id":{{{id}}},"modId":{{{modId}}},"displayName":"f{{{id}}}","fileName":"mod-{{{id}}}.jar","releaseType":1,"fileDate":"2026-01-0{{{id % 9 + 1}}}T00:00:00Z","fileLength":10,"downloadUrl":{{{download}}},"isAvailable":true,"hashes":[{"value":"{{{Sha(id)}}}","algo":1}],"gameVersions":["1.21.1"],"dependencies":[{{{deps}}}]}""";
    }

    private static string Sha(long id) => id.ToString("D40");

    /// <summary>POST /v1/mods answers with the requested ids from this catalogue.</summary>
    private static string? Mods(HttpRequestMessage r, params (long Id, string Json)[] catalogue)
    {
        var body = r.Content!.ReadAsStringAsync().Result;
        var ids = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("modIds").EnumerateArray().Select(e => e.GetInt64()).ToHashSet();
        return "{\"data\":[" + string.Join(",", catalogue.Where(c => ids.Contains(c.Id)).Select(c => c.Json)) + "]}";
    }

    private static (CurseForgeClient Client, FakeApi Api, FakeDownloads Downloads) Create(Func<HttpRequestMessage, string?> respond, string? key = "test-key-000000000000")
    {
        var api = new FakeApi(respond);
        var downloads = new FakeDownloads();
        return (new CurseForgeClient(new HttpClient(api), _ => Task.FromResult(key), downloads), api, downloads);
    }

    [Fact]
    public async Task Search_sends_key_and_minecraft_filters_and_never_caches()
    {
        var (client, api, _) = Create(_ => "{\"data\":[" + Mod(1, "Jei") + "],\"pagination\":{\"index\":0,\"pageSize\":20,\"resultCount\":1,\"totalCount\":1}}");
        var r = await client.SearchAsync("jei", ProjectKind.Mod, "1.21.1", ModLoaderKind.Fabric, 0, 20, TestContext.Current.CancellationToken);
        await client.SearchAsync("jei", ProjectKind.Mod, "1.21.1", ModLoaderKind.Fabric, 0, 20, TestContext.Current.CancellationToken);

        Assert.Equal("Jei", Assert.Single(r.Mods).Name);
        Assert.Equal(2, api.Requests.Count); // terms §3.1: no caching of API data
        var url = api.Requests[0].RequestUri!.Query;
        Assert.Contains("gameId=432", url);
        Assert.Contains("classId=6", url);
        Assert.Contains("modLoaderType=4", url);
        Assert.Contains("gameVersion=1.21.1", url);
        Assert.Equal("test-key-000000000000", api.Requests[0].Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var (client, api, _) = Create(_ => "{}", key: null);
        await Assert.ThrowsAsync<CurseForgeKeyMissingException>(() => client.SearchAsync("x", ProjectKind.Mod, null, ModLoaderKind.None, 0, 20, TestContext.Current.CancellationToken));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Install_downloads_allowed_files_with_hash_and_their_required_dependencies()
    {
        var (client, _, downloads) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/1/files" => $$"""{"data":[{{File(10, 1, "https://edge.forgecdn.net/files/1/10/mod-10.jar", 2)}}]}""",
            "/v1/mods/2/files" => $$"""{"data":[{{File(20, 2, "https://mediafilez.forgecdn.net/files/2/20/mod-20.jar")}}]}""",
            "/v1/mods" => Mods(r, (1, Mod(1, "Main")), (2, Mod(2, "Api"))),
            _ => null,
        });
        var result = await client.InstallAsync(System.Text.Json.JsonSerializer.Deserialize<CurseForgeMod>(Mod(1, "Main"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!,
            ProjectKind.Mod, Fabric, Path.Combine(Path.GetTempPath(), "cf-game"), TestContext.Current.CancellationToken);
        Assert.Equal(["mod-10.jar", "mod-20.jar"], result.Installed.Select(i => i.FileName));
        Assert.Empty(result.Blocked);
        Assert.Equal([Sha(10), Sha(20)], downloads.Items.Select(i => i.Sha1));
        Assert.All(downloads.Items, i => Assert.True(i.VerifyExisting)); // existing files are re-hashed, not trusted by size
        Assert.All(downloads.Items, i => Assert.Contains($"{Path.DirectorySeparatorChar}mods{Path.DirectorySeparatorChar}", i.DestinationPath));
    }

    [Theory]
    [InlineData(false, "https://edge.forgecdn.net/files/1/10/mod-10.jar")] // author disallows third-party distribution
    [InlineData(true, null)]                                               // no download URL published
    public async Task Blocked_files_are_never_downloaded(bool allow, string? url)
    {
        var (client, _, downloads) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/1/files" => $$"""{"data":[{{File(10, 1, url)}}]}""",
            "/v1/mods" => Mods(r, (1, Mod(1, "Main", allow))),
            _ => null,
        });
        // The search result claims distribution is allowed; the fresh lookup at install time decides.
        var mod = System.Text.Json.JsonSerializer.Deserialize<CurseForgeMod>(Mod(1, "Main", true), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var result = await client.InstallAsync(mod, ProjectKind.Mod, Fabric, Path.Combine(Path.GetTempPath(), "cf-game"), TestContext.Current.CancellationToken);
        var blocked = Assert.Single(result.Blocked);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/main/files/10", blocked.PageUrl);
        Assert.Empty(downloads.Items);
    }

    [Theory]
    [InlineData("https://evil.example/mod.jar")]
    [InlineData("http://edge.forgecdn.net/files/1/10/mod-10.jar")]
    [InlineData("https://forgecdn.net.evil.example/mod.jar")]
    public async Task Downloads_outside_the_curseforge_cdn_are_refused(string url)
    {
        var (client, _, downloads) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/1/files" => $$"""{"data":[{{File(10, 1, url)}}]}""",
            "/v1/mods" => Mods(r, (1, Mod(1, "Main"))),
            _ => null,
        });
        var mod = System.Text.Json.JsonSerializer.Deserialize<CurseForgeMod>(Mod(1, "Main"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(mod, ProjectKind.Mod, Fabric, Path.GetTempPath(), TestContext.Current.CancellationToken));
        Assert.Empty(downloads.Items);
    }

    [Fact]
    public async Task Missing_required_dependency_fails_loudly()
    {
        var (client, _, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/1/files" => $$"""{"data":[{{File(10, 1, "https://edge.forgecdn.net/files/1/10/mod-10.jar", 2, 3)}}]}""",
            "/v1/mods" => Mods(r, (1, Mod(1, "Main")), (2, Mod(2, "Api"))), // project 3 no longer exists
            _ => null,
        });
        var mod = System.Text.Json.JsonSerializer.Deserialize<CurseForgeMod>(Mod(1, "Main"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(mod, ProjectKind.Mod, Fabric, Path.GetTempPath(), TestContext.Current.CancellationToken));
        Assert.Contains("3", e.Message);
    }

    [Fact]
    public async Task File_without_sha1_is_refused()
    {
        var json = File(10, 1, "https://edge.forgecdn.net/files/1/10/mod-10.jar").Replace(Sha(10), "md5only");
        var (client, _, downloads) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/1/files" => "{\"data\":[" + json + "]}",
            "/v1/mods" => Mods(r, (1, Mod(1, "Main"))),
            _ => null,
        });
        var mod = System.Text.Json.JsonSerializer.Deserialize<CurseForgeMod>(Mod(1, "Main"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(mod, ProjectKind.Mod, Fabric, Path.GetTempPath(), TestContext.Current.CancellationToken));
        Assert.Empty(downloads.Items);
    }

    [Theory]
    [InlineData("forge-47.2.0", ModLoaderKind.Forge, "1.20.1-47.2.0")]
    [InlineData("neoforge-47.1.106", ModLoaderKind.NeoForge, "47.1.106")]
    [InlineData("fabric-0.16.5", ModLoaderKind.Fabric, "0.16.5")]
    [InlineData("quilt-0.26.0", ModLoaderKind.Quilt, "0.26.0")]
    public void Manifest_loader_ids_map_to_our_versions(string id, ModLoaderKind kind, string version)
    {
        var manifest = new CurseForgeManifest(new("1.20.1", [new(id, true)]), "P", "1", [], "overrides");
        Assert.Equal((kind, version), CurseForgeModpackInstaller.MapLoader(manifest));
    }

    [Theory]
    [InlineData("""{"minecraft":{"version":"1.20.1","modLoaders":[]},"files":[{"projectID":0,"fileID":1}]}""")]
    [InlineData("""{"minecraft":{"version":"","modLoaders":[]},"files":[]}""")]
    [InlineData("""{"minecraft":{"version":"1.20.1","modLoaders":[]},"files":[],"overrides":"../escape"}""")]
    public void Unsafe_or_invalid_manifests_are_rejected(string json)
    {
        var zip = Path.Combine(Path.GetTempPath(), $"cf-{Guid.NewGuid():N}.zip");
        try
        {
            using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
                using (var w = new StreamWriter(a.CreateEntry("manifest.json").Open())) w.Write(json);
            Assert.Throws<InvalidDataException>(() => CurseForgeModpackInstaller.ReadManifest(zip));
        }
        finally { System.IO.File.Delete(zip); }
    }

    [Fact]
    public void Fingerprint_is_murmur2_seed_1_over_non_whitespace_bytes()
    {
        // MurmurHash2 reference vectors (Austin Appleby's 32-bit variant).
        Assert.Equal(0x5BD15E36u, CurseForgeFingerprint.MurmurHash2(""u8, 1));
        Assert.Equal(CurseForgeFingerprint.Compute("ab"u8), CurseForgeFingerprint.Compute(" a\tb\r\n "u8)); // whitespace ignored
        Assert.NotEqual(CurseForgeFingerprint.Compute("ab"u8), CurseForgeFingerprint.Compute("ba"u8));
    }

    [Theory]
    [InlineData(ModLoaderKind.Forge, "1.20.1-47.2.0", "forge-47.2.0")]
    [InlineData(ModLoaderKind.NeoForge, "21.1.65", "neoforge-21.1.65")]
    [InlineData(ModLoaderKind.Fabric, "0.16.5", "fabric-0.16.5")]
    public void Export_loader_ids_match_the_import_mapping(ModLoaderKind kind, string version, string id)
    {
        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "x", MinecraftVersion = "1.20.1", Loader = kind, LoaderVersion = version };
        Assert.Equal(id, CurseForgeExporter.LoaderId(instance));
        Assert.Equal((kind, version), CurseForgeModpackInstaller.MapLoader(new CurseForgeManifest(new("1.20.1", [new(id, true)]), "x", "1", [], null)));
    }

    [Fact]
    public async Task Export_without_key_bundles_everything_and_passes_import_validation()
    {
        var game = Directory.CreateTempSubdirectory("cf-export-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "mods"));
            Directory.CreateDirectory(Path.Combine(game, "saves", "W"));
            System.IO.File.WriteAllText(Path.Combine(game, "mods", "a.jar"), "jar");
            System.IO.File.WriteAllText(Path.Combine(game, "mods", "b.jar.disabled"), "jar");
            System.IO.File.WriteAllText(Path.Combine(game, "saves", "W", "level.dat"), "x");
            var (client, api, _) = Create(_ => null, key: null);
            var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Pack", MinecraftVersion = "1.20.1", Loader = ModLoaderKind.Forge, LoaderVersion = "1.20.1-47.2.0" };
            var output = Path.Combine(game, "out.zip");
            var r = await new CurseForgeExporter(client).ExportAsync(instance, game, output, "1.0.0", null, TestContext.Current.CancellationToken);
            Assert.False(r.CurseForgeChecked);
            Assert.Equal((0, 1, 1), (r.LinkedFiles, r.OverrideFiles, r.SkippedDisabled));
            Assert.Empty(api.Requests);
            var manifest = CurseForgeModpackInstaller.ReadManifest(output);
            Assert.Equal("forge-47.2.0", manifest.Minecraft.ModLoaders[0].Id);
            using var zip = ZipFile.OpenRead(output);
            Assert.Contains(zip.Entries, e => e.FullName == "overrides/mods/a.jar");
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("saves") || e.FullName.Contains("b.jar"));
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData("edge.forgecdn.net", true)]
    [InlineData("mediafilez.forgecdn.net", true)]
    [InlineData("forgecdn.net.evil.com", false)]
    [InlineData("notforgecdn.net", false)]
    public void Cdn_host_check(string host, bool ok) => Assert.Equal(ok, CurseForgeClient.IsCurseForgeCdn(host));
}
