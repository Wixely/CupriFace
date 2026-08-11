using CupriFace.Dom;
using CupriFace.Style;

namespace CupriFace.Interaction;

/// <summary>
/// Point → node hit-testing over the laid-out render tree (Layer 0 → input). Mirrors the
/// painter: overlays (top-layer / position:fixed) are tested first, topmost z-index first,
/// then the main tree — so a dialog backdrop or dropdown correctly captures clicks.
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

    private static RenderNode? Hit(RenderNode node, float originX, float originY, float x, float y, bool inTopLayer)
    {
        if (node.Style.Display == DisplayType.None) return null;
        if (!inTopLayer && node.IsTopLayer) return null; // reached via the main pass — skip; tested in the overlay pass

        var ax = originX + node.X;
        var ay = originY + node.Y;
        var inside = x >= ax && x < ax + node.Width && y >= ay && y < ay + node.Height;

        RenderNode? best = inside && !node.IsText ? node : null;
        // Children of a scrolled element are shifted up by the scroll offset.
        var childOy = ay - (node.IsScrollable ? Math.Clamp(node.ScrollY, 0, node.MaxScrollY) : 0f);
        foreach (var child in node.Children)
        {
            var hit = Hit(child, ax, childOy, x, y, inTopLayer);
            if (hit is not null) best = hit;
        }
        return best;
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
    public static (float X, float Y, float W, float H) ScreenBox(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X;
            y += n.Y;
            if (n.IsTopLayer) break;
            if (n.Parent is { IsScrollable: true } p) y -= Math.Clamp(p.ScrollY, 0, p.MaxScrollY);
        }
        return (x, y, node.Width, node.Height);
    }
}
