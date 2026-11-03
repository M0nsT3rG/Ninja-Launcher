using System.Globalization;
using System.Text.Json;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Desktop.Views;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.UnitTests;

public sealed class ModsSearchTests
{
    /// <summary>Records the URLs the client asks for and answers with canned JSON.</summary>
    private sealed class RecordingDownloads(string body) : IDownloadService
    {
        public readonly List<Uri> Requests = [];
        public Task DownloadAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetStringAsync(Uri url, CancellationToken ct) { Requests.Add(url); return Task.FromResult(body); }
        public Task<string> PostJsonAsync(Uri url, string json, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Modrinth_search_sends_the_sort_and_one_facet_per_filter()
    {
        var downloads = new RecordingDownloads("""{"hits":[],"offset":0,"limit":20,"total_hits":0}""");
        await new ModrinthClient(downloads).SearchAsync("map", ProjectKind.Modpack, "1.21.1", ModLoaderKind.Fabric, 40, 20,
            TestContext.Current.CancellationToken, SearchSort.Follows, ["adventure", "magic"]);

        var q = Query(Assert.Single(downloads.Requests));
        Assert.Equal("follows", q["index"]);
        Assert.Equal("40", q["offset"]);
        var facets = JsonSerializer.Deserialize<string[][]>(q["facets"])!;
        Assert.Equal([["project_type:modpack"], ["versions:1.21.1"], ["categories:fabric"], ["categories:adventure"], ["categories:magic"]], facets);
    }

    [Fact]
    public async Task Modrinth_categories_are_the_ones_for_the_tab_and_data_packs_use_the_mod_list()
    {
        var downloads = new RecordingDownloads("""
            [{"name":"magic","project_type":"mod","header":"categories"},{"name":"kitchen-sink","project_type":"modpack","header":"categories"},
             {"name":"16x","project_type":"resourcepack","header":"resolutions"}]
            """);
        var client = new ModrinthClient(downloads);

        Assert.Equal([new CategoryOption("kitchen-sink", "Kitchen sink")], await client.GetCategoriesAsync(ProjectKind.Modpack, TestContext.Current.CancellationToken));
        Assert.Equal("magic", Assert.Single(await client.GetCategoriesAsync(ProjectKind.DataPack, TestContext.Current.CancellationToken)).Id);
    }

    private sealed class HashLookup(string body) : IDownloadService
    {
        public string? Posted;
        public Task DownloadAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetStringAsync(Uri url, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> PostJsonAsync(Uri url, string json, CancellationToken ct) { Posted = json; return Task.FromResult(body); }
    }

    [Fact]
    public async Task Installed_projects_are_found_by_hash_including_disabled_files()
    {
        var dir = Directory.CreateTempSubdirectory("ninja-installed-").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "a.jar"), "a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "pack.zip.disabled"), "b", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "not content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "odd.j.disabledar"), "c", TestContext.Current.CancellationToken); // not a jar Minecraft loads
        var sha1OfA = "86f7e437faa5a7fce15d1ddcb9eaeaea377667b8";
        var lookup = new HashLookup($$$"""
            {"{{{sha1OfA}}}":{"id":"v1","project_id":"AANobbMI","name":"Sodium","version_number":"1","game_versions":[],"loaders":[],"files":[],"dependencies":[],"date_published":"2026-01-01T00:00:00Z"}}
            """);

        var ids = await new ModrinthClient(lookup).InstalledProjectIdsAsync(dir, TestContext.Current.CancellationToken);

        Assert.Equal(["AANobbMI"], ids);
        var hashes = JsonDocument.Parse(lookup.Posted!).RootElement.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()).ToList();
        Assert.Equal(2, hashes.Count); // the jar and the disabled zip, not the text file
        Assert.Contains(sha1OfA, hashes);
        Assert.Empty(await new ModrinthClient(lookup).InstalledProjectIdsAsync(Path.Combine(dir, "missing"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(812L, "812")]
    [InlineData(37_432L, "37K")]
    [InlineData(1_500L, "1.5K")]
    [InlineData(294_620_270L, "295M")]
    [InlineData(2_400_000L, "2.4M")]
    public void Counts_are_shortened_like_the_catalogues_do(long count, string expected) =>
        Assert.Equal(expected, Converters.Compact.Convert(count, typeof(string), null, CultureInfo.InvariantCulture));
}
