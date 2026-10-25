using System.Runtime.InteropServices;

namespace NinjaLauncher.Application.Abstractions;

/// <summary>Current OS/architecture expressed in the vocabularies Mojang metadata uses.</summary>
public sealed record PlatformInfo(string MojangOsName, string MojangArch, string JavaRuntimeKey, string ClasspathSeparator)
{
    public static PlatformInfo Current { get; } = Detect();

    public bool IsWindows => MojangOsName == "windows";
    public bool IsMac => MojangOsName == "osx";
    public bool IsLinux => MojangOsName == "linux";
    public string JavaExecutableName => IsWindows ? "javaw.exe" : "java";

    private static PlatformInfo Detect()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
            return new("windows", ArchName(arch), arch == Architecture.Arm64 ? "windows-arm64" : arch == Architecture.X86 ? "windows-x86" : "windows-x64", ";");
        if (OperatingSystem.IsMacOS())
            return new("osx", ArchName(arch), arch == Architecture.Arm64 ? "mac-os-arm64" : "mac-os", ":");
        // Mojang publishes no Linux ARM runtimes; these keys are absent from all.json so a system Java is used instead.
        return new("linux", ArchName(arch), arch switch
        {
            Architecture.X86 => "linux-i386",
            Architecture.Arm64 => "linux-arm64",
            Architecture.Arm => "linux-arm32",
            _ => "linux",
        }, ":");
    }

    private static string ArchName(Architecture a) => a switch
    {
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm32",
        _ => "x86_64",
    };

    /// <summary>OS version string compared against "os.version" regexes in rules.</summary>
    public static string OsVersion => Environment.OSVersion.Version.ToString();
}

public sealed record JavaInstallation(string ExecutablePath, int MajorVersion, string Version, string Source);

public interface IJavaDiscovery
{
    Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(CancellationToken ct = default);
}
