namespace NinjaLauncher.Minecraft.Java.Metadata;

/// <summary>Flattens an <c>inheritsFrom</c> chain into one effective version.</summary>
public static class VersionMerger
{
    public static VersionJson Merge(VersionJson child, VersionJson parent)
    {
        var merged = new VersionJson
        {
            Id = child.Id,
            InheritsFrom = parent.InheritsFrom,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Assets = child.Assets ?? parent.Assets,
            Downloads = child.Downloads ?? parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,
            // Only an explicit "jar" is inherited. Otherwise the client jar lives at versions/<child>/<child>.jar like the
            // official launcher does; NeoForge/Forge rely on that name (ignoreList=${version_name}.jar).
            Jar = child.Jar ?? parent.Jar,
            Libraries = DedupeLibraries(child.Libraries.Concat(parent.Libraries)),
        };
        if (child.Arguments is not null || parent.Arguments is not null)
            merged.Arguments = new VersionArguments
            {
                Game = [.. parent.Arguments?.Game ?? [], .. child.Arguments?.Game ?? []],
                Jvm = [.. parent.Arguments?.Jvm ?? [], .. child.Arguments?.Jvm ?? []],
            };
        return merged;
    }

    /// <summary>Keeps the first library per group:artifact[:classifier], so loader libraries override vanilla ones.</summary>
    public static List<Library> DedupeLibraries(IEnumerable<Library> libraries)
    {
        var seen = new HashSet<string>();
        var result = new List<Library>();
        foreach (var lib in libraries)
        {
            var key = MavenCoordinate.TryParse(lib.Name, out var c) ? $"{c.Group}:{c.Artifact}:{c.Classifier}:{RuleKey(lib)}" : lib.Name;
            if (seen.Add(key)) result.Add(lib);
        }
        return result;
    }

    // Libraries with different OS rules (e.g. per-platform natives) must not collapse into one.
    private static string RuleKey(Library lib) => lib.Rules is null ? "" : string.Join(',', lib.Rules.Select(r => $"{r.Action}/{r.Os?.Name}/{r.Os?.Arch}"));
}

public readonly record struct MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    /// <summary>Parses <c>group:artifact:version[:classifier][@ext]</c>.</summary>
    public static bool TryParse(string name, out MavenCoordinate coordinate)
    {
        coordinate = default;
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }
        var parts = name.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        coordinate = new MavenCoordinate(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null, ext);
        return true;
    }

    public string RelativePath =>
        $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{Artifact}-{Version}{(Classifier is null ? "" : "-" + Classifier)}.{Extension}";

    public MavenCoordinate WithClassifier(string classifier) => this with { Classifier = classifier };
}
