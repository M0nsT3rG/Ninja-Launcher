using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NinjaLauncher.Desktop.Controls;

/// <summary>
/// Draws a Java Edition skin (and optional cape) as a 3D figure from the skin file itself; no rendering service is
/// contacted. Drag sideways or use the arrow keys to turn it.
/// </summary>
public sealed class SkinView : Control
{
    public static readonly StyledProperty<Bitmap?> SkinProperty = AvaloniaProperty.Register<SkinView, Bitmap?>(nameof(Skin));
    public static readonly StyledProperty<Bitmap?> CapeProperty = AvaloniaProperty.Register<SkinView, Bitmap?>(nameof(Cape));
    public static readonly StyledProperty<bool> SlimProperty = AvaloniaProperty.Register<SkinView, bool>(nameof(Slim));
    public static readonly StyledProperty<double> YawProperty = AvaloniaProperty.Register<SkinView, double>(nameof(Yaw), 25);
    public static readonly StyledProperty<double> PitchProperty = AvaloniaProperty.Register<SkinView, double>(nameof(Pitch), 10);

    private Point? _drag;

    static SkinView()
    {
        AffectsRender<SkinView>(SkinProperty, CapeProperty, SlimProperty, YawProperty, PitchProperty);
        FocusableProperty.OverrideDefaultValue<SkinView>(true);
    }

    public Bitmap? Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public Bitmap? Cape { get => GetValue(CapeProperty); set => SetValue(CapeProperty, value); }
    public bool Slim { get => GetValue(SlimProperty); set => SetValue(SlimProperty, value); }
    public double Yaw { get => GetValue(YawProperty); set => SetValue(YawProperty, value); }
    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Skin is not { } skin || skin.PixelSize.Width < 64) return;
        var size = Bounds.Size;
        var scale = Math.Min(size.Height / 36, size.Width / 20); // the figure is 32 skin pixels tall, about 16 wide when turned
        var legacy = skin.PixelSize.Height * 2 == skin.PixelSize.Width;
        var cape = Cape is { PixelSize.Width: >= 64 } c ? c : null;
        var faces = SkinModel.Build(Slim, legacy, cape is not null, Yaw, Pitch, scale, new Point(size.Width / 2, size.Height / 2),
            skin.PixelSize.Width / 64.0, (cape?.PixelSize.Width ?? 64) / 64.0);
        using var _ = context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None });
        foreach (var face in faces)
            using (context.PushTransform(face.Transform))
                context.DrawImage(face.Cape ? cape! : skin, face.Source, new Rect(0, 0, 1, 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } start) return;
        var now = e.GetPosition(this);
        Yaw = (Yaw + (now.X - start.X) * 0.8) % 360;
        _drag = now;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Left or Key.Right) { Yaw = (Yaw + (e.Key == Key.Left ? -15 : 15)) % 360; e.Handled = true; }
    }
}
