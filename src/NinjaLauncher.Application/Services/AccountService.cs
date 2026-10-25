using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Application.Services;

/// <summary>Result of a full Microsoft → Xbox → Minecraft sign-in. Tokens stay in memory except the refresh token.</summary>
public sealed record MinecraftSignIn(string ProfileId, string Username, string Xuid, string MinecraftAccessToken, DateTimeOffset ExpiresUtc, string MicrosoftRefreshToken);

public interface IMinecraftAuthenticator
{
    /// <summary>False when the build has no approved Azure application (client) ID configured.</summary>
    bool IsConfigured { get; }
    Task<MinecraftSignIn> SignInInteractiveAsync(CancellationToken ct);
    Task<MinecraftSignIn> RefreshAsync(string microsoftRefreshToken, CancellationToken ct);
}

public sealed class AuthenticationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Multi-account management on top of the authenticator and the OS secret store.</summary>
public sealed class AccountService(IAccountRepository accounts, ISecretStore secrets, IMinecraftAuthenticator authenticator, IActivityLog activity)
{
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
        await activity.AddAsync($"Signed in as {account.Username}", ct);
        return account with { IsActive = true };
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
}
