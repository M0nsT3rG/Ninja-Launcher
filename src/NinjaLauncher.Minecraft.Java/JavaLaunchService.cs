using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Launch;
using NinjaLauncher.Minecraft.Java.Metadata;
using NinjaLauncher.Minecraft.Java.Runtime;

namespace NinjaLauncher.Minecraft.Java;

/// <summary>Install-then-launch pipeline for one Java Edition instance.</summary>
public sealed class JavaLaunchService(VersionCatalog catalog, GameInstaller installer, JavaRuntimeManager runtimes, ILogger<JavaLaunchService> logger)
{
    public static string EffectiveVersionId(GameInstance instance) => instance.LaunchVersionId ?? instance.MinecraftVersion;

    /// <summary>Downloads/verifies everything the instance needs. Safe to call repeatedly (repair = same call with full verification).</summary>
    public async Task<(InstalledVersion Installed, string Java)> PrepareAsync(GameInstance instance, string gameDirectory,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Resolving version metadata", 0, 0));
        var version = await catalog.ResolveAsync(EffectiveVersionId(instance), ct);
        var installed = await installer.InstallAsync(version, gameDirectory, progress, ct);

        string java;
        if (!string.IsNullOrWhiteSpace(instance.JavaPath)) java = instance.JavaPath;
        else
        {
            progress?.Report(new InstallProgress($"Preparing Java {version.JavaVersion?.MajorVersion ?? 8}", 0, 0));
            java = await runtimes.EnsureRuntimeAsync(version.JavaVersion,
                new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress($"Downloading Java runtime ({p.CompletedFiles}/{p.TotalFiles})",
                    100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles), p.BytesPerSecond))), ct);
        }
        return (installed, java);
    }

    public async Task<int> LaunchAsync(GameInstance instance, string gameDirectory, LaunchSession session,
        IProgress<InstallProgress>? progress, Action<GameLogLine> onLog, CancellationToken ct)
    {
        var (installed, java) = await PrepareAsync(instance, gameDirectory, progress, ct);
        Directory.CreateDirectory(gameDirectory);
        var args = LaunchArgumentBuilder.Build(installed, session,
            new LaunchOptions(gameDirectory, instance.MinMemoryMb, instance.MaxMemoryMb, instance.ExtraJvmArguments));
        logger.LogInformation("Launching {Version}: {Command}", installed.Version.Id, GameProcess.DescribeCommand(java, args));
        progress?.Report(new InstallProgress("Game running", 100, 0));
        var exit = await GameProcess.RunAsync(java, args, gameDirectory, onLog, pid => logger.LogInformation("Game process {Pid} started", pid), ct);
        logger.LogInformation("Game exited with status {Exit}", exit);
        return exit;
    }
}
