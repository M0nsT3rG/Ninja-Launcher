using System.Text.RegularExpressions;
using NinjaLauncher.Application.Security;

namespace NinjaLauncher.Minecraft.Java.Diagnostics;

public sealed record CrashFile(string Path, string Name, DateTime WrittenUtc, bool IsJvmCrash);

/// <param name="Title">What crashed, in plain words.</param>
/// <param name="Exception">The top-level exception line (or the JVM error signal).</param>
/// <param name="RootCause">The deepest "Caused by" line, or the problematic native frame.</param>
/// <param name="Hint">A plain-language suggestion for well-known causes; null when nothing specific applies.</param>
public sealed record CrashSummary(string Title, string? Exception, string? RootCause, IReadOnlyList<string> SuspectedMods, string? Hint);

/// <summary>Finds and summarises Minecraft crash reports (<c>crash-reports/*.txt</c>) and JVM fatal error logs (<c>hs_err_pid*.log</c>).</summary>
public static partial class CrashReports
{
    public const int MaxReadBytes = 512 * 1024;

    public static IReadOnlyList<CrashFile> List(string gameDirectory)
    {
        var files = new List<CrashFile>();
        var reports = Path.Combine(gameDirectory, "crash-reports");
        if (Directory.Exists(reports))
            files.AddRange(Directory.GetFiles(reports, "*.txt").Select(f => new CrashFile(f, Path.GetFileName(f), File.GetLastWriteTimeUtc(f), false)));
        if (Directory.Exists(gameDirectory))
            files.AddRange(Directory.GetFiles(gameDirectory, "hs_err_pid*.log").Select(f => new CrashFile(f, Path.GetFileName(f), File.GetLastWriteTimeUtc(f), true)));
        return files.OrderByDescending(f => f.WrittenUtc).ToList();
    }

    /// <summary>Reads at most <see cref="MaxReadBytes"/> and redacts anything that looks like a token.</summary>
    public static string ReadText(CrashFile file)
    {
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[Math.Min(stream.Length, MaxReadBytes)];
        stream.ReadExactly(buffer);
        var text = System.Text.Encoding.UTF8.GetString(buffer);
        return SecretRedactor.Redact(stream.Length > MaxReadBytes ? text + "\n\n[… truncated …]" : text);
    }

    public static CrashSummary Summarize(string text, bool isJvmCrash) => isJvmCrash ? SummarizeJvm(text) : SummarizeMinecraft(text);

    internal static CrashSummary SummarizeMinecraft(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var description = lines.Select(l => DescriptionLine().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();
        var descIndex = lines.FindIndex(l => l.StartsWith("Description:", StringComparison.Ordinal));
        // The exception is the first non-blank line after "Description:" (a message after ':' is optional).
        var exception = descIndex < 0 ? null : lines.Skip(descIndex + 1).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).FirstOrDefault();
        if (exception is not null && (exception.StartsWith("A detailed walkthrough", StringComparison.Ordinal) || exception.StartsWith("--", StringComparison.Ordinal)))
            exception = null;
        var rootCause = lines.LastOrDefault(l => l.StartsWith("Caused by: ", StringComparison.Ordinal))?["Caused by: ".Length..].Trim();

        var suspects = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = SuspectHeader().Match(lines[i]);
            if (!m.Success) continue;
            var inline = m.Groups[1].Value.Trim();
            if (inline.Length > 0 && !inline.Equals("NONE", StringComparison.OrdinalIgnoreCase)) suspects.Add(inline);
            for (var j = i + 1; j < lines.Count && lines[j].StartsWith('\t'); j++) suspects.Add(lines[j].Trim());
        }
        suspects.AddRange(MixinMod().Matches(text).Select(m => m.Groups[1].Value));

        var title = description is null or "" ? "Minecraft crashed" : $"Minecraft crashed: {description}";
        return new CrashSummary(title, exception, rootCause, suspects.Distinct().Take(10).ToList(), HintFor(text, rootCause ?? exception));
    }

    internal static CrashSummary SummarizeJvm(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var signal = lines.SkipWhile(l => !l.Contains("A fatal error has been detected")).Skip(2).FirstOrDefault(l => l.StartsWith("#  ", StringComparison.Ordinal))?.TrimStart('#', ' ');
        var frameIndex = lines.FindIndex(l => l.StartsWith("# Problematic frame:", StringComparison.Ordinal));
        var frame = frameIndex >= 0 && frameIndex + 1 < lines.Count ? lines[frameIndex + 1].TrimStart('#', ' ') : null;
        return new CrashSummary("The Java runtime crashed (native crash)", signal, frame, [], HintFor(text, frame));
    }

    private static string? HintFor(string text, string? cause)
    {
        var c = cause ?? "";
        if (GraphicsDriver().IsMatch(c) || text.Contains("Pixel format not accelerated") || text.Contains("GLFW error 65542"))
            return "This looks like a graphics driver problem. Update your GPU driver from AMD, NVIDIA or Intel, and turn off shader packs to test.";
        if (text.Contains("java.lang.OutOfMemoryError"))
            return "Minecraft ran out of memory. Raise the instance's maximum memory in the Library, or remove heavy mods.";
        if (text.Contains("UnsupportedClassVersionError"))
            return "A mod or library needs a newer Java. Clear the instance's custom Java path so the launcher picks the right runtime.";
        if (text.Contains("Mixin apply") || text.Contains("MixinApplyError") || text.Contains("InvalidInjectionException"))
            return "A mod failed to apply its changes (mixin). Usually one of the suspected mods is outdated or incompatible: update or disable it.";
        if (text.Contains("ModResolutionException") || text.Contains("requires") && text.Contains("which is missing"))
            return "A mod dependency is missing or the wrong version. Run Check compatibility in the Library.";
        return null;
    }

    [GeneratedRegex(@"^Description:\s*(.*)$")]
    private static partial Regex DescriptionLine();

    [GeneratedRegex(@"^\s*Suspected Mods?(?:\(s\))?:\s*(.*)$")]
    private static partial Regex SuspectHeader();

    [GeneratedRegex(@"Mixin apply (?:for|failed for) mod ([A-Za-z0-9_\-]+)")]
    private static partial Regex MixinMod();

    [GeneratedRegex(@"(?i)(atio6axx|atioglxx|amdxx|nvoglv|nvwgf2um|ig[0-9a-z]*icd|libnvidia|radeonsi|iris_dri)")]
    private static partial Regex GraphicsDriver();
}
