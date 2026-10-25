using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Launch;
using NinjaLauncher.Minecraft.Java.Metadata;

namespace NinjaLauncher.UnitTests;

public sealed class LaunchTests
{
    private static readonly PlatformInfo Windows64 = new("windows", "x86_64", "windows-x64", ";");
    private static readonly LaunchSession Session = new("Steve", "0123456789abcdef0123456789abcdef", "secret-token", "2535400000000000");

    private static InstalledVersion Installed(VersionJson v) => new(v, ["C:/libs/a.jar", "C:/versions/x/x.jar"], "C:/versions/x/x.jar",
        "C:/natives", "C:/libs", "C:/assets", "17", "C:/assets", "-Dlog4j.configurationFile=C:/log.xml");

    [Fact]
    public void Modern_arguments_follow_rules_and_substitute_placeholders()
    {
        var v = MetadataJson.Parse<VersionJson>("""
            {"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main",
             "arguments":{
               "game":["--username","${auth_player_name}","--accessToken","${auth_access_token}","--gameDir","${game_directory}",
                       {"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}"]}],
               "jvm":[{"rules":[{"action":"allow","os":{"name":"osx"}}],"value":["-XstartOnFirstThread"]},
                      {"rules":[{"action":"allow","os":{"name":"windows"}}],"value":"-XX:HeapDumpPath=heap"},
                      "-Djava.library.path=${natives_directory}","-cp","${classpath}"]}}
            """);
        var args = LaunchArgumentBuilder.Build(Installed(v), Session, new LaunchOptions("C:/game dir", 512, 4096, ""), Windows64, "10.0.22631").ToList();

        Assert.Equal("-Xms512M", args[0]);
        Assert.Equal("-Xmx4096M", args[1]);
        Assert.Contains("-XX:HeapDumpPath=heap", args);
        Assert.DoesNotContain("-XstartOnFirstThread", args);
        Assert.Contains("-Djava.library.path=C:/natives", args);
        Assert.Contains("C:/libs/a.jar;C:/versions/x/x.jar", args);
        Assert.Contains("-Dlog4j.configurationFile=C:/log.xml", args);
        Assert.Equal("C:/game dir", args[args.IndexOf("--gameDir") + 1]);
        Assert.Equal("secret-token", args[args.IndexOf("--accessToken") + 1]);
        Assert.DoesNotContain("--width", args);
        Assert.True(args.IndexOf("net.minecraft.client.main.Main") < args.IndexOf("--username"));
    }

    [Fact]
    public void Legacy_arguments_are_split_and_jvm_defaults_added()
    {
        var v = MetadataJson.Parse<VersionJson>("""
            {"id":"1.8.9","mainClass":"net.minecraft.client.main.Main",
             "minecraftArguments":"--username ${auth_player_name} --uuid ${auth_uuid} --userProperties ${user_properties} --assetIndex ${assets_index_name}"}
            """);
        var args = LaunchArgumentBuilder.Build(Installed(v), Session, new LaunchOptions("C:/g", 512, 2048, ""), Windows64, "10.0").ToList();
        Assert.Contains("-cp", args);
        Assert.Contains("-Djava.library.path=C:/natives", args);
        Assert.Equal("{}", args[args.IndexOf("--userProperties") + 1]);
        Assert.Equal("17", args[args.IndexOf("--assetIndex") + 1]);
        Assert.Equal(Session.Uuid, args[args.IndexOf("--uuid") + 1]);
    }

    [Fact]
    public void Default_user_jvm_is_used_only_without_user_arguments_and_never_overrides_heap()
    {
        var v = MetadataJson.Parse<VersionJson>("""
            {"id":"26.3","mainClass":"M","arguments":{"game":[],"jvm":["-cp","${classpath}"],
             "default-user-jvm":["-Xms2G","-Xmx4G","-XX:+UseCompactObjectHeaders",
               {"rules":[{"action":"allow","os":{"name":"windows","versionRange":{"min":"10.0.17134"}}}],"value":"-XX:+UseZGC"}]}}
            """);
        var defaults = LaunchArgumentBuilder.Build(Installed(v), Session, new LaunchOptions("g", 1024, 6144, ""), Windows64, "10.0.26100");
        Assert.Contains("-XX:+UseZGC", defaults);
        Assert.Contains("-XX:+UseCompactObjectHeaders", defaults);
        Assert.DoesNotContain("-Xmx4G", defaults);
        Assert.Contains("-Xmx6144M", defaults);

        var custom = LaunchArgumentBuilder.Build(Installed(v), Session, new LaunchOptions("g", 1024, 6144, "-XX:+UseG1GC \"-Dname=a b\""), Windows64, "10.0.26100");
        Assert.DoesNotContain("-XX:+UseZGC", custom);
        Assert.Contains("-Dname=a b", custom);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("-Xss2M  -Dfoo=bar", new[] { "-Xss2M", "-Dfoo=bar" })]
    [InlineData("\"-Dpath=C:/My Folder\" -Da=\"\"", new[] { "-Dpath=C:/My Folder", "-Da=" })]
    public void Command_line_splitting_honours_quotes(string input, string[] expected) =>
        Assert.Equal(expected, LaunchArgumentBuilder.SplitCommandLine(input));

    [Fact]
    public void Unknown_placeholders_are_left_untouched() =>
        Assert.Equal("${quickPlayPath}", LaunchArgumentBuilder.Substitute("${quickPlayPath}", new Dictionary<string, string>()));

    [Fact]
    public void DescribeCommand_redacts_access_token()
    {
        var text = GameProcess.DescribeCommand("java", ["-cp", "x", "--accessToken", "super-secret", "--username", "Steve"]);
        Assert.DoesNotContain("super-secret", text);
        Assert.Contains("Steve", text);
    }

    [Fact]
    public void Log4j_xml_events_become_plain_lines()
    {
        var lines = new List<GameLogLine>();
        var parser = new GameProcess.Log4jStreamParser(lines.Add);
        parser.Feed("<log4j:Event logger=\"ekx\" timestamp=\"1\" level=\"WARN\" thread=\"Render thread\">");
        parser.Feed("  <log4j:Message><![CDATA[Something odd]]></log4j:Message>");
        parser.Feed("</log4j:Event>");
        parser.Feed("plain stdout line");
        Assert.Equal(2, lines.Count);
        Assert.Equal(new GameLogLine(GameLogLevel.Warn, "Something odd"), lines[0]);
        Assert.Equal("plain stdout line", lines[1].Text);
    }
}
