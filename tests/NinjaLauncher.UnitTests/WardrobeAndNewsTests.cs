using System.Text.Json.Nodes;
using Avalonia;
using NinjaLauncher.Desktop.Controls;
using NinjaLauncher.Infrastructure.Auth;
using NinjaLauncher.Infrastructure.News;

namespace NinjaLauncher.UnitTests;

public sealed class WardrobeAndNewsTests
{
    private static readonly Point Center = new(100, 200);

    [Fact]
    public void Facing_the_viewer_only_front_faces_are_drawn_and_mapped_to_the_skin()
    {
        var faces = SkinModel.Build(slim: false, legacy: false, cape: false, yawDegrees: 0, pitchDegrees: 0, scale: 10, Center);

        Assert.Equal(12, faces.Count); // 6 body parts and their 6 outer layers, front faces only
        var head = Assert.Single(faces, f => f.Source == new Rect(8, 8, 8, 8));
        // Head front: top-left corner at (-4, 32) skin pixels, 8×8 pixels wide, 10 screen pixels per skin pixel.
        Assert.Equal(new Matrix(80, 0, 0, 80, 60, 40), head.Transform);
        Assert.Contains(faces, f => f.Source == new Rect(40, 8, 8, 8)); // hat layer
    }

    [Fact]
    public void From_behind_the_cape_and_back_faces_show_and_are_drawn_last()
    {
        var faces = SkinModel.Build(slim: false, legacy: false, cape: true, yawDegrees: 180, pitchDegrees: 0, scale: 10, Center);

        Assert.DoesNotContain(faces, f => f.Source == new Rect(8, 8, 8, 8)); // no head front
        Assert.Contains(faces, f => f.Source == new Rect(24, 8, 8, 8)); // head back
        var cape = Assert.Single(faces, f => f.Cape);
        Assert.Equal(new Rect(1, 1, 10, 16), cape.Source); // the cape's outer pattern
        Assert.True(faces.IndexOf(cape) > faces.FindIndex(f => f.Source == new Rect(32, 20, 8, 12)), "cape must cover the body's back");
    }

    [Fact]
    public void Legacy_skins_mirror_the_right_limbs_and_slim_arms_are_three_pixels()
    {
        var legacy = SkinModel.Build(slim: false, legacy: true, cape: false, yawDegrees: 0, pitchDegrees: 0, scale: 1, Center);
        Assert.Equal(7, legacy.Count); // no outer layers except the hat
        Assert.Equal(2, legacy.Count(f => f.Source == new Rect(44, 20, 4, 12))); // both arms use the right arm's front
        var mirrored = legacy.Where(f => f.Source == new Rect(44, 20, 4, 12)).Select(f => f.Transform.M11).ToList();
        Assert.Contains(mirrored, m => m < 0); // the left one is flipped

        var slim = SkinModel.Build(slim: true, legacy: false, cape: false, yawDegrees: 0, pitchDegrees: 0, scale: 1, Center);
        Assert.Contains(slim, f => f.Source == new Rect(44, 20, 3, 12));
        Assert.Contains(slim, f => f.Source == new Rect(36, 52, 3, 12)); // left arm, 64×64 layout
    }

    [Fact]
    public void The_cape_is_painted_before_the_legs_from_the_front_and_the_head_always_last()
    {
        var front = SkinModel.Build(slim: false, legacy: false, cape: true, yawDegrees: 25, pitchDegrees: 10, scale: 1, Center);
        Assert.True(front.FindIndex(f => f.Cape) < front.FindIndex(f => f.Source == new Rect(4, 20, 4, 12)), "legs cover the cape from the front");

        var back = SkinModel.Build(slim: false, legacy: false, cape: true, yawDegrees: 180, pitchDegrees: 10, scale: 1, Center);
        Assert.True(back.FindLastIndex(f => f.Cape) < back.FindIndex(f => f.Source == new Rect(24, 8, 8, 8)), "the head overhangs the cape");
    }

    [Fact]
    public void Turned_and_tilted_the_top_and_one_side_become_visible()
    {
        var faces = SkinModel.Build(slim: false, legacy: false, cape: false, yawDegrees: 25, pitchDegrees: 10, scale: 1, Center);
        Assert.Contains(faces, f => f.Source == new Rect(8, 0, 8, 8)); // head top
        Assert.Contains(faces, f => f.Source == new Rect(0, 8, 8, 8)); // head right side
        Assert.DoesNotContain(faces, f => f.Source == new Rect(16, 8, 8, 8)); // head left side faces away
    }

    [Fact]
    public void News_feed_keeps_https_links_resolves_images_and_sorts_newest_first()
    {
        var json = JsonNode.Parse("""
            {"entries":[
              {"title":"Older","date":"2026-09-01","text":"a","newsType":["Java","News page"],
               "newsPageImage":{"url":"/v2/images/a.jpg"},"readMoreLink":"https://www.minecraft.net/article/a"},
              {"title":"Newer","date":"2026-10-30","text":"b","newsType":["Bedrock"],"readMoreLink":"http://example.com/b"},
              {"title":"","date":"2026-10-31"},
              {"title":"No date"}
            ]}
            """)!;

        var items = MinecraftNews.Parse(json);

        Assert.Equal(["Newer", "Older"], items.Select(i => i.Title));
        Assert.Null(items[0].ArticleUrl); // not HTTPS
        Assert.True(items[0].Bedrock && !items[0].Java);
        Assert.Equal("https://launchercontent.mojang.com/v2/images/a.jpg", items[1].ImageUrl);
        Assert.Equal(new Uri("https://www.minecraft.net/article/a"), items[1].ArticleUrl);
    }

    [Fact]
    public void Profile_textures_are_read_with_the_active_skin_and_cape()
    {
        var profile = MinecraftProfileClient.Parse(JsonNode.Parse("""
            {"id":"6183","name":"Steve",
             "skins":[{"id":"s1","state":"ACTIVE","url":"http://textures.minecraft.net/texture/abc","variant":"SLIM"}],
             "capes":[{"id":"c1","state":"INACTIVE","url":"http://textures.minecraft.net/texture/c1","alias":"Migrator"},
                      {"id":"c2","state":"ACTIVE","url":"http://textures.minecraft.net/texture/c2","alias":"Pan"}]}
            """)!);

        Assert.Equal("Steve", profile.Name);
        Assert.True(profile.ActiveSkin!.Slim);
        Assert.Equal("c2", profile.ActiveCape!.Id);
        Assert.Equal(["Migrator", "Pan"], profile.Capes.Select(c => c.Alias));
    }

    [Fact]
    public void Skins_are_checked_like_minecraft_before_upload()
    {
        static byte[] Png(int w, int h)
        {
            var b = new byte[33];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
            return b;
        }

        MinecraftProfileClient.CheckSkin(Png(64, 64));
        MinecraftProfileClient.CheckSkin(Png(64, 32));
        Assert.Throws<ArgumentException>(() => MinecraftProfileClient.CheckSkin(Png(128, 128)));
        Assert.Throws<ArgumentException>(() => MinecraftProfileClient.CheckSkin("GIF89a........................."u8.ToArray()));
        Assert.Null(MinecraftProfileClient.PngSize([]));
    }

    [Fact]
    public async Task Textures_only_come_from_mojangs_texture_server()
    {
        var client = new MinecraftProfileClient(new HttpClient());
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadTextureAsync("https://evil.example/texture/abc", TestContext.Current.CancellationToken));
    }
}
