using CupriFace.Dom;
using CupriFace.Style;

namespace CupriFace.Diagnostics;

/// <summary>
/// Does this box's content fit inside it?
///
/// <para><b>The failure this exists for.</b> A container given an explicit height smaller than its
/// contents does not clip and does not complain — <c>overflow: visible</c> is the CSS default, so
/// the children simply paint outside it, and the NEXT sibling is positioned using the container's
/// declared height. The result on screen is two unrelated elements drawn on top of each other. That
/// reads as a paint or z-order bug, which is the wrong place to go looking, and it is why this is
/// worth a check rather than an eye: the screenshot actively misleads.</para>
///
/// <para><b>The engine is not wrong here.</b> A browser does exactly the same thing. What a browser
/// also has is an inspector that draws the box, and that is the part being replaced.</para>
///
/// <para>Shared by <see cref="CupriDoctor"/> and <c>CupriDocument.DumpTree</c> so both answer the
/// question identically — one rule, two places to read it.</para>
/// </summary>
public static class BoxOverflow
{
    /// <summary>Sub-pixel differences are rounding, not mistakes. A whole pixel of overshoot is the
    /// smallest thing worth interrupting someone about.</summary>
    private const float Tolerance = 1f;

    /// <summary>
    /// How far the content spills past the box vertically, or null when it fits — or when spilling
    /// is not a mistake.
    ///
    /// <para>Null is returned deliberately for: a box whose height is <c>auto</c> (it grew to fit,
    /// so it cannot overflow by definition), <c>overflow: hidden</c> or <c>scroll</c> (clipping and
    /// scrolling are the author saying they meant it), and out-of-flow children, which are
    /// positioned against something other than this box's flow and routinely sit outside it on
    /// purpose.</para>
    /// </summary>
    public static float? Overshoot(RenderNode n)
    {
        if (n.IsText || n.Children.Count == 0) return null;
        if (n.Style.Overflow != OverflowMode.Visible) return null;
        // An auto height already grew to its content. Only a height the author pinned can be too
        // small — which is exactly the case being looked for.
        if (!n.Style.Height.IsDefinite && !n.Style.MaxHeight.IsDefinite) return null;

        var content = n.Height - n.VerticalInsets;
        if (content <= 0) return null;

        var extent = 0f;
        foreach (var c in n.Children)
        {
            if (c.Style.Position is PositionType.Absolute or PositionType.Fixed) continue;
            extent = Math.Max(extent, c.Y + c.Height + c.MarginBottom);
        }

        var over = extent - content;
        return over > Tolerance ? over : null;
    }

    /// <summary>
    /// A box that has something to show but no area to show it in. Distinct from
    /// <see cref="Overshoot"/>: nothing spills, because there is nowhere for it to spill FROM — the
    /// usual causes are a zero-height flex item, a collapsed percentage height, or an image that
    /// never resolved a size.
    ///
    /// <para><b>Content means VISIBLE content</b>, not merely child nodes. An element wrapping an
    /// empty string collapses to nothing and that is correct, not a fault — and it is the shape a
    /// template takes while its data is still loading, or when a binding elsewhere is wrong. Warning
    /// about it would fire on every such document and bury the findings that matter, which is how a
    /// checker gets switched off and stays off.</para>
    /// </summary>
    public static bool IsEmptyBoxWithContent(RenderNode n) =>
        !n.IsText
        // An INLINE box is not described by Width/Height at all — it flows, and its geometry lives
        // in the line fragments the inline formatting context produced. A <span> reporting 0x0 is
        // the normal state of a healthy inline element, so judging one by its box manufactures a
        // finding for every span in the document.
        && n.Style.Display != DisplayType.Inline
        && (n.Width <= 0.5f || n.Height <= 0.5f)
        && HasVisibleContent(n);

    /// <summary>Is there anything in this subtree that would have painted, given room?</summary>
    private static bool HasVisibleContent(RenderNode n)
    {
        if (n.IsText) return !string.IsNullOrWhiteSpace(n.Text);
        if (n.ImageSrc is { Length: > 0 } || n.IconPath is { Length: > 0 }
            || n.SurfaceKey is { Length: > 0 }) return true;
        foreach (var c in n.Children) if (HasVisibleContent(c)) return true;
        return false;
    }
}
