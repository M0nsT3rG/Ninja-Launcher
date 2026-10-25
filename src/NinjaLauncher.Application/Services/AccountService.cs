using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Application.Services;

/// <summary>Result of a full Microsoft → Xbox → Minecraft sign-in. Tokens stay in memory except the refresh token.</summary>
/// <param name="Offline">True for an offline profile: no Microsoft session, placeholder token.</param>
public sealed record MinecraftSignIn(string ProfileId, string Username, string Xuid, string MinecraftAccessToken, DateTimeOffset ExpiresUtc,
    string MicrosoftRefreshToken, bool Offline = false);

public interface IMinecraftAuthenticator
{
    /// <summary>False when the build has no approved Azure application (client) ID configured.</summary>
    bool IsConfigured { get; }
    Task<MinecraftSignIn> SignInInteractiveAsync(CancellationToken ct);
    Task<MinecraftSignIn> RefreshAsync(string microsoftRefreshToken, CancellationToken ct);
}

public sealed class AuthenticationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Multi-account management on top of the authenticator and the OS secret store.</summary>
public sealed partial class AccountService(IAccountRepository accounts, ISecretStore secrets, IMinecraftAuthenticator authenticator,
    IActivityLog activity, ISettingsStore store)
{
    /// <summary>Set when a Microsoft account that owns Java Edition signs in; it unlocks offline profiles (as Prism Launcher does).</summary>
    internal const string OwnershipKey = "ownership-verified-utc";

    private readonly Dictionary<string, MinecraftSignIn> _sessions = new();

    public bool CanSignIn => authenticator.IsConfigured;
    public ISecretStore SecretStore => secrets;

    public Task<IReadOnlyList<MinecraftAccount>> ListAsync(CancellationToken ct = default) => accounts.ListAsync(ct);

    public async Task<MinecraftAccount?> GetActiveAsync(CancellationToken ct = default) => (await accounts.ListAsync(ct)).FirstOrDefault(a => a.IsActive);

    public async Task<MinecraftAccount> SignInAsync(CancellationToken ct = default)
    {
        var result = await authenticator.SignInInteractiveAsync(ct);
        var account = await StoreAsync(result, ct);
        await accounts.SetActiveAsync(account.ProfileId, ct);
        // Sign-in only succeeds when Minecraft Services returns a Java profile, i.e. the account owns the game.
        await store.SetValueAsync(OwnershipKey, DateTimeOffset.UtcNow.ToString("O"), ct);
        await activity.AddAsync($"Signed in as {account.Username}", ct);
        return account with { IsActive = true };
    }

    /// <summary>True once an account that owns Minecraft: Java Edition has signed in on this launcher.</summary>
    public async Task<bool> CanUseOfflineAsync(CancellationToken ct = default) => await store.GetValueAsync(OwnershipKey, ct) is not null;

    /// <summary>Adds a local offline profile for singleplayer, LAN and offline-mode servers. Requires verified ownership.</summary>
    public async Task<MinecraftAccount> AddOfflineAsync(string username, CancellationToken ct = default)
    {
        if (!await CanUseOfflineAsync(ct))
            throw new InvalidOperationException("Sign in once with a Microsoft account that owns Minecraft: Java Edition to unlock offline profiles.");
        username = username.Trim();
        if (!ValidName().IsMatch(username))
            throw new ArgumentException("Offline names must be 3–16 characters: letters, digits or underscore.");
        var account = new MinecraftAccount { Kind = AccountKind.Offline, ProfileId = OfflineUuid(username), Username = username };
        if ((await accounts.ListAsync(ct)).Any(a => a.ProfileId == account.ProfileId))
            throw new InvalidOperationException($"An offline profile named {username} already exists.");
        await accounts.UpsertAsync(account, ct);
        await accounts.SetActiveAsync(account.ProfileId, ct);
        await activity.AddAsync($"Added offline profile {username}", ct);
        return account with { IsActive = true };
    }

    /// <summary>The UUID a vanilla server assigns offline players: name-based UUIDv3 of "OfflinePlayer:&lt;name&gt;", no dashes.</summary>
    public static string OfflineUuid(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username)); // MD5 mandated by UUIDv3, not used for security
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task SetActiveAsync(string profileId, CancellationToken ct = default) => accounts.SetActiveAsync(profileId, ct);

    public async Task RemoveAsync(string profileId, CancellationToken ct = default)
    {
        lock (_sessions) _sessions.Remove(profileId);
        await secrets.DeleteAsync(SecretKey(profileId), ct);
        await accounts.DeleteAsync(profileId, ct);
        await activity.AddAsync("Removed an account", ct);
    }

    /// <summary>Returns a valid Minecraft token for the account, refreshing through Microsoft when needed.</summary>
    public async Task<MinecraftSignIn> GetSessionAsync(MinecraftAccount account, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Offline)
        {
            if (!await CanUseOfflineAsync(ct)) throw new AuthenticationException("Offline profiles need a verified Microsoft account first.");
            return new MinecraftSignIn(account.ProfileId, account.Username, "0", "0", DateTimeOffset.MaxValue, "", Offline: true);
        }

        // Single-flight: refresh tokens rotate, so two concurrent refreshes would invalidate each other.
        await _refreshLock.WaitAsync(ct);
        try
        {
            lock (_sessions)
                if (_sessions.TryGetValue(account.ProfileId, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5))
                    return cached;
            var refresh = await secrets.GetAsync(SecretKey(account.ProfileId), ct)
                ?? throw new AuthenticationException($"The session for {account.Username} has expired. Please sign in again.");
            var result = await authenticator.RefreshAsync(refresh, ct);
            if (!string.Equals(result.ProfileId, account.ProfileId, StringComparison.OrdinalIgnoreCase))
                throw new AuthenticationException("The refreshed Microsoft account belongs to a different Minecraft profile.");
            await StoreAsync(result, ct);
            return result;
        }
        finally { _refreshLock.Release(); }
    }

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private async Task<MinecraftAccount> StoreAsync(MinecraftSignIn result, CancellationToken ct)
    {
        await secrets.SetAsync(SecretKey(result.ProfileId), result.MicrosoftRefreshToken, ct); // refresh tokens rotate; keep the newest
        lock (_sessions) _sessions[result.ProfileId] = result;
        var account = new MinecraftAccount { ProfileId = result.ProfileId, Username = result.Username, Xuid = result.Xuid };
        await accounts.UpsertAsync(account, ct);
        return account;
    }

    private static string SecretKey(string profileId) => $"msa-refresh:{profileId}";

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex ValidName();
}
