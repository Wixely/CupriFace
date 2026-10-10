using CupriFace.Dom;
using CupriFace.Style;

namespace CupriFace.Interaction;

/// <summary>
/// Point → node hit-testing over the laid-out render tree (Layer 0 → input). Mirrors the
/// painter: overlays (top-layer / position:fixed) are tested first, topmost z-index first,
/// then the main tree — so a dialog backdrop or dropdown correctly captures clicks.
///
/// <para>"Mirrors the painter" is load-bearing rather than descriptive. Siblings are walked in
/// <see cref="Paint.PaintOrder"/>'s order and the LAST hit wins, which means "the topmost one" only
/// while the two walks agree. A scrim that z-index lifts above the photo it covers must also be
/// what a click lands on, or the pointer falls through what is visibly in front of it.</para>
/// </summary>
public static class HitTesting
{
    public static RenderNode? HitTest(RenderNode root, float x, float y)
    {
        // Overlays are on top: test them first, highest z-index first.
        var overlays = new List<RenderNode>();
        Collect(root, overlays);
        foreach (var overlay in overlays.OrderByDescending(n => n.Style.ZIndex))
        {
            var hit = Hit(overlay, 0, 0, x, y, inTopLayer: true);
            if (hit is not null) return hit;
        }
        // Then the normal content (top-layer subtrees are skipped).
        return Hit(root, 0, 0, x, y, inTopLayer: false);
    }

    private static void Collect(RenderNode node, List<RenderNode> overlays)
    {
        foreach (var child in node.Children)
        {
            if (child.IsTopLayer) overlays.Add(child);
            Collect(child, overlays);
        }
    }

    /// <summary>
    /// How far a <c>position:sticky</c> node is pushed from where it would otherwise be, in the
    /// same screen space the painter draws in.
    ///
    /// <para>This is the single definition of "where is a stuck node", and it is deliberately
    /// shared: paint, hit-testing and <see cref="ScreenBox"/> each used to answer separately, and a
    /// stuck header was painted in one place, clicked in another and reported to assistive tech in
    /// a third.</para>
    ///
    /// <para><b>Each axis is decided on its own</b>, as CSS decides it. An axis whose two insets are
    /// both <c>auto</c> is not sticky at all and behaves as <c>relative</c> — so a header with only
    /// <c>top</c> is free to move sideways, and a first column with only <c>left</c> scrolls away
    /// vertically. With BOTH insets on one axis the start edge wins, which is the CSS rule whenever
    /// the box is shorter than the scrollport; a box larger than the scrollport cannot honour both
    /// whichever you pick first.</para>
    ///
    /// <para><b>The containing block of a scroll container is its scrolled CONTENT, not its visible
    /// box.</b> Clamping against the border-box size instead drags the node out of the scrollport
    /// as soon as the scroll passes one scrollport's worth of content — the node stops being
    /// painted at all, which reads as "sticky does not work" rather than as a clamp that is one
    /// value wrong.</para>
    /// </summary>
    internal static (float X, float Y) StickyShift(
        RenderNode node, RenderNode cb, float cbOriginX, float cbOriginY,
        float nodeX, float nodeY, StickyPort port)
    {
        var s = node.Style;
        var dy = ShiftOnAxis(
            s.Top, s.Bottom, nodeY, node.Height, port.Top, port.Bottom,
            cbOriginY + cb.ContentTopInset,
            cb.IsScrollable ? cb.ScrollContentHeight : cb.ContentBoxHeight);
        var dx = ShiftOnAxis(
            s.Left, s.Right, nodeX, node.Width, port.Left, port.Right,
            cbOriginX + cb.ContentLeftInset,
            cb.IsScrollableX ? cb.ScrollContentWidth : cb.ContentBoxWidth);
        return (dx, dy);
    }

    // One axis of the rule above. `start` is top/left, `end` is bottom/right.
    private static float ShiftOnAxis(
        Style.Length startInset, Style.Length endInset,
        float nodePos, float nodeSize, float? portStart, float? portEnd,
        float cbStart, float cbSize)
    {
        // Both auto: not sticky on this axis. CSS says such a box is `relative` here, and an axis
        // nobody asked to pin must stay free to move with the content.
        if (!startInset.IsDefinite && !endInset.IsDefinite) return 0f;
        if (portStart is not { } ps || portEnd is not { } pe) return 0f;   // nothing scrolls on this axis

        var maxShift = (cbStart + cbSize - nodeSize) - nodePos;   // rides out at the far edge
        var minShift = cbStart - nodePos;                         // …and at the near one
        if (maxShift < minShift) return 0f;                       // a block too small to hold it

        if (startInset.IsDefinite)
        {
            var wanted = ps + startInset.Resolve(0f);
            if (nodePos < wanted) return Clamp(wanted - nodePos, minShift, maxShift);
        }
        if (endInset.IsDefinite)
        {
            // Pins a footer or a right-hand rail: it holds `end` px inside the far edge while its
            // natural place is still beyond it, and moves BACK to get there (a negative shift).
            var wantedEnd = pe - endInset.Resolve(0f);
            var naturalEnd = nodePos + nodeSize;
            if (naturalEnd > wantedEnd) return Clamp(wantedEnd - naturalEnd, minShift, maxShift);
        }
        return 0f;                                                // still in its natural place

        static float Clamp(float shift, float lo, float hi) => MathF.Max(lo, MathF.Min(shift, hi));
    }

    private static RenderNode? Hit(RenderNode node, float originX, float originY, float x, float y, bool inTopLayer,
                                    RenderNode? parent = null, StickyPort port = default,
                                    List<StickyHit>? stickyCollect = null)
    {
        if (node.Style.Display == DisplayType.None) return null;
        if (!inTopLayer && node.IsTopLayer) return null; // reached via the main pass — skip; tested in the overlay pass

        // Deferred exactly as Painter defers it: a sticky node is lifted out of the normal walk and
        // tested after its scroll container's content, because that is when it is PAINTED. Testing
        // it in document order would let a later sibling that it visibly covers take the click.
        if (stickyCollect is not null && node.Style.Position == PositionType.Sticky)
        {
            stickyCollect.Add(new StickyHit(node, originX, originY));
            return null;
        }

        var ax = originX + node.X;
        var ay = originY + node.Y;

        // Stuck nodes are hit where they are PAINTED, not where they were laid out. Children ride
        // along, because childOy is derived from ay below.
        if (node.Style.Position == PositionType.Sticky && parent is not null)
        {
            var (sdx, sdy) = StickyShift(node, parent, originX, originY, ax, ay, port);
            ax += sdx; ay += sdy;
        }

        // A transformed node PAINTS somewhere other than its layout box, so the pointer has to be
        // mapped into that box's space before anything is compared. Without this a scaled-up tile
        // can only be grabbed inside its original rectangle and a rotated one only near its
        // unrotated corners — it looks like the shape moved but its handle stayed behind. The
        // mapping is inherited by the subtree, because children paint through the same matrix.
        // The same matrix the painter pushed (Transform3D), so the element is hit where it is
        // drawn — a tilted card included (#269). A face turned away with its back hidden was not
        // painted, so it cannot be hit either. A preserve-3d ancestor's space is not composed in
        // here: its faces are tested through their own projection alone.
        if (node.Style.HasTransform)
        {
            var full = Style.Transform3D.ForNode(node, ax, ay);
            if (node.Style.BackfaceHidden && Style.Transform3D.IsBackFacing(full)) return null;
            if (Style.Transform3D.Project(full).TryInvert(out var inverse))
            {
                var p = inverse.MapPoint(x, y);
                x = p.X;
                y = p.Y;
            }
        }

        var inside = x >= ax && x < ax + node.Width && y >= ay && y < ay + node.Height;

        // `visibility: hidden` takes the box out of the pointer's reach without taking it out of
        // layout. NOT a subtree skip: a descendant that sets `visibility: visible` is visible and
        // must be clickable, so each node answers for itself.
        RenderNode? best = inside && !node.IsText && node.Style.Visible ? node : null;
        // Children of a horizontally scrolled box are shifted left by its offset, exactly as the
        // painter shifts them — otherwise a card dragged into view could not be tapped where it
        // now appears.
        var childOx = ax - (node.IsScrollableX ? node.EffectiveScrollX : 0f);

        // Inline content owns no box: LayoutInline zeroes an inline element's X/Y/W/H and positions
        // its text through fragments instead. Without this, a link inside a paragraph is invisible
        // to the pointer — it paints, it looks clickable, and every tap falls through to the
        // paragraph behind it. Fragment coordinates are relative to the block that established the
        // inline formatting context, and the zeroed boxes in between mean (ax, ay) is already that
        // block's origin, so they compose without special-casing the nesting depth.
        if (best is null)
        {
            if (node.InlineFragments is { } frags)
                foreach (var f in frags)
                    if (x >= ax + f.X && x < ax + f.X + f.W && y >= ay + f.Y && y < ay + f.Y + f.H)
                    { best = node; break; }

            // A text run answers for the element that owns it — the contract here is that a hit is
            // always an element, never a text node.
            if (best is null && node.IsText && node.Lines is { } lines && node.Parent is { } owner)
                foreach (var ln in lines)
                    if (x >= ax + ln.X && x < ax + ln.X + ln.Width && y >= ay + ln.Y && y < ay + ln.Y + ln.Height)
                    { best = owner; break; }
        }
        // Children of a scrolled element are shifted up by the scroll offset.
        var childOy = ay - (node.IsScrollable ? node.EffectiveScrollY : 0f);
        // A scroll container is the scrollport its sticky descendants pin to (padding-box top,
        // matching Painter); a non-scrolling node just passes the enclosing one through.
        // Each axis tracks the nearest ancestor that scrolls on THAT axis — a row that only scrolls
        // sideways is the scrollport for `left`, and the page above it is still the one for `top`.
        var childPort = port.Enter(node, ax, ay);
        // A scroll container collects the sticky nodes beneath it; anything else passes the
        // collector straight through, so a sticky node defers to its CONTAINER, not its parent.
        var scrolls = node.IsScrollable || node.IsScrollableX;
        var stickyOwn = scrolls ? new List<StickyHit>() : null;
        var childSticky = scrolls ? stickyOwn : stickyCollect;

        // Paint clips descendants to the padding box whenever overflow is not visible. Keep the
        // element itself hittable in its border, but never descend to a child at a point where that
        // child cannot be seen. Top-layer descendants are tested separately from the viewport and
        // therefore remain independent of this normal-tree clip, exactly as they are in Painter.
        if (node.Style.Overflow != OverflowMode.Visible && !InsideOverflowClip(node, ax, ay, x, y))
            return best;

        // The painter's order, because this loop keeps the LAST hit and that is only "the topmost
        // one" while the two agree. Once z-index reorders painting (#290), a scrim declared before
        // the photo it covers paints last and must also be what a click lands on — otherwise the
        // pointer falls through whatever is visibly in front of it.
        foreach (var child in Paint.PaintOrder.Children(node))
        {
            var hit = Hit(child, childOx, childOy, x, y, inTopLayer, node, childPort, childSticky);
            if (hit is not null) best = hit;
        }

        // The deferred sticky pass, after the scrolled content it sits on top of.
        if (stickyOwn is { Count: > 0 })
            foreach (var it in stickyOwn)
            {
                var hit = Hit(it.Node, it.OriginX, it.OriginY, x, y, inTopLayer, it.Node.Parent, childPort);
                if (hit is not null) best = hit;
            }
        return best;
    }

    private readonly record struct StickyHit(RenderNode Node, float OriginX, float OriginY);

    /// <summary>The scrollport edges a sticky descendant pins to, tracked per axis: the nearest
    /// ancestor that scrolls vertically supplies top/bottom, the nearest that scrolls horizontally
    /// supplies left/right, and they are often different elements.</summary>
    /// <remarks><c>default</c> is deliberately "no scrollport on either axis": null, not zero. A
    /// zero default would tell a sticky node at the top of the page that it had a port at 0 and pin
    /// it there for ever.</remarks>
    internal readonly record struct StickyPort(float? Left, float? Right, float? Top, float? Bottom)
    {
        /// <summary>This port, with any axis <paramref name="node"/> scrolls replaced by its own
        /// padding box — the edge the painter pins to.</summary>
        public StickyPort Enter(RenderNode node, float ax, float ay) => new(
            node.IsScrollableX ? ax + node.BorderLeftW : Left,
            node.IsScrollableX ? ax + node.Width - node.BorderRightW : Right,
            node.IsScrollable ? ay + node.BorderTopW : Top,
            node.IsScrollable ? ay + node.Height - node.BorderBottomW : Bottom);
    }

    // Painter clips overflow to this same rounded padding box. The node itself still owns its
    // rectangular border box, but descendants in a visually cut-away corner must not receive input.
    private static bool InsideOverflowClip(RenderNode node, float ax, float ay, float x, float y)
    {
        var left = ax + node.BorderLeftW;
        var top = ay + node.BorderTopW;
        var right = ax + node.Width - node.BorderRightW;
        var bottom = ay + node.Height - node.BorderBottomW;
        if (x < left || x >= right || y < top || y >= bottom) return false;

        // The corner nearest the point is the only one that can exclude it, and each has its own
        // radius now — a card rounded at the top must not refuse a click at its square bottom edge.
        // Percentages resolve against the border box, the same box the painter rounded.
        var corners = node.Style.BorderRadius.Resolve(node.Width, node.Height);
        if (corners.IsZero) return true;
        var corner = y < (top + bottom) / 2f
            ? x < (left + right) / 2f ? corners.TopLeft : corners.TopRight
            : x < (left + right) / 2f ? corners.BottomLeft : corners.BottomRight;

        var rx = MathF.Min(MathF.Max(0, corner.X), (right - left) / 2f);
        var ry = MathF.Min(MathF.Max(0, corner.Y), (bottom - top) / 2f);
        if (rx <= 0 || ry <= 0) return true;

        var dx = x < left + rx ? x - (left + rx)
            : x > right - rx ? x - (right - rx) : 0f;
        var dy = y < top + ry ? y - (top + ry)
            : y > bottom - ry ? y - (bottom - ry) : 0f;
        // An ellipse, not a circle: a percentage radius on a rectangle has different radii per axis.
        return (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry) <= 1f;
    }

    /// <summary>Absolute border-box of a node. Stops accumulating at a top-layer ancestor
    /// (whose X/Y is already absolute viewport coordinates).</summary>
    public static (float X, float Y, float W, float H) AbsoluteBox(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X;
            y += n.Y;
            if (n.IsTopLayer) break;
        }
        return (x, y, node.Width, node.Height);
    }

    /// <summary>On-screen border-box: <see cref="AbsoluteBox"/> corrected for scrolled ancestors with
    /// exactly the shift <see cref="HitTest"/> applies — so a click synthesized at this box's centre
    /// lands on the node even inside a scrolled container. (AbsoluteBox is the box where the node
    /// WOULD be unscrolled; this is where it IS.)</summary>
    private static (float X, float Y) Origin(RenderNode node)
    {
        // Walked ROOT-DOWN rather than node-up, because a sticky node's shift depends on the
        // scrollport above it — and because a stuck ANCESTOR carries its whole subtree with it,
        // which a bottom-up accumulation cannot see.
        var chain = new List<RenderNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            chain.Add(n);
            if (n.IsTopLayer) break;
        }
        chain.Reverse();

        float x = 0, y = 0;
        var port = default(StickyPort);
        RenderNode? parent = null;
        foreach (var n in chain)
        {
            if (parent is { IsScrollable: true } p) y -= p.EffectiveScrollY;
            if (parent is { IsScrollableX: true } px) x -= px.EffectiveScrollX;
            // The containing block's border-box origin, already shifted by its own scroll —
            // exactly what the painter passes its children, and what StickyShift expects.
            float originX = x, originY = y;
            x += n.X;
            y += n.Y;
            if (n.Style.Position == PositionType.Sticky && parent is not null)
            {
                var (sdx, sdy) = StickyShift(n, parent, originX, originY, x, y, port);
                x += sdx; y += sdy;
            }
            port = port.Enter(n, x, y);
            parent = n;
        }
        return (x, y);
    }

    /// <summary>Where to aim a synthesised click at this node. The centre of the box, except for
    /// inline content: a link that WRAPS has a bounding box whose centre can land between its two
    /// lines — on the paragraph, not the link — so aim at its first fragment instead. (Found on
    /// Linux, where different font metrics wrapped a link that fitted on one line elsewhere.)</summary>
    public static (float X, float Y) ActivationPoint(RenderNode node)
    {
        var (x, y) = Origin(node);
        if (node.Width > 0.01f && node.Height > 0.01f)
            return (x + node.Width / 2, y + node.Height / 2);

        var first = FirstFragment(node);
        return first is { } f ? (x + f.X + f.W / 2, y + f.Y + f.H / 2)
                              : (x + node.Width / 2, y + node.Height / 2);
    }

    private static InlineRect? FirstFragment(RenderNode node)
    {
        if (node.InlineFragments is { Count: > 0 } frags) return frags[0];
        if (node.Lines is { Count: > 0 } lines)
            return new InlineRect(lines[0].X, lines[0].Y, lines[0].Width, lines[0].Height);
        foreach (var c in node.Children) if (FirstFragment(c) is { } f) return f;
        return null;
    }

    public static (float X, float Y, float W, float H) ScreenBox(RenderNode node)
    {
        var (x, y) = Origin(node);
        if (node.Width > 0.01f && node.Height > 0.01f) return (x, y, node.Width, node.Height);

        // Inline content is positioned through fragments, not a box (LayoutInline zeroes it), so
        // the honest answer for a link inside a paragraph is the union of the text it occupies.
        // Everything that aims at a node's centre — synthesised clicks, screen-reader activation,
        // the a11y bridges' bounds — would otherwise aim at an empty point beside the words.
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        void Union(float ux, float uy, float uw, float uh)
        {
            if (uw <= 0 || uh <= 0) return;
            l = MathF.Min(l, ux); t = MathF.Min(t, uy);
            r = MathF.Max(r, ux + uw); b = MathF.Max(b, uy + uh);
        }
        void Walk(RenderNode n)
        {
            if (n.InlineFragments is { } frags) foreach (var f in frags) Union(x + f.X, y + f.Y, f.W, f.H);
            if (n.Lines is { } lines) foreach (var ln in lines) Union(x + ln.X, y + ln.Y, ln.Width, ln.Height);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(node);
        return r > l && b > t ? (l, t, r - l, b - t) : (x, y, node.Width, node.Height);
    }

    /// <summary>The accumulated CSS-transform matrix mapping this node's untransformed
    /// <see cref="ScreenBox"/> to where the rasteriser actually painted it — the same per-node
    /// matrices the paint path applies (centre = each transformed node's own box centre),
    /// composed outermost-first like the nested transform scopes. Identity when nothing on the
    /// chain is transformed. A host compositing an underlay under a painted hole (the web
    /// <c>&lt;video&gt;</c>) needs this: the HOLE paints through the transforms, so the element
    /// must follow the very same mapping or the two shear apart.</summary>
    public static SkiaSharp.SKMatrix ScreenTransform(RenderNode node)
    {
        var m = SkiaSharp.SKMatrix.Identity;
        Accumulate(node);
        return m;

        void Accumulate(RenderNode n)
        {
            if (n.Parent is { } parent) Accumulate(parent);   // outermost transform applies first
            if (!n.Style.HasTransform) return;
            var (x, y, _, _) = ScreenBox(n);
            m = m.PreConcat(Style.Transform3D.Project(Style.Transform3D.ForNode(n, x, y)));
        }
    }
}
