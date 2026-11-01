using Avalonia;

namespace NinjaLauncher.Desktop.Controls;

/// <summary>One textured face ready to draw: its rectangle in the texture and the transform that maps a unit square onto the screen.</summary>
public readonly record struct SkinFace(Rect Source, Matrix Transform, double Depth, bool Cape);

/// <summary>
/// The Java Edition player model as boxes in skin pixels, projected orthographically with a yaw and pitch. Faces
/// pointing away from the viewer are dropped and boxes are sorted back to front, which is enough for this model
/// because its boxes never interpenetrate.
/// </summary>
public static class SkinModel
{
    private sealed record Part(double X, double Y, double Z, double W, double H, double D, int U, int V,
        double Inflate = 0, bool Mirror = false, bool Cape = false, Part? Over = null);

    private readonly record struct Vec(double X, double Y, double Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a) => new(-a.X, -a.Y, -a.Z);
    }

    /// <param name="legacy">A 64×32 skin: no second layer except the hat, left limbs mirror the right ones.</param>
    /// <param name="skinScale">Texture pixels per skin pixel (1 for a 64-wide skin).</param>
    public static List<SkinFace> Build(bool slim, bool legacy, bool cape, double yawDegrees, double pitchDegrees,
        double scale, Point center, double skinScale = 1, double capeScale = 1)
    {
        var arm = slim ? 3 : 4;
        Part head = new(-4, 24, -4, 8, 8, 8, 0, 0);
        Part body = new(-4, 12, -2, 8, 12, 4, 16, 16);
        Part rightArm = new(-4 - arm, 12, -2, arm, 12, 4, 40, 16);
        Part leftArm = legacy ? rightArm with { X = 4, Mirror = true } : new(4, 12, -2, arm, 12, 4, 32, 48);
        Part rightLeg = new(-4, 0, -2, 4, 12, 4, 0, 16);
        Part leftLeg = legacy ? rightLeg with { X = 0, Mirror = true } : new(0, 0, -2, 4, 12, 4, 16, 48);
        var parts = new List<Part> { head, body, rightArm, leftArm, rightLeg, leftLeg, head with { U = 32, Inflate = 0.5, Over = head } };
        if (!legacy)
            parts.AddRange([
                body with { V = 32, Inflate = 0.25, Over = body }, rightArm with { V = 32, Inflate = 0.25, Over = rightArm },
                leftArm with { U = 48, Inflate = 0.25, Over = leftArm }, rightLeg with { V = 32, Inflate = 0.25, Over = rightLeg },
                leftLeg with { U = 0, Inflate = 0.25, Over = leftLeg },
            ]);
        if (cape) parts.Add(new Part(-5, 8, -3.01, 10, 16, 1, 0, 0, Cape: true)); // hangs just behind the back

        var (yaw, pitch) = (yawDegrees * Math.PI / 180, pitchDegrees * Math.PI / 180);
        var (cy, sy, cp, sp) = (Math.Cos(yaw), Math.Sin(yaw), Math.Cos(pitch), Math.Sin(pitch));
        // Rotates around the model's middle (y = 16); returns screen x, screen y and depth (larger is nearer).
        Vec Rotate(Vec v, bool point)
        {
            var y = point ? v.Y - 16 : v.Y;
            var x = v.X * cy + v.Z * sy;
            var z = -v.X * sy + v.Z * cy;
            return new(x, y * cp - z * sp, y * sp + z * cp);
        }
        Point Screen(Vec r) => new(center.X + r.X * scale, center.Y - r.Y * scale);

        double Depth(Part p) => Rotate(new Vec(p.X + p.W / 2, p.Y + p.H / 2, p.Z + p.D / 2), true).Z;

        // Box-centre depth is right for the limbs and body, which never overlap from the front or back. The head
        // overhangs the cape and is always drawn last; seen from the front half, the cape is behind everything else.
        double Order(Part p) =>
            p == head || p.Over == head ? 1e6 + (p.Over is null ? 0 : 1)
            : p.Cape && cy >= 0 ? -1e6
            : Depth(p.Over ?? p) + (p.Over is null ? 0 : 1e-3);

        var faces = new List<SkinFace>();
        foreach (var part in parts.OrderBy(Order))
        {
            var (i, u, v, w, h, d) = (part.Inflate, part.U, part.V, part.W, part.H, part.D);
            var (x0, y0, z0) = (part.X - i, part.Y - i, part.Z - i);
            var (x1, y1, z1) = (part.X + w + i, part.Y + h + i, part.Z + d + i);
            var (W, H, D) = (x1 - x0, y1 - y0, z1 - z0);
            Rect top = new(u + d, v, w, d), right = new(u, v + d, d, h), front = new(u + d, v + d, w, h),
                left = new(u + d + w, v + d, d, h), back = new(u + 2 * d + w, v + d, w, h);
            if (part.Mirror) (right, left) = (left, right);
            if (part.Cape) (front, back, right, left) = (back, front, left, right); // a cape's pattern faces backwards

            (Vec O, Vec U, Vec V, Vec N, Rect R)[] sides =
            [
                (new(x0, y1, z1), new(W, 0, 0), new(0, -H, 0), new(0, 0, 1), front),
                (new(x1, y1, z0), new(-W, 0, 0), new(0, -H, 0), new(0, 0, -1), back),
                (new(x0, y1, z0), new(0, 0, D), new(0, -H, 0), new(-1, 0, 0), right),
                (new(x1, y1, z1), new(0, 0, -D), new(0, -H, 0), new(1, 0, 0), left),
                (new(x0, y1, z0), new(W, 0, 0), new(0, 0, D), new(0, 1, 0), top),
            ];
            var texScale = part.Cape ? capeScale : skinScale;
            foreach (var (o0, u0, vEdge, n, r) in sides)
            {
                if (Rotate(n, false).Z <= 1e-6) continue; // facing away
                var (o, uEdge) = part.Mirror ? (o0 + u0, -u0) : (o0, u0);
                var (so, su, sv) = (Screen(Rotate(o, true)), Rotate(uEdge, false), Rotate(vEdge, false));
                var transform = new Matrix(su.X * scale, -su.Y * scale, sv.X * scale, -sv.Y * scale, so.X, so.Y);
                var source = new Rect(r.X * texScale, r.Y * texScale, r.Width * texScale, r.Height * texScale);
                faces.Add(new SkinFace(source, transform, Depth(part.Over ?? part), part.Cape));
            }
        }
        return faces;
    }
}
