using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;

namespace NinjaLauncher.Infrastructure.Downloads;

/// <summary>
/// Downloads to a <c>.part</c> file, verifies SHA-1/size, then atomically moves into place.
/// Bounded concurrency, bounded retries with backoff, HTTPS only.
/// </summary>
public sealed class HttpDownloadService(HttpClient http, Func<int> concurrency, ILogger<HttpDownloadService> logger) : IDownloadService
{
    public const int MaxAttempts = 4;
    public static string UserAgent => $"NinjaLauncher/{typeof(HttpDownloadService).Assembly.GetName().Version?.ToString(3)} (+https://github.com/M0nsT3rG/Ninja-Launcher)";

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>When true, existing files are SHA-1 checked instead of only size checked (repair mode).</summary>
    public bool FullVerification { get; set; }

    public async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        EnsureHttps(url);
        return await WithRetry(async token =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await http.GetAsync(url, timeout.Token);
            ThrowForStatus(response);
            return await response.Content.ReadAsStringAsync(timeout.Token);
        }, url, ct);
    }

    public async Task<string> PostJsonAsync(Uri url, string json, CancellationToken ct)
    {
        EnsureHttps(url);
        return await WithRetry(async token =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, content, timeout.Token);
            ThrowForStatus(response);
            return await response.Content.ReadAsStringAsync(timeout.Token);
        }, url, ct);
    }

    public async Task DownloadAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var unique = items.GroupBy(i => Path.GetFullPath(i.DestinationPath), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var pending = await Task.Run(() => unique.Where(i => !IsValid(i, FullVerification)).ToList(), ct);
        long? totalBytes = pending.All(i => i.Size.HasValue) ? pending.Sum(i => i.Size!.Value) : null;
        int completed = unique.Count - pending.Count;
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        void Report() => progress?.Report(new DownloadProgress(Volatile.Read(ref completed), unique.Count, Interlocked.Read(ref bytes), totalBytes,
            sw.Elapsed.TotalSeconds > 0 ? Interlocked.Read(ref bytes) / sw.Elapsed.TotalSeconds : 0));
        Report();
        if (pending.Count == 0) return;

        var failures = new List<string>();
        using var ticker = new Timer(_ => Report(), null, 250, 250);
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(concurrency(), 1, 32), CancellationToken = ct }, async (item, token) =>
        {
            try
            {
                await WithRetry(async t => { await DownloadOne(item, n => Interlocked.Add(ref bytes, n), t); return true; }, item.Url, token);
                Interlocked.Increment(ref completed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Download failed for {Url}", item.Url);
                lock (failures) failures.Add($"{item.Url}: {e.Message}");
            }
        });
        Report();
        if (failures.Count > 0)
            throw new DownloadFailedException($"{failures.Count} file(s) failed to download. First error: {failures[0]}", failures);
    }

    internal static bool IsValid(DownloadItem item, bool fullVerification)
    {
        var file = new FileInfo(item.DestinationPath);
        if (!file.Exists) return false;
        if (item.Size is { } size && file.Length != size) return false;
        if (item.Sha1 is null) return item.Size is not null || file.Length > 0;
        return !fullVerification && item.Size is not null || HashMatches(file.FullName, item.Sha1);
    }

    internal static bool HashMatches(string path, string sha1)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadOne(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        EnsureHttps(item.Url);
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        var part = item.DestinationPath + ".part";
        long written = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ThrowForStatus(response);
            using var sha = item.Sha1 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    sha?.AppendData(buffer, 0, read);
                    written += read;
                    onBytes(read);
                    if (item.Size is { } max && written > max) throw new InvalidDataException("Server sent more data than expected.");
                }
            }
            if (item.Size is { } size && written != size) throw new InvalidDataException($"Size mismatch: expected {size}, got {written}.");
            if (sha is not null && !Convert.ToHexString(sha.GetHashAndReset()).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-1 mismatch.");
            File.Move(part, item.DestinationPath, overwrite: true);
        }
        catch
        {
            onBytes(-written);
            TryDelete(part);
            throw;
        }
    }

    private async Task<T> WithRetry<T>(Func<CancellationToken, Task<T>> action, Uri url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                try { return await action(ct); }
                catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out downloading {url}.", e); // a timeout, not a user cancel
                }
            }
            catch (Exception e) when (attempt < MaxAttempts && !ct.IsCancellationRequested && IsTransient(e))
            {
                logger.LogWarning("Attempt {Attempt} for {Url} failed: {Message}", attempt, url, e.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    private static bool IsTransient(Exception e) => e switch
    {
        HttpStatusException { StatusCode: { } code } => (int)code >= 500 || code is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout,
        HttpRequestException or IOException or InvalidDataException or TimeoutException => true,
        _ => false,
    };

    private static void ThrowForStatus(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) throw new HttpStatusException(r.StatusCode, $"HTTP {(int)r.StatusCode} from {r.RequestMessage?.RequestUri}");
    }

    private static void EnsureHttps(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException($"Refusing non-HTTPS download: {url}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

public sealed class HttpStatusException(HttpStatusCode statusCode, string message) : HttpRequestException(message, null, statusCode);

public sealed class DownloadFailedException(string message, IReadOnlyList<string> failures) : Exception(message)
{
    public IReadOnlyList<string> Failures { get; } = failures;
}
