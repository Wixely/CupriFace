using CupriFace.Dom;
using SkiaSharp;

namespace CupriFace.Style;

/// <summary>
/// The CSS transform as a 4×4 matrix, and its projection to the 3×3 the rasteriser draws with (#269).
///
/// <para>Row-major, points as columns: <c>M · (x, y, z, 1)</c>. A 2D transform is the same matrix
/// it always was with a unit z row and column; <c>rotateX</c>/<c>rotateY</c>, <c>translateZ</c>
/// and <c>perspective()</c> fill the rest in. Painting drops z: the 3×3 that is left carries the
/// perspective terms, which Skia's <see cref="SKMatrix"/> has fields for, so a tilted card is one
/// <c>Concat</c> like a rotated one.</para>
///
/// <para>Flat by default, as CSS is: an element's 3D transform is projected where it is pushed, and
/// its children paint into that image. <c>transform-style: preserve-3d</c> instead hands the
/// element's 4×4 to its children, each of which composes its own transform into it and projects
/// the product — which is what makes a parent's rotation carry a flipped face through to the
/// back-face test rather than mirroring its front.</para>
/// </summary>
public static class Transform3D
{
    public static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary><c>a · b</c>: apply <paramref name="b"/> first, then <paramref name="a"/>.</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                var v = 0f;
                for (var k = 0; k < 4; k++) v += a[i * 4 + k] * b[k * 4 + j];
                r[i * 4 + j] = v;
            }
        return r;
    }

    public static float[] Translation(float x, float y, float z)
    { var m = (float[])Identity.Clone(); m[3] = x; m[7] = y; m[11] = z; return m; }

    public static float[] Scale(float x, float y, float z)
    { var m = (float[])Identity.Clone(); m[0] = x; m[5] = y; m[10] = z; return m; }

    public static float[] RotationX(float deg)
    { var (s, c) = SinCos(deg); var m = (float[])Identity.Clone(); m[5] = c; m[6] = -s; m[9] = s; m[10] = c; return m; }

    public static float[] RotationY(float deg)
    { var (s, c) = SinCos(deg); var m = (float[])Identity.Clone(); m[0] = c; m[2] = s; m[8] = -s; m[10] = c; return m; }

    public static float[] RotationZ(float deg)
    { var (s, c) = SinCos(deg); var m = (float[])Identity.Clone(); m[0] = c; m[1] = -s; m[4] = s; m[5] = c; return m; }

    /// <summary>The viewer <paramref name="d"/> px in front of the z=0 plane: nearer points grow,
    /// farther ones shrink, by <c>1 / (1 − z/d)</c>.</summary>
    public static float[] Perspective(float d)
    { var m = (float[])Identity.Clone(); m[14] = -1f / d; return m; }

    private static (float Sin, float Cos) SinCos(float deg)
    {
        var rad = deg * MathF.PI / 180f;
        return (MathF.Sin(rad), MathF.Cos(rad));
    }

    /// <summary>The 3×3 the painter concatenates: z dropped, the perspective row kept.</summary>
    public static SKMatrix Project(float[] m) =>
        new(m[0], m[1], m[3], m[4], m[5], m[7], m[12], m[13], m[15]);

    /// <summary>Whether the element's front is turned away from the viewer — the z component of its
    /// transformed normal is negative. A 2D transform can never be; a <c>scaleX(-1)</c> mirror is
    /// not, which is what CSS says too; a half-turn about x or y is.</summary>
    public static bool IsBackFacing(float[] m) => m[10] < 0;

    /// <summary>The element's own transform about its pivot, in absolute coordinates: the
    /// functions in the engine's fixed order — <c>perspective()</c>, translate, rotateX, rotateY,
    /// rotate, scale — bracketed by the move to and from the origin.</summary>
    public static float[] Local(ComputedStyle s, float absX, float absY, float w, float h)
    {
        var (px, py) = s.TransformPivot(w, h);
        var (tx, ty) = s.ResolvedTranslate(w, h);
        float cx = absX + px, cy = absY + py;
        var m = Translation(cx, cy, 0);
        if (s.PerspectiveFn > 0) m = Multiply(m, Perspective(s.PerspectiveFn));
        if (tx != 0 || ty != 0 || s.TranslateZ != 0) m = Multiply(m, Translation(tx, ty, s.TranslateZ));
        if (s.RotateXDeg != 0) m = Multiply(m, RotationX(s.RotateXDeg));
        if (s.RotateYDeg != 0) m = Multiply(m, RotationY(s.RotateYDeg));
        if (s.RotateDeg != 0) m = Multiply(m, RotationZ(s.RotateDeg));
        if (s.ScaleX != 1 || s.ScaleY != 1) m = Multiply(m, Scale(s.ScaleX, s.ScaleY, 1));
        return Multiply(m, Translation(-cx, -cy, 0));
    }

    /// <summary>The perspective a parent's <c>perspective</c> property lends its children, about the
    /// parent's <c>perspective-origin</c>; identity when it sets none.</summary>
    public static float[] Lent(ComputedStyle parent, float parentAbsX, float parentAbsY, float pw, float ph)
    {
        if (parent.Perspective <= 0) return Identity;
        var ox = parentAbsX + parent.PerspectiveOriginX.Resolve(pw, pw / 2f);
        var oy = parentAbsY + parent.PerspectiveOriginY.Resolve(ph, ph / 2f);
        return Multiply(Translation(ox, oy, 0), Multiply(Perspective(parent.Perspective), Translation(-ox, -oy, 0)));
    }

    /// <summary>
    /// The matrix a node paints through, in absolute coordinates: its own transform, seen through
    /// the perspective its parent lends, composed into the <paramref name="space"/> of a
    /// <c>preserve-3d</c> ancestor when there is one. Painting, hit-testing and the damage diff all
    /// ask here, so the element is clickable exactly where it is drawn.
    /// </summary>
    /// <param name="absX">The node's untransformed absolute origin (its parent's origin plus its
    /// own offset), which is also how the parent's origin is recovered for the lent perspective.</param>
    public static float[] ForNode(RenderNode node, float absX, float absY, float[]? space = null)
    {
        var s = node.Style;
        var m = s.HasTransform ? Local(s, absX, absY, node.Width, node.Height) : Identity;
        if (s.HasTransform && node.Parent is { } p && p.Style.Perspective > 0)
            m = Multiply(Lent(p.Style, absX - node.X - node.DragOffsetX, absY - node.Y - node.DragOffsetY, p.Width, p.Height), m);
        return space is null ? m : Multiply(space, m);
    }
}
