using System.Net;

namespace NinjaLauncher.Infrastructure.Downloads;

/// <summary>
/// Follows redirects itself (the inner handler must have auto-redirect off) and refuses any hop that is not HTTPS
/// or not allowed by <paramref name="allowed"/>. Custom headers (e.g. API keys) are never forwarded to another host.
/// </summary>
public sealed class RedirectGuardHandler(Func<Uri, bool> allowed, int maxRedirects = 5) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Check(request.RequestUri!);
        var response = await base.SendAsync(request, ct);
        for (var hop = 0; IsRedirect(response.StatusCode); hop++)
        {
            var location = response.Headers.Location;
            if (location is null || hop >= maxRedirects) return response;
            var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            Check(next);
            var follow = new HttpRequestMessage(HttpMethod.Get, next);
            foreach (var accept in request.Headers.Accept) follow.Headers.Accept.Add(accept);
            if (next.Host.Equals(request.RequestUri!.Host, StringComparison.OrdinalIgnoreCase))
                foreach (var h in request.Headers.Where(h => h.Key != "Host")) follow.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Dispose();
            request = follow;
            response = await base.SendAsync(follow, ct);
        }
        return response;
    }

    private void Check(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !allowed(uri)) throw new HttpRequestException($"Refusing request or redirect to {uri.Host}.");
    }

    private static bool IsRedirect(HttpStatusCode s) => (int)s is 301 or 302 or 303 or 307 or 308;

    /// <summary>An HttpClient whose every request and redirect must satisfy <paramref name="allowed"/>.</summary>
    public static HttpClient CreateClient(Func<Uri, bool> allowed)
    {
        var inner = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(new RedirectGuardHandler(allowed) { InnerHandler = inner }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDownloadService.UserAgent);
        return client;
    }
}
