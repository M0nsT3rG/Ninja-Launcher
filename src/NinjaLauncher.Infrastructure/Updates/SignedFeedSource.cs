using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace NinjaLauncher.Infrastructure.Updates;

/// <summary>
/// Velopack update source that refuses any release feed whose detached signature does not verify against the
/// embedded public key. Package files are then checked by Velopack against the hashes in that verified feed, so
/// nothing can be installed that the release key holder did not sign.
/// </summary>
public sealed class SignedFeedSource(string baseUrl, string publicKeyPem, HttpClient http) : IUpdateSource
{
    private const int MaxFeedBytes = 4 * 1024 * 1024;
    private readonly SimpleWebSource _packages = new(baseUrl);

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new InvalidOperationException("An explicit update channel is required.");
        var feedUrl = new Uri($"{baseUrl.TrimEnd('/')}/releases.{channel}.json");
        if (feedUrl.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Update feeds must use HTTPS.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Two attempts: a release published between the feed and .sig requests yields a mismatched pair once.
        for (var attempt = 1; ; attempt++)
        {
            var feed = await GetBoundedAsync(feedUrl, timeout.Token);
            var signature = Encoding.ASCII.GetString(await GetBoundedAsync(new Uri(feedUrl + FeedSignature.Extension), timeout.Token));
            if (FeedSignature.Verify(feed, channel, signature, publicKeyPem)) return VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feed));
            if (attempt == 2) throw new UpdateSignatureException("The update feed signature is invalid. The update was refused.");
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
    {
        // The asset came from the verified feed; Velopack checks the downloaded file against its hashes.
        if (releaseEntry.FileName.Contains('/') || releaseEntry.FileName.Contains('\\') || releaseEntry.FileName.Contains(".."))
            throw new UpdateSignatureException("Update package name is not a plain file name.");
        return _packages.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }

    private async Task<byte[]> GetBoundedAsync(Uri url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Update feed redirected away from HTTPS.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFeedBytes) throw new InvalidDataException("Update feed is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

public sealed class UpdateSignatureException(string message) : Exception(message);
