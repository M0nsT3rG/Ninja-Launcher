using System.Buffers.Binary;
using NinjaLauncher.Minecraft.Java.Launch;

namespace NinjaLauncher.UnitTests;

public sealed class ServerListTests
{
    // A servers.dat as the game writes it: one server with an icon, acceptTextures and hidden flags.
    private static byte[] GameWrittenServersDat()
    {
        using var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void Str(string s) { var u = ServerList.Utf8(s); U16(u.Length); ms.Write(u); }
        void Named(byte type, string name) { ms.WriteByte(type); Str(name); }

        Named(10, "");
        Named(9, "servers");
        ms.WriteByte(10);
        I32(1);
        Named(8, "ip"); Str("friends.example.net:25566");
        Named(8, "name"); Str("Friends — café ☕");
        Named(8, "icon"); Str("iVBORw0KGgo=");
        Named(1, "acceptTextures"); ms.WriteByte(1);
        Named(1, "hidden"); ms.WriteByte(0);
        ms.WriteByte(0); // end of server
        ms.WriteByte(0); // end of root
        return ms.ToArray();
    }

    private static string Dir() => Directory.CreateTempSubdirectory("ninja-servers-").FullName;

    [Fact]
    public void Reading_and_writing_keeps_the_file_byte_for_byte()
    {
        var original = GameWrittenServersDat();
        Assert.Equal(original, ServerList.Write(ServerList.Read(original)));
    }

    [Fact]
    public void Adds_the_server_first_and_keeps_existing_servers()
    {
        var dir = Dir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, ServerList.FileName), GameWrittenServersDat());
            Assert.True(ServerList.AddOnce(dir, "Example Server", "play.example.org"));

            var servers = (ServerList.ListTag)ServerList.Read(File.ReadAllBytes(Path.Combine(dir, ServerList.FileName))).Get("servers")!;
            Assert.Equal(2, servers.Items.Count);
            var first = (ServerList.Compound)servers.Items[0];
            Assert.Equal("Example Server", ServerList.Text((byte[])first.Get("name")!));
            Assert.Equal("play.example.org", ServerList.Text((byte[])first.Get("ip")!));
            var kept = (ServerList.Compound)servers.Items[1];
            Assert.Equal("Friends — café ☕", ServerList.Text((byte[])kept.Get("name")!));
            Assert.Equal("iVBORw0KGgo=", ServerList.Text((byte[])kept.Get("icon")!));
            Assert.Equal([1], (byte[])kept.Get("acceptTextures")!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Creates_the_list_for_a_new_instance_and_never_adds_it_back_after_the_player_deletes_it()
    {
        var dir = Dir();
        try
        {
            Assert.True(ServerList.AddOnce(dir, "Example Server", "play.example.org"));
            Assert.False(ServerList.AddOnce(dir, "Example Server", "play.example.org")); // no duplicate

            File.Delete(Path.Combine(dir, ServerList.FileName)); // the player removed it (or the whole list)
            Assert.False(ServerList.AddOnce(dir, "Example Server", "play.example.org"));
            Assert.False(File.Exists(Path.Combine(dir, ServerList.FileName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_server_the_player_already_has_is_not_duplicated()
    {
        var dir = Dir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, ServerList.FileName), GameWrittenServersDat());
            Assert.False(ServerList.AddOnce(dir, "Example Server", "friends.example.net:25566"));
            Assert.Equal(GameWrittenServersDat(), File.ReadAllBytes(Path.Combine(dir, ServerList.FileName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unreadable_list_is_left_untouched()
    {
        var dir = Dir();
        try
        {
            var junk = GameWrittenServersDat()[..20];
            File.WriteAllBytes(Path.Combine(dir, ServerList.FileName), junk);
            Assert.False(ServerList.AddOnce(dir, "Example Server", "play.example.org"));
            Assert.Equal(junk, File.ReadAllBytes(Path.Combine(dir, ServerList.FileName)));
            Assert.False(File.Exists(Path.Combine(dir, ServerList.MarkerFileName))); // tried again next launch
        }
        finally { Directory.Delete(dir, true); }
    }
}
