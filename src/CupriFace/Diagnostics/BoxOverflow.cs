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

        // Two facts, both measured rather than read off a comment (#145):
        //
        //   1. A child's X/Y are relative to the parent's BORDER-BOX origin — padding and border
        //      already included. A 20px child in a box with border 2 and padding 10 sits at Y=12,
        //      and HitTesting.AbsoluteBox sums X/Y with no inset for exactly that reason. (The old
        //      RenderNode comment said "content coordinates"; it was wrong, and this rule believed it.)
        //   2. The limit is the PADDING box, not the content box. CSS overflow clips at the padding
        //      edge, and content may sit in the padding — a label centred in a fixed-height button
        //      routinely does, and paints correctly inside the border.
        //
        // Getting either wrong reports every fixed-height button as overflowing by its padding —
        // "a 13px label in a 30px button, overflowing by 10", the report this was fixed from.
        var limit = n.Height - n.BorderBottomW;   // padding-box bottom, in the children's coordinates
        if (limit <= 0) return null;

        var extent = 0f;
        foreach (var c in n.Children)
        {
            if (c.Style.Position is PositionType.Absolute or PositionType.Fixed) continue;
            extent = Math.Max(extent, c.Y + c.Height + c.MarginBottom);
        }

        var over = extent - limit;
        return over > Tolerance ? over : null;
    }

    /// <summary>
    /// How far the content spills past the box HORIZONTALLY, or null when it fits.
    ///
    /// <para><b>Why this one does not require a pinned width.</b> Its vertical twin does, because a
    /// box with <c>height: auto</c> grows to its content and cannot overflow by definition — so only
    /// a height the author pinned can be too small. Width does not work that way: a block box's
    /// <c>auto</c> width is FILLED FROM THE PARENT, not grown from the content, so the ordinary case
    /// is a box that was never given a width at all and whose children do not fit the space it
    /// inherited. That is exactly how a desktop layout fails on a phone — three fixed columns of
    /// 72 + 248 + 236 in a 412dp viewport — and requiring a definite width would have missed every
    /// instance of it.</para>
    ///
    /// <para>The consequence differs from the vertical case too, and so does the advice. Vertical
    /// overflow paints over the next element, which is the misleading part. Horizontal overflow
    /// usually runs off the side of the window, where it is simply not there: a column, a button, or
    /// the right-hand end of a row, gone, with nothing on screen to say it ever existed.</para>
    /// </summary>
    public static float? OvershootX(RenderNode n)
    {
        if (n.IsText || n.Children.Count == 0) return null;
        if (n.Style.Overflow != OverflowMode.Visible) return null;
        // A box that shrink-wraps its content cannot be too narrow for it — it was SIZED by it.
        // (An inline box has no width of its own at all; its geometry lives in line fragments.)
        if (n.Style.Display is DisplayType.Inline or DisplayType.InlineBlock) return null;

        var limit = n.Width - n.BorderRightW;     // padding-box right edge, in the children's coordinates
        if (limit <= 0) return null;

        var extent = 0f;
        foreach (var c in n.Children)
        {
            if (c.Style.Position is PositionType.Absolute or PositionType.Fixed) continue;
            // Text is measured by its LINE BOXES, not by the child node's width: a text node inside a
            // block spans the full content width whether or not the glyphs do, so trusting its box
            // would report every paragraph whose last line ends near the edge.
            if (c.IsText)
            {
                if (c.Lines is { } lines)
                    foreach (var line in lines) extent = MathF.Max(extent, c.X + line.X + line.Width);
                continue;
            }
            extent = MathF.Max(extent, c.X + c.Width + c.MarginRight);
        }

        var over = extent - limit;
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

    /// <summary>Is there anything IN-FLOW in this subtree that would have painted, given room?
    /// Out-of-flow children do not count: a zero-height host whose content is entirely
    /// position:fixed or absolute is the normal shape of an overlay anchor — a dialog's backdrop
    /// and panel hang off a 0px element on purpose. Hidden subtrees do not count either.</summary>
    private static bool HasVisibleContent(RenderNode n)
    {
        if (n.Style.Display == DisplayType.None) return false;
        if (n.Style.Position is PositionType.Absolute or PositionType.Fixed) return false;
        if (n.IsText) return !string.IsNullOrWhiteSpace(n.Text);
        if (n.ImageSrc is { Length: > 0 } || n.IconPath is { Length: > 0 }
            || n.SurfaceKey is { Length: > 0 }) return true;
        foreach (var c in n.Children) if (HasVisibleContent(c)) return true;
        return false;
    }
}
