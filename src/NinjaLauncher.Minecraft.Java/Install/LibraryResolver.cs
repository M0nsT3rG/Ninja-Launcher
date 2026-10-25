using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Minecraft.Java.Metadata;

namespace NinjaLauncher.Minecraft.Java.Install;

/// <param name="Url">Null when the file is produced locally (e.g. by a Forge installer processor).</param>
/// <param name="IsNativeArchive">Legacy natives jar: extracted, not put on the classpath.</param>
public sealed record ResolvedLibrary(string Name, string Path, Uri? Url, string? Sha1, long? Size, bool IsNativeArchive, IReadOnlyList<string> ExtractExclude);

/// <summary>Turns library entries into concrete files for the current platform.</summary>
public sealed class LibraryResolver(string librariesDirectory, RuleEvaluator rules)
{
    public const string DefaultRepository = "https://libraries.minecraft.net/";

    public IReadOnlyList<ResolvedLibrary> Resolve(IEnumerable<Library> libraries)
    {
        var result = new List<ResolvedLibrary>();
        foreach (var lib in libraries)
        {
            if (!rules.IsAllowed(lib.Rules)) continue;
            var hasCoordinate = MavenCoordinate.TryParse(lib.Name, out var coordinate);
            var exclude = lib.Extract?.Exclude ?? [];

            if (lib.Natives is not null)
            {
                if (lib.Natives.TryGetValue(rules.Platform.MojangOsName, out var classifier))
                {
                    classifier = classifier.Replace("${arch}", rules.Platform.MojangArch == "x86" ? "32" : "64");
                    var native = lib.Downloads?.Classifiers?.GetValueOrDefault(classifier);
                    if (native is not null) result.Add(FromArtifact(lib.Name, native, coordinate.WithClassifier(classifier), true, exclude));
                    else if (hasCoordinate) result.Add(FromMaven(lib, coordinate.WithClassifier(classifier), true, exclude));
                }
                // Some legacy entries carry both natives and a main artifact.
                if (lib.Downloads?.Artifact is { } main) result.Add(FromArtifact(lib.Name, main, coordinate, false, []));
                continue;
            }

            if (lib.Downloads?.Artifact is { } artifact) result.Add(FromArtifact(lib.Name, artifact, coordinate, false, []));
            else if (lib.Downloads is null && hasCoordinate) result.Add(FromMaven(lib, coordinate, false, []));
        }
        return result;
    }

    private ResolvedLibrary FromArtifact(string name, Artifact a, MavenCoordinate coordinate, bool native, IReadOnlyList<string> exclude)
    {
        var relative = string.IsNullOrEmpty(a.Path) ? coordinate.RelativePath : a.Path;
        var url = string.IsNullOrWhiteSpace(a.Url) ? null : new Uri(a.Url);
        return new ResolvedLibrary(name, SafePath.Combine(librariesDirectory, relative), url, a.Sha1, a.Size, native, exclude);
    }

    private ResolvedLibrary FromMaven(Library lib, MavenCoordinate coordinate, bool native, IReadOnlyList<string> exclude)
    {
        var repo = string.IsNullOrWhiteSpace(lib.Url) ? DefaultRepository : lib.Url.EndsWith('/') ? lib.Url : lib.Url + "/";
        return new ResolvedLibrary(lib.Name, SafePath.Combine(librariesDirectory, coordinate.RelativePath),
            new Uri(new Uri(repo), coordinate.RelativePath), lib.Sha1, lib.Size, native, exclude);
    }
}
