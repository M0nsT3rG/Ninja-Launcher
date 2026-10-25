using NinjaLauncher.Domain;
using NinjaLauncher.Mods.Modrinth;
using LocalMod = NinjaLauncher.Mods.Modrinth.ModCompatibilityChecker.LocalMod;

namespace NinjaLauncher.UnitTests;

public sealed class CompatibilityTests
{
    private static readonly GameInstance Fabric121 = new() { Id = Guid.NewGuid(), Name = "t", MinecraftVersion = "1.21.1", Loader = ModLoaderKind.Fabric, LoaderVersion = "x" };
    private static readonly Dictionary<string, string> Titles = new() { ["sodium"] = "Sodium", ["api"] = "Fabric API", ["iris"] = "Iris", ["of"] = "OptiFabric" };

    private static ModrinthVersion V(string project, string[]? loaders = null, string[]? games = null, params ModrinthDependency[] deps) =>
        new("v-" + project, project, project, "1.0", games ?? ["1.21.1"], loaders ?? ["fabric"], [], deps, DateTimeOffset.UnixEpoch, "release");

    private static ModrinthDependency Requires(string p) => new(null, p, "required");
    private static ModrinthDependency Incompatible(string p) => new(null, p, "incompatible");

    private static IReadOnlyList<ModIssue> Run(GameInstance instance, params LocalMod[] mods) => ModCompatibilityChecker.Analyze(instance, mods, Titles);

    [Fact]
    public void Healthy_set_has_no_issues() =>
        Assert.Empty(Run(Fabric121, new LocalMod("sodium.jar", true, V("sodium", deps: Requires("api"))), new LocalMod("api.jar", true, V("api"))));

    [Fact]
    public void Missing_required_dependency_offers_install()
    {
        var issue = Assert.Single(Run(Fabric121, new LocalMod("iris.jar", true, V("iris", deps: Requires("sodium")))));
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("sodium", issue.MissingProjectId);
        Assert.Contains("Iris requires Sodium, which is not installed", issue.Message);
    }

    [Fact]
    public void Disabled_dependency_is_an_error_without_install_offer()
    {
        var issue = Assert.Single(Run(Fabric121,
            new LocalMod("iris.jar", true, V("iris", deps: Requires("sodium"))), new LocalMod("sodium.jar.disabled", false, V("sodium"))));
        Assert.Null(issue.MissingProjectId);
        Assert.Contains("disabled", issue.Message);
    }

    [Fact]
    public void Declared_incompatibility_is_reported()
    {
        var issues = Run(Fabric121, new LocalMod("sodium.jar", true, V("sodium", deps: Incompatible("of"))), new LocalMod("of.jar", true, V("of")));
        Assert.Contains(issues, i => i.Message.Contains("Sodium is incompatible with OptiFabric"));
    }

    [Fact]
    public void Incompatible_mod_that_is_disabled_is_fine() =>
        Assert.Empty(Run(Fabric121, new LocalMod("sodium.jar", true, V("sodium", deps: Incompatible("of"))), new LocalMod("of.jar.disabled", false, V("of"))));

    [Fact]
    public void Duplicate_copies_are_reported_once()
    {
        var issues = Run(Fabric121, new LocalMod("sodium-0.5.jar", true, V("sodium")), new LocalMod("sodium-0.6.jar", true, V("sodium")));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("installed 2 times"));
    }

    [Fact]
    public void Wrong_loader_and_version_are_flagged()
    {
        var issues = Run(Fabric121, new LocalMod("forge-mod.jar", true, V("api", loaders: ["forge"], games: ["1.20.1"])));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("made for forge"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("not listed for Minecraft 1.21.1"));
    }

    [Fact]
    public void Quilt_accepts_fabric_mods_and_neoforge_only_warns_for_forge()
    {
        Assert.Empty(Run(Fabric121 with { Loader = ModLoaderKind.Quilt }, new LocalMod("sodium.jar", true, V("sodium"))));
        var neo = Run(Fabric121 with { Loader = ModLoaderKind.NeoForge }, new LocalMod("x.jar", true, V("api", loaders: ["forge"])));
        Assert.Equal(IssueSeverity.Warning, Assert.Single(neo).Severity);
    }

    private static ModrinthVersion Pinned(string project, string versionId, string number) => V(project) with { Id = versionId, VersionNumber = number };

    [Fact]
    public void Version_only_dependency_is_resolved_and_pinned_install_is_offered()
    {
        var pins = new Dictionary<string, ModrinthVersion> { ["v-sod-05"] = Pinned("sodium", "v-sod-05", "0.5.0") };
        var issue = Assert.Single(ModCompatibilityChecker.Analyze(Fabric121,
            [new LocalMod("iris.jar", true, V("iris", deps: new ModrinthDependency("v-sod-05", null, "required")))], Titles, pins));
        Assert.Equal(("sodium", "v-sod-05"), (issue.MissingProjectId, issue.MissingVersionId));
        Assert.Contains("Sodium 0.5.0", issue.Message);
    }

    [Fact]
    public void Different_version_of_a_pinned_dependency_only_warns()
    {
        var pins = new Dictionary<string, ModrinthVersion> { ["v-sod-05"] = Pinned("sodium", "v-sod-05", "0.5.0") };
        var issue = Assert.Single(ModCompatibilityChecker.Analyze(Fabric121,
        [
            new LocalMod("iris.jar", true, V("iris", deps: new ModrinthDependency("v-sod-05", "sodium", "required"))),
            new LocalMod("sodium.jar", true, Pinned("sodium", "v-sod-06", "0.6.0")),
        ], Titles, pins));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("0.6.0 is installed", issue.Message);
    }

    [Fact]
    public void Incompatibility_with_a_specific_version_ignores_other_versions()
    {
        var pins = new Dictionary<string, ModrinthVersion> { ["v-of-1"] = Pinned("of", "v-of-1", "1.0") };
        var dep = new ModrinthDependency("v-of-1", "of", "incompatible");
        Assert.Empty(ModCompatibilityChecker.Analyze(Fabric121,
            [new LocalMod("sodium.jar", true, V("sodium", deps: dep)), new LocalMod("of.jar", true, Pinned("of", "v-of-2", "2.0"))], Titles, pins));
        Assert.Single(ModCompatibilityChecker.Analyze(Fabric121,
            [new LocalMod("sodium.jar", true, V("sodium", deps: dep)), new LocalMod("of.jar", true, Pinned("of", "v-of-1", "1.0"))], Titles, pins));
    }

    [Fact]
    public void Same_missing_dependency_is_suggested_once_and_unknown_mods_are_summarised()
    {
        var issues = Run(Fabric121,
            new LocalMod("iris.jar", true, V("iris", deps: Requires("api"))),
            new LocalMod("sodium.jar", true, V("sodium", deps: Requires("api"))),
            new LocalMod("custom.jar", true, null));
        Assert.Single(issues, i => i.MissingProjectId == "api");
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Info && i.Message.StartsWith("1 mod(s)"));
    }
}
