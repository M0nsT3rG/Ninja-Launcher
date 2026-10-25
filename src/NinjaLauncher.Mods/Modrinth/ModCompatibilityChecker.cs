using NinjaLauncher.Domain;

namespace NinjaLauncher.Mods.Modrinth;

public enum IssueSeverity { Info, Warning, Error }

/// <param name="MissingProjectId">Set for a required dependency that can be installed with one click.</param>
public sealed record ModIssue(IssueSeverity Severity, string Message, string? FileName = null, string? MissingProjectId = null);

/// <summary>
/// Checks an instance's mods against Modrinth metadata: missing/disabled required dependencies, declared
/// incompatibilities, duplicate copies, wrong loader and wrong Minecraft version. Mods not on Modrinth are reported once.
/// </summary>
public sealed class ModCompatibilityChecker(ModrinthClient modrinth)
{
    internal sealed record LocalMod(string FileName, bool Enabled, ModrinthVersion? Version);

    public async Task<IReadOnlyList<ModIssue>> AnalyzeAsync(GameInstance instance, string gameDirectory, CancellationToken ct)
    {
        var dir = Path.Combine(gameDirectory, "mods");
        var files = ModrinthClient.ListMods(gameDirectory);
        if (files.Count == 0) return [];
        var hashed = await Task.Run(() => files.Select(f => (f.FileName, f.Enabled, Sha1: ModrinthClient.Sha1Of(Path.Combine(dir, f.FileName)))).ToList(), ct);
        var versions = await modrinth.IdentifyFilesAsync(hashed.Select(h => h.Sha1).Distinct().ToList(), ct);
        var mods = hashed.Select(h => new LocalMod(h.FileName, h.Enabled, versions.GetValueOrDefault(h.Sha1))).ToList();
        var referenced = mods.Where(m => m.Version is not null)
            .SelectMany(m => m.Version!.Dependencies.Select(d => d.ProjectId).Append(m.Version.ProjectId)).OfType<string>().Distinct().ToList();
        return Analyze(instance, mods, await modrinth.GetProjectTitlesAsync(referenced, ct));
    }

    internal static IReadOnlyList<ModIssue> Analyze(GameInstance instance, IReadOnlyList<LocalMod> mods, IReadOnlyDictionary<string, string> titles)
    {
        var known = mods.Where(m => m.Version is not null).ToList();
        var enabled = known.Where(m => m.Enabled).ToLookup(m => m.Version!.ProjectId);
        var disabled = known.Where(m => !m.Enabled).Select(m => m.Version!.ProjectId).ToHashSet();
        string Title(string projectId) => titles.GetValueOrDefault(projectId, projectId);

        var issues = new List<ModIssue>();
        var loader = ModrinthClient.LoaderName(instance.Loader);
        foreach (var group in enabled)
        {
            var copies = group.ToList();
            if (copies.Count > 1)
                issues.Add(new(IssueSeverity.Error, $"{Title(group.Key)} is installed {copies.Count} times ({string.Join(", ", copies.Select(c => c.FileName))}). Keep one.", copies[0].FileName));

            var mod = copies[0];
            var v = mod.Version!;
            var name = Title(v.ProjectId);
            if (loader is not null && !LoaderCompatible(v.Loaders, loader))
                issues.Add(new(IsCrossForge(v.Loaders, loader) ? IssueSeverity.Warning : IssueSeverity.Error,
                    $"{name} is made for {string.Join("/", v.Loaders)}, not {instance.Loader}.", mod.FileName));
            if (!v.GameVersions.Contains(instance.MinecraftVersion))
                issues.Add(new(IssueSeverity.Warning, $"{name} {v.VersionNumber} is not listed for Minecraft {instance.MinecraftVersion}.", mod.FileName));

            foreach (var dep in v.Dependencies.Where(d => d.ProjectId is not null))
            {
                var depId = dep.ProjectId!;
                if (dep.DependencyType == "required" && !enabled.Contains(depId))
                    issues.Add(disabled.Contains(depId)
                        ? new(IssueSeverity.Error, $"{name} requires {Title(depId)}, which is disabled.", mod.FileName)
                        : new(IssueSeverity.Error, $"{name} requires {Title(depId)}, which is not installed.", mod.FileName, depId));
                else if (dep.DependencyType == "incompatible" && enabled.Contains(depId))
                    issues.Add(new(IssueSeverity.Error, $"{name} is incompatible with {Title(depId)}. Disable one of them.", mod.FileName));
            }
        }

        var unknown = mods.Count(m => m.Enabled && m.Version is null);
        if (unknown > 0) issues.Add(new(IssueSeverity.Info, $"{unknown} mod(s) are not on Modrinth, so they could not be checked."));

        // The same missing dependency can be reported by several mods; keep one install suggestion per project.
        return issues
            .GroupBy(i => i.MissingProjectId is null ? i.Message : "missing:" + i.MissingProjectId).Select(g => g.First())
            .OrderByDescending(i => i.Severity).ThenBy(i => i.Message, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Quilt loads Fabric mods.
    internal static bool LoaderCompatible(IReadOnlyList<string> modLoaders, string instanceLoader) =>
        modLoaders.Contains(instanceLoader) || instanceLoader == "quilt" && modLoaders.Contains("fabric");

    // Some NeoForge builds (notably 1.20.1) still load Forge mods: warn rather than error.
    private static bool IsCrossForge(IReadOnlyList<string> modLoaders, string instanceLoader) =>
        instanceLoader == "neoforge" && modLoaders.Contains("forge") || instanceLoader == "forge" && modLoaders.Contains("neoforge");
}
