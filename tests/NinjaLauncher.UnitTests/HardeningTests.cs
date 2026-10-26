using System.Buffers.Binary;
using System.IO.Compression;
using NinjaLauncher.Desktop.Controls;
using NinjaLauncher.Mods;
using NinjaLauncher.Mods.CurseForge;
using SkiaSharp;

namespace NinjaLauncher.UnitTests;

public sealed class HardeningTests
{
    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A real 1x1 PNG whose IHDR claims other dimensions (a classic decompression/pixel bomb shape).</summary>
    private static byte[] LyingPng(int w, int h)
    {
        var png = Png(1, 1);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), h);
        var crc = Crc32(png.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), crc);
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void Normal_icons_pass_and_pixel_bombs_are_rejected()
    {
        Assert.True(RemoteImage.IsSafeImage(Png(64, 64)));
        Assert.True(RemoteImage.IsSafeImage(Png(512, 256)));
        Assert.False(RemoteImage.IsSafeImage(LyingPng(20000, 20000)));
        Assert.False(RemoteImage.IsSafeImage(LyingPng(1, 100000)));
        Assert.False(RemoteImage.IsSafeImage("not an image"u8.ToArray()));
    }

    [Fact]
    public void Streaming_fingerprint_matches_in_memory_fingerprint()
    {
        var file = Path.GetTempFileName();
        try
        {
            var rnd = new Random(42);
            foreach (var size in new[] { 0, 1, 3, 4, 5, 81919, 81920, 81921, 200_003 })
            {
                var bytes = new byte[size];
                rnd.NextBytes(bytes);
                for (var i = 0; i < size; i += 7) bytes[i] = (byte)" \t\r\n"[i % 4]; // plenty of whitespace to filter
                File.WriteAllBytes(file, bytes);
                Assert.Equal(CurseForgeFingerprint.Compute(bytes), CurseForgeFingerprint.OfFile(file));
            }
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_selection_skips_private_files_and_includes_pack_folders()
    {
        var game = Directory.CreateTempSubdirectory("ninja-sel-").FullName;
        try
        {
            void Write(string rel) { var p = Path.Combine(game, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }
            Write("mods/a.jar");
            Write("mods/b.jar.disabled");
            Write("config/sodium-options.json");
            Write("config/reauth.toml");
            Write("config/voicechat/username-cache.json");
            Write("config/.git/HEAD");
            Write("resourcepacks/MyPack/pack.mcmeta");
            Write("saves/World/level.dat");
            Write("options.txt");

            var s = PackExportFiles.Collect(game);
            Assert.Equal(["mods/a.jar"], s.Content.Select(c => c.Relative));
            var overrides = s.Overrides.Select(o => o.Relative).Order().ToList();
            Assert.Equal(["config/sodium-options.json", "resourcepacks/MyPack/pack.mcmeta"], overrides);
            Assert.Equal(1, s.SkippedDisabled);
            Assert.Equal(2, s.SkippedPrivate); // reauth.toml and username-cache.json
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void Formatted_engine_messages_are_translated_including_nested_ones()
    {
        try
        {
            NinjaLauncher.Desktop.Localization.L.Use("pt-PT");
            Assert.Equal("Instância 'My Pack 2' criada (1.21.1 Fabric)", NinjaLauncher.Desktop.Localization.L.T("Created instance 'My Pack 2' (1.21.1 Fabric)"));
            Assert.Equal("Não foi possível carregar as versões do Minecraft: Tempo esgotado a transferir https://x/y.",
                NinjaLauncher.Desktop.Localization.L.T("Could not load Minecraft versions: Timed out downloading https://x/y."));
            Assert.Equal("Sodium", NinjaLauncher.Desktop.Localization.L.T("Sodium")); // unknown text passes through
        }
        finally { NinjaLauncher.Desktop.Localization.L.Use("en"); }
    }

    [Fact]
    public void Localize_converter_formats_and_strips_the_xaml_brace_escape()
    {
        var c = new NinjaLauncher.Desktop.Views.LocalizeConverter();
        Assert.Equal("1,234 downloads", c.Convert(1234L, typeof(string), "{}{0:N0} downloads", System.Globalization.CultureInfo.InvariantCulture)
            ?.ToString()?.Replace("\u00a0", ",").Replace(".", ","));
        Assert.Null(c.Convert(null, typeof(string), "by {0}", System.Globalization.CultureInfo.InvariantCulture));
    }
}
