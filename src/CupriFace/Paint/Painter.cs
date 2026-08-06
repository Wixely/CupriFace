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
    public Painter(ImageStore? images = null) => _images = images;

    /// <summary>Dev overlay: outline every element's border box (scrollers in a second colour) on top
    /// of the normal paint. Toggled via <c>CupriDocument.DebugOverlay</c>.</summary>
    public bool DebugOutline;

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

        // backdrop-filter on a top-layer scrim (a modal/drawer/shelf) blurs the page BEHIND it. Skia has
        // no backdrop-capture we can reach, but the top layer paints last — so we blur the whole
        // background as one group (the main content AND any OTHER open overlay, e.g. a pinned tooltip),
        // then paint the modal's own scrim + panel sharp on top. Blurring the other overlays too is what
        // keeps them from poking through the frost; the modal owns everything under its container element.
        var backdropNode = FindBackdropNode(root);
        var modalContainer = backdropNode?.Element?.ParentElement;

        if (backdropNode is not null) list.Add(new PushFilter(backdropNode.Style.BackdropFilter!));
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
        return list;
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

    private void PaintNode(DisplayList list, RenderNode node, float originX, float originY, List<RenderNode> topLayer, bool inTopLayer)
    {
        // Lift a top-layer (fixed) node out of the normal walk; paint it in the deferred pass.
        if (!inTopLayer && node.IsTopLayer)
        {
            topLayer.Add(node);
            return;
        }

        // node.X/Y are relative to the parent's border-box origin.
        var absX = originX + node.X;
        var absY = originY + node.Y;
        var s = node.Style;

        if (node.IsText)
        {
            PaintText(list, node, absX, absY);
            return;
        }

        // Filter wraps the whole subtree (outermost — the filter sees the composited element).
        var filtered = s.Filter is { Count: > 0 };
        if (filtered) list.Add(new PushFilter(s.Filter!));

        // Opacity composites the whole subtree as a group (wrapping any transform).
        var faded = s.Opacity < 1f;
        if (faded) list.Add(new PushOpacity(Math.Clamp(s.Opacity, 0f, 1f)));

        // Transform wraps the node's whole subtree, applied around its centre.
        var transformed = s.HasTransform;
        if (transformed)
            list.Add(new PushTransform(
                absX + node.Width / 2f, absY + node.Height / 2f,
                s.TranslateX, s.TranslateY, s.ScaleX, s.ScaleY, s.RotateDeg));

        // Background (fills the border box; drawn under the border).
        if (s.Background.Alpha > 0)
            list.Add(new FillRect(absX, absY, node.Width, node.Height, s.BorderRadius, s.Background));

        // Border frame.
        var hasBorder = (node.BorderTopW + node.BorderRightW + node.BorderBottomW + node.BorderLeftW) > 0
                        && s.BorderColor.Alpha > 0 && s.BorderStyle != BorderLineStyle.None;
        if (hasBorder)
            list.Add(new BorderRect(absX, absY, node.Width, node.Height, s.BorderRadius,
                node.BorderTopW, node.BorderRightW, node.BorderBottomW, node.BorderLeftW, s.BorderColor, s.BorderStyle));

        // Icon: fill an SVG path in the content box with the current color.
        if (node.IconPath is { Length: > 0 } iconPath)
        {
            var iw = node.Width - node.HorizontalInsets;
            var ih = node.Height - node.VerticalInsets;
            list.Add(new FillPath(absX + node.ContentLeftInset, absY + node.ContentTopInset, iw, ih, 24f, iconPath, s.Color));
        }

        // Image: decode + draw into the content box, fitted per object-fit.
        if (node.ImageSrc is { Length: > 0 } imageSrc && _images?.Get(imageSrc) is { } image)
            list.Add(new DrawImage(
                absX + node.ContentLeftInset, absY + node.ContentTopInset,
                node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets,
                image, ParseFit(node.Element?.GetAttribute("data-object-fit")), s.BorderRadius));

        // Chart line (line chart / sparkline): a polyline through normalised points scaled into the
        // content box, with an optional area fill (data-cupri-area) and dots (data-cupri-dots).
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
                var lineW = float.TryParse(el?.GetAttribute("data-cupri-width"), out var lw) ? lw : 2f;
                var fillCol = el?.HasAttribute("data-cupri-area") == true
                    ? new SKColor(s.Color.Red, s.Color.Green, s.Color.Blue, 0x2E) : SKColor.Empty;
                list.Add(new Polyline(pts, lineW, s.Color, fillCol, cy + ch));
                if (el?.HasAttribute("data-cupri-dots") == true)
                {
                    var r = lineW + 1.5f;
                    for (var i = 0; i + 1 < pts.Count; i += 2)
                        list.Add(new FillRect(pts[i] - r, pts[i + 1] - r, r * 2f, r * 2f, r, s.Color));
                }
            }
        }

        // Clip children if overflow is not visible.
        var clip = s.Overflow != OverflowMode.Visible;
        if (clip)
            list.Add(new PushClip(absX + node.BorderLeftW, absY + node.BorderTopW,
                node.Width - node.BorderLeftW - node.BorderRightW,
                node.Height - node.BorderTopW - node.BorderBottomW, s.BorderRadius));

        // Scroll: shift children up by the (clamped) vertical offset, and left by the horizontal
        // caret-follow offset (single-line fields). Both are computed before paint.
        var scrollY = 0f;
        if (node.IsScrollable)
        {
            scrollY = Math.Clamp(node.ScrollY, 0, node.MaxScrollY);
            node.ScrollY = scrollY;
        }
        var scrollX = node.ScrollX;

        // Virtualisation: in a scroll container, skip painting children whose box is entirely outside
        // the visible band (plus a margin). Long lists then cost paint+raster for the visible rows
        // only, not every row — the win during scrolling. Layout is unaffected (culling is paint-only).
        var cull = node.IsScrollable;
        var bandTop = node.ContentTopInset + scrollY - CullMargin;
        var bandBottom = node.ContentTopInset + scrollY + node.ContentBoxHeight + CullMargin;

        foreach (var child in node.Children)
        {
            if (child.Style.Display == DisplayType.None) continue;
            if (cull && !child.IsTopLayer && (child.Y + child.Height < bandTop || child.Y > bandBottom)) continue;
            PaintNode(list, child, absX - scrollX, absY - scrollY, topLayer, inTopLayer);
        }

        if (clip) list.Add(new PopClip());

        // Scrollbar thumb (on top of content, inside the padding box).
        if (node.IsScrollable)
        {
            var boxH = node.ContentBoxHeight;
            var thumbH = MathF.Max(28f, boxH * boxH / node.ScrollContentHeight);
            var thumbY = absY + node.ContentTopInset + scrollY / node.MaxScrollY * (boxH - thumbH);
            var thumbX = absX + node.Width - node.BorderRightW - 8f;
            list.Add(new FillRect(thumbX, thumbY, 5f, thumbH, 2.5f, new SKColor(0x60, 0x6a, 0x7a, 0xB0)));
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

        if (transformed) list.Add(new PopTransform());
        if (faded) list.Add(new PopOpacity());
        if (filtered) list.Add(new PopFilter());
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
                Color: s.Color, Align: s.TextAlign));
        }
    }
}
