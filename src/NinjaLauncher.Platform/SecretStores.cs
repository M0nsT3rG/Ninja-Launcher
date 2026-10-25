using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NinjaLauncher.Application.Abstractions;

namespace NinjaLauncher.Platform;

public static class SecretStoreFactory
{
    /// <summary>Picks the OS-backed store. Never returns a plaintext store; falls back to memory-only.</summary>
    public static ISecretStore Create(IAppPaths paths)
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecretStore(Path.Combine(paths.DataDirectory, "secrets"));
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/security")) return new MacKeychainSecretStore();
        if (OperatingSystem.IsLinux() && LinuxSecretToolStore.IsSupported()) return new LinuxSecretToolStore();
        return new SessionOnlySecretStore();
    }

    internal static string KeyFileName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
}

/// <summary>Windows: DPAPI (CurrentUser scope) encrypted blobs, one file per key.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = "NinjaLauncher.Secrets.v1"u8.ToArray();

    public bool IsAvailable => true;
    public string BackendName => "Windows DPAPI";

    public async Task SetAsync(string key, string secret, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(key);
        await File.WriteAllBytesAsync(path + ".tmp", blob, ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, ct), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // blob from another user/machine: treat as signed out
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(string key) => Path.Combine(directory, SecretStoreFactory.KeyFileName(key) + ".bin");
}

/// <summary>
/// macOS: login Keychain through /usr/bin/security. The secret is piped to <c>security -i</c> as hex (-X)
/// so it never appears in the process list. Limitation: the item trusts /usr/bin/security (see AUTHENTICATION.md).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore : ISecretStore
{
    private const string Service = "NinjaLauncher";
    public bool IsAvailable => true;
    public string BackendName => "macOS Keychain";

    public async Task SetAsync(string key, string secret, CancellationToken ct = default)
    {
        if (key.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'')) throw new ArgumentException("Invalid secret key.", nameof(key));
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(secret));
        if (hex.Length > 3900) throw new InvalidOperationException("Secret too large for the keychain command interface.");
        var r = await CliRunner.RunAsync("/usr/bin/security", ["-i"], $"add-generic-password -U -a {key} -s {Service} -X {hex}\n", ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Keychain refused to store the secret.");
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var r = await CliRunner.RunAsync("/usr/bin/security", ["find-generic-password", "-a", key, "-s", Service, "-w"], null, ct);
        return r.ExitCode == 0 ? r.StdOut.TrimEnd('\n') : null;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await CliRunner.RunAsync("/usr/bin/security", ["delete-generic-password", "-a", key, "-s", Service], null, ct);
}

/// <summary>Linux: freedesktop Secret Service via libsecret's secret-tool. The secret is passed on stdin.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretToolStore : ISecretStore
{
    private const string Tool = "secret-tool";
    public bool IsAvailable => true;
    public string BackendName => "Secret Service (libsecret)";

    public static bool IsSupported() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) &&
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, Tool)));

    public async Task SetAsync(string key, string secret, CancellationToken ct = default)
    {
        var r = await CliRunner.RunAsync(Tool, ["store", "--label=Ninja Launcher", "service", "NinjaLauncher", "account", key], secret, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Secret Service refused to store the secret (is a keyring unlocked?).");
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var r = await CliRunner.RunAsync(Tool, ["lookup", "service", "NinjaLauncher", "account", key], null, ct);
        return r.ExitCode == 0 && r.StdOut.Length > 0 ? r.StdOut.TrimEnd('\n') : null;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await CliRunner.RunAsync(Tool, ["clear", "service", "NinjaLauncher", "account", key], null, ct);
}

/// <summary>No secure backend: secrets live in memory for this session only and are never written to disk.</summary>
public sealed class SessionOnlySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new();
    public bool IsAvailable => false;
    public string BackendName => "Session only (no secure storage available)";

    public Task SetAsync(string key, string secret, CancellationToken ct = default)
    {
        lock (_secrets) _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_secrets) return Task.FromResult(_secrets.GetValueOrDefault(key));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_secrets) _secrets.Remove(key);
        return Task.CompletedTask;
    }
}

internal static class CliRunner
{
    public sealed record Result(int ExitCode, string StdOut);

    public static async Task<Result> RunAsync(string file, IEnumerable<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw; }
        return new Result(p.ExitCode, await stdout);
    }
}
