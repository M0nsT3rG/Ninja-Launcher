using System.Text.RegularExpressions;
using NinjaLauncher.Application.Abstractions;

namespace NinjaLauncher.Platform;

/// <summary>Finds Java installations by reading each JDK/JRE's <c>release</c> file (no process spawning).</summary>
public sealed partial class JavaDiscovery(IAppPaths paths) : IJavaDiscovery
{
    public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var homes = new List<(string Home, string Source)>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) homes.Add((javaHome, "JAVA_HOME"));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.GetFileName(dir.TrimEnd('/', '\\')) == "bin") homes.Add((Path.GetDirectoryName(dir.TrimEnd('/', '\\'))!, "PATH"));

        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var sub in SafeDirs(root))
            {
                homes.Add((sub, "System"));
                var macHome = Path.Combine(sub, "Contents", "Home");
                if (Directory.Exists(macHome)) homes.Add((macHome, "System"));
            }
        }

        // Runtimes the launcher downloaded from Mojang (game/runtimes/<component>/<platform>/<component>).
        var managed = Path.Combine(paths.GameDirectory, "runtimes");
        if (Directory.Exists(managed))
            foreach (var home in Directory.EnumerateDirectories(managed, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                homes.Add((home, "Managed"));

        var result = new Dictionary<string, JavaInstallation>(StringComparer.OrdinalIgnoreCase);
        foreach (var (home, source) in homes)
        {
            ct.ThrowIfCancellationRequested();
            if (TryRead(home, source) is { } install) result.TryAdd(Path.GetFullPath(install.ExecutablePath), install);
        }
        return (IReadOnlyList<JavaInstallation>)result.Values.OrderByDescending(j => j.MajorVersion).ToList();
    }, ct);

    internal static JavaInstallation? TryRead(string home, string source)
    {
        var exe = FindExecutable(home);
        var release = Path.Combine(home, "release");
        if (exe is null || !File.Exists(release)) return null;
        var match = JavaVersionLine().Match(File.ReadAllText(release));
        if (!match.Success) return null;
        var version = match.Groups[1].Value;
        return new JavaInstallation(exe, ParseMajor(version), version, source);
    }

    internal static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], out var first)) return 0;
        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second) ? second : first;
    }

    private static string? FindExecutable(string home)
    {
        string[] names = OperatingSystem.IsWindows() ? ["javaw.exe", "java.exe"] : ["java"];
        return names.Select(n => Path.Combine(home, "bin", n)).FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return []; }
    }

    private static IEnumerable<string> CandidateRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto" })
                    yield return Path.Combine(pf, vendor);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Java/JavaVirtualMachines";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Java/JavaVirtualMachines");
        }
        else
        {
            yield return "/usr/lib/jvm";
            yield return "/usr/java";
            yield return "/opt/java";
        }
    }

    [GeneratedRegex("^JAVA_VERSION=\"([^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex JavaVersionLine();
}
