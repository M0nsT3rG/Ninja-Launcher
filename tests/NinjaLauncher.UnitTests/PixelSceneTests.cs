using NinjaLauncher.Desktop.Controls;

namespace NinjaLauncher.UnitTests;

public sealed class PixelSceneTests
{
    [Fact]
    public void The_same_profile_always_gets_the_same_scene_and_others_differ()
    {
        var id = Guid.Parse("1f5fb23d-1f17-4e8c-a7b0-6531d6cda938");
        var seed = PixelScene.SeedOf(id);

        Assert.Equal(PixelScene.Draw(seed, SceneTime.Day), PixelScene.Draw(seed, SceneTime.Day));
        Assert.NotEqual(PixelScene.Draw(seed, SceneTime.Day), PixelScene.Draw(seed + 1, SceneTime.Day));
    }

    [Theory]
    [InlineData(SceneTime.Day)]
    [InlineData(SceneTime.Sunset)]
    [InlineData(SceneTime.Night)]
    public void Scenes_are_opaque_with_sky_on_top_and_ground_below(SceneTime time)
    {
        var px = PixelScene.Draw(12345, time);

        Assert.Equal(PixelScene.Cols * PixelScene.Rows, px.Length);
        Assert.All(px, p => Assert.Equal(0xFFu, p >> 24)); // no transparent holes
        static (uint R, uint G, uint B) Rgb(uint p) => ((p >> 16) & 255, (p >> 8) & 255, p & 255);
        var bottom = px[^PixelScene.Cols..].Select(Rgb).ToList();
        // The bottom row is dirt or stone, never sky: no blue-dominant pixels there.
        Assert.DoesNotContain(bottom, c => c.B > c.R + 40 && c.B > c.G + 40);
    }
}
