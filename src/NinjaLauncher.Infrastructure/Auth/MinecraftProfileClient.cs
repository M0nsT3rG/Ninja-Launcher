using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NinjaLauncher.Application.Services;

namespace NinjaLauncher.Infrastructure.Auth;

public sealed record SkinTexture(string Id, string Url, bool Slim, bool Active);
public sealed record CapeTexture(string Id, string Url, string Alias, bool Active);
public sealed record MinecraftProfileTextures(string Name, IReadOnlyList<SkinTexture> Skins, IReadOnlyList<CapeTexture> Capes)
{
    public SkinTexture? ActiveSkin => Skins.FirstOrDefault(s => s.Active);
    public CapeTexture? ActiveCape => Capes.FirstOrDefault(c => c.Active);
}

/// <summary>
/// The account's skin and capes through Minecraft Services, the same endpoints the official launcher uses. Every call
/// needs the signed-in Minecraft access token; nothing here works for offline profiles.
/// </summary>
public sealed class MinecraftProfileClient(HttpClient http)
{
    private const string Profile = "https://api.minecraftservices.com/minecraft/profile";
    public const int MaxSkinBytes = 256 * 1024; // a real 64×64 skin is a few KB; this only stops absurd files

    public Task<MinecraftProfileTextures> GetAsync(string token, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, Profile, token, null, ct);

    /// <summary>Uploads a 64×64 or legacy 64×32 PNG; <paramref name="slim"/> picks the 3-pixel-arm model.</summary>
    public Task<MinecraftProfileTextures> UploadSkinAsync(string token, byte[] png, bool slim, CancellationToken ct = default)
    {
        CheckSkin(png);
        var form = new MultipartFormDataContent
        {
            { new StringContent(slim ? "slim" : "classic"), "variant" },
        };
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");
        return SendAsync(HttpMethod.Post, Profile + "/skins", token, form, ct);
    }

    /// <summary>Goes back to the default skin Minecraft assigns to the account.</summary>
    public Task<MinecraftProfileTextures> ResetSkinAsync(string token, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, Profile + "/skins/active", token, null, ct);

    /// <summary>Shows one of the account's own capes, or hides the cape when <paramref name="capeId"/> is null.</summary>
    public Task<MinecraftProfileTextures> SetCapeAsync(string token, string? capeId, CancellationToken ct = default) => capeId is null
        ? SendAsync(HttpMethod.Delete, Profile + "/capes/active", token, null, ct)
        : SendAsync(HttpMethod.Put, Profile + "/capes/active", token, JsonContent.Create(new { capeId }), ct);

    /// <summary>Downloads a skin or cape image. Only Mojang's texture server, always over HTTPS.</summary>
    public async Task<byte[]> DownloadTextureAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "textures.minecraft.net")
            throw new ArgumentException("Textures are only downloaded from textures.minecraft.net.");
        var https = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri; // the API hands out http:// links
        return await WithDeadline(ct, async token =>
        {
            using var response = await http.GetAsync(https, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Texture too large.");
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            return bytes.Length <= 1024 * 1024 ? bytes : throw new InvalidDataException("Texture too large.");
        });
    }

    /// <summary>Same rules Minecraft applies, checked before anything is sent: a PNG of 64×64 or 64×32.</summary>
    public static void CheckSkin(byte[] png)
    {
        if (png.Length > MaxSkinBytes) throw new ArgumentException("Skins must be PNG files under 256 KB.");
        if (PngSize(png) is not (64, 64) and not (64, 32))
            throw new ArgumentException("Skins must be PNG images of 64×64 pixels (or the older 64×32).");
    }

    /// <summary>Width and height from the PNG header, or null when the bytes are not a PNG.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)));
    }

    /// <summary>
    /// The shared HttpClient has no timeout (it also streams large game files), so each call gets 30 seconds,
    /// body included. A timeout is reported as such instead of looking like a silent cancel.
    /// </summary>
    private static async Task<T> WithDeadline<T>(CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await call(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Minecraft's servers didn't answer in time. Try again in a moment.");
        }
    }

    private Task<MinecraftProfileTextures> SendAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct) =>
        WithDeadline(ct, t => SendOnceAsync(method, url, token, content, t));

    private async Task<MinecraftProfileTextures> SendOnceAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthenticationException((int)response.StatusCode switch
            {
                400 => "Minecraft rejected the skin. Use a 64×64 PNG made for Java Edition.",
                401 => "Your Minecraft session expired. Sign in again on the Accounts page.",
                404 => "This account has no Minecraft profile.",
                429 => "Too many skin or cape changes. Please wait a minute.",
                var s => $"Minecraft profile request failed ({s}).",
            });
        return Parse(await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? new JsonObject());
    }

    internal static MinecraftProfileTextures Parse(JsonNode json)
    {
        static bool Active(JsonNode n) => string.Equals((string?)n["state"], "ACTIVE", StringComparison.OrdinalIgnoreCase);
        var skins = (json["skins"] as JsonArray ?? []).OfType<JsonNode>()
            .Select(s => new SkinTexture((string?)s["id"] ?? "", (string?)s["url"] ?? "",
                string.Equals((string?)s["variant"], "SLIM", StringComparison.OrdinalIgnoreCase), Active(s)))
            .Where(s => s.Url.Length > 0).ToList();
        var capes = (json["capes"] as JsonArray ?? []).OfType<JsonNode>()
            .Select(c => new CapeTexture((string?)c["id"] ?? "", (string?)c["url"] ?? "", (string?)c["alias"] ?? "Cape", Active(c)))
            .Where(c => c.Id.Length > 0 && c.Url.Length > 0).ToList();
        return new MinecraftProfileTextures((string?)json["name"] ?? "", skins, capes);
    }
}
