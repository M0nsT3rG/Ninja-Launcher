using System.Text.RegularExpressions;
using NinjaLauncher.Application.Abstractions;

namespace NinjaLauncher.Minecraft.Java.Metadata;

/// <summary>Evaluates Mojang allow/disallow rules: no rules = allowed; otherwise the last matching rule wins, default disallow.</summary>
public sealed class RuleEvaluator(PlatformInfo platform, string osVersion, IReadOnlyDictionary<string, bool>? features = null)
{
    public static RuleEvaluator Current { get; } = new(PlatformInfo.Current, PlatformInfo.OsVersion);

    public PlatformInfo Platform => platform;

    public bool IsAllowed(IReadOnlyList<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
            if (Matches(rule)) allowed = rule.Action == "allow";
        return allowed;
    }

    private bool Matches(Rule rule)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && os.Name != platform.MojangOsName) return false;
            if (os.Arch is not null && os.Arch != platform.MojangArch) return false;
            if (os.Version is not null && !SafeRegexMatch(os.Version, osVersion)) return false;
            if (os.VersionRange is { } range && !InRange(range, osVersion)) return false;
        }
        if (rule.Features is not null)
            foreach (var (name, value) in rule.Features)
                if ((features?.GetValueOrDefault(name) ?? false) != value) return false;
        return true;
    }

    // shortcut: min inclusive / max exclusive is unverified against the official launcher (docs/research/JAVA_ENGINE.md J-04).
    private static bool InRange(VersionRange range, string osVersion)
    {
        if (!Version.TryParse(osVersion, out var current)) return false;
        if (range.Min is not null && (!Version.TryParse(range.Min, out var min) || current < min)) return false;
        if (range.Max is not null && (!Version.TryParse(range.Max, out var max) || current >= max)) return false;
        return true;
    }

    private static bool SafeRegexMatch(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return false; }
    }
}
