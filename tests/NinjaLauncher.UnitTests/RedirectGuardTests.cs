using System.Net;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Infrastructure.Downloads;

namespace NinjaLauncher.UnitTests;

public sealed class RedirectGuardTests
{
    private sealed class Redirecting(Dictionary<string, string> redirects) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            if (redirects.TryGetValue(request.RequestUri!.AbsoluteUri, out var to))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    private static (HttpClient Client, Redirecting Inner) Create(Func<Uri, bool> allowed, Dictionary<string, string> redirects)
    {
        var inner = new Redirecting(redirects);
        return (new HttpClient(new RedirectGuardHandler(allowed) { InnerHandler = inner }), inner);
    }

    [Fact]
    public async Task Redirect_to_a_disallowed_host_is_refused_and_key_not_forwarded()
    {
        var (client, inner) = Create(u => u.Host == "api.curseforge.com", new() { ["https://api.curseforge.com/v1/x"] = "https://evil.example/steal" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.curseforge.com/v1/x");
        request.Headers.Add("x-api-key", "secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Single(inner.Seen); // the evil host was never contacted
    }

    [Fact]
    public async Task Allowed_cross_host_redirect_drops_custom_headers()
    {
        var (client, inner) = Create(u => u.Host.EndsWith(".forgecdn.net"),
            new() { ["https://edge.forgecdn.net/files/1/2/a.jar"] = "https://mediafilez.forgecdn.net/files/1/2/a.jar" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://edge.forgecdn.net/files/1/2/a.jar");
        request.Headers.Add("x-api-key", "secret");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mediafilez.forgecdn.net", inner.Seen[1].RequestUri!.Host);
        Assert.False(inner.Seen[1].Headers.Contains("x-api-key"));
    }

    [Fact]
    public async Task Https_to_http_downgrade_is_refused()
    {
        var (client, _) = Create(_ => true, new() { ["https://edge.forgecdn.net/a"] = "http://edge.forgecdn.net/a" });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://edge.forgecdn.net/a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Verify_existing_rehashes_instead_of_trusting_size()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "hello");
            var wrongHash = new DownloadItem(new Uri("https://x/y"), file, "0000000000000000000000000000000000000000", 5, VerifyExisting: true);
            Assert.False(HttpDownloadService.IsValid(wrongHash, fullVerification: false));
            Assert.True(HttpDownloadService.IsValid(wrongHash with { VerifyExisting = false }, fullVerification: false));
        }
        finally { File.Delete(file); }
    }
}
