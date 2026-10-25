using System.Reflection;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Infrastructure.Updates;
using Velopack;

namespace NinjaLauncher.Desktop.Services;

/// <summary>
/// Opt-in self-update through Velopack with a signed feed. Never installs without the user clicking
/// "Install and restart". Disabled in development runs and in builds without an embedded update public key.
/// </summary>
public sealed class UpdateService
{
    public const string FeedBaseUrl = "https://github.com/M0nsT3rG/Ninja-Launcher/releases/latest/download";
    private const string PublicKeyResource = "NinjaLauncher.UpdateSigningKey.pem";

    private readonly UpdateManager? _manager;
    private readonly ILogger<UpdateService> _logger;
    private UpdateInfo? _pending;

    public UpdateService(HttpClient http, ILogger<UpdateService> logger)
    {
        _logger = logger;
        var key = ReadPublicKey();
        if (key is null)
        {
            UnavailableReason = "This build has no update signing key, so automatic updates are disabled. Download new versions from the project's releases page.";
            return;
        }
        // No explicit channel: Velopack uses the one baked into the installed package by `vpk pack --channel`,
        // which stays correct under emulation (x64 on ARM) where the OS architecture would not.
        var manager = new UpdateManager(new SignedFeedSource(FeedBaseUrl, key, http));
        if (!manager.IsInstalled)
        {
            UnavailableReason = "Updates only work in an installed copy of Ninja Launcher (not in development or portable runs).";
            return;
        }
        _manager = manager;
    }

    public bool IsAvailable => _manager is not null;
    public string? UnavailableReason { get; }
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    public async Task<string?> CheckAsync()
    {
        if (_manager is null) return null;
        _pending = await _manager.CheckForUpdatesAsync();
        _logger.LogInformation("Update check: {Result}", PendingVersion ?? "up to date");
        return PendingVersion;
    }

    /// <summary>Background startup check: failures (offline, private repo) are logged, never shown as errors.</summary>
    public async Task<string?> CheckQuietlyAsync()
    {
        try { return await CheckAsync(); }
        catch (Exception e)
        {
            _logger.LogWarning("Startup update check failed: {Message}", e.Message);
            return null;
        }
    }

    public async Task DownloadAndRestartAsync(Action<int> progress)
    {
        if (_manager is null) throw new InvalidOperationException("Updates are not available.");
        // Re-check right before downloading: "latest" may have moved since the earlier check.
        _pending = await _manager.CheckForUpdatesAsync() ?? throw new InvalidOperationException("No update is available any more.");
        await _manager.DownloadUpdatesAsync(_pending, progress);
        _manager.ApplyUpdatesAndRestart(_pending);
    }

    private static string? ReadPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PublicKeyResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
