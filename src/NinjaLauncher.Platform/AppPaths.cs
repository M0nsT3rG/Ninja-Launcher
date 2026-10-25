using NinjaLauncher.Application.Abstractions;

namespace NinjaLauncher.Platform;

/// <summary>
/// Per-OS standard locations. <c>NINJA_LAUNCHER_HOME</c> overrides everything (portable mode and tests).
/// Windows: %APPDATA%\NinjaLauncher. macOS: ~/Library/Application Support/NinjaLauncher. Linux: $XDG_DATA_HOME/NinjaLauncher.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string AppFolderName = "NinjaLauncher";

    public AppPaths(string? rootOverride = null, string? customInstancesDirectory = null)
    {
        rootOverride ??= Environment.GetEnvironmentVariable("NINJA_LAUNCHER_HOME");
        DataDirectory = rootOverride ?? DefaultDataDirectory();
        CacheDirectory = rootOverride is null ? DefaultCacheDirectory() : Path.Combine(rootOverride, "cache");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        GameDirectory = Path.Combine(DataDirectory, "game");
        InstancesDirectory = string.IsNullOrWhiteSpace(customInstancesDirectory) ? Path.Combine(DataDirectory, "instances") : Path.GetFullPath(customInstancesDirectory);
        BackupsDirectory = Path.Combine(DataDirectory, "backups");
        DatabasePath = Path.Combine(DataDirectory, "ninja.db");
        foreach (var d in new[] { DataDirectory, CacheDirectory, LogsDirectory, GameDirectory, InstancesDirectory, BackupsDirectory })
            Directory.CreateDirectory(d);
    }

    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string LogsDirectory { get; }
    public string GameDirectory { get; }
    public string InstancesDirectory { get; }
    public string BackupsDirectory { get; }
    public string DatabasePath { get; }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library", "Application Support", AppFolderName);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".local", "share") : xdg, AppFolderName);
    }

    private static string DefaultCacheDirectory()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "Cache");
        if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library", "Caches", AppFolderName);
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".cache") : xdg, AppFolderName);
    }
}
