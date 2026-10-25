using NinjaLauncher.Domain;

namespace NinjaLauncher.Application.Abstractions;

/// <summary>Standard per-OS directories. All paths are absolute.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string LogsDirectory { get; }
    /// <summary>Shared immutable game files: versions, libraries, assets, runtimes.</summary>
    string GameDirectory { get; }
    string InstancesDirectory { get; }
    string BackupsDirectory { get; }
    string DatabasePath { get; }
}

public interface IInstanceRepository
{
    Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken ct = default);
    Task<GameInstance?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpsertAsync(GameInstance instance, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IAccountRepository
{
    Task<IReadOnlyList<MinecraftAccount>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(MinecraftAccount account, CancellationToken ct = default);
    Task DeleteAsync(string profileId, CancellationToken ct = default);
    Task SetActiveAsync(string profileId, CancellationToken ct = default);
}

public interface ISettingsStore
{
    Task<LauncherSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(LauncherSettings settings, CancellationToken ct = default);
}

public interface IActivityLog
{
    Task AddAsync(string message, CancellationToken ct = default);
    Task<IReadOnlyList<ActivityEntry>> RecentAsync(int count, CancellationToken ct = default);
}

/// <summary>OS-backed secret storage. Implementations must never fall back to plaintext.</summary>
public interface ISecretStore
{
    /// <summary>False when no secure backend exists (e.g. Linux without a Secret Service).</summary>
    bool IsAvailable { get; }
    string BackendName { get; }
    Task SetAsync(string key, string secret, CancellationToken ct = default);
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public interface IUrlLauncher
{
    void Open(Uri uri);
    /// <summary>Shows an existing local directory in the OS file manager.</summary>
    void OpenDirectory(string path);
}

public sealed record DownloadItem(Uri Url, string DestinationPath, string? Sha1, long? Size);

public sealed record DownloadProgress(int CompletedFiles, int TotalFiles, long BytesDownloaded, long? TotalBytes, double BytesPerSecond);

public interface IDownloadService
{
    /// <summary>Downloads files that are missing or fail hash verification. Throws if any file ultimately fails.</summary>
    Task DownloadAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct);
    Task<string> GetStringAsync(Uri url, CancellationToken ct);
    /// <summary>POSTs a JSON body to an idempotent API endpoint and returns the response text.</summary>
    Task<string> PostJsonAsync(Uri url, string json, CancellationToken ct);
}
