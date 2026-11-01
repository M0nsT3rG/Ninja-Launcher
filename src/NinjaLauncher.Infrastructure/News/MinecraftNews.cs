using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace NinjaLauncher.Infrastructure.News;

public sealed record NewsItem(string Title, string Text, DateOnly Date, string? ImageUrl, Uri? ArticleUrl, bool Java, bool Bedrock);

/// <summary>
/// The news shown by the official launcher: Mojang's launcher content feed, whose entries link to the matching
/// articles on minecraft.net. Read-only and anonymous; nothing is sent but the request itself.
/// </summary>
public sealed class MinecraftNews(HttpClient http)
{
    public const string FeedUrl = "https://launchercontent.mojang.com/v2/news.json";
    private const string ContentHost = "https://launchercontent.mojang.com";

    private IReadOnlyList<NewsItem>? _cached;
    private DateTime _fetchedUtc;

    /// <summary>Newest first. Kept for 30 minutes so switching pages doesn't refetch.</summary>
    public async Task<IReadOnlyList<NewsItem>> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTime.UtcNow - _fetchedUtc < TimeSpan.FromMinutes(30)) return _cached;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20)); // the shared HttpClient has no timeout of its own
        var json = await http.GetFromJsonAsync<JsonNode>(FeedUrl, cts.Token) ?? new JsonObject();
        (_cached, _fetchedUtc) = (Parse(json), DateTime.UtcNow);
        return _cached;
    }

    internal static IReadOnlyList<NewsItem> Parse(JsonNode json)
    {
        var items = new List<NewsItem>();
        foreach (var e in (json["entries"] as JsonArray ?? []).OfType<JsonNode>())
        {
            var title = (string?)e["title"];
            if (string.IsNullOrWhiteSpace(title) || !DateOnly.TryParse((string?)e["date"], out var date)) continue;
            var types = (e["newsType"] as JsonArray ?? []).Select(t => (string?)t).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Images are site-relative; links must be HTTPS (they open in the browser).
            var image = (string?)e["newsPageImage"]?["url"] ?? (string?)e["playPageImage"]?["url"];
            var link = Uri.TryCreate((string?)e["readMoreLink"], UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
            items.Add(new NewsItem(title.Trim(), ((string?)e["text"] ?? "").Trim(), date,
                image is { Length: > 1 } && image[0] == '/' ? ContentHost + image : null, link,
                types.Contains("Java"), types.Contains("Bedrock")));
        }
        return items.OrderByDescending(i => i.Date).ToList();
    }
}
