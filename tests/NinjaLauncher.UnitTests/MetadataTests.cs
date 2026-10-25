using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Metadata;

namespace NinjaLauncher.UnitTests;

public sealed class MetadataTests
{
    private static readonly PlatformInfo Windows64 = new("windows", "x86_64", "windows-x64", ";");
    private static readonly PlatformInfo MacArm = new("osx", "arm64", "mac-os-arm64", ":");
    private static readonly PlatformInfo Linux64 = new("linux", "x86_64", "linux", ":");

    private static List<Rule> Rules(string json) => MetadataJson.Parse<List<Rule>>(json);

    [Fact]
    public void No_rules_means_allowed() => Assert.True(new RuleEvaluator(Linux64, "6.1").IsAllowed(null));

    [Fact]
    public void Last_matching_rule_wins()
    {
        var rules = Rules("""[{"action":"allow"},{"action":"disallow","os":{"name":"osx"}}]""");
        Assert.True(new RuleEvaluator(Windows64, "10.0.22631").IsAllowed(rules));
        Assert.False(new RuleEvaluator(MacArm, "15.0").IsAllowed(rules));
    }

    [Fact]
    public void Os_only_allow_defaults_to_disallow_elsewhere()
    {
        var rules = Rules("""[{"action":"allow","os":{"name":"windows"}}]""");
        Assert.True(new RuleEvaluator(Windows64, "10.0.1").IsAllowed(rules));
        Assert.False(new RuleEvaluator(Linux64, "6.1").IsAllowed(rules));
    }

    [Fact]
    public void Arch_and_version_regex_are_checked()
    {
        Assert.False(new RuleEvaluator(Windows64, "10.0").IsAllowed(Rules("""[{"action":"allow","os":{"arch":"x86"}}]""")));
        var regex = Rules("""[{"action":"allow","os":{"name":"osx","version":"^10\\.5\\.\\d$"}}]""");
        Assert.True(new RuleEvaluator(MacArm, "10.5.8").IsAllowed(regex));
        Assert.False(new RuleEvaluator(MacArm, "15.1").IsAllowed(regex));
    }

    [Theory]
    [InlineData("10.0.17133", false, true)]
    [InlineData("10.0.17134", true, false)] // min inclusive, max exclusive: exactly one GC block applies
    [InlineData("10.0.26100", true, false)]
    public void VersionRange_selects_exactly_one_block(string osVersion, bool zgc, bool g1)
    {
        var zgcRules = Rules("""[{"action":"allow","os":{"name":"windows","versionRange":{"min":"10.0.17134"}}}]""");
        var g1Rules = Rules("""[{"action":"allow","os":{"name":"windows","versionRange":{"max":"10.0.17134"}}}]""");
        var eval = new RuleEvaluator(Windows64, osVersion);
        Assert.Equal(zgc, eval.IsAllowed(zgcRules));
        Assert.Equal(g1, eval.IsAllowed(g1Rules));
    }

    [Fact]
    public void Feature_rules_require_matching_flags()
    {
        var rules = Rules("""[{"action":"allow","features":{"has_custom_resolution":true}}]""");
        Assert.False(new RuleEvaluator(Linux64, "6").IsAllowed(rules));
        Assert.True(new RuleEvaluator(Linux64, "6", new Dictionary<string, bool> { ["has_custom_resolution"] = true }).IsAllowed(rules));
    }

    [Theory]
    [InlineData("org.lwjgl:lwjgl:3.3.3", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("de.oceanlabs.mcp:mcp_config:1.20.1@zip", "de/oceanlabs/mcp/mcp_config/1.20.1/mcp_config-1.20.1.zip")]
    public void Maven_coordinates_map_to_paths(string name, string path)
    {
        Assert.True(MavenCoordinate.TryParse(name, out var c));
        Assert.Equal(path, c.RelativePath);
    }

    [Theory]
    [InlineData("nocolons")]
    [InlineData("a:b")]
    [InlineData("a::c")]
    public void Invalid_coordinates_are_rejected(string name) => Assert.False(MavenCoordinate.TryParse(name, out _));

    [Fact]
    public void Merge_puts_loader_libraries_first_and_appends_arguments()
    {
        var parent = MetadataJson.Parse<VersionJson>("""
            {"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","type":"release",
             "downloads":{"client":{"sha1":"aa","size":1,"url":"https://piston-data.mojang.com/c.jar"}},
             "javaVersion":{"component":"java-runtime-delta","majorVersion":21},
             "arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]},
             "libraries":[{"name":"org.ow2.asm:asm:9.6"},{"name":"com.google.guava:guava:32.1.2-jre"}]}
            """);
        var child = MetadataJson.Parse<VersionJson>("""
            {"id":"fabric-loader-0.16.0-1.21.1","inheritsFrom":"1.21.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
             "arguments":{"game":[],"jvm":["-DFabricMcEmu= net.minecraft.client.main.Main "]},
             "libraries":[{"name":"org.ow2.asm:asm:9.7","url":"https://maven.fabricmc.net/"},{"name":"net.fabricmc:fabric-loader:0.16.0","url":"https://maven.fabricmc.net/"}]}
            """);
        var merged = VersionMerger.Merge(child, parent);
        Assert.Equal("fabric-loader-0.16.0-1.21.1", merged.Id);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);
        Assert.Null(merged.Jar); // resolved to the child id: versions/<child>/<child>.jar
        Assert.Equal("https://piston-data.mojang.com/c.jar", merged.Downloads!.Client!.Url);
        Assert.Equal(21, merged.JavaVersion!.MajorVersion);
        Assert.Equal(["org.ow2.asm:asm:9.7", "net.fabricmc:fabric-loader:0.16.0", "com.google.guava:guava:32.1.2-jre"], merged.Libraries.Select(l => l.Name));
        Assert.Equal(3, merged.Arguments!.Jvm.Count);
        Assert.Equal("-cp", merged.Arguments.Jvm[0].GetString());
    }

    [Fact]
    public void Resolver_handles_legacy_natives_maven_urls_and_rules()
    {
        var libs = MetadataJson.Parse<VersionJson>("""
            {"id":"x","libraries":[
              {"name":"org.lwjgl.lwjgl:lwjgl-platform:2.9.4","natives":{"windows":"natives-windows-${arch}","linux":"natives-linux"},
               "downloads":{"classifiers":{"natives-windows-64":{"path":"org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows-64.jar","sha1":"ab","size":10,"url":"https://libraries.minecraft.net/n.jar"}}},
               "extract":{"exclude":["META-INF/"]}},
              {"name":"net.fabricmc:fabric-loader:0.16.0","url":"https://maven.fabricmc.net"},
              {"name":"ca.weblite:java-objc-bridge:1.1","rules":[{"action":"allow","os":{"name":"osx"}}]}]}
            """).Libraries;
        var root = Path.Combine(Path.GetTempPath(), "ninja-libs");
        var resolved = new LibraryResolver(root, new RuleEvaluator(Windows64, "10.0")).Resolve(libs);
        Assert.Equal(2, resolved.Count);
        Assert.True(resolved[0].IsNativeArchive);
        Assert.EndsWith("natives-windows-64.jar", resolved[0].Path);
        Assert.Equal("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar", resolved[1].Url!.AbsoluteUri);
    }

    [Fact]
    public void Resolver_rejects_artifact_paths_that_escape_libraries()
    {
        var libs = MetadataJson.Parse<VersionJson>("""
            {"id":"x","libraries":[{"name":"a:b:1","downloads":{"artifact":{"path":"../../evil.jar","sha1":"x","size":1,"url":"https://x/y.jar"}}}]}
            """).Libraries;
        Assert.Throws<NinjaLauncher.Application.Security.UnsafePathException>(() =>
            new LibraryResolver(Path.Combine(Path.GetTempPath(), "ninja-libs"), new RuleEvaluator(Windows64, "10")).Resolve(libs));
    }
}
