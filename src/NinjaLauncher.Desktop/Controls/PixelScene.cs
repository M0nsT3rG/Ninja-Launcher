using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NinjaLauncher.Desktop.Controls;

public enum SceneTime { Day, Sunset, Night }

/// <summary>
/// A small Minecraft-style landscape (sky, sun or moon, clouds, grass and dirt hills, trees) drawn pixel by pixel
/// from a seed, so every profile gets its own scene without shipping any game art.
/// </summary>
public sealed class PixelScene : Control
{
    public const int Cols = 128, Rows = 48; // the scene's size in pixels

    public static readonly StyledProperty<int> SeedProperty = AvaloniaProperty.Register<PixelScene, int>(nameof(Seed));
    /// <summary>Null picks the time of day from the seed.</summary>
    public static readonly StyledProperty<SceneTime?> TimeProperty = AvaloniaProperty.Register<PixelScene, SceneTime?>(nameof(Time));

    private static readonly Dictionary<(int, SceneTime), Bitmap> Cache = new();

    static PixelScene() => AffectsRender<PixelScene>(SeedProperty, TimeProperty);

    public int Seed { get => GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public SceneTime? Time { get => GetValue(TimeProperty); set => SetValue(TimeProperty, value); }

    /// <summary>A stable seed for anything with an id (profiles use their Guid).</summary>
    public static int SeedOf(Guid id) => BitConverter.ToInt32(id.ToByteArray(), 0);

    public override void Render(DrawingContext context)
    {
        var time = Time ?? (SceneTime)((uint)Seed % 3);
        Bitmap bitmap;
        lock (Cache)
            if (!Cache.TryGetValue((Seed, time), out bitmap!))
            {
                if (Cache.Count >= 64) Cache.Clear(); // shortcut: drops all at once; bitmaps on screen stay alive through their controls
                Cache[(Seed, time)] = bitmap = ToBitmap(Draw(Seed, time));
            }
        // Fill the control like UniformToFill, cropping the sky first so the ground stays in view.
        var scale = Math.Max(Bounds.Width / Cols, Bounds.Height / Rows);
        var (w, h) = (Bounds.Width / scale, Bounds.Height / scale);
        var source = new Rect((Cols - w) / 2, Rows - h, w, h);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            context.DrawImage(bitmap, source, new Rect(Bounds.Size));
    }

    private static Bitmap ToBitmap(uint[] pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(Cols, Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var rows = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(pixels).ToArray();
        for (var y = 0; y < Rows; y++) // rows may be padded, so copy one row at a time
            System.Runtime.InteropServices.Marshal.Copy(rows, y * Cols, buffer.Address + y * buffer.RowBytes, Cols);
        return bitmap;
    }

    /// <summary>The scene as BGRA pixels, row by row. Same seed and time, same picture.</summary>
    public static uint[] Draw(int seed, SceneTime time)
    {
        var rng = new Random(seed);
        var px = new uint[Cols * Rows];
        void Set(int x, int y, uint c) { if (x is >= 0 and < Cols && y is >= 0 and < Rows) px[y * Cols + x] = c; }
        uint Noise(uint c, int amount) // per-pixel variation, like block textures
        {
            var d = rng.Next(-amount, amount + 1);
            static uint Clamp(int v) => (uint)Math.Clamp(v, 0, 255);
            return 0xFF000000 | Clamp((int)((c >> 16) & 255) + d) << 16 | Clamp((int)((c >> 8) & 255) + d) << 8 | Clamp((int)(c & 255) + d);
        }

        // Sky: a vertical gradient in the launcher's purple-and-pink palette at dusk and night.
        var (top, bottom) = time switch
        {
            SceneTime.Night => (0xFF120A26u, 0xFF3A1A62u),
            SceneTime.Sunset => (0xFF3B1670u, 0xFFE0607Eu),
            _ => (0xFF5AA8F0u, 0xFFBFE4FFu),
        };
        for (var y = 0; y < Rows; y++)
        {
            var c = Lerp(top, bottom, y / (double)(Rows - 1));
            for (var x = 0; x < Cols; x++) px[y * Cols + x] = c;
        }
        if (time == SceneTime.Night)
            for (var i = 0; i < 40; i++) Set(rng.Next(Cols), rng.Next(Rows / 2), rng.Next(3) == 0 ? 0xFFFFFFFFu : 0xFFB9A6E8u);

        // Sun or moon: a square, as in the game.
        var (sx, sy) = (rng.Next(12, Cols - 20), rng.Next(3, 10));
        var orb = time switch { SceneTime.Night => 0xFFE8E4F4u, SceneTime.Sunset => 0xFFFFC46Bu, _ => 0xFFFFF3A0u };
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 6; x++) Set(sx + x, sy + y, time == SceneTime.Night && (x + y) % 5 == 0 ? 0xFFC9C3DCu : orb);

        // Clouds: flat blocky strips.
        var cloud = time == SceneTime.Night ? 0xFF5A4685u : time == SceneTime.Sunset ? 0xFFF3A6B8u : 0xFFFFFFFFu;
        for (var i = 0; i < 3; i++)
        {
            var (cx, cy, cw) = (rng.Next(-10, Cols), rng.Next(2, 16), rng.Next(10, 20));
            for (var x = 0; x < cw; x++) { Set(cx + x, cy + 1, cloud); if (x > 2 && x < cw - 3) Set(cx + x, cy, cloud); }
        }

        // Far hills, darker and flatter, then the near ground with grass, dirt and stone.
        var far = Heights(rng, 24, 4);
        var farColour = time == SceneTime.Night ? 0xFF241640u : time == SceneTime.Sunset ? 0xFF5A2A6Eu : 0xFF6E9E6Au;
        for (var x = 0; x < Cols; x++)
            for (var y = far[x]; y < Rows; y++) Set(x, y, farColour);

        var ground = Heights(rng, 33, 3);
        var shade = time == SceneTime.Night ? 0.55 : time == SceneTime.Sunset ? 0.8 : 1.0;
        uint Shaded(uint c) => Lerp(0xFF000000, c, shade);
        for (var x = 0; x < Cols; x++)
        {
            var g = ground[x];
            for (var y = g; y < Rows; y++)
            {
                var depth = y - g;
                var c = depth == 0 ? 0xFF6CB33Fu : depth == 1 ? (rng.Next(3) == 0 ? 0xFF6CB33Fu : 0xFF7A5230u) : depth < 9 ? 0xFF7A5230u : 0xFF7F7F7Fu;
                Set(x, y, Shaded(Noise(c, depth == 0 ? 10 : 14)));
            }
            if (rng.Next(14) == 0) Set(x, g - 1, Shaded(rng.Next(2) == 0 ? 0xFFE04848u : 0xFFF2D04Bu)); // flowers
        }

        // Trees: a trunk and a blocky crown, standing on the grass.
        var trees = rng.Next(2, 5);
        for (var i = 0; i < trees; i++)
        {
            var tx = rng.Next(4, Cols - 4);
            var height = rng.Next(4, 7);
            var baseY = ground[tx];
            for (var y = 1; y <= height; y++) Set(tx, baseY - y, Shaded(Noise(0xFF6B4A2B, 8)));
            var crownY = baseY - height - 2;
            for (var y = 0; y < 4; y++)
                for (var x = -2; x <= 2; x++)
                    if (!(y == 0 && Math.Abs(x) == 2)) Set(tx + x, crownY + y, Shaded(Noise(0xFF3F8F2F, 14)));
        }
        return px;
    }

    /// <summary>A gently rolling skyline around <paramref name="level"/>, moving up or down by at most one block at a time.</summary>
    private static int[] Heights(Random rng, int level, int range)
    {
        var h = new int[Cols];
        var y = level + rng.Next(-range, range + 1);
        for (var x = 0; x < Cols; x++)
        {
            if (rng.Next(4) == 0) y = Math.Clamp(y + rng.Next(-1, 2), level - range, level + range);
            h[x] = y;
        }
        return h;
    }

    private static uint Lerp(uint a, uint b, double t)
    {
        uint Channel(int shift) => (uint)Math.Round(((a >> shift) & 255) + (((b >> shift) & 255) - (double)((a >> shift) & 255)) * t);
        return 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }
}
