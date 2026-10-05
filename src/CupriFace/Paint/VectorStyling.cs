using CupriFace.Dom;
using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// What the cascade says about one shape of a vector drawing (#262).
///
/// <para>A drawing is parsed once from its markup and cached across rebuilds, so the shapes it
/// holds know only their presentation ATTRIBUTES. The elements that produced them are also nodes
/// in the render tree, with a computed style the stylesheet, the inline style, the presentation
/// attributes (as lowest-priority declarations) and the animation engine have all written to.
/// This is where the two meet: at paint time each shape bound to a node is re-read from that
/// node's style — the paint, the opacity of every group above it, and a CSS <c>transform</c> —
/// so a rule or a keyframe written against a <c>&lt;path&gt;</c> reaches it exactly as one written
/// against a <c>&lt;div&gt;</c> would.</para>
///
/// <para>A shape with no node (a drawing registered by something other than the SVG package, or
/// an element the tree did not keep) paints as parsed, which is what it always did.</para>
/// </summary>
public static class VectorStyling
{
    /// <summary>The nodes of the svg's subtree that produced a shape, by shape index — or null
    /// when none did, so the common unstyled icon pays one walk and no allocation.</summary>
    public static RenderNode?[]? Bind(RenderNode svg, int shapeCount)
    {
        if (shapeCount == 0 || svg.Children.Count == 0) return null;
        RenderNode?[]? map = null;
        Walk(svg);
        return map;

        void Walk(RenderNode n)
        {
            foreach (var c in n.Children)
            {
                if (c.VectorShapeIndex >= 0 && c.VectorShapeIndex < shapeCount)
                    (map ??= new RenderNode?[shapeCount])[c.VectorShapeIndex] = c;
                if (c.Children.Count > 0) Walk(c);
            }
        }
    }

    /// <summary>The shape as the cascade would have it painted.</summary>
    public static VectorShape Apply(VectorShape shape, RenderNode node, RenderNode svg, SKRect viewBox)
    {
        var s = node.Style;
        if (s.Display == DisplayType.None) return shape with { Hidden = true };

        // Paint: the cascade's answer where it gave one, the drawing's own otherwise. The
        // attributes are in the cascade too, so "where it gave one" covers them; the fallback is
        // for a drawing the tree knows nothing about.
        var fill = s.SvgFill is { } f ? f.WithAlpha((byte)Math.Clamp(f.Alpha * (s.SvgFillOpacity ?? 1f), 0, 255)) : shape.Fill;
        var stroke = s.SvgStroke is { } st ? st.WithAlpha((byte)Math.Clamp(st.Alpha * (s.SvgStrokeOpacity ?? 1f), 0, 255)) : shape.Stroke;
        var strokeWidth = s.SvgStrokeWidth ?? shape.StrokeWidth;
        var dash = s.SvgStrokeDashArray is { } d ? (d.Length == 0 ? null : d) : shape.DashArray;
        var dashOffset = s.SvgStrokeDashOffset ?? shape.DashOffset;

        // Opacity is GROUP opacity in SVG — not inherited, multiplied down — so every node between
        // the shape and the svg contributes its own. The svg element itself is excluded: the
        // painter has already composited its opacity around the whole drawing.
        var opacity = 1f;
        for (var n = node; n is not null && !ReferenceEquals(n, svg); n = n.Parent)
            opacity *= Math.Clamp(n.Style.Opacity, 0f, 1f);

        // A CSS transform on the shape or on a group above it. The shape's own is composed in its
        // LOCAL space (after the drawing's attribute transforms, which is where `translateX(600px)`
        // on a <rect> moves it); a group's is composed in viewBox space, outermost first. Only a
        // shape's own fill-box can be known here, so a group's reference box is always the viewBox.
        var transform = SKMatrix.Identity;
        var any = false;
        var chain = Chain(node, svg);
        for (var i = chain.Count - 1; i > 0; i--)                       // groups, outermost first
        {
            var g = chain[i].Style;
            if (!g.HasTransform) continue;
            transform = transform.PreConcat(CssMatrix(g, viewBox, viewBox, null));
            any = true;
        }
        transform = transform.PreConcat(shape.Transform);
        if (s.HasTransform)
        {
            var bounds = shape.Bounds.Width > 0 || shape.Bounds.Height > 0 ? shape.Bounds : viewBox;
            // The origin is given in the reference box's space; the shape's reference box
            // (view-box, the default) is the viewBox, which is not its local space when the
            // drawing has transformed it — so the pivot is mapped back through that transform.
            SKMatrix? toLocal = null;
            if (!s.TransformBoxFill && !shape.Transform.IsIdentity && shape.Transform.TryInvert(out var inv)) toLocal = inv;
            transform = transform.PreConcat(CssMatrix(s, s.TransformBoxFill ? bounds : viewBox, s.TransformBoxFill ? bounds : viewBox, toLocal));
            any = true;
        }

        return shape with
        {
            Fill = fill, Stroke = stroke, StrokeWidth = strokeWidth,
            DashArray = dash, DashOffset = dashOffset,
            Opacity = opacity,
            Transform = any ? transform : shape.Transform,
        };
    }

    // node → … → the child of svg, as a list with the node first.
    private static List<RenderNode> Chain(RenderNode node, RenderNode svg)
    {
        var chain = new List<RenderNode>(4);
        for (var n = node; n is not null && !ReferenceEquals(n, svg); n = n.Parent) chain.Add(n);
        return chain;
    }

    /// <summary>The matrix a CSS <c>transform</c> describes, about its origin in
    /// <paramref name="originBox"/> and with percentages of <paramref name="percentBox"/>. The
    /// initial origin of an SVG shape is the reference box's own origin — <c>0 0</c>, not the
    /// centre an HTML box pivots about — so an element that said nothing turns about the corner
    /// of the viewBox, exactly as it does in a browser.</summary>
    private static SKMatrix CssMatrix(ComputedStyle s, SKRect originBox, SKRect percentBox, SKMatrix? toLocal)
    {
        var (ox, oy) = s.TransformOriginSet
            ? (originBox.Left + s.TransformOriginX.Resolve(originBox.Width, 0f),
               originBox.Top + s.TransformOriginY.Resolve(originBox.Height, 0f))
            : (originBox.Left, originBox.Top);
        if (toLocal is { } m) { var p = m.MapPoint(ox, oy); ox = p.X; oy = p.Y; }
        var (tx, ty) = s.ResolvedTranslate(percentBox.Width, percentBox.Height);

        var local = SKMatrix.CreateTranslation(ox + tx, oy + ty);
        local = local.PreConcat(SKMatrix.CreateRotationDegrees(s.RotateDeg));
        local = local.PreConcat(SKMatrix.CreateScale(s.ScaleX, s.ScaleY));
        local = local.PreConcat(SKMatrix.CreateTranslation(-ox, -oy));
        return local;
    }
}
