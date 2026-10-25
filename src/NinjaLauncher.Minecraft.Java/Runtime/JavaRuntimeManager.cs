using System.Text.Json;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Minecraft.Java.Metadata;

namespace NinjaLauncher.Minecraft.Java.Runtime;

/// <summary>
/// Installs Mojang-published Java runtimes (the same ones the official launcher uses) and
/// falls back to a matching system Java when Mojang has none for this platform.
/// </summary>
public sealed class JavaRuntimeManager(IDownloadService downloads, IAppPaths paths, IJavaDiscovery discovery, ILogger<JavaRuntimeManager> logger)
{
    public static readonly Uri RuntimeIndexUrl = new("https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json");

    private string RuntimesDirectory => Path.Combine(paths.GameDirectory, "runtimes");

    /// <summary>Returns a java executable suitable for <paramref name="requirement"/> (defaults to Java 8 for very old versions).</summary>
    public async Task<string> EnsureRuntimeAsync(JavaVersionRef? requirement, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var component = requirement?.Component ?? "jre-legacy";
        var major = requirement?.MajorVersion ?? 8;
        var platform = PlatformInfo.Current;
        var home = Path.Combine(RuntimesDirectory, component, platform.JavaRuntimeKey, component);
        var exe = JavaExecutable(home);
        var marker = Path.Combine(home, ".ninja-complete");
        if (File.Exists(marker) && File.Exists(exe)) return exe;

        var manifestUrl = await FindManifestUrlAsync(platform.JavaRuntimeKey, component, ct);
        if (manifestUrl is null)
        {
            var system = (await discovery.DiscoverAsync(ct)).FirstOrDefault(j => j.MajorVersion == major);
            if (system is not null)
            {
                logger.LogInformation("Mojang has no {Component} for {Platform}; using system Java {Path}", component, platform.JavaRuntimeKey, system.ExecutablePath);
                return system.ExecutablePath;
            }
            throw new JavaRuntimeUnavailableException(
                $"No Java {major} runtime is available from Mojang for {platform.JavaRuntimeKey}, and none was found on this computer. Install Java {major} and select it in the instance settings.");
        }

        await InstallFromManifestAsync(manifestUrl, home, progress, ct);
        if (!File.Exists(exe)) throw new FileNotFoundException("Runtime installed but java executable is missing.", exe);
        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), ct);
        return exe;
    }

    internal static string JavaExecutable(string home)
    {
        var p = PlatformInfo.Current;
        return p.IsMac ? Path.Combine(home, "jre.bundle", "Contents", "Home", "bin", "java") : Path.Combine(home, "bin", p.JavaExecutableName);
    }

    private async Task<Uri?> FindManifestUrlAsync(string platformKey, string component, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await downloads.GetStringAsync(RuntimeIndexUrl, ct));
        if (!doc.RootElement.TryGetProperty(platformKey, out var platform) || !platform.TryGetProperty(component, out var entries)) return null;
        foreach (var entry in entries.EnumerateArray())
            if (entry.TryGetProperty("manifest", out var m) && m.TryGetProperty("url", out var url))
                return new Uri(url.GetString()!);
        return null;
    }

    private async Task InstallFromManifestAsync(Uri manifestUrl, string home, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await downloads.GetStringAsync(manifestUrl, ct));
        var items = new List<DownloadItem>();
        var executables = new List<string>();
        var links = new List<(string Path, string Target)>();
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = SafePath.Combine(home, file.Name);
            var type = file.Value.GetProperty("type").GetString();
            if (type == "directory") Directory.CreateDirectory(path);
            else if (type == "link") links.Add((path, file.Value.GetProperty("target").GetString()!));
            else if (type == "file")
            {
                var raw = file.Value.GetProperty("downloads").GetProperty("raw");
                items.Add(new DownloadItem(new Uri(raw.GetProperty("url").GetString()!), path, raw.GetProperty("sha1").GetString(), raw.GetProperty("size").GetInt64()));
                if (file.Value.TryGetProperty("executable", out var x) && x.GetBoolean()) executables.Add(path);
            }
        }
        await downloads.DownloadAsync(items, progress, ct);

        if (OperatingSystem.IsWindows()) return;
        foreach (var exe in executables)
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        foreach (var (path, target) in links)
        {
            // Links must stay inside the runtime directory.
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
            if (!SafePath.IsUnder(home, resolved)) throw new UnsafePathException($"Runtime link escapes its directory: {target}");
            if (File.Exists(path) || Directory.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.CreateSymbolicLink(path, target);
        }
    }
}

public sealed class JavaRuntimeUnavailableException(string message) : Exception(message);
