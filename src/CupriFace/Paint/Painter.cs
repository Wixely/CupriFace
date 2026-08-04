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
    public DisplayList Build(RenderNode root)
    {
        var list = new DisplayList();
        var topLayer = new List<RenderNode>();
        PaintNode(list, root, 0, 0, topLayer, inTopLayer: false);

        // Overlays: painted last (above everything), ordered by z-index. Their X/Y are
        // already absolute viewport coordinates, so origin is (0,0).
        foreach (var overlay in topLayer.OrderBy(n => n.Style.ZIndex))
            PaintNode(list, overlay, 0, 0, topLayer, inTopLayer: true);
        return list;
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

        // Opacity composites the whole subtree as a group (outermost, wrapping any transform).
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
                        && s.BorderColor.Alpha > 0;
        if (hasBorder)
            list.Add(new BorderRect(absX, absY, node.Width, node.Height, s.BorderRadius,
                node.BorderTopW, node.BorderRightW, node.BorderBottomW, node.BorderLeftW, s.BorderColor));

        // Icon: fill an SVG path in the content box with the current color.
        if (node.IconPath is { Length: > 0 } iconPath)
        {
            var iw = node.Width - node.HorizontalInsets;
            var ih = node.Height - node.VerticalInsets;
            list.Add(new FillPath(absX + node.ContentLeftInset, absY + node.ContentTopInset, iw, ih, 24f, iconPath, s.Color));
        }

        // Clip children if overflow is not visible.
        var clip = s.Overflow != OverflowMode.Visible;
        if (clip)
            list.Add(new PushClip(absX + node.BorderLeftW, absY + node.BorderTopW,
                node.Width - node.BorderLeftW - node.BorderRightW,
                node.Height - node.BorderTopW - node.BorderBottomW, s.BorderRadius));

        // Scroll: shift children up by the (clamped) offset.
        var scrollY = 0f;
        if (node.IsScrollable)
        {
            scrollY = Math.Clamp(node.ScrollY, 0, node.MaxScrollY);
            node.ScrollY = scrollY;
        }

        foreach (var child in node.Children)
        {
            if (child.Style.Display == DisplayType.None) continue;
            PaintNode(list, child, absX, absY - scrollY, topLayer, inTopLayer);
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

        if (transformed) list.Add(new PopTransform());
        if (faded) list.Add(new PopOpacity());
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
