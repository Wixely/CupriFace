using AngleSharp.Dom;
using CupriFace.Dom;
using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// Walks the laid-out render tree and builds an immutable <see cref="DisplayList"/> of
/// absolute-positioned paint commands. Pure data-in/data-out — no Skia here, so it can
/// run on the UI thread and hand the snapshot to the rasteriser.
/// </summary>
public sealed class Painter
{
    private readonly ImageStore? _images;
    private readonly SurfaceRegistry? _surfaces;
    private readonly VectorRegistry? _vectors;
    public Painter(ImageStore? images = null, SurfaceRegistry? surfaces = null,
                   VectorRegistry? vectors = null)
    { _images = images; _surfaces = surfaces; _vectors = vectors; }

    /// <summary>Dev overlay: outline every element's border box (scrollers in a second colour) on top
    /// of the normal paint. Toggled via <c>CupriDocument.DebugOverlay</c>.</summary>
    public bool DebugOutline;

    // The lifted reorder/kanban card, deferred to a global layer painted last (over other columns and any
    // content below the board) — a per-container defer would leave it behind whatever paints after its
    // column. Node + the origin it was reached at; reset each Build.
    private RenderNode? _dragCard;
    private float _dragOx, _dragOy;

    private const float CullMargin = 60f; // paint a little past the viewport so scrolling never flashes blank
    private static readonly SKColor _dbgBox = new(0xE0, 0x2F, 0x8A, 0x66);    // magenta box outline
    private static readonly SKColor _dbgScroll = new(0x2F, 0x8A, 0xE0, 0x99); // blue for scroll containers

    private static ObjectFit ParseFit(string? v) => v switch
    {
        "cover" => ObjectFit.Cover,
        "fill" => ObjectFit.Fill,
        "none" => ObjectFit.None,
        _ => ObjectFit.Contain,
    };

    public DisplayList Build(RenderNode root)
    {
        var list = new DisplayList();
        var topLayer = new List<RenderNode>();
        _dragCard = null;

        // backdrop-filter on a top-layer scrim (a modal/drawer/shelf) blurs the page BEHIND it. Skia has
        // no backdrop-capture we can reach, but the top layer paints last — so we blur the whole
        // background as one group (the main content AND any OTHER open overlay, e.g. a pinned tooltip),
        // then paint the modal's own scrim + panel sharp on top. Blurring the other overlays too is what
        // keeps them from poking through the frost; the modal owns everything under its container element.
        var backdropNode = FindBackdropNode(root);
        var modalContainer = backdropNode?.Element?.ParentElement;

        // A backdrop filter blurs the whole page behind it, so its layer must span the viewport — pass
        // W/H ≤ 0 to leave it unbounded (the whole clip).
        if (backdropNode is not null) list.Add(new PushFilter(backdropNode.Style.BackdropFilter!, 0, 0, 0, 0));
        PaintNode(list, root, 0, 0, topLayer, inTopLayer: false);

        // Overlays paint last (above everything), ordered by z-index. Their X/Y are already absolute
        // viewport coordinates, so origin is (0,0).
        var ordered = topLayer.OrderBy(n => n.Style.ZIndex).ToList();
        if (backdropNode is not null && modalContainer is not null)
        {
            foreach (var o in ordered.Where(n => !IsUnder(n, modalContainer))) // background overlays → blurred
                PaintNode(list, o, 0, 0, topLayer, inTopLayer: true);
            list.Add(new PopFilter());
            foreach (var o in ordered.Where(n => IsUnder(n, modalContainer)))  // the modal itself → sharp, on top
                PaintNode(list, o, 0, 0, topLayer, inTopLayer: true);
        }
        else
        {
            if (backdropNode is not null) list.Add(new PopFilter());
            foreach (var o in ordered)
                PaintNode(list, o, 0, 0, topLayer, inTopLayer: true);
        }

        // The lifted card floats above everything — its shadow, then the card at its dragged offset.
        if (_dragCard is { } d)
        {
            var dx = _dragOx + d.X + d.DragOffsetX;
            var dy = _dragOy + d.Y + d.DragOffsetY;
            list.Add(new ShadowRect(dx, dy, d.Width, d.Height, d.Style.BorderRadius.Resolve(d.Width, d.Height), 0, 4, 16, 0, new SKColor(0, 0, 0, 0x33), false));
            PaintNode(list, d, _dragOx, _dragOy, topLayer, inTopLayer: false);
        }
        return list;
    }

    // How far a filter's result spreads beyond the element's box (blur halo, shadow offset+blur), so the
    // bounded layer doesn't clip it. Colour-matrix ops (grayscale/…) don't spread.
    private static float FilterMargin(IReadOnlyList<FilterOp> ops)
    {
        var m = 0f;
        foreach (var op in ops)
            m = op.Kind switch
            {
                FilterKind.Blur => MathF.Max(m, op.A * 3f),
                FilterKind.DropShadow => MathF.Max(m, MathF.Abs(op.A) + MathF.Abs(op.B) + op.C * 3f),
                _ => m,
            };
        return m;
    }

    // The first top-layer (fixed) element that requests a backdrop-filter — its filter blurs the page
    // behind it. Only top-layer scrims qualify (a full-viewport backdrop over the whole page).
    private static RenderNode? FindBackdropNode(RenderNode n)
    {
        if (n.IsTopLayer && n.Style.BackdropFilter is { Count: > 0 }) return n;
        foreach (var c in n.Children)
            if (FindBackdropNode(c) is { } found) return found;
        return null;
    }

    // Parse "x0,y0 x1,y1 …" normalised (0..1, y=0 top) chart points into a flat absolute [x,y,…] list
    // scaled into the (x,y,w,h) content box.
    private static List<float> ParsePoints(string data, float x, float y, float w, float h)
    {
        var pts = new List<float>();
        foreach (var pair in data.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var c = pair.Split(',');
            if (c.Length == 2
                && float.TryParse(c[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nx)
                && float.TryParse(c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ny))
            {
                pts.Add(x + nx * w);
                pts.Add(y + ny * h);
            }
        }
        return pts;
    }

    // Is <paramref name="container"/> an ancestor-or-self of node's element? Used to tell the modal's own
    // parts (scrim, panel, a popup opened inside it) from unrelated background overlays.
    private static bool IsUnder(RenderNode node, IElement container)
    {
        for (var e = node.Element; e is not null; e = e.ParentElement)
            if (ReferenceEquals(e, container)) return true;
        return false;
    }

    /// <summary>One border command, carrying each edge's colour only when they differ — so the
    /// common single-colour box still takes the rasteriser's stroked fast path.</summary>
    private static BorderRect BorderCmd(float x, float y, float w, float h, CornerRadii radius,
                                        RenderNode node, ComputedStyle s)
    {
        var uniform = s.BorderRightColor == s.BorderTopColor
                      && s.BorderBottomColor == s.BorderTopColor
                      && s.BorderLeftColor == s.BorderTopColor;
        return new BorderRect(x, y, w, h, radius,
            node.BorderTopW, node.BorderRightW, node.BorderBottomW, node.BorderLeftW,
            s.BorderTopColor, s.BorderStyle,
            uniform ? null : s.BorderRightColor,
            uniform ? null : s.BorderBottomColor,
            uniform ? null : s.BorderLeftColor);
    }

    /// <param name="space">The 4×4 of a <c>transform-style: preserve-3d</c> parent, in absolute
    /// coordinates, for this node to compose its own transform into (#269); null under a flat
    /// parent, which is every parent unless it says otherwise.</param>
    private void PaintNode(DisplayList list, RenderNode node, float originX, float originY, List<RenderNode> topLayer, bool inTopLayer,
        List<StickyItem>? stickyCollect = null, float scrollTop = float.NegativeInfinity, float[]? space = null)
    {
        // Lift a top-layer (fixed) node out of the normal walk; paint it in the deferred pass.
        if (!inTopLayer && node.IsTopLayer)
        {
            topLayer.Add(node);
            return;
        }

        // A position:sticky node inside a scroll container is deferred to a pass after the container's
        // normal content — so a stuck header paints ON TOP of what scrolls under it. Record where it is.
        if (stickyCollect is not null && node.Style.Position == PositionType.Sticky)
        {
            stickyCollect.Add(new StickyItem(node, originX, originY));
            return;
        }

        // node.X/Y are relative to the parent's border-box origin. DragOffsetX/Y shift a reorder item
        // (and its subtree) at paint time while it's being dragged / making room for the dragged one.
        var absX = originX + node.X + node.DragOffsetX;
        var absY = originY + node.Y + node.DragOffsetY;
        var s = node.Style;
        // Resolved ONCE, here, because a percentage radius is a fraction of this box and the box is
        // only known now. Every command below takes the px it produces (an inline fragment resolves
        // against its own line box instead — see the fragment loop).
        var radius = s.BorderRadius.Resolve(node.Width, node.Height);

        if (node.IsText)
        {
            PaintText(list, node, absX, absY);
            return;
        }

        // The transform the node paints through, in absolute coordinates: its own, about its
        // transform-origin (a percentage translate is of THIS box, which is why it resolves here
        // and not in the cascade, #258), seen through the perspective its parent lends, composed
        // into a preserve-3d ancestor's space (#269). Decided before any layer is pushed, because
        // a face turned away with its back hidden paints NOTHING — not its filter, not its opacity
        // group, not its children.
        var full = s.HasTransform || space is not null ? Transform3D.ForNode(node, absX, absY, space) : null;
        if (full is not null && s.BackfaceHidden && Transform3D.IsBackFacing(full)) return;

        // Filter wraps the whole subtree (outermost — the filter sees the composited element). The layer
        // is bounded to the element's box grown by the filter's spread, so the offscreen stays small.
 		// The box a filter's or an opacity group's offscreen LAYER has to cover. Both are pushed
        // OUTSIDE the transform, so their bounds live in the pre-transform space and must be the
        // element's box as the transform will place it — not the box it was laid out in.
        //
        // They used to be the laid-out box, which clipped a transformed element to where it would
        // have been: `transform: translateX(60px); opacity: .5` painted NOTHING, and a 20px shift
        // painted the sliver still overlapping its old position. That is the commonest pairing
        // there is — every fade-and-slide entrance — and it took a two-animation element to make it
        // obvious, because one animation rarely moves AND fades at once (found while fixing #284).
        var layer = TransformedBounds(node, absX, absY, full);

        var filtered = s.Filter is { Count: > 0 };
        if (filtered)
        {
            var m = FilterMargin(s.Filter!);
            list.Add(new PushFilter(s.Filter!, layer.Left - m, layer.Top - m, layer.Width + 2 * m, layer.Height + 2 * m));
        }

        // Opacity composites the whole subtree as a group (wrapping any transform).
        var faded = s.Opacity < 1f;
        if (faded) list.Add(new PushOpacity(Math.Clamp(s.Opacity, 0f, 1f), layer.Left, layer.Top, layer.Width, layer.Height));

        // Transform wraps the node's whole subtree (inside the opacity group, so the group is of
        // the transformed image). The origin is resolved against the BORDER box, which is what its
        // percentages refer to; the initial value is 50% 50%, so an element that says nothing still
        // turns about its centre.
        var transformed = full is not null;
        if (transformed) list.Add(new PushTransform(Transform3D.Project(full!)));

        // clip-path: everything this element paints — shadow, box, children — clipped to a shape
        // in its own border box (#268). Inside the transform so the shape turns with the element.
        var shaped = s.ClipPath is not null;
        if (shaped) list.Add(new PushClipShape(absX, absY, node.Width, node.Height, s.ClipPath!));

        // Box shadow: outset (drop) shadows paint BEHIND the background.
        if (s.BoxShadow is { Count: > 0 } shadows)
            foreach (var sh in shadows)
                if (!sh.Inset)
                    list.Add(new ShadowRect(absX, absY, node.Width, node.Height, radius,
                        sh.Dx, sh.Dy, sh.Blur, sh.Spread, sh.Color, false));

        // Background (fills the border box; drawn under the border).
        if (s.Background.Alpha > 0 && node.Width > 0)
            list.Add(new FillRect(absX, absY, node.Width, node.Height, radius, s.Background));

        // The image layer (a gradient or a raster image), painted over any solid background colour,
        // sized and tiled per background-size/-position/-repeat (#267).
        PaintBackgroundLayer(list, s, absX, absY, node.Width, node.Height, radius);

        // Border frame.
        // ANY edge that has both a width and a visible colour. Testing one shared colour answered
        // for the wrong edge once a border could differ per side (#170).
        var hasBorder = s.AnyBorderVisible && s.BorderStyle != BorderLineStyle.None;
        if (hasBorder && node.Width > 0)
            list.Add(BorderCmd(absX, absY, node.Width, node.Height, radius, node, s));

        // Outline: the same frame command, drawn OUTSIDE the border box and offset outwards.
        //
        // Emitted after the border so it sits on top where the two meet, and sized from the box the
        // layout already produced rather than feeding anything back into it -- that is the property's
        // entire point. The radius grows with the ring so a rounded button keeps a concentric one
        // instead of a rounded box sitting inside a square frame.
        if (s.HasOutline && node.Width > 0)
        {
            var spread = s.OutlineOffset + s.OutlineWidth;
            list.Add(new BorderRect(absX - spread, absY - spread,
                node.Width + 2 * spread, node.Height + 2 * spread, radius.Grow(spread),
                s.OutlineWidth, s.OutlineWidth, s.OutlineWidth, s.OutlineWidth,
                s.OutlineColor, s.OutlineStyle));
        }

        // Inline element with a background/border (a <code> chip): one rounded box per line it spans
        // (Width is 0 — a passthrough inline box), painted behind its text. Coords are in the block's
        // content box, i.e. relative to the same origin the element's text fragments use.
        if (node.InlineFragments is { Count: > 0 } inlineBoxes)
            foreach (var f in inlineBoxes)
            {
                // An inline box has no Width of its own, so a percentage here means a fraction of
                // the fragment — which is the box actually being painted.
                var fragRadius = s.BorderRadius.Resolve(f.W, f.H);
                if (s.Background.Alpha > 0)
                    list.Add(new FillRect(absX + f.X, absY + f.Y, f.W, f.H, fragRadius, s.Background));
                PaintBackgroundLayer(list, s, absX + f.X, absY + f.Y, f.W, f.H, fragRadius);
                if (hasBorder)
                    list.Add(BorderCmd(absX + f.X, absY + f.Y, f.W, f.H, fragRadius, node, s));
            }

        // Box shadow: inset (inner) shadows paint on top of the background, clipped inside the box.
        if (s.BoxShadow is { Count: > 0 } insetShadows)
            foreach (var sh in insetShadows)
                if (sh.Inset)
                    list.Add(new ShadowRect(absX, absY, node.Width, node.Height, radius,
                        sh.Dx, sh.Dy, sh.Blur, sh.Spread, sh.Color, true));

        // Icon: fill an SVG path in the content box with the current color.
        if (node.IconPath is { Length: > 0 } iconPath)
        {
            var iw = node.Width - node.HorizontalInsets;
            var ih = node.Height - node.VerticalInsets;
            list.Add(new FillPath(absX + node.ContentLeftInset, absY + node.ContentTopInset, iw, ih, 24f, iconPath, s.Color));
        }

        // A vector drawing an optional package prepared for this element (CupriFace.Svg). One
        // command per shape, in paint order, each mapped from the drawing's viewBox into the content
        // box — so it rasterises at the resolution the frame is drawn at rather than at layout size,
        // and composes with the transform/clip/opacity already on the stack.
        var isDrawing = false;
        if (_vectors?.Get(node.VectorKey) is { } drawing)
        {
            isDrawing = true;
            var vw = node.Width - node.HorizontalInsets;
            var vh = node.Height - node.VerticalInsets;
            var vx = absX + node.ContentLeftInset;
            var vy = absY + node.ContentTopInset;
            // Each shape bound to an element in the tree takes that element's cascaded paint,
            // opacity and transform — a stylesheet rule or a keyframe on a <path> (#262).
            var bound = VectorStyling.Bind(node, drawing.Shapes.Count);
            for (var i = 0; i < drawing.Shapes.Count; i++)
            {
                var shape = drawing.Shapes[i];
                if (bound?[i] is { } shapeNode) shape = VectorStyling.Apply(shape, shapeNode, node, drawing.ViewBox);
                list.Add(new VectorPath(vx, vy, vw, vh, drawing.ViewBox, shape));
            }
        }

        // Live surface (video, future 3D viewports): the current frame, if one exists. Falls
        // through to ImageSrc otherwise — which is exactly how a poster shows until the first
        // frame arrives. Same DrawImage command, so object-fit/radius/damage all behave alike.
        // A HOST-COMPOSITED surface (web underlay video) paints no frames at all: punch a
        // transparent hole so the host's own element shows through; later paint stays on top.
        SKImage? frame = null;
        var surfaced = false; // a hole was punched OR a live surface command was emitted
        if (node.SurfaceKey is { Length: > 0 } surfaceKey && _surfaces?.Get(surfaceKey) is { } source)
        {
            if (source.HostComposited)
            {
                surfaced = true; // the poster must not paint into it — the underlay is the picture now
                list.Add(new ClearHole(
                    absX + node.ContentLeftInset, absY + node.ContentTopInset,
                    node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets, radius));
            }
            else if (source.CurrentFrame is not null)
            {
                // Frames flow: emit a raster-time-resolved DrawSurface (NOT a captured DrawImage),
                // so a new frame leaves the display list unchanged — the surface fast path.
                surfaced = true;
                list.Add(new DrawSurface(
                    absX + node.ContentLeftInset, absY + node.ContentTopInset,
                    node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets,
                    source, ParseFit(node.Element?.GetAttribute("data-object-fit")), radius));
            }
        }

        // Image: decode + draw into the content box, fitted per object-fit.
        if (frame is null && !surfaced && node.ImageSrc is { Length: > 0 } imageSrc) frame = _images?.Get(imageSrc);
        if (frame is { } img)
            list.Add(new DrawImage(
                absX + node.ContentLeftInset, absY + node.ContentTopInset,
                node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets,
                img, ParseFit(node.Element?.GetAttribute("data-object-fit")), radius));

        // A preserve-3d node hands its 4×4 to its children, each of which composes its own
        // transform into it and pushes the product (#269) — so its own transform wraps only its
        // own paint above, and is pushed again for the chrome drawn after the children.
        var preserve = transformed && s.Preserve3D;
        if (preserve) list.Add(new PopTransform());

        // Clip children if overflow is not visible.
        var clip = s.Overflow != OverflowMode.Visible;
        if (clip)
            list.Add(new PushClip(absX + node.BorderLeftW, absY + node.BorderTopW,
                node.Width - node.BorderLeftW - node.BorderRightW,
                node.Height - node.BorderTopW - node.BorderBottomW, radius));

        // Chart line (line / sparkline / rolling): a polyline through normalised points scaled into the
        // content box, with an optional area fill (data-cupri-area) and dots (data-cupri-dots). Emitted
        // inside the clip so a plot with overflow:hidden crops the line to its (rounded) box.
        if (node.ChartLine is { Length: > 0 } chartLine)
        {
            var cx = absX + node.ContentLeftInset;
            var cy = absY + node.ContentTopInset;
            var cw = node.Width - node.HorizontalInsets;
            var ch = node.Height - node.VerticalInsets;
            var pts = ParsePoints(chartLine, cx, cy, cw, ch);
            if (pts.Count >= 4)
            {
                var el = node.Element;
                var lineW = CssNumber.TryParse(el?.GetAttribute("data-cupri-width"), out var lw) ? lw : 2f;
                var fillCol = el?.HasAttribute("data-cupri-area") == true
                    ? new SKColor(s.Color.Red, s.Color.Green, s.Color.Blue, 0x2E) : SKColor.Empty;
                var curved = el?.HasAttribute("data-cupri-curve") == true;
                list.Add(new Polyline(pts, lineW, s.Color, fillCol, cy + ch, curved));
                if (el?.HasAttribute("data-cupri-dots") == true)
                {
                    var r = lineW + 1.5f;
                    for (var i = 0; i + 1 < pts.Count; i += 2)
                        list.Add(new FillRect(pts[i] - r, pts[i + 1] - r, r * 2f, r * 2f, r, s.Color));
                }
            }
        }

        // Scroll: shift children up by the (clamped) vertical offset, and left by the horizontal
        // caret-follow offset (single-line fields). Both are computed before paint.
        var scrollY = 0f;
        if (node.IsScrollable)
        {
            scrollY = Math.Clamp(node.ScrollY, 0, node.MaxScrollY);
            node.ScrollY = scrollY;
            scrollY += node.OverscrollY;      // the rubber band, while a finger is stretching it
        }
        // Clamped for a container that overflows horizontally; passed through untouched for a
        // single-line text field, which owns its own caret-follow shift.
        var scrollX = node.ClampedScrollX;
        if (node.IsScrollableX) { node.ScrollX = scrollX; scrollX += node.OverscrollX; }

        // Virtualisation: in a scroll container, skip painting children whose box is entirely outside
        // the visible band (plus a margin). Long lists then cost paint+raster for the visible rows
        // only, not every row — the win during scrolling. Layout is unaffected (culling is paint-only).
        var cull = node.IsScrollable;
        var bandTop = node.ContentTopInset + scrollY - CullMargin;
        var bandBottom = node.ContentTopInset + scrollY + node.ContentBoxHeight + CullMargin;

        // A scroll container collects the sticky nodes in its subtree; they paint in a deferred pass below
        // (sticking to the top of its content box). A non-scroll node just passes the collector through.
        var stickyOwn = node.IsScrollable ? new List<StickyItem>() : null;
        var childSticky = node.IsScrollable ? stickyOwn : stickyCollect;
        // Sticky pins to the scrollport (padding-box) top, not the content box — content scrolls under the
        // padding, so a `top:0` header sits flush at the very top and covers it.
        var childScrollTop = node.IsScrollable ? absY + node.BorderTopW : scrollTop;

        RenderNode? dragged = null; // the lifted reorder item — painted last so it sits on top of its siblings
        // The elements inside a drawing are its shapes, painted above as part of it. They have no
        // boxes of their own, and walking them would push an opacity layer per hidden shape.
        if (!isDrawing)
            // In PAINT order, not document order: a positioned sibling that declares a z-index
            // belongs above or below its neighbours whatever the markup's order (#290).
            foreach (var child in PaintOrder.Children(node))
            {
                if (child.Style.Display == DisplayType.None) continue;
                if (child.Dragging) { dragged = child; continue; }
                // Sticky children are never culled — a stuck header's natural box may be scrolled out of band.
                if (cull && !child.IsTopLayer && child.Style.Position != PositionType.Sticky
                    && (child.Y + child.Height < bandTop || child.Y > bandBottom)) continue;
                PaintNode(list, child, absX - scrollX, absY - scrollY, topLayer, inTopLayer, childSticky, childScrollTop,
                    preserve ? full : null);
            }
        // Defer the lifted card to a single global layer (painted after everything, incl. other columns and
        // whatever sits below the board), so it floats on top instead of hiding behind a later-painted sibling.
        if (dragged is not null) { _dragCard = dragged; _dragOx = absX - scrollX; _dragOy = absY - scrollY; }

        // Sticky pass: each deferred sticky node paints at its stuck position (clamped to its containing
        // block), on top of the scrolled content but still inside this container's clip.
        if (stickyOwn is { Count: > 0 })
            foreach (var it in stickyOwn)
                PaintSticky(list, it, childScrollTop, topLayer, inTopLayer);

        if (clip) list.Add(new PopClip());
        if (preserve) list.Add(new PushTransform(Transform3D.Project(full!)));

        // Scrollbar (on top of content, inside the padding box). The geometry is Interaction's, not
        // the painter's: the hit-test asks the same question and the two answers used to be written
        // out separately and drift.
        if (Interaction.Scrollbar.Applies(node))
        {
            var hot = node.ScrollbarHot;
            // The track shows only while the pointer is in it. That is the whole affordance — an
            // empty column is invisible until it is worth knowing about, and then it is obvious.
            if (hot)
            {
                var tr = Interaction.Scrollbar.Track(node, absX, absY);
                list.Add(new FillRect(tr.X, tr.Y, tr.W, tr.H, tr.W / 2f, new SKColor(0x60, 0x6a, 0x7a, 0x20)));
            }
            var th = Interaction.Scrollbar.Thumb(node, absX, absY, hot);
            list.Add(new FillRect(th.X, th.Y, th.W, th.H, th.W / 2f,
                new SKColor(0x60, 0x6a, 0x7a, hot ? (byte)0xE0 : (byte)0xB0)));
        }

        // Resize grip (CSS resize) in the bottom-right corner.
        if (s.Resize != ResizeMode.None)
        {
            const float grip = 13f;
            list.Add(new ResizeGrip(
                absX + node.Width - node.BorderRightW - grip - 2f,
                absY + node.Height - node.BorderBottomW - grip - 2f,
                grip, new SKColor(0x8b, 0x93, 0xa7)));
        }

        // Debug overlay: outline this element's border box on top of its content.
        if (DebugOutline && node.Width > 0 && node.Height > 0)
            list.Add(new BorderRect(absX, absY, node.Width, node.Height, 0f, 1, 1, 1, 1,
                node.IsScrollable ? _dbgScroll : _dbgBox));

        if (shaped) list.Add(new PopClip());
        if (transformed) list.Add(new PopTransform());
        if (faded) list.Add(new PopOpacity());
        if (filtered) list.Add(new PopFilter());
    }

    /// <summary>The background's image layer for one box: a gradient spanning the box when nothing
    /// says otherwise (the fast path every pre-#267 document takes), else a tile sized and placed by
    /// the geometry and repeated across the box. A raster image needs its pixels before the tile can
    /// be sized — an image still loading paints nothing this frame, as it does for cupri-image.</summary>
    private void PaintBackgroundLayer(DisplayList list, ComputedStyle s, float x, float y, float w, float h, CornerRadii radius)
    {
        if (w <= 0 || h <= 0 || s.BackgroundLayers is not { Count: > 0 } layers) return;
        // Back to front: CSS lists the TOPMOST layer first, so the last one is painted first (#279).
        for (var i = layers.Count - 1; i >= 0; i--)
            PaintOneLayer(list, layers[i], s.GeometryFor(i), x, y, w, h, radius);
    }

    private void PaintOneLayer(DisplayList list, BackgroundLayer layer, BackgroundGeometry geom,
                               float x, float y, float w, float h, CornerRadii radius)
    {
        if (layer.Gradient is { } grad)
        {
            if (geom.FillsBox(0, 0, w, h)) { list.Add(new GradientRect(x, y, w, h, radius, grad)); return; }
            var (tx, ty, tw, th) = geom.Tile(0, 0, w, h);
            if (tw <= 0 || th <= 0) return;
            list.Add(new GradientRect(x, y, w, h, radius, grad, new BackgroundTile(x + tx, y + ty, tw, th, geom.RepeatX, geom.RepeatY)));
            return;
        }
        if (layer.ImageSrc is { Length: > 0 } src && _images?.Get(src) is { } img)
        {
            var (tx, ty, tw, th) = geom.Tile(img.Width, img.Height, w, h);
            if (tw <= 0 || th <= 0) return;
            list.Add(new TiledImage(x, y, w, h, radius, img, new BackgroundTile(x + tx, y + ty, tw, th, geom.RepeatX, geom.RepeatY)));
        }
    }

    /// <summary>The element's border box where its transform will actually put it, unioned with the
    /// box it was laid out in. Used to size the offscreen layers that wrap the transform, which are
    /// recorded in the space OUTSIDE it.</summary>
    private static SKRect TransformedBounds(RenderNode node, float absX, float absY, float[]? full)
    {
        var box = SKRect.Create(absX, absY, node.Width, node.Height);
        if (full is null) return box;
        var mapped = Transform3D.Project(full).MapRect(box);
        // Unioned rather than replaced: a perspective projection can map a box to something that no
        // longer covers where children in flow are drawn, and a layer that is too big only costs
        // memory, while one that is too small silently eats pixels.
        return SKRect.Union(box, mapped);
    }

    // A collected position:sticky node and the origin it was reached at (its parent's painted top-left).
    private readonly record struct StickyItem(RenderNode Node, float OriginX, float OriginY);

    // Paint a sticky node at its stuck position: it sticks `top` px below the scroll container's content
    // top, but never scrolls above its natural place, and never past its containing block's bottom (so it
    // rides out with the parent). scrollTop is the container's absolute content-box top.
    private void PaintSticky(DisplayList list, StickyItem it, float scrollTop, List<RenderNode> topLayer, bool inTopLayer)
    {
        var n = it.Node;
        var natural = it.OriginY + n.Y;                                     // where the node scrolled to
        var top = n.Style.Top.IsDefinite ? n.Style.Top.Resolve(0f) : 0f;
        var parentBottom = it.OriginY + (n.Parent?.Height ?? n.Height);     // its containing block's bottom
        var stuck = MathF.Min(MathF.Max(natural, scrollTop + top), parentBottom - n.Height);
        PaintNode(list, n, it.OriginX, it.OriginY + (stuck - natural), topLayer, inTopLayer);
    }

    private static void PaintText(DisplayList list, RenderNode node, float absX, float absY)
    {
        if (node.Lines is null) return;
        var s = node.Style;
        foreach (var line in node.Lines)
        {
            if (line.Text.Length == 0) continue;
            list.Add(new TextRun(
                X: absX + line.X, Y: absY + line.Y,
                ContainerWidth: node.Width, LineWidth: line.Width, LineHeight: line.Height,
                Text: line.Text, Family: s.FontFamily, Weight: s.FontWeight, Size: s.FontSize,
                Color: s.Color, Align: s.TextAlign, Slant: s.FontStyle, Decorations: s.Decorations,
                LetterSpacing: s.LetterSpacing, TabularNums: s.TabularNums));
        }
    }
}
