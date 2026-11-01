using Avalonia.Media.Imaging;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Controls;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.Auth;

namespace NinjaLauncher.Desktop.Services;

/// <summary>An account's current look: its profile textures and the decoded skin and capes.</summary>
/// <param name="SkinPng">The active skin's file, so the model (classic or slim) can be changed without picking it again.</param>
public sealed record PlayerLook(MinecraftProfileTextures Profile, Bitmap? Skin, byte[]? SkinPng, IReadOnlyDictionary<string, Bitmap> Capes)
{
    public bool Slim => Profile.ActiveSkin?.Slim ?? false;
    public Bitmap? ActiveCape => Profile.ActiveCape is { } c ? Capes.GetValueOrDefault(c.Id) : null;
}

/// <summary>Reads and changes the signed-in account's skin and cape; shared by Home and the Wardrobe.</summary>
public sealed class SkinService(AccountService accounts, MinecraftProfileClient client)
{
    private readonly Dictionary<string, PlayerLook> _looks = new();
    private readonly Dictionary<string, (byte[] Png, Bitmap Bitmap)> _textures = new(); // by URL: texture URLs change whenever the image does

    public event Action<string>? Changed; // profile id

    /// <summary>Null for offline profiles, which have no Minecraft profile to read.</summary>
    public async Task<PlayerLook?> GetAsync(MinecraftAccount account, bool refresh = false, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Offline) return null;
        lock (_looks)
            if (!refresh && _looks.TryGetValue(account.ProfileId, out var cached)) return cached;
        var session = await accounts.GetSessionAsync(account, ct);
        return await StoreAsync(account, await client.GetAsync(session.MinecraftAccessToken, ct), ct);
    }

    public async Task<PlayerLook> UploadSkinAsync(MinecraftAccount account, byte[] png, bool slim, CancellationToken ct = default)
    {
        var session = await accounts.GetSessionAsync(account, ct);
        return await StoreAsync(account, await client.UploadSkinAsync(session.MinecraftAccessToken, png, slim, ct), ct, notify: true);
    }

    public async Task<PlayerLook> ResetSkinAsync(MinecraftAccount account, CancellationToken ct = default)
    {
        var session = await accounts.GetSessionAsync(account, ct);
        return await StoreAsync(account, await client.ResetSkinAsync(session.MinecraftAccessToken, ct), ct, notify: true);
    }

    public async Task<PlayerLook> SetCapeAsync(MinecraftAccount account, string? capeId, CancellationToken ct = default)
    {
        var session = await accounts.GetSessionAsync(account, ct);
        return await StoreAsync(account, await client.SetCapeAsync(session.MinecraftAccessToken, capeId, ct), ct, notify: true);
    }

    /// <summary>Decodes a local PNG for previewing, after the same checks Minecraft applies.</summary>
    public static Bitmap DecodeSkin(byte[] png)
    {
        MinecraftProfileClient.CheckSkin(png);
        return Decode(png) ?? throw new ArgumentException("This file is not a readable PNG image.");
    }

    private async Task<PlayerLook> StoreAsync(MinecraftAccount account, MinecraftProfileTextures profile, CancellationToken ct, bool notify = false)
    {
        var skin = profile.ActiveSkin is { } s ? await TextureAsync(s.Url, ct) : null;
        var capes = new Dictionary<string, Bitmap>();
        foreach (var cape in profile.Capes)
            if (await TextureAsync(cape.Url, ct) is { } texture) capes[cape.Id] = texture.Bitmap;
        var look = new PlayerLook(profile, skin?.Bitmap, skin?.Png, capes);
        lock (_looks) _looks[account.ProfileId] = look;
        if (notify) Changed?.Invoke(account.ProfileId);
        return look;
    }

    private async Task<(byte[] Png, Bitmap Bitmap)?> TextureAsync(string url, CancellationToken ct)
    {
        lock (_textures)
            if (_textures.TryGetValue(url, out var hit)) return hit;
        var png = await client.DownloadTextureAsync(url, ct);
        if (Decode(png) is not { } bitmap) return null;
        lock (_textures) _textures[url] = (png, bitmap);
        return (png, bitmap);
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        if (!RemoteImage.IsSafeImage(bytes)) return null;
        try { return new Bitmap(new MemoryStream(bytes)); }
        catch (Exception) { return null; } // corrupt texture: the figure is just not drawn
    }
}
