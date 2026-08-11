using System.Text;
using CupriFace.Dom;
using CupriFace.Style;
using CupriFace.Text;
using SkiaSharp;

namespace CupriFace.Layout;

public readonly record struct Size(float W, float H);

/// <summary>
/// Managed block + flexbox layout engine (DESIGN.md Layer 3). Single-line-per-item
/// flex with grow/shrink/justify/align/gap, block stacking, the content-box model,
/// and greedy word-wrapped text. Wrap-to-multiple-lines flex and grid are later work.
///
/// Coordinates: each node's <c>X,Y</c> is its border-box top-left in the *parent's*
/// border-box coordinate space (already including the parent's content inset), so the
/// painter simply accumulates <c>parent.abs + child.(X,Y)</c>.
/// </summary>
public sealed class LayoutEngine
{
    private readonly FontService _fonts;
    private readonly Paint.ImageStore? _images;
    private readonly Paint.SurfaceRegistry? _surfaces;
    public LayoutEngine(FontService fonts, Paint.ImageStore? images = null, Paint.SurfaceRegistry? surfaces = null)
    { _fonts = fonts; _images = images; _surfaces = surfaces; }

    public void Layout(RenderNode root, float viewportWidth, float viewportHeight)
    {
        // The root (body) is the initial containing block: it fills the viewport, so
        // percentage heights resolve and `height:100%` fills the window.
        LayoutNode(root, viewportWidth, viewportHeight, viewportWidth, viewportHeight);
        root.X = 0;
        root.Y = 0;
        LayoutFixedNodes(root, viewportWidth, viewportHeight);
        PositionAnchoredPopups(root, viewportWidth, viewportHeight);
    }

    /// <summary>Reposition fixed popups (data-cupri-anchor) relative to their anchor element.</summary>
    private void PositionAnchoredPopups(RenderNode root, float vw, float vh)
    {
        var byId = new Dictionary<string, RenderNode>();
        IndexIds(root, byId);
        Walk(root);

        void Walk(RenderNode n)
        {
            foreach (var child in n.Children)
            {
                var anchorId = child.Element?.GetAttribute("data-cupri-anchor");
                if (child.Style.Position == PositionType.Fixed && anchorId is { Length: > 0 } && byId.TryGetValue(anchorId, out var anchor))
                {
                    var (ax, ay, aw, ah) = AbsoluteBox(anchor);
                    var placement = child.Element?.GetAttribute("data-cupri-placement") ?? "bottom";
                    var gap = 4f;
                    float x, y;
                    switch (placement)
                    {
                        case "top": x = ax; y = ay - child.Height - gap; break;
                        case "right": x = ax + aw + gap; y = ay; break;
                        case "left": x = ax - child.Width - gap; y = ay; break;
                        default: x = ax; y = ay + ah + gap; break; // bottom
                    }
                    // Flip if it would overflow, then clamp to the viewport.
                    if (placement == "bottom" && y + child.Height > vh - gap) y = ay - child.Height - gap;
                    if (placement == "top" && y < gap) y = ay + ah + gap;
                    child.X = Math.Clamp(x, gap, MathF.Max(gap, vw - child.Width - gap));
                    child.Y = Math.Clamp(y, gap, MathF.Max(gap, vh - child.Height - gap));
                }
                Walk(child);
            }
        }
    }

    private static void IndexIds(RenderNode n, Dictionary<string, RenderNode> byId)
    {
        var id = n.Element?.GetAttribute("id");
        if (id is { Length: > 0 }) byId[id] = n;
        foreach (var c in n.Children) IndexIds(c, byId);
    }

    private static (float X, float Y, float W, float H) AbsoluteBox(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X; y += n.Y;
            // A scrolled ancestor shifts this node's on-screen position (matching the painter/hit-test), so
            // an anchored popup lands over the anchor's *painted* spot — not where it would sit unscrolled.
            if (n.Parent is { IsScrollable: true } sp) y -= Math.Clamp(sp.ScrollY, 0, sp.MaxScrollY);
        }
        return (x, y, node.Width, node.Height);
    }

    /// <summary>Position position:fixed nodes against the viewport and lift them to the top layer.</summary>
    private void LayoutFixedNodes(RenderNode node, float vw, float vh)
    {
        foreach (var child in node.Children)
        {
            if (child.Style.Display == DisplayType.None) continue;
            if (child.Style.Position == PositionType.Fixed)
            {
                var s = child.Style;
                // Auto-width overlays (popups/tooltips) shrink to their content, not the viewport.
                float? forceW = s.Width.IsAuto ? MathF.Max(0, MaxContentWidth(child) - PadBorderX(s)) : null;
                LayoutNode(child, vw, vh, forceW);
                child.X = s.Left.IsDefinite ? s.Left.Resolve(vw)
                    : s.Right.IsDefinite ? vw - s.Right.Resolve(vw) - child.Width
                    : (vw - child.Width) / 2f;         // no horizontal inset ⇒ centre
                child.Y = s.Top.IsDefinite ? s.Top.Resolve(vh)
                    : s.Bottom.IsDefinite ? vh - s.Bottom.Resolve(vh) - child.Height
                    : (vh - child.Height) / 2f;         // no vertical inset ⇒ centre
                // Keep a context menu fully on-screen (it opens at the pointer, which may be near an edge).
                if (child.Element?.HasAttribute("data-ctx-clamp") == true)
                {
                    child.X = Math.Clamp(child.X, 4f, MathF.Max(4f, vw - child.Width - 4f));
                    child.Y = Math.Clamp(child.Y, 4f, MathF.Max(4f, vh - child.Height - 4f));
                }
                child.IsTopLayer = true;
            }
            LayoutFixedNodes(child, vw, vh);
        }
    }

    /// <summary>
    /// Lay out <paramref name="node"/> inside a containing block of content size
    /// (<paramref name="cbW"/>,<paramref name="cbH"/>). Optionally force the content
    /// size (used by flex to impose resolved item sizes). Returns the border-box size.
    /// </summary>
    private Size LayoutNode(RenderNode node, float cbW, float cbH, float? forceContentW = null, float? forceContentH = null)
    {
        var s = node.Style;
        node.LaidOut = true; // this node got a real layout pass this frame (see CaptureScroll)

        node.MarginTop = s.Margin.Top.Resolve(cbW);
        node.MarginRight = s.Margin.Right.Resolve(cbW);
        node.MarginBottom = s.Margin.Bottom.Resolve(cbW);
        node.MarginLeft = s.Margin.Left.Resolve(cbW);
        node.PadTop = s.Padding.Top.Resolve(cbW);
        node.PadRight = s.Padding.Right.Resolve(cbW);
        node.PadBottom = s.Padding.Bottom.Resolve(cbW);
        node.PadLeft = s.Padding.Left.Resolve(cbW);
        node.BorderTopW = s.BorderTop;
        node.BorderRightW = s.BorderRight;
        node.BorderBottomW = s.BorderBottom;
        node.BorderLeftW = s.BorderLeft;

        float contentW;
        if (node.ResizeW is { } rw && forceContentW is null) contentW = rw - node.HorizontalInsets; // user-dragged size
        else if (forceContentW is { } fw) contentW = fw;
        else if (s.Width.IsDefinite) contentW = s.Width.Resolve(cbW);
        else contentW = MathF.Max(0, cbW - node.MarginLeft - node.MarginRight - node.HorizontalInsets);
        contentW = ClampW(s, contentW, cbW);

        // Image / live surface: size from intrinsic pixels + CSS width/height, aspect-preserving (a
        // leaf with no flow children would otherwise be height 0). Resolves before the block/flex/
        // grid path. A live surface's natural size wins over its poster image once it is known.
        var intrinsic = node.SurfaceKey is { Length: > 0 } sk ? _surfaces?.Get(sk)?.NaturalSize : null;
        if (intrinsic is null && node.ImageSrc is { Length: > 0 } imgSrc) intrinsic = _images?.Size(imgSrc);
        if (intrinsic is { W: > 0, H: > 0 } px)
        {
            var aspect = (float)px.W / px.H;
            var wDef = s.Width.IsDefinite || forceContentW is not null || node.ResizeW is not null;
            var hDef = s.Height.IsDefinite || forceContentH is not null || node.ResizeH is not null;
            var hh = node.ResizeH is { } rh2 ? rh2 - node.VerticalInsets
                   : hDef ? (forceContentH ?? s.Height.Resolve(cbH))
                   : (wDef ? contentW / aspect : px.H);
            var ww = wDef ? contentW : (hDef ? hh * aspect : px.W);
            node.Width = ClampW(s, ww, cbW) + node.HorizontalInsets;
            node.Height = ClampH(s, hh, cbH) + node.VerticalInsets;
            node.ContentNaturalHeight = node.Height;
            return new Size(node.Width, node.Height);
        }

        float usedH;
        if (node.IsText)
            usedH = LayoutText(node, contentW).H;
        else if (s.IsFlexContainer)
        {
            var heightKnown = forceContentH.HasValue || s.Height.IsDefinite;
            var providedH = forceContentH ?? (s.Height.IsDefinite ? s.Height.Resolve(cbH) : 0f);
            usedH = LayoutFlex(node, contentW, providedH, heightKnown);
        }
        else if (s.IsGridContainer)
        {
            var providedH = forceContentH ?? (s.Height.IsDefinite ? s.Height.Resolve(cbH) : cbH);
            usedH = LayoutGrid(node, contentW, providedH);
        }
        else
            usedH = LayoutBlock(node, contentW, cbH);

        // Natural border-box height (content-sized, before any explicit/resize/transition constraint) —
        // the target a `transition: height` uses when the CSS height is `auto`.
        node.ContentNaturalHeight = usedH + node.VerticalInsets;

        float contentH;
        if (node.ResizeH is { } rh && forceContentH is null) contentH = rh - node.VerticalInsets; // user-dragged size
        else if (forceContentH is { } fh) contentH = fh;
        else if (s.Height.IsDefinite) contentH = s.Height.Resolve(cbH);
        else contentH = usedH;
        contentH = ClampH(s, contentH, cbH);

        node.Width = contentW + node.HorizontalInsets;
        node.Height = contentH + node.VerticalInsets;

        // Scroll: remember the full children extent (ScrollY is preserved across layouts).
        node.ScrollContentHeight = !node.IsText && s.Overflow == OverflowMode.Scroll ? usedH : 0f;

        // Absolutely-positioned children are placed against this node's content box.
        if (!node.IsText)
            LayoutAbsoluteChildren(node, contentW, contentH);

        return new Size(node.Width, node.Height);
    }

    // ---- block ---------------------------------------------------------------
    private float LayoutBlock(RenderNode node, float contentW, float cbH)
    {
        float cursorY = 0;
        var insetL = node.ContentLeftInset;
        var insetT = node.ContentTopInset;

        // Use the child list directly when nothing is out of flow (the common case) — only materialise a
        // filtered copy when there's actually something to skip. Runs for every block every frame.
        var kids = AllInFlow(node.Children) ? node.Children : Filtered(node.Children);

        var i = 0;
        while (i < kids.Count)
        {
            // A run of ≥2 consecutive inline-level children forms an inline formatting context (text +
            // inline/inline-block elements flow into wrapping lines). Everything else — a block child, or
            // a lone inline child — stacks as its own box (the original behaviour, untouched).
            if (IsInlineLevel(kids[i]))
            {
                var j = i + 1;
                while (j < kids.Count && IsInlineLevel(kids[j])) j++;
                if (j - i >= 2) { cursorY += LayoutInline(node, kids, i, j, contentW, cbH, cursorY); i = j; continue; }
            }
            var child = kids[i];
            LayoutNode(child, contentW, cbH);
            child.X = insetL + child.MarginLeft;
            child.Y = insetT + cursorY + child.MarginTop;
            if (child.Style.Position == PositionType.Relative) ApplyRelativeOffset(child, contentW, cbH);
            cursorY += child.MarginTop + child.Height + child.MarginBottom;
            i++;
        }
        return cursorY;
    }

    private static bool IsInlineLevel(RenderNode n) =>
        n.IsText || n.Style.Display is DisplayType.Inline or DisplayType.InlineBlock;

    // Inline formatting context: flow kids[start..end) (text + inline/inline-block) into wrapping line
    // boxes, starting at startY within the block's content box. Text/inline elements are positioned via
    // their fragments (their own X/Y are zeroed, so the fragment coords work through any nesting);
    // inline-blocks are placed as atomic boxes. Returns the total height used.
    private float LayoutInline(RenderNode block, List<RenderNode> kids, int start, int end,
        float contentW, float cbH, float startY)
    {
        var insetL = block.ContentLeftInset;
        var insetT = block.ContentTopInset;
        var align = block.Style.TextAlign;

        var toks = new List<InlineTok>();
        var boxes = new List<(RenderNode El, int Start, int End)>(); // inline elements that paint a bg/border
        var wsPending = false;
        for (var k = start; k < end; k++) CollectInline(kids[k], toks, boxes, ref wsPending, contentW, cbH);

        float penX = 0, penY = 0, lineH = 0;
        var lineItems = new List<InlineTok>();

        void Finish()
        {
            if (lineItems.Count == 0) return;
            var off = align switch
            {
                TextAlign.Center => MathF.Max(0, (contentW - penX) / 2f),
                TextAlign.Right => MathF.Max(0, contentW - penX),
                _ => 0f,
            };
            foreach (var p in lineItems)
            {
                p.AbsX = insetL + p.PlacedX + off;
                p.PlacedY = insetT + startY + penY;
                p.PlacedH = lineH;
                if (p.Box is { } box)
                {
                    box.X = p.AbsX + box.MarginLeft;
                    box.Y = insetT + startY + penY + (lineH - p.H) / 2f + box.MarginTop;
                }
                else
                    p.Owner!.Lines!.Add(new TextLine
                    {
                        Text = p.Text!, X = p.AbsX,
                        Y = insetT + startY + penY, Width = p.W, Height = lineH,
                    });
            }
            penY += lineH;
            penX = 0; lineH = 0;
            lineItems.Clear();
        }

        foreach (var tok in toks)
        {
            var sp = tok.SpaceBefore ? tok.SpaceW : 0f;
            var foot = tok.LeadPad + tok.W + tok.TrailPad; // token + the inline padding reserved around it
            if (penX > 0 && penX + sp + foot > contentW) Finish(); // doesn't fit → wrap
            if (penX > 0) penX += sp;                               // a leading space collapses at line start
            penX += tok.LeadPad;                                   // reserve an enclosing element's padding-left
            tok.PlacedX = penX;
            lineItems.Add(tok);
            penX += tok.W + tok.TrailPad;
            lineH = MathF.Max(lineH, tok.H);
        }
        Finish();

        // Turn each painting inline element's token range into one background box per line it spans.
        foreach (var (el, s0, e0) in boxes)
        {
            if (e0 <= s0) continue;
            var frags = new List<InlineRect>();
            var boxH = el.Style.FontSize * 1.15f + el.PadTop + el.PadBottom; // snug chip, centred in the line
            var i = s0;
            while (i < e0)
            {
                var y = toks[i].PlacedY;
                var left = toks[i].AbsX - (i == s0 ? toks[i].LeadPad : 0f); // left padding only on the first line
                var right = toks[i].AbsX + toks[i].W;
                var j = i;
                while (j < e0 && toks[j].PlacedY == y)
                {
                    right = toks[j].AbsX + toks[j].W + (j == e0 - 1 ? toks[j].TrailPad : 0f);
                    j++;
                }
                frags.Add(new InlineRect(left, y + (toks[i].PlacedH - boxH) / 2f, MathF.Max(0, right - left), boxH));
                i = j;
            }
            el.InlineFragments = frags;
        }
        return penY;
    }

    // Flatten an inline node into tokens (words / atomic inline-block boxes), preserving inter-run
    // whitespace via wsPending (seeded by WsBefore/WsAfter). Zeroes each text/inline node's box so its
    // fragments carry the position.
    private void CollectInline(RenderNode node, List<InlineTok> toks,
        List<(RenderNode El, int Start, int End)> boxes, ref bool wsPending, float contentW, float cbH)
    {
        if (node.IsText)
        {
            node.X = 0; node.Y = 0;
            node.Lines = new List<TextLine>();
            var st = node.Style;
            var sw = _fonts.MeasureText(st, " ");
            var lh = FontService.LineHeightPx(st);
            if (node.WsBefore) wsPending = true;
            var words = (node.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var w = 0; w < words.Length; w++)
            {
                toks.Add(new InlineTok
                {
                    Owner = node, Text = words[w], W = _fonts.MeasureText(st, words[w]),
                    H = lh, SpaceW = sw, SpaceBefore = w == 0 ? wsPending : true,
                });
                wsPending = false;
            }
            wsPending = node.WsAfter;
            return;
        }

        if (node.Style.Display == DisplayType.Inline)
        {
            node.X = 0; node.Y = 0; node.Width = 0; node.Height = 0; // passthrough; children carry the text
            node.InlineFragments = null;
            if (node.WsBefore) wsPending = true;

            // A passthrough inline element never runs LayoutNode, so resolve the box metrics we need to
            // paint a background/border here. Horizontal padding+border reserves flow space around the
            // element's content (via LeadPad/TrailPad on its first/last token).
            var st = node.Style;
            node.PadLeft = st.Padding.Left.Resolve(contentW); node.PadRight = st.Padding.Right.Resolve(contentW);
            node.PadTop = st.Padding.Top.Resolve(contentW); node.PadBottom = st.Padding.Bottom.Resolve(contentW);
            node.BorderLeftW = st.BorderLeft; node.BorderRightW = st.BorderRight;
            node.BorderTopW = st.BorderTop; node.BorderBottomW = st.BorderBottom;
            var paints = st.Background.Alpha > 0 || st.BackgroundGradient is not null
                || (st.BorderColor.Alpha > 0 && st.BorderStyle != BorderLineStyle.None
                    && node.BorderLeftW + node.BorderRightW + node.BorderTopW + node.BorderBottomW > 0);

            var s0 = toks.Count;
            foreach (var c in node.Children)
                if (c.Style.Display != DisplayType.None && c.Style.Position is not (PositionType.Absolute or PositionType.Fixed))
                    CollectInline(c, toks, boxes, ref wsPending, contentW, cbH);
            if (paints && toks.Count > s0)
            {
                toks[s0].LeadPad += node.BorderLeftW + node.PadLeft;
                toks[^1].TrailPad += node.PadRight + node.BorderRightW;
                boxes.Add((node, s0, toks.Count));
            }
            if (node.WsAfter) wsPending = true;
            return;
        }

        // inline-block (or a stray block-level node in the run): an atomic box on the line, shrunk to its
        // content width when it has no explicit width (so a chip is chip-sized, not full width).
        if (node.WsBefore) wsPending = true;
        float? forceW = node.Style.Width.IsAuto
            ? MathF.Min(contentW, MathF.Max(0, MaxContentWidth(node) - PadBorderX(node.Style)))
            : null;
        LayoutNode(node, contentW, cbH, forceW);
        toks.Add(new InlineTok
        {
            Box = node, W = node.Width + node.MarginLeft + node.MarginRight,
            H = node.Height + node.MarginTop + node.MarginBottom,
            SpaceW = _fonts.MeasureText(node.Style, " "), SpaceBefore = wsPending,
        });
        wsPending = node.WsAfter;
    }

    private sealed class InlineTok
    {
        public RenderNode? Owner;   // text node this word belongs to (null for a box)
        public string? Text;        // the word (null for a box)
        public RenderNode? Box;     // an atomic inline-block (null for a word)
        public float W, H, SpaceW;  // token width, line height, space width for the boundary
        public bool SpaceBefore;    // a collapsible space precedes this token
        public float PlacedX;       // x within the content box once placed on a line
        public float LeadPad, TrailPad; // padding+border reserved before/after (from an enclosing inline element)
        public float AbsX, PlacedY, PlacedH; // content-box position + line height once placed (for inline bg boxes)
    }

    private static void ApplyRelativeOffset(RenderNode n, float cbW, float cbH)
    {
        if (n.Style.Left.IsDefinite) n.X += n.Style.Left.Resolve(cbW);
        else if (n.Style.Right.IsDefinite) n.X -= n.Style.Right.Resolve(cbW);
        if (n.Style.Top.IsDefinite) n.Y += n.Style.Top.Resolve(cbH);
        else if (n.Style.Bottom.IsDefinite) n.Y -= n.Style.Bottom.Resolve(cbH);
    }

    /// <summary>Lay out absolutely-positioned children against this node's content box.</summary>
    private void LayoutAbsoluteChildren(RenderNode node, float contentW, float contentH)
    {
        foreach (var child in node.Children)
        {
            if (child.Style.Display == DisplayType.None || child.Style.Position != PositionType.Absolute) continue;
            LayoutNode(child, contentW, contentH);

            float x = child.Style.Left.IsDefinite ? child.Style.Left.Resolve(contentW)
                : child.Style.Right.IsDefinite ? contentW - child.Style.Right.Resolve(contentW) - child.Width
                : 0f;
            float y = child.Style.Top.IsDefinite ? child.Style.Top.Resolve(contentH)
                : child.Style.Bottom.IsDefinite ? contentH - child.Style.Bottom.Resolve(contentH) - child.Height
                : 0f;

            child.X = node.ContentLeftInset + x;
            child.Y = node.ContentTopInset + y;
        }
    }

    // ---- flex (multi-line / wrap) -------------------------------------------
    // contentH is the container's content height; heightKnown says whether it is
    // definite/forced (stretch target) vs shrink-to-content.
    private float LayoutFlex(RenderNode node, float contentW, float contentH, bool heightKnown)
    {
        var s = node.Style;
        var horizontal = s.MainIsHorizontal;

        var mainKnown = horizontal || heightKnown; // width always fills in M1..M3
        var crossKnown = horizontal ? heightKnown : true;
        var crossContainer = horizontal ? contentH : contentW;
        var mainForBasis = horizontal ? contentW : (heightKnown ? contentH : 0f);

        // Flex items: skip absolutely-positioned children (out of flow). Use the list directly when none
        // are skipped (common) — avoids materialising a copy every frame for every flex container.
        var items = FlexItems(node.Children);
        var n = items.Count;
        if (n == 0) return 0;

        var baseMain = new float[n];
        var naturalCross = new float[n];
        var mainMargin = new float[n];
        var crossMargin = new float[n];

        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            var size0 = LayoutNode(item, contentW, heightKnown ? contentH : 0f);
            mainMargin[i] = horizontal ? item.MarginLeft + item.MarginRight : item.MarginTop + item.MarginBottom;
            crossMargin[i] = horizontal ? item.MarginTop + item.MarginBottom : item.MarginLeft + item.MarginRight;
            if (item.Style.FlexBasis.IsDefinite)
                baseMain[i] = item.Style.FlexBasis.Resolve(mainForBasis) + (horizontal ? item.HorizontalInsets : item.VerticalInsets);
            else if (horizontal)
                // Auto width in a row → shrink-to-fit (max-content), not fill-the-container.
                baseMain[i] = item.Style.Width.IsDefinite ? size0.W : MaxContentWidth(item);
            else
                baseMain[i] = size0.H;
            naturalCross[i] = horizontal ? size0.H : size0.W;
        }

        var gap = horizontal ? s.ColumnGap : s.RowGap;
        var crossGap = horizontal ? s.RowGap : s.ColumnGap;
        var mainSize = mainKnown ? (horizontal ? contentW : contentH) : SumOuter(baseMain, mainMargin, gap, 0, n);

        // Partition items into lines.
        var lines = new List<(int start, int end)>();
        if (s.FlexWrap == FlexWrapMode.Wrap && mainKnown)
        {
            var lineStart = 0;
            var run = 0f;
            for (var i = 0; i < n; i++)
            {
                var outer = baseMain[i] + mainMargin[i];
                var withGap = i > lineStart ? gap + outer : outer;
                if (i > lineStart && run + withGap > mainSize + 0.5f)
                {
                    lines.Add((lineStart, i));
                    lineStart = i;
                    run = outer;
                }
                else run += withGap;
            }
            lines.Add((lineStart, n));
        }
        else lines.Add((0, n));

        var insetL = node.ContentLeftInset;
        var insetT = node.ContentTopInset;
        var crossCursor = 0f;
        var maxMainExtent = 0f;
        var single = lines.Count == 1;

        foreach (var (start, end) in lines)
        {
            var count = end - start;
            var lineSum = SumOuter(baseMain, mainMargin, gap, start, end);
            var lineMainSize = mainKnown ? mainSize : lineSum;
            var free = lineMainSize - lineSum;

            var finalMain = ResolveFlexibleRange(items, baseMain, free, start, end);

            // Re-measure each item at its resolved MAIN size with a content-driven cross, so an item that
            // re-wraps at its reduced width reports its true (taller) cross. The first pass measured the
            // cross at the full container width, which underflows a shrunk, wrapping item — e.g. a card body
            // (flex:1) whose text wraps to a second line only once the grip has taken its share of the row.
            // A stretched item on a definite-cross line is skipped (it's sized by the stretch pass below,
            // not by its content), so the common stretch container keeps the same number of layout passes.
            var contentCross = !(crossKnown && single);      // the line's cross comes from the items' cross sizes
            var stretch = s.AlignItems == AlignItems.Stretch;
            for (var i = start; i < end; i++)
            {
                var item = items[i];
                var itemStretch = stretch && (horizontal ? item.Style.Height.IsAuto : item.Style.Width.IsAuto);
                if (!contentCross && itemStretch) continue;
                var main = MathF.Max(0, finalMain[i - start] - (horizontal ? item.HorizontalInsets : item.VerticalInsets));
                if (horizontal) LayoutNode(item, contentW, contentH, main, null);
                else            LayoutNode(item, contentW, contentH, null, main);
                naturalCross[i] = horizontal ? item.Height : item.Width;
            }

            var lineCross = contentCross ? 0f : crossContainer;
            if (contentCross)
                for (var i = start; i < end; i++) lineCross = MathF.Max(lineCross, naturalCross[i] + crossMargin[i]);

            // Stretch auto-cross items up to the line's cross size.
            if (stretch)
                for (var i = start; i < end; i++)
                {
                    var item = items[i];
                    if (!(horizontal ? item.Style.Height.IsAuto : item.Style.Width.IsAuto)) continue;
                    var cross = MathF.Max(0, lineCross - crossMargin[i] - (horizontal ? item.VerticalInsets : item.HorizontalInsets));
                    var main = MathF.Max(0, finalMain[i - start] - (horizontal ? item.HorizontalInsets : item.VerticalInsets));
                    if (horizontal) LayoutNode(item, contentW, contentH, main, cross);
                    else            LayoutNode(item, contentW, contentH, cross, main);
                }

            var totalMain = gap * MathF.Max(0, count - 1);
            for (var i = start; i < end; i++) totalMain += finalMain[i - start] + mainMargin[i];
            var (lineStartPos, between) = Justify(s.JustifyContent, lineMainSize - totalMain, gap, count);

            var cursor = lineStartPos;
            for (var i = start; i < end; i++)
            {
                var item = items[i];
                var itemCrossOuter = (horizontal ? item.Height : item.Width) + crossMargin[i];
                var crossPos = s.AlignItems switch
                {
                    AlignItems.Center => (lineCross - itemCrossOuter) / 2f,
                    AlignItems.FlexEnd => lineCross - itemCrossOuter,
                    _ => 0f,
                };

                if (horizontal)
                {
                    item.X = insetL + cursor + item.MarginLeft;
                    item.Y = insetT + crossCursor + crossPos + item.MarginTop;
                }
                else
                {
                    item.Y = insetT + cursor + item.MarginTop;
                    item.X = insetL + crossCursor + crossPos + item.MarginLeft;
                }
                if (item.Style.Position == PositionType.Relative) ApplyRelativeOffset(item, contentW, contentH);
                cursor += finalMain[i - start] + mainMargin[i] + between;
            }

            maxMainExtent = MathF.Max(maxMainExtent, totalMain);
            crossCursor += lineCross + crossGap;
        }

        crossCursor -= crossGap; // no trailing gap after the last line
        return horizontal ? crossCursor : maxMainExtent;
    }

    private static float SumOuter(float[] baseMain, float[] margin, float gap, int start, int end)
    {
        var sum = gap * MathF.Max(0, end - start - 1);
        for (var i = start; i < end; i++) sum += baseMain[i] + margin[i];
        return sum;
    }

    private static float[] ResolveFlexibleRange(List<RenderNode> items, float[] baseMain, float free, int start, int end)
    {
        var count = end - start;
        var result = new float[count];
        for (var i = 0; i < count; i++) result[i] = baseMain[start + i];
        if (MathF.Abs(free) < 0.01f) return result;

        if (free > 0)
        {
            float totalGrow = 0;
            for (var i = start; i < end; i++) totalGrow += GrowOf(items[i]);
            if (totalGrow > 0)
                for (var i = 0; i < count; i++) result[i] = baseMain[start + i] + free * GrowOf(items[start + i]) / totalGrow;
        }
        else
        {
            float scaled = 0;
            for (var i = start; i < end; i++) scaled += items[i].Style.FlexShrink * baseMain[i];
            if (scaled > 0)
                for (var i = 0; i < count; i++)
                    result[i] = MathF.Max(0, baseMain[start + i] + free * (items[start + i].Style.FlexShrink * baseMain[start + i]) / scaled);
        }
        return result;
    }

    // ---- grid ----------------------------------------------------------------
    // Subset: explicit columns (px/%/fr/auto/repeat), row-major auto-placement with
    // column/row spans, explicit start lines and named grid lines, gap, content-sized
    // (or grid-auto-rows) rows, items stretched to their cell.
    private float LayoutGrid(RenderNode node, float contentW, float contentH)
    {
        var s = node.Style;
        var items = InFlowChildren(node);
        var templates = s.GridTemplateColumns ?? new List<TrackSize> { new(TrackKind.Fraction, 1) };
        var nCols = Math.Max(1, templates.Count);

        var colGap = s.ColumnGap;
        var rowGap = s.RowGap;
        var colWidths = ResolveTracks(templates, contentW, colGap);

        var colX = new float[nCols];
        var acc = 0f;
        for (var c = 0; c < nCols; c++) { colX[c] = acc; acc += colWidths[c] + colGap; }

        // --- auto-placement (row-major) with column + row spans ---
        var occupied = new List<bool[]>();
        void Ensure(int r) { while (occupied.Count <= r) occupied.Add(new bool[nCols]); }
        bool Free(int r, int rowSpan, int c0, int span)
        {
            if (c0 < 0 || c0 + span > nCols) return false;
            for (var rr = r; rr < r + rowSpan; rr++)
            {
                Ensure(rr);
                for (var c = c0; c < c0 + span; c++) if (occupied[rr][c]) return false;
            }
            return true;
        }
        void Occupy(int r, int rowSpan, int c0, int span)
        {
            for (var rr = r; rr < r + rowSpan; rr++) { Ensure(rr); for (var c = c0; c < c0 + span; c++) occupied[rr][c] = true; }
        }

        var placedRow = new int[items.Count];
        var placedCol = new int[items.Count];
        var placedSpan = new int[items.Count];
        var placedRowSpan = new int[items.Count];
        int cursorRow = 0, cursorCol = 0;

        for (var i = 0; i < items.Count; i++)
        {
            var gc = items[i].Style.GridColumn;
            var gr = items[i].Style.GridRow;
            // Resolve named grid lines against the container's template line names.
            var colStart = ResolveLine(gc, s.GridColumnLines);
            var rowStart = ResolveLine(gr, s.GridRowLines);
            var span = Math.Min(ResolveSpan(gc, colStart, s.GridColumnLines), nCols);
            var rowSpan = Math.Max(1, ResolveSpan(gr, rowStart, s.GridRowLines));

            int r, c;
            if (colStart is { } startLine)
            {
                c = Math.Clamp(startLine - 1, 0, nCols - span);
                r = rowStart is { } rowLine ? rowLine - 1 : cursorRow;
                while (!Free(r, rowSpan, c, span)) r++;
            }
            else
            {
                r = cursorRow; c = cursorCol;
                while (!Free(r, rowSpan, c, span)) { c++; if (c + span > nCols) { c = 0; r++; } }
            }

            Occupy(r, rowSpan, c, span);
            placedRow[i] = r; placedCol[i] = c; placedSpan[i] = span; placedRowSpan[i] = rowSpan;
            cursorRow = r; cursorCol = c + span;
            if (cursorCol >= nCols) { cursorCol = 0; cursorRow = r + 1; }
        }

        var nRows = occupied.Count;

        // --- size items to their cell width, measure natural heights per (single) row ---
        var rowHeights = new float[Math.Max(1, nRows)];
        var natural = new float[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var cellW = SpanWidth(colWidths, colGap, placedCol[i], placedSpan[i]);
            natural[i] = LayoutNode(item, contentW, contentH, MathF.Max(0, cellW - item.HorizontalInsets)).H;
            if (placedRowSpan[i] == 1)
                rowHeights[placedRow[i]] = MathF.Max(rowHeights[placedRow[i]], natural[i]);
        }

        // Explicit row tracks / grid-auto-rows override content heights.
        for (var r = 0; r < nRows; r++)
        {
            if (s.GridTemplateRows is { } rows && r < rows.Count && rows[r].Kind != TrackKind.Fraction)
                rowHeights[r] = ResolveTrack(rows[r], contentH);
            else if (s.GridAutoRows is { } auto && auto.Kind != TrackKind.Fraction)
                rowHeights[r] = ResolveTrack(auto, contentH);
        }

        // Row-spanning items: grow their last row if the span can't contain them.
        for (var i = 0; i < items.Count; i++)
        {
            if (placedRowSpan[i] == 1) continue;
            var have = SpanHeight(rowHeights, rowGap, placedRow[i], placedRowSpan[i]);
            var lastRow = placedRow[i] + placedRowSpan[i] - 1;
            if (natural[i] > have) rowHeights[lastRow] += natural[i] - have;
        }

        var rowY = new float[Math.Max(1, nRows)];
        acc = 0f;
        for (var r = 0; r < nRows; r++) { rowY[r] = acc; acc += rowHeights[r] + rowGap; }

        // --- position each item within its cell (align-items / justify-items) ---
        var insetL = node.ContentLeftInset;
        var insetT = node.ContentTopInset;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var cellW = SpanWidth(colWidths, colGap, placedCol[i], placedSpan[i]);
            var cellH = SpanHeight(rowHeights, rowGap, placedRow[i], placedRowSpan[i]);

            var stretchX = s.JustifyItems == AlignItems.Stretch;
            var stretchY = s.AlignItems == AlignItems.Stretch;
            var forceW = stretchX ? cellW - item.HorizontalInsets : (float?)null;
            var forceH = stretchY ? cellH - item.VerticalInsets : (float?)null;
            var size = LayoutNode(item, contentW, contentH,
                forceW is { } fw ? MathF.Max(0, fw) : null, forceH is { } fh ? MathF.Max(0, fh) : null);

            var offX = AlignOffset(s.JustifyItems, cellW - size.W);
            var offY = AlignOffset(s.AlignItems, cellH - size.H);
            item.X = insetL + colX[placedCol[i]] + offX + item.MarginLeft;
            item.Y = insetT + rowY[placedRow[i]] + offY + item.MarginTop;
        }

        var total = 0f;
        for (var r = 0; r < nRows; r++) total += rowHeights[r];
        total += rowGap * MathF.Max(0, nRows - 1);
        return total;
    }

    // Resolve a placement's start line: a named line (via the container's line-name map) else the
    // numeric start (both 1-based).
    private static int? ResolveLine(GridPlacement p, Dictionary<string, int>? names)
        => p.StartName is { } n && names is not null && names.TryGetValue(n, out var li) ? li : p.Start;

    // Resolve a placement's span: from a named end line (end − start) when both resolve, else the span.
    private static int ResolveSpan(GridPlacement p, int? start, Dictionary<string, int>? names)
    {
        if (p.EndName is { } en && names is not null && names.TryGetValue(en, out var el) && start is { } st)
            return Math.Max(1, el - st);
        return p.Span;
    }

    private static float[] ResolveTracks(List<TrackSize> tracks, float available, float gap)
    {
        var n = tracks.Count;
        var widths = new float[n];
        var innerAvail = available - gap * MathF.Max(0, n - 1);

        float fixedSum = 0, frSum = 0;
        foreach (var t in tracks)
        {
            switch (t.Kind)
            {
                case TrackKind.Px: fixedSum += t.Value; break;
                case TrackKind.Percent: fixedSum += t.Value / 100f * available; break;
                default: frSum += t.Kind == TrackKind.Fraction ? t.Value : 1f; break; // auto ≈ 1fr (M-grid)
            }
        }
        var frUnit = frSum > 0 ? MathF.Max(0, innerAvail - fixedSum) / frSum : 0f;

        for (var i = 0; i < n; i++)
        {
            var t = tracks[i];
            widths[i] = t.Kind switch
            {
                TrackKind.Px => t.Value,
                TrackKind.Percent => t.Value / 100f * available,
                TrackKind.Fraction => MathF.Max(t.Value * frUnit, t.MinPx), // minmax floor
                _ => MathF.Max(frUnit, t.MinPx), // auto
            };
        }
        return widths;
    }

    private static float ResolveTrack(TrackSize t, float basis) => t.Kind switch
    {
        TrackKind.Px => t.Value,
        TrackKind.Percent => t.Value / 100f * basis,
        _ => t.Value,
    };

    private static float SpanWidth(float[] colWidths, float gap, int col, int span)
    {
        float w = 0;
        for (var c = col; c < col + span && c < colWidths.Length; c++) w += colWidths[c];
        return w + gap * MathF.Max(0, span - 1);
    }

    private static float SpanHeight(float[] rowHeights, float gap, int row, int span)
    {
        float h = 0;
        for (var r = row; r < row + span && r < rowHeights.Length; r++) h += rowHeights[r];
        return h + gap * MathF.Max(0, span - 1);
    }

    private static float AlignOffset(AlignItems align, float free) => align switch
    {
        AlignItems.Center => free / 2f,
        AlignItems.FlexEnd => free,
        _ => 0f, // FlexStart / Stretch
    };

    private static (float start, float between) Justify(JustifyContent j, float leftover, float gap, int n)
    {
        switch (j)
        {
            case JustifyContent.Center: return (leftover / 2f, gap);
            case JustifyContent.FlexEnd: return (leftover, gap);
            case JustifyContent.SpaceBetween: return (0, gap + (n > 1 ? leftover / (n - 1) : 0));
            case JustifyContent.SpaceAround: { var u = leftover / n; return (u / 2f, gap + u); }
            case JustifyContent.SpaceEvenly: { var u = leftover / (n + 1); return (u, gap + u); }
            default: return (0, gap); // FlexStart
        }
    }

    // ---- text ----------------------------------------------------------------
    private Size LayoutText(RenderNode node, float maxWidth)
    {
        var s = node.Style;
        var lh = FontService.LineHeightPx(s);
        var text = node.Text ?? "";

        // Reuse the wrapped lines when nothing that affects them changed (the common case each frame during
        // an animation that isn't resizing this text) — skips the split/measure/TextLine allocations.
        var key = new TextLayoutKey(text, maxWidth, s.FontSize, s.FontWeight, s.FontFamily, lh, s.WhiteSpace, s.FontStyle);
        if (node.Lines is not null && node.TextKey == key) return new Size(node.TextW, node.TextH);

        var lines = new List<TextLine>();

        // white-space:nowrap — one line, no wrapping (it overflows and is clipped by the field). Report
        // a width clamped to the box so the container doesn't grow unbounded; the line keeps its true
        // width for caret/selection/horizontal-scroll.
        if (s.WhiteSpace == WhiteSpaceMode.NoWrap)
        {
            var full = text.Length == 0 ? 0 : _fonts.MeasureText(s, text);
            node.Lines = [new TextLine { Text = text, X = 0, Y = 0, Width = full, Height = lh }];
            node.TextKey = key; node.TextW = MathF.Min(full, maxWidth); node.TextH = lh;
            return new Size(node.TextW, node.TextH);
        }

        var sb = new StringBuilder();
        var lineW = 0f;
        var spaceW = _fonts.MeasureText(s, " ");
        var longest = 0f;

        void Flush()
        {
            var t = sb.ToString();
            var w = t.Length == 0 ? 0 : _fonts.MeasureText(s, t);
            lines.Add(new TextLine { Text = t, X = 0, Y = lines.Count * lh, Width = w, Height = lh });
            longest = MathF.Max(longest, w);
            sb.Clear();
            lineW = 0;
        }

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ww = _fonts.MeasureText(s, word);
            var prospective = sb.Length == 0 ? ww : lineW + spaceW + ww;
            if (sb.Length > 0 && prospective > maxWidth) Flush();
            if (sb.Length > 0) { sb.Append(' '); lineW += spaceW; }
            sb.Append(word);
            lineW += ww;
        }
        Flush();

        node.Lines = lines;
        node.TextKey = key; node.TextW = maxWidth; node.TextH = lines.Count * lh;
        return new Size(node.TextW, node.TextH);
    }

    // ---- intrinsic sizing ----------------------------------------------------
    /// <summary>Preferred (max-content) border-box width: the width the node wants if
    /// nothing forces it to wrap. Used to shrink-to-fit auto-width flex items.</summary>
    private float MaxContentWidth(RenderNode node)
    {
        var s = node.Style;
        if (node.IsText)
            return _fonts.MeasureText(s, node.Text ?? "");

        float baseW;
        if (s.Width.Unit == LengthUnit.Px)
            baseW = s.Width.Value + PadBorderX(s);
        else
        {
            // Iterate the in-flow children directly — MaxContentWidth recurses the whole subtree and runs
            // per auto-width item, so materialising a filtered list at each node was the layout pass's
            // dominant allocation.
            float inner = 0;
            if (s.IsFlexContainer && s.MainIsHorizontal)
            {
                var first = true;
                foreach (var c in node.Children)
                {
                    if (!IsInFlow(c)) continue;
                    if (!first) inner += s.ColumnGap;
                    inner += MaxContentWidth(c) + MarginX(c.Style);
                    first = false;
                }
            }
            else
            {
                foreach (var c in node.Children)
                    if (IsInFlow(c)) inner = MathF.Max(inner, MaxContentWidth(c) + MarginX(c.Style));
            }
            baseW = PadBorderX(s) + inner;
        }

        // Respect the node's own min/max-width (content-box floors/ceilings) so an auto-width control
        // that only sets min-width (e.g. a picker trigger) reports its true footprint to flex/intrinsic
        // sizing — otherwise it under-reports and the next flex item overlaps it.
        if (s.MinWidth.IsDefinite) baseW = MathF.Max(baseW, s.MinWidth.Resolve(0) + PadBorderX(s));
        if (s.MaxWidth.IsDefinite) baseW = MathF.Min(baseW, s.MaxWidth.Resolve(0) + PadBorderX(s));
        return baseW;
    }

    // A split pane's divider drag overrides a panel's flex-grow (its share of the container).
    private static float GrowOf(RenderNode n) => n.SplitGrow ?? n.Style.FlexGrow;

    private static bool IsInFlow(RenderNode c) =>
        c.Style.Display != DisplayType.None && c.Style.Position is not (PositionType.Absolute or PositionType.Fixed);

    private static bool AllInFlow(List<RenderNode> kids)
    {
        foreach (var c in kids) if (!IsInFlow(c)) return false;
        return true;
    }
    private static List<RenderNode> Filtered(List<RenderNode> kids)
    {
        var outp = new List<RenderNode>(kids.Count);
        foreach (var c in kids) if (IsInFlow(c)) outp.Add(c);
        return outp;
    }

    private static List<RenderNode> InFlowChildren(RenderNode node) =>
        node.Children.Where(IsInFlow).ToList();

    // Out-of-flow children (display:none, position:absolute *and* fixed) don't take a slot in the flex
    // line — matching IsInFlow and block layout. A fixed popup (menu/tooltip/context menu) is sized and
    // placed by LayoutFixedNodes, so counting it here only reserved a phantom slot that shoved its
    // siblings (e.g. centred text jumping aside when a context menu opened over its region). Reuse the
    // child list when nothing is skipped.
    private static List<RenderNode> FlexItems(List<RenderNode> kids)
    {
        var skip = false;
        foreach (var c in kids) if (!IsInFlow(c)) { skip = true; break; }
        if (!skip) return kids;
        var outp = new List<RenderNode>(kids.Count);
        foreach (var c in kids) if (IsInFlow(c)) outp.Add(c);
        return outp;
    }

    private static float PadBorderX(ComputedStyle s) =>
        s.Padding.Left.Resolve(0) + s.Padding.Right.Resolve(0) + s.BorderLeft + s.BorderRight;

    private static float MarginX(ComputedStyle s) => s.Margin.Left.Resolve(0) + s.Margin.Right.Resolve(0);

    // ---- clamps --------------------------------------------------------------
    private static float ClampW(ComputedStyle s, float v, float cb)
    {
        if (s.MinWidth.IsDefinite) v = MathF.Max(v, s.MinWidth.Resolve(cb));
        if (s.MaxWidth.IsDefinite) v = MathF.Min(v, s.MaxWidth.Resolve(cb));
        return MathF.Max(0, v);
    }

    private static float ClampH(ComputedStyle s, float v, float cb)
    {
        if (s.MinHeight.IsDefinite) v = MathF.Max(v, s.MinHeight.Resolve(cb));
        if (s.MaxHeight.IsDefinite) v = MathF.Min(v, s.MaxHeight.Resolve(cb));
        return MathF.Max(0, v);
    }
}
