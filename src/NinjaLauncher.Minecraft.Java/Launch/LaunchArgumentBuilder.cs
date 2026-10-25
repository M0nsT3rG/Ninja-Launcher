using System.Text.Json;
using System.Text.RegularExpressions;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Metadata;

namespace NinjaLauncher.Minecraft.Java.Launch;

/// <summary>Authenticated identity passed to the game. The access token never leaves memory except as a process argument.</summary>
public sealed record LaunchSession(string Username, string Uuid, string AccessToken, string Xuid, string UserType = "msa");

public sealed record LaunchOptions(
    string GameDirectory,
    int MinMemoryMb,
    int MaxMemoryMb,
    string UserJvmArguments,
    int? WindowWidth = null,
    int? WindowHeight = null,
    string ClientId = "");

/// <summary>Builds the JVM + game argument list from version metadata. The result is passed as an argv array, never through a shell.</summary>
public static partial class LaunchArgumentBuilder
{
    public const string LauncherName = "NinjaLauncher";

    public static IReadOnlyList<string> Build(InstalledVersion installed, LaunchSession session, LaunchOptions options, PlatformInfo? platform = null, string? osVersion = null)
    {
        platform ??= PlatformInfo.Current;
        var v = installed.Version;
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = options.WindowWidth is not null && options.WindowHeight is not null };
        var rules = new RuleEvaluator(platform, osVersion ?? PlatformInfo.OsVersion, features);

        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = session.Username,
            ["version_name"] = v.Id,
            ["game_directory"] = options.GameDirectory,
            ["assets_root"] = installed.AssetsRoot,
            ["game_assets"] = installed.GameAssetsDirectory,
            ["assets_index_name"] = installed.AssetIndexId,
            ["auth_uuid"] = session.Uuid,
            ["auth_access_token"] = session.AccessToken,
            ["auth_session"] = $"token:{session.AccessToken}:{session.Uuid}",
            ["auth_xuid"] = session.Xuid,
            ["clientid"] = options.ClientId,
            ["user_type"] = session.UserType,
            ["user_properties"] = "{}",
            ["version_type"] = v.Type ?? "release",
            ["natives_directory"] = installed.NativesDirectory,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = typeof(LaunchArgumentBuilder).Assembly.GetName().Version?.ToString(3) ?? "0",
            ["classpath"] = string.Join(platform.ClasspathSeparator, installed.Classpath),
            ["classpath_separator"] = platform.ClasspathSeparator,
            ["library_directory"] = installed.LibrariesDirectory,
            ["resolution_width"] = options.WindowWidth?.ToString() ?? "",
            ["resolution_height"] = options.WindowHeight?.ToString() ?? "",
        };

        var args = new List<string> { $"-Xms{options.MinMemoryMb}M", $"-Xmx{options.MaxMemoryMb}M" };

        // A legacy (minecraftArguments) base needs the synthesized classpath even if a child profile adds its own JVM args.
        if (v.MinecraftArguments is not null || v.Arguments?.Jvm is not { Count: > 0 }) args.AddRange(LegacyJvmArguments(platform));
        if (v.Arguments?.Jvm is { Count: > 0 } jvm) args.AddRange(Expand(jvm, rules));

        if (installed.LoggingArgument is { } logging) args.Add(logging);

        var user = SplitCommandLine(options.UserJvmArguments);
        if (user.Count == 0 && v.Arguments?.DefaultUserJvm is { } defaults)
            user = Expand(defaults, rules).Where(a => !a.StartsWith("-Xms") && !a.StartsWith("-Xmx")).ToList(); // heap comes from our settings
        args.AddRange(user); // user flags come last so the JVM honours them over earlier ones

        args.Add(v.MainClass ?? throw new InvalidDataException($"Version {v.Id} has no mainClass."));

        if (v.MinecraftArguments is { } legacy) args.AddRange(legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (v.Arguments?.Game is { Count: > 0 } game) args.AddRange(Expand(game, rules));

        return args.Select(a => Substitute(a, vars)).ToList();
    }

    private static IEnumerable<string> LegacyJvmArguments(PlatformInfo platform)
    {
        if (platform.IsWindows) yield return "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump";
        if (platform.IsMac) yield return "-XstartOnFirstThread";
        if (platform.MojangArch == "x86") yield return "-Xss1M";
        yield return "-Djava.library.path=${natives_directory}";
        yield return "-cp";
        yield return "${classpath}";
    }

    internal static List<string> Expand(IEnumerable<JsonElement> elements, RuleEvaluator rules)
    {
        var result = new List<string>();
        foreach (var e in elements)
        {
            if (e.ValueKind == JsonValueKind.String) { result.Add(e.GetString()!); continue; }
            if (e.ValueKind != JsonValueKind.Object) continue;
            var ruleList = e.TryGetProperty("rules", out var r) ? r.Deserialize<List<Rule>>(MetadataJson.Options) : null;
            if (!rules.IsAllowed(ruleList) || !e.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) result.Add(value.GetString()!);
            else if (value.ValueKind == JsonValueKind.Array) result.AddRange(value.EnumerateArray().Select(x => x.GetString()!));
        }
        return result;
    }

    internal static string Substitute(string arg, IReadOnlyDictionary<string, string> vars) =>
        Placeholder().Replace(arg, m => vars.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    /// <summary>Splits user-entered JVM arguments, honouring double quotes. No shell interpretation happens.</summary>
    public static List<string> SplitCommandLine(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new System.Text.StringBuilder();
        bool inQuotes = false, hasToken = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else { current.Append(c); hasToken = true; }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Placeholder();
}
