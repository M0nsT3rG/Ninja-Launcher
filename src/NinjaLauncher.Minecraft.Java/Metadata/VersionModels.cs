using System.Text.Json;
using System.Text.Json.Serialization;

namespace NinjaLauncher.Minecraft.Java.Metadata;

public sealed record VersionManifest(LatestVersions Latest, IReadOnlyList<ManifestVersion> Versions);

public sealed record LatestVersions(string Release, string Snapshot);

public sealed record ManifestVersion(string Id, string Type, string Url, DateTimeOffset Time, DateTimeOffset ReleaseTime, string? Sha1);

/// <summary>A client version JSON (vanilla or a loader profile that uses <c>inheritsFrom</c>).</summary>
public sealed class VersionJson
{
    public string Id { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    /// <summary>Pre-1.13 single-string game arguments.</summary>
    public string? MinecraftArguments { get; set; }
    public VersionArguments? Arguments { get; set; }
    public List<Library> Libraries { get; set; } = [];
    public AssetIndexRef? AssetIndex { get; set; }
    public string? Assets { get; set; }
    public VersionDownloads? Downloads { get; set; }
    public JavaVersionRef? JavaVersion { get; set; }
    public LoggingInfo? Logging { get; set; }
    /// <summary>Id of the version whose client jar is used; set by inheritance resolution.</summary>
    public string? Jar { get; set; }
}

public sealed class VersionArguments
{
    /// <summary>Each element is a string or an object with <c>rules</c> and <c>value</c> (string or array).</summary>
    public List<JsonElement> Game { get; set; } = [];
    public List<JsonElement> Jvm { get; set; } = [];
    /// <summary>26.x+: suggested defaults for the user's own JVM arguments (heap, GC). Not required by the game.</summary>
    [JsonPropertyName("default-user-jvm")]
    public List<JsonElement>? DefaultUserJvm { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    /// <summary>Maven repository base URL (used by Fabric/Quilt profiles without a downloads section).</summary>
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public List<Rule>? Rules { get; set; }
    public ExtractRules? Extract { get; set; }
}

public sealed record LibraryDownloads(Artifact? Artifact, Dictionary<string, Artifact>? Classifiers);

public sealed record Artifact(string? Path, string? Sha1, long? Size, string? Url);

public sealed record ExtractRules(List<string>? Exclude);

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed record OsRule(string? Name, string? Version, string? Arch, VersionRange? VersionRange = null);

public sealed record VersionRange(string? Min, string? Max);

public sealed record AssetIndexRef(string Id, string Sha1, long Size, long? TotalSize, string Url);

public sealed record VersionDownloads(DownloadRef? Client, DownloadRef? Server);

public sealed record DownloadRef(string Sha1, long Size, string Url);

public sealed record JavaVersionRef(string Component, int MajorVersion);

public sealed record LoggingInfo(LoggingConfig? Client);

public sealed record LoggingConfig(string Argument, LoggingFile File, string? Type);

public sealed record LoggingFile(string Id, string Sha1, long Size, string Url);

public sealed record AssetIndexFile(Dictionary<string, AssetObject> Objects, bool Virtual = false,
    [property: JsonPropertyName("map_to_resources")] bool MapToResources = false);

public sealed record AssetObject(string Hash, long Size);

public static class MetadataJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Empty {typeof(T).Name} document.");
}
