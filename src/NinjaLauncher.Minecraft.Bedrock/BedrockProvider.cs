using System.Diagnostics;
using System.Text.Json;

namespace NinjaLauncher.Minecraft.Bedrock;

public enum BedrockSupport { Supported, NotInstalled, Unsupported }

/// <param name="LaunchTarget">Windows: AppUserModelID for shell:AppsFolder. Linux/macOS: path of an externally installed mcpelauncher.</param>
public sealed record BedrockStatus(BedrockSupport Support, string Message, string? Version = null, string? LaunchTarget = null, string? DataDirectory = null);

/// <summary>
/// Capability-driven Bedrock integration. It only detects and launches a legitimately installed game through
/// OS mechanisms; it never reads, unpacks or modifies the protected package (docs/research/BEDROCK_FEASIBILITY.md).
/// </summary>
public sealed class BedrockProvider
{
    public const string PackageName = "Microsoft.MinecraftUWP";

    public async Task<BedrockStatus> DetectAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return await DetectWindowsAsync(ct);
        var launcher = FindOnPath("mcpelauncher-client") ?? FindOnPath("mcpelauncher-ui-qt");
        return launcher is null
            ? new BedrockStatus(BedrockSupport.Unsupported,
                "Bedrock Edition has no official release for this OS. The community project mcpelauncher (requires owning the Android version on Google Play) can be installed separately; Ninja Launcher will then launch it. It is unofficial and not bundled.")
            : new BedrockStatus(BedrockSupport.Supported, "Unofficial mcpelauncher found (community project, not affiliated with Mojang).", null, launcher);
    }

    public void Launch(BedrockStatus status)
    {
        if (status.Support != BedrockSupport.Supported || status.LaunchTarget is null) throw new InvalidOperationException(status.Message);
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"shell:AppsFolder\\{status.LaunchTarget}" }, UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(status.LaunchTarget) { UseShellExecute = false });
    }

    /// <summary>Store product id of "Minecraft for Windows".</summary>
    public const string StoreProductId = "9NBLGGH2JHXJ";

    /// <summary>The game's page in the Microsoft Store app, where the user installs it with their own licence.</summary>
    public static readonly Uri StoreAppPage = new($"ms-windows-store://pdp/?productid={StoreProductId}");

    /// <summary>
    /// Windows only. Opens the Store app on the game's page (winget's Store catalogue does not list Minecraft for
    /// Windows, so this is the shortest legitimate path). Returns false if the Store app could not be opened.
    /// </summary>
    public bool OpenStoreApp()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { Process.Start(new ProcessStartInfo(StoreAppPage.AbsoluteUri) { UseShellExecute = true })?.Dispose(); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; } // Store app removed or blocked by policy
    }

    private static async Task<BedrockStatus> DetectWindowsAsync(CancellationToken ct)
    {
        // Read-only queries; the GDK build keeps the package identity but may use app id "Game" instead of "App".
        const string script = "$p = Get-AppxPackage -Name " + PackageName + " | Select-Object -First 1; " +
            "$a = Get-StartApps | Where-Object { $_.AppID -like '" + PackageName + "_*' } | Select-Object -First 1; " +
            "[pscustomobject]@{ Version = \"$($p.Version)\"; AppId = \"$($a.AppID)\" } | ConvertTo-Json -Compress";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
            var version = doc.RootElement.TryGetProperty("Version", out var v) ? v.GetString() : null;
            var appId = doc.RootElement.TryGetProperty("AppId", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(version))
                return new BedrockStatus(BedrockSupport.NotInstalled, "Minecraft for Windows (Bedrock) is not installed. Get it from the Microsoft Store or the Xbox app.");
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minecraft Bedrock");
            return string.IsNullOrEmpty(appId)
                ? new BedrockStatus(BedrockSupport.NotInstalled, "Bedrock is installed but no launchable entry was found in the Start menu.", version)
                : new BedrockStatus(BedrockSupport.Supported, "Minecraft for Windows is installed.", version, appId, Directory.Exists(data) ? data : null);
        }
        catch (Exception e) when (e is JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new BedrockStatus(BedrockSupport.NotInstalled, $"Could not query installed apps: {e.Message}");
        }
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
