using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace NinjaLauncher.Desktop.Controls;

/// <summary>
/// <c>&lt;Image controls:RemoteImage.Url="{Binding IconUrl}" /&gt;</c> — loads a project logo asynchronously.
/// HTTPS from known catalogue CDNs only, 2 MB cap, decoded small. Modrinth icons are cached on disk; CurseForge
/// ones stay in memory only (its API terms forbid caching data obtained through the API).
/// </summary>
public static class RemoteImage
{
    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Url", typeof(RemoteImage));

    public static string? GetUrl(Image image) => image.GetValue(UrlProperty);
    public static void SetUrl(Image image, string? value) => image.SetValue(UrlProperty, value);

    /// <summary>Set at startup; null disables the disk cache.</summary>
    public static string? CacheDirectory { get; set; }

    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MemoryLimit = 400;
    private static readonly SemaphoreSlim Gate = new(6);
    private static readonly Dictionary<string, Bitmap> Memory = new();
    private static readonly Queue<string> MemoryOrder = new();
    private static readonly HttpClient Http = CreateClient();

    static RemoteImage() => UrlProperty.Changed.AddClassHandler<Image>((image, e) => _ = LoadAsync(image, e.NewValue as string));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Infrastructure.Downloads.HttpDownloadService.UserAgent);
        return client;
    }

    internal static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "cdn.modrinth.com" || uri.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase));

    private static bool DiskCacheable(Uri uri) => uri.Host == "cdn.modrinth.com";

    private static async Task LoadAsync(Image image, string? url)
    {
        image.Source = null;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (url.StartsWith("avares://", StringComparison.Ordinal))
        {
            using var asset = Avalonia.Platform.AssetLoader.Open(new Uri(url));
            image.Source = Bitmap.DecodeToWidth(asset, 96, BitmapInterpolationMode.MediumQuality);
            return;
        }
        if (Path.IsPathRooted(url) && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // Local instance icons: read fresh each time (they can change in place), never through the network path.
            var local = await Task.Run(() => File.Exists(url) && new FileInfo(url).Length <= MaxBytes ? Decode(File.ReadAllBytes(url)) : null);
            if (GetUrl(image) == url) image.Source = local;
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowed(uri)) return;
        lock (Memory)
            if (Memory.TryGetValue(url, out var hit)) { image.Source = hit; return; }

        var bitmap = await Task.Run(() => FetchAsync(uri));
        if (bitmap is null) return;
        lock (Memory)
        {
            if (Memory.TryAdd(url, bitmap)) MemoryOrder.Enqueue(url);
            while (MemoryOrder.Count > MemoryLimit && MemoryOrder.TryDequeue(out var old)) Memory.Remove(old);
        }
        // The control may have been recycled for another item while we were downloading.
        await Dispatcher.UIThread.InvokeAsync(() => { if (GetUrl(image) == url) image.Source = bitmap; });
    }

    private static async Task<Bitmap?> FetchAsync(Uri uri)
    {
        var diskPath = CacheDirectory is not null && DiskCacheable(uri)
            ? Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32] + ".img")
            : null;
        try
        {
            if (diskPath is not null && File.Exists(diskPath)) return Decode(await File.ReadAllBytesAsync(diskPath));
            await Gate.WaitAsync();
            try
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk)) > 0)
                {
                    if (buffer.Length + read > MaxBytes) return null;
                    buffer.Write(chunk, 0, read);
                }
                var bytes = buffer.ToArray();
                var bitmap = Decode(bytes);
                if (bitmap is not null && diskPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                    await File.WriteAllBytesAsync(diskPath, bytes);
                }
                return bitmap;
            }
            finally { Gate.Release(); }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            return null; // a missing logo is cosmetic
        }
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(ms, 96, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception) // corrupt, unsupported (e.g. SVG) or truncated images just show no logo
        {
            return null;
        }
    }
}
