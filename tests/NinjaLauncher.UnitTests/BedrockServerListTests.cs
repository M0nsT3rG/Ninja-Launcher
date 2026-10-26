using System.Text;
using NinjaLauncher.Minecraft.Bedrock;

namespace NinjaLauncher.UnitTests;

public sealed class BedrockServerListTests
{
    // Same shape as a file written by Bedrock 1.26.52 after adding one server (index:name:host:port:unix-seconds, LF).
    private const string GameWritten = "1:Friends:friends.example.net:19132:1793009437\n";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);

    private static string Dir() => Directory.CreateTempSubdirectory("ninja-bedrock-").FullName;
    private static string ListOf(string dir) => File.ReadAllText(Path.Combine(dir, BedrockServerList.FileName));

    [Fact]
    public void Appends_with_the_next_index_and_keeps_existing_lines()
    {
        var dir = Dir();
        try
        {
            File.WriteAllText(Path.Combine(dir, BedrockServerList.FileName), GameWritten);
            Assert.True(BedrockServerList.AddOnce(dir, "Example Server", "play.example.org", 19132, Now));
            Assert.Equal(GameWritten + "2:Example Server:play.example.org:19132:1800000000\n", ListOf(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Creates_the_list_and_never_adds_it_back_after_the_player_deletes_it()
    {
        var dir = Dir();
        try
        {
            Assert.True(BedrockServerList.AddOnce(dir, "Example Server", "play.example.org", 19133, Now));
            Assert.Equal("1:Example Server:play.example.org:19133:1800000000\n", ListOf(dir));

            File.WriteAllText(Path.Combine(dir, BedrockServerList.FileName), ""); // the player removed it
            Assert.False(BedrockServerList.AddOnce(dir, "Example Server", "play.example.org", 19133, Now));
            Assert.Equal("", ListOf(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Same_host_and_port_is_not_duplicated_and_a_missing_final_newline_is_handled()
    {
        var dir = Dir();
        try
        {
            File.WriteAllText(Path.Combine(dir, BedrockServerList.FileName), GameWritten);
            Assert.False(BedrockServerList.AddOnce(dir, "Example Server", "FRIENDS.example.net", 19132, Now));
            Assert.Equal(GameWritten, ListOf(dir));

            var other = Dir();
            File.WriteAllText(Path.Combine(other, BedrockServerList.FileName), GameWritten.TrimEnd('\n'));
            Assert.True(BedrockServerList.AddOnce(other, "Example Server", "play.example.org", 19132, Now));
            Assert.Equal(GameWritten + "2:Example Server:play.example.org:19132:1800000000\n", ListOf(other));
            Directory.Delete(other, true);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unknown_format_is_left_untouched_and_bad_config_is_rejected()
    {
        var dir = Dir();
        try
        {
            File.WriteAllText(Path.Combine(dir, BedrockServerList.FileName), "something else entirely\n");
            Assert.False(BedrockServerList.AddOnce(dir, "Example Server", "play.example.org", 19132, Now));
            Assert.Equal("something else entirely\n", ListOf(dir));
            Assert.False(File.Exists(Path.Combine(dir, BedrockServerList.MarkerFileName)));

            Assert.Throws<ArgumentException>(() => BedrockServerList.AddOnce(dir, "Bad:Name", "play.example.org", 19132, Now));
            Assert.Throws<ArgumentException>(() => BedrockServerList.AddOnce(dir, "Example", "play.example.org", 0, Now));
        }
        finally { Directory.Delete(dir, true); }
    }
}
