using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Metadata;
using NinjaLauncher.Minecraft.Java.Runtime;

namespace NinjaLauncher.Mods.Loaders;

public sealed record LoaderVersion(string Version, bool Stable);

/// <summary>A mod loader integration. Installing produces a version JSON that inherits the vanilla version.</summary>
public interface ILoaderProvider
{
    ModLoaderKind Kind { get; }
    Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct);
    /// <returns>The version id to launch.</returns>
    Task<string> InstallAsync(string minecraftVersion, string loaderVersion, IProgress<InstallProgress>? progress, CancellationToken ct);
}

/// <summary>Fabric and Quilt publish ready-made inheriting profiles; installing is a metadata download (see MOD_LOADERS.md).</summary>
public sealed class MetaProfileLoaderProvider(ModLoaderKind kind, string metaBase, IDownloadService downloads, VersionCatalog catalog) : ILoaderProvider
{
    public static MetaProfileLoaderProvider Fabric(IDownloadService d, VersionCatalog c) => new(ModLoaderKind.Fabric, "https://meta.fabricmc.net/v2", d, c);
    public static MetaProfileLoaderProvider Quilt(IDownloadService d, VersionCatalog c) => new(ModLoaderKind.Quilt, "https://meta.quiltmc.org/v3", d, c);

    public ModLoaderKind Kind => kind;

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await downloads.GetStringAsync(new Uri($"{metaBase}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}"), ct));
        return doc.RootElement.EnumerateArray().Select(e =>
        {
            var loader = e.GetProperty("loader");
            var version = loader.GetProperty("version").GetString()!;
            var stable = loader.TryGetProperty("stable", out var s) ? s.GetBoolean() : !version.Contains('-');
            return new LoaderVersion(version, stable);
        }).ToList();
    }

    public async Task<string> InstallAsync(string minecraftVersion, string loaderVersion, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress($"Fetching {kind} {loaderVersion} profile", 0, 0));
        var json = await downloads.GetStringAsync(new Uri(
            $"{metaBase}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json"), ct);
        var profile = MetadataJson.Parse<VersionJson>(json);
        if (profile.InheritsFrom != minecraftVersion) throw new InvalidDataException($"{kind} profile does not target Minecraft {minecraftVersion}.");
        await catalog.SaveProfileAsync(profile.Id, json, ct);
        return profile.Id;
    }
}

/// <summary>
/// Forge and NeoForge patch the game with Java processors, so the official installer is run headless
/// (<c>--installClient</c>/<c>--install-client</c>) against the shared game directory. The installer jar is SHA-1 verified first.
/// </summary>
public sealed class InstallerLoaderProvider(
    ModLoaderKind kind, IDownloadService downloads, IAppPaths paths, VersionCatalog catalog, GameInstaller installer,
    JavaRuntimeManager runtimes, ILogger<InstallerLoaderProvider> logger) : ILoaderProvider
{
    public ModLoaderKind Kind => kind;

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct)
    {
        if (kind == ModLoaderKind.NeoForge)
        {
            var xml = XDocument.Parse(await downloads.GetStringAsync(new Uri("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"), ct));
            var prefix = NeoForgePrefix(minecraftVersion);
            if (prefix is null) return [];
            return xml.Descendants("version").Select(v => v.Value).Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .Reverse().Select(v => new LoaderVersion(v, !v.Contains("beta") && !v.Contains("alpha"))).ToList();
        }
        using var doc = JsonDocument.Parse(await downloads.GetStringAsync(new Uri("https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json"), ct));
        if (!doc.RootElement.TryGetProperty(minecraftVersion, out var list)) return [];
        if (ForgeUnsupported(minecraftVersion)) return [];
        return list.EnumerateArray().Select(v => v.GetString()!).Reverse().Select(v => new LoaderVersion(v, true)).ToList();
    }

    /// <summary>NeoForge 1.20.2+ uses "minor.patch." (21.1.x = 1.21.1); 26.x+ uses the full MC version plus a build number.</summary>
    internal static string? NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.');
        if (parts[0] == "1")
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out var minor) || minor < 20 || mc == "1.20.1" || mc == "1.20") return null;
            return $"{parts[1]}.{(parts.Length > 2 ? parts[2] : "0")}.";
        }
        if (!parts.All(p => int.TryParse(p, out _))) return null;
        return $"{parts[0]}.{(parts.Length > 1 ? parts[1] : "0")}.{(parts.Length > 2 ? parts[2] : "0")}.";
    }

    // Before 1.12.2 there is no headless client install (docs/research/MOD_LOADERS.md).
    internal static bool ForgeUnsupported(string mc)
    {
        var p = mc.Split('.');
        return p[0] == "1" && p.Length > 1 && int.TryParse(p[1], out var minor) && (minor < 12 || minor == 12 && mc != "1.12.2");
    }

    public async Task<string> InstallAsync(string minecraftVersion, string loaderVersion, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        // The installer needs the vanilla client present, and its Java version.
        var vanilla = await catalog.ResolveAsync(minecraftVersion, ct);
        await installer.InstallAsync(vanilla, Path.Combine(paths.CacheDirectory, "installer-gamedir"), progress, ct);
        var java = await runtimes.EnsureRuntimeAsync(vanilla.JavaVersion, null, ct);

        var (url, flag) = kind == ModLoaderKind.NeoForge
            ? ($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar", "--install-client")
            : ($"https://maven.minecraftforge.net/net/minecraftforge/forge/{loaderVersion}/forge-{loaderVersion}-installer.jar", "--installClient");
        var jar = Path.Combine(paths.CacheDirectory, "installers", Path.GetFileName(new Uri(url).AbsolutePath));
        progress?.Report(new InstallProgress($"Downloading {kind} installer", 0, 0));
        var sha1 = (await downloads.GetStringAsync(new Uri(url + ".sha1"), ct)).Trim().Split(' ')[0];
        await downloads.DownloadAsync([new DownloadItem(new Uri(url), jar, sha1, null)], null, ct);

        var root = paths.GameDirectory;
        var profiles = Path.Combine(root, "launcher_profiles.json");
        var createdStub = !File.Exists(profiles);
        if (createdStub) await File.WriteAllTextAsync(profiles, """{"profiles":{}}""", ct);
        var before = catalog.InstalledVersionIds().ToHashSet();
        try
        {
            progress?.Report(new InstallProgress($"Running {kind} installer (this can take a few minutes)", 50, 0));
            var psi = new ProcessStartInfo(java) { WorkingDirectory = Path.GetDirectoryName(jar)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-jar", jar, flag, root }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the installer.");
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogDebug("[{Kind} installer] {Line}", kind, e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogWarning("[{Kind} installer] {Line}", kind, e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                p.Kill(true);
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"The {kind} installer did not finish within 15 minutes.");
            }
            if (p.ExitCode != 0) throw new InvalidOperationException($"{kind} installer failed with exit code {p.ExitCode}. See the launcher log for details.");
        }
        finally
        {
            if (createdStub) File.Delete(profiles);
        }

        var created = catalog.InstalledVersionIds().Where(id => !before.Contains(id)).ToList();
        var id = created.FirstOrDefault() ?? GuessId(minecraftVersion, loaderVersion);
        if (!File.Exists(catalog.VersionJsonPath(id))) throw new InvalidDataException($"{kind} installer finished but no version profile was found.");
        return id;
    }

    private string GuessId(string mc, string loader) => kind == ModLoaderKind.NeoForge
        ? $"neoforge-{loader}"
        : $"{mc}-forge-{loader[(loader.IndexOf('-') + 1)..]}";
}
