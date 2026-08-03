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
    public LayoutEngine(FontService fonts) => _fonts = fonts;

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
        for (var n = node; n is not null; n = n.Parent) { x += n.X; y += n.Y; }
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
        if (forceContentW is { } fw) contentW = fw;
        else if (s.Width.IsDefinite) contentW = s.Width.Resolve(cbW);
        else contentW = MathF.Max(0, cbW - node.MarginLeft - node.MarginRight - node.HorizontalInsets);
        contentW = ClampW(s, contentW, cbW);

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

        float contentH;
        if (forceContentH is { } fh) contentH = fh;
        else if (s.Height.IsDefinite) contentH = s.Height.Resolve(cbH);
        else contentH = usedH;
        contentH = ClampH(s, contentH, cbH);

        node.Width = contentW + node.HorizontalInsets;
        node.Height = contentH + node.VerticalInsets;

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

        foreach (var child in node.Children)
        {
            if (child.Style.Display == DisplayType.None) continue;
            if (child.Style.Position is PositionType.Absolute or PositionType.Fixed) continue; // out of flow
            LayoutNode(child, contentW, cbH);
            child.X = insetL + child.MarginLeft;
            child.Y = insetT + cursorY + child.MarginTop;
            if (child.Style.Position == PositionType.Relative) ApplyRelativeOffset(child, contentW, cbH);
            cursorY += child.MarginTop + child.Height + child.MarginBottom;
        }
        return cursorY;
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

        // Flex items: skip absolutely-positioned children (out of flow).
        var items = node.Children
            .Where(c => c.Style.Display != DisplayType.None && c.Style.Position != PositionType.Absolute)
            .ToList();
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

            float lineCross;
            if (crossKnown && single) lineCross = crossContainer;
            else
            {
                lineCross = 0;
                for (var i = start; i < end; i++) lineCross = MathF.Max(lineCross, naturalCross[i] + crossMargin[i]);
            }

            // Re-layout each item at its resolved main (and stretched cross) size.
            for (var i = start; i < end; i++)
            {
                var item = items[i];
                var crossAuto = horizontal ? item.Style.Height.IsAuto : item.Style.Width.IsAuto;
                var crossBorder = (s.AlignItems == AlignItems.Stretch && crossAuto)
                    ? MathF.Max(0, lineCross - crossMargin[i])
                    : naturalCross[i];
                var fw = horizontal ? finalMain[i - start] - item.HorizontalInsets : crossBorder - item.HorizontalInsets;
                var fh = horizontal ? crossBorder - item.VerticalInsets : finalMain[i - start] - item.VerticalInsets;
                LayoutNode(item, contentW, contentH, MathF.Max(0, fw), MathF.Max(0, fh));
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
            for (var i = start; i < end; i++) totalGrow += items[i].Style.FlexGrow;
            if (totalGrow > 0)
                for (var i = 0; i < count; i++) result[i] = baseMain[start + i] + free * items[start + i].Style.FlexGrow / totalGrow;
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
    // column spans and explicit start lines, gap, content-sized (or grid-auto-rows)
    // rows, items stretched to their cell. rowSpan>1 and named lines are future work.
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
            var span = Math.Min(gc.Span, nCols);
            var rowSpan = Math.Max(1, gr.Span);

            int r, c;
            if (gc.Start is { } startLine)
            {
                c = Math.Clamp(startLine - 1, 0, nCols - span);
                r = gr.Start is { } rowLine ? rowLine - 1 : cursorRow;
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

        var lines = new List<TextLine>();
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
        return new Size(maxWidth, lines.Count * lh);
    }

    // ---- intrinsic sizing ----------------------------------------------------
    /// <summary>Preferred (max-content) border-box width: the width the node wants if
    /// nothing forces it to wrap. Used to shrink-to-fit auto-width flex items.</summary>
    private float MaxContentWidth(RenderNode node)
    {
        var s = node.Style;
        if (node.IsText)
            return _fonts.MeasureText(s, node.Text ?? "");

        if (s.Width.Unit == LengthUnit.Px)
            return s.Width.Value + PadBorderX(s);

        float inner = 0;
        if (s.IsFlexContainer && s.MainIsHorizontal)
        {
            var kids = InFlowChildren(node);
            for (var i = 0; i < kids.Count; i++)
            {
                inner += MaxContentWidth(kids[i]) + MarginX(kids[i].Style);
                if (i > 0) inner += s.ColumnGap;
            }
        }
        else
        {
            foreach (var c in InFlowChildren(node))
                inner = MathF.Max(inner, MaxContentWidth(c) + MarginX(c.Style));
        }
        return PadBorderX(s) + inner;
    }

    private static List<RenderNode> InFlowChildren(RenderNode node) =>
        node.Children.Where(c => c.Style.Display != DisplayType.None
            && c.Style.Position is not (PositionType.Absolute or PositionType.Fixed)).ToList();

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
