using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CupriFace.Binding;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Layout;
using CupriFace.Paint;
using CupriFace.Style;
using CupriFace.Text;
using SkiaSharp;

namespace CupriFace;

/// <summary>
/// The public entry point: loads HTML + CSS, applies data binding, resolves styles,
/// and renders to a Skia canvas. Ties together Layers 1–5 of DESIGN.md.
/// </summary>
public sealed partial class CupriDocument : IDisposable
{
    private readonly string _templateHtml;
    private readonly string? _css;
    private object? _model;
    private ComponentRegistry? _components;

    private readonly FontService _fonts;
    private readonly LayoutEngine _layout;
    private readonly Painter _painter;
    private readonly SkiaRasterizer _rasterizer;

    private IDocument? _dom;
    private RenderNode _root = null!;
    private List<CssRule> _rules = new();   // reused by ReStyle (hover/active without a full rebuild)
    private Dictionary<string, List<Keyframe>> _keyframes = new();
    private List<CssRule>? _cachedRules;    // parsed once (CSS is immutable) and reused every rebuild
    private Dictionary<string, List<Keyframe>>? _cachedKeyframes;
    private float _viewportWidth = 1024f;
    private bool _hasMedia;

    // Hover + drag + text-focus state
    private readonly List<IElement> _hoverChain = new();
    private string? _focusKey;  // the focused field's bound path (survives rebuilds)
    private bool _focusNumeric; // focused field is validated as a number
    private bool _focusMultiline; // focused field is a textarea (Enter inserts a newline)
    private double? _focusMin, _focusMax; // numeric field bounds (for validation/clamping)
    private string? _editBuffer; // raw text being edited (permissive); validated/committed on blur
    private int _caret;
    private int _selAnchor;      // selection anchor; selection is [min(anchor,caret), max]. anchor==caret ⇒ none.
    private bool _textDrag;      // a mouse drag is extending the text selection
    private int _kbIndex = -1;      // keyboard focus: index into the focusable list (-1 = none)
    private bool _focusVisible;     // show the focus ring? true after Tab, false after a mouse click
    private bool _dragging;
    private float _dragX0, _dragInnerW, _dragPad;
    private double _dragMin, _dragMax;
    private string? _dragPath;
    private bool _caretMoved;           // caret changed since last render → scroll it into view once
    private RenderNode? _scrollDrag;    // scrollable node whose scrollbar thumb is being dragged
    private float _scrollDragY0, _scrollDragScroll0;

    private CupriDocument(string html, string? css)
    {
        _templateHtml = html;
        _css = css;
        _fonts = new FontService();
        _layout = new LayoutEngine(_fonts);
        _painter = new Painter();
        _rasterizer = new SkiaRasterizer(_fonts);
    }

    public RenderNode Root => _root;

    /// <summary>Parse an HTML document and an optional external stylesheet.</summary>
    public static CupriDocument Load(string html, string? css = null)
    {
        var doc = new CupriDocument(html, css);
        doc.Rebuild();
        return doc;
    }

    /// <summary>Register a component library; custom elements expand during build (§10).</summary>
    public CupriDocument UseComponents(ComponentRegistry components)
    {
        _components = components;
        _cachedRules = null; // component CSS changes the rule set — rebuild the cache
        _cachedKeyframes = null;
        Rebuild();
        return this;
    }

    /// <summary>Bind a model; interpolations and <c>data-repeat</c> resolve against it.</summary>
    public CupriDocument Bind(object model)
    {
        _model = model;
        Rebuild();
        return this;
    }

    /// <summary>Re-apply bindings with the current model (call after model changes).</summary>
    public void Refresh() => Rebuild();

    /// <summary>Optional diagnostic hook: invoked with (phaseName, ms) for each Rebuild phase.</summary>
    public static Action<string, double>? ProfileHook;

    private void Rebuild()
    {
        var prof = ProfileHook;
        var t = prof is not null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        void Mark(string phase)
        {
            if (prof is null) return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            prof(phase, System.Diagnostics.Stopwatch.GetElapsedTime(t, now).TotalMilliseconds);
            t = now;
        }

        // A rebuild re-parses the DOM, so scroll offsets on the fresh tree would reset — carry them
        // over (keyed by structural path, since element identity isn't stable across re-parse).
        var scroll = CaptureScroll();

        _dom?.Dispose();
        var dom = new HtmlParser().ParseDocument(_templateHtml);
        Mark("parse-html");

        if (_model is not null)
            BindingEngine.Apply(dom, _model);
        Mark("bind");

        // Expand custom elements after binding so components see concrete attribute values.
        _components?.Expand(dom);
        Mark("expand-components");

        // Re-apply text focus across the rebuild (typing rebuilds the DOM each keystroke), and
        // paint the raw edit buffer (which may be invalid) over the bound value, flagging
        // data-invalid so the field can show a red border while the user is mid-edit.
        if (_focusKey is not null &&
            dom.QuerySelector($"[data-bind-value=\"{_focusKey}\"]") is { } focusEl)
        {
            focusEl.SetAttribute("data-focus", "");
            if (_editBuffer is not null)
            {
                // Only overwrite the value text when the buffer is non-empty: blanking a text
                // node removes it (no line box → the field collapses and its rounded corners
                // read as a pill). An empty buffer keeps the component's own content — e.g. the
                // placeholder for a text field stays visible until the user types.
                if (_editBuffer.Length > 0)
                {
                    var anchor = focusEl.QuerySelector("[data-caret-anchor]") ?? focusEl;
                    if (focusEl.HasAttribute("data-multiline"))
                        anchor.InnerHtml = Components.Controls.TextAreaComponent.RenderLines(_editBuffer);
                    else
                        anchor.TextContent = _editBuffer;
                }
                if (!BufferValid(_editBuffer)) focusEl.SetAttribute("data-invalid", "");
            }
        }

        Mark("focus");

        // CSS rules + @keyframes come from immutable sources (component CSS, author CSS, template
        // <style> tags), so parse them ONCE and reuse across rebuilds — a rebuild happens on every
        // click/keystroke, and re-parsing + re-allocating the rule set each time was pure waste.
        if (_cachedRules is null)
        {
            var rules = new List<CssRule>();
            if (_components is not null) rules.AddRange(CssParser.Parse(_components.AggregatedCss));
            rules.AddRange(CssParser.Parse(_css));
            foreach (var styleEl in dom.QuerySelectorAll("style"))
                rules.AddRange(CssParser.Parse(styleEl.TextContent));
            for (var i = 0; i < rules.Count; i++) rules[i].Order = i; // later stylesheets win ties

            var kf = Animation.Parse(_css);
            if (_components is not null)
                foreach (var (k, frames) in Animation.Parse(_components.AggregatedCss)) kf[k] = frames;
            foreach (var styleEl in dom.QuerySelectorAll("style"))
                foreach (var (k, frames) in Animation.Parse(styleEl.TextContent)) kf[k] = frames;

            _cachedRules = rules;
            _cachedKeyframes = kf;
            _hasMedia = rules.Exists(r => r.Media is not null);
        }
        _rules = _cachedRules;
        _keyframes = _cachedKeyframes!;
        Mark("parse-css");

        _hoverChain.Clear();
        _root = new StyleResolver(_rules, _viewportWidth).BuildTree(dom);
        RestoreScroll(scroll);
        Mark("style+tree");
        _hasActiveAnim = _keyframes.Count > 0 && AnyAnimated(_root);
        _dom = dom;
    }

    // Scroll-offset preservation across a rebuild, keyed by structural path (child-index chain from
    // root) since the DOM — and thus element identity — is re-parsed each rebuild.
    private Dictionary<string, (float ScrollY, bool AtBottom, bool FollowTail)>? CaptureScroll()
    {
        if (_root is null) return null;
        Dictionary<string, (float, bool, bool)>? map = null;
        void Walk(RenderNode n)
        {
            var tail = n.Element?.HasAttribute("data-follow-tail") == true;
            if (n.Style.Overflow == OverflowMode.Scroll && (n.ScrollY > 0.01f || tail))
                (map ??= new())[PathOf(n)] = (n.ScrollY, n.ScrollY >= n.MaxScrollY - 1f, tail);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        return map;
    }

    private void RestoreScroll(Dictionary<string, (float ScrollY, bool AtBottom, bool FollowTail)>? map)
    {
        if (map is null) return;
        void Walk(RenderNode n)
        {
            if (n.Style.Overflow == OverflowMode.Scroll && map.TryGetValue(PathOf(n), out var s))
                // float.MaxValue clamps to the NEW bottom at paint time → tail-follow.
                n.ScrollY = s.FollowTail && s.AtBottom ? float.MaxValue : s.ScrollY;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
    }

    private static string PathOf(RenderNode n)
    {
        var sb = new System.Text.StringBuilder();
        for (var cur = n; cur.Parent is { } p; cur = p) sb.Insert(0, "/" + p.Children.IndexOf(cur));
        return sb.ToString();
    }

    /// <summary>Advance @keyframes animations to the given elapsed time (paint-only).</summary>
    public bool Animate(double timeSeconds)
    {
        if (_keyframes.Count == 0) return false;
        Animation.Apply(_root, _keyframes, timeSeconds);
        return true;
    }

    public bool HasAnimations => _keyframes.Count > 0;

    /// <summary>True only if a *visible* node is currently animating (display:none subtrees are
    /// absent from the render tree). Lets a host render continuously only when it must, instead
    /// of every frame — critical for the CPU-rendered web host. Cached per rebuild (the animated
    /// set only changes when the tree does), so a host may poll it every frame for free.</summary>
    public bool HasActiveAnimations => _hasActiveAnim;
    private bool _hasActiveAnim;

    private static bool AnyAnimated(RenderNode n)
    {
        if (n.Style.AnimationName is not null && n.Style.AnimationDuration > 0) return true;
        foreach (var c in n.Children) if (AnyAnimated(c)) return true;
        return false;
    }

    /// <summary>Lay out at the given viewport size and paint onto <paramref name="canvas"/>.</summary>
    public void Render(SKCanvas canvas, float width, float height)
    {
        // @media depends on viewport width — re-resolve styles when it changes.
        if (_hasMedia && Math.Abs(width - _viewportWidth) > 0.5f)
        {
            _viewportWidth = width;
            Rebuild();
        }
        _layout.Layout(_root, width, height);
        ScrollCaretIntoView(); // after layout, before paint: keep the caret visible in a scrolled field

        var list = _painter.Build(_root);
        // Caret + selection are drawn outside the scrolled subtree, so clip them to the focused
        // field's scroll box — otherwise they'd draw over neighbours when the field is scrolled.
        var clip = CaretClipRect();
        if (clip is { } c) list.Add(new PushClip(c.X, c.Y, c.W, c.H, c.Radius));
        AppendSelection(list);
        AppendCaret(list);
        if (clip is not null) list.Add(new PopClip());
        AppendFocusRing(list);
        _rasterizer.Paint(canvas, list);
    }

    // If the caret moved (typing/nav, not wheel) and its field scrolls, nudge that container's
    // ScrollY so the caret's row sits inside the visible band.
    private void ScrollCaretIntoView()
    {
        if (!_caretMoved || _focusKey is null) return;
        _caretMoved = false;
        var field = FindFocused(_root);
        if (field is null) return;
        var sc = ScrollableContainer(field);
        if (sc is null) return;

        var anchor = FindCaretAnchor(field) ?? field;
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var caret = Math.Clamp(_caret, 0, value.Length);
        float rowY, rowH;
        if (field.Element?.HasAttribute("data-multiline") == true)
        {
            var rows = BuildTextRows(anchor, value);
            var t = RowForCaret(rows, caret);
            rowY = t.Y; rowH = t.Height;
        }
        else
        {
            var (_, py) = PaintedTopLeft(anchor);
            rowY = py + anchor.ContentTopInset; rowH = FontService.LineHeightPx(anchor.Style);
        }

        var (_, scY) = PaintedTopLeft(sc);
        var bandTop = scY + sc.ContentTopInset;
        var bandBottom = bandTop + sc.ContentBoxHeight;
        var newScroll = sc.ScrollY;
        if (rowY < bandTop) newScroll = sc.ScrollY + (rowY - bandTop);              // scroll up to reveal
        else if (rowY + rowH > bandBottom) newScroll = sc.ScrollY + (rowY + rowH - bandBottom); // down
        sc.ScrollY = Math.Clamp(newScroll, 0, sc.MaxScrollY);
    }

    // The focused field's scroll-container content box (painted), for clipping caret/selection; null if none.
    private (float X, float Y, float W, float H, float Radius)? CaretClipRect()
    {
        if (_focusKey is null) return null;
        var sc = ScrollableContainer(FindFocused(_root));
        if (sc is null) return null;
        var (sx, sy) = PaintedTopLeft(sc);
        return (sx + sc.ContentLeftInset, sy + sc.ContentTopInset,
                sc.Width - sc.HorizontalInsets, sc.Height - sc.VerticalInsets, 0f);
    }

    // Draw the text caret for the focused field, after the field's own text.
    private void AppendCaret(DisplayList list)
    {
        if (_focusKey is null) return;
        var field = FindFocused(_root);
        if (field is null) return;

        // Anchor the caret to the node that actually paints the value text (a component may
        // nest it in a padded span — e.g. cupri-number puts its padding on the text span, not
        // the field), so the caret lines up regardless of where the padding lives.
        var anchor = FindCaretAnchor(field) ?? field;
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var caret = Math.Clamp(_caret, 0, value.Length);
        var (px, py) = PaintedTopLeft(anchor);
        var lh = FontService.LineHeightPx(anchor.Style);
        var ch = anchor.Style.FontSize * 1.1f;

        // Place the caret at its real painted position. Multi-line (textarea) uses wrap-aware
        // visual rows; a single-line field is one row of text at the anchor's content origin.
        float cx, cy;
        if (field.Element?.HasAttribute("data-multiline") == true)
        {
            var rows = BuildTextRows(anchor, value);
            var target = RowForCaret(rows, caret);
            var col = Math.Clamp(caret - target.Start, 0, target.Text.Length);
            cx = target.X + _fonts.MeasureText(anchor.Style, target.Text[..col]);
            cy = target.Y + (target.Height - ch) / 2f;
        }
        else
        {
            cx = px + anchor.ContentLeftInset + _fonts.MeasureText(anchor.Style, value[..caret]);
            cy = py + anchor.ContentTopInset + (lh - ch) / 2f;
        }
        list.Add(new FillRect(cx, cy, 2f, ch, 0f, anchor.Style.Color));
    }

    // Draw the text selection highlight (behind the text) for the focused field.
    private void AppendSelection(DisplayList list)
    {
        if (_focusKey is null || _selAnchor == _caret) return;
        var field = FindFocused(_root);
        if (field is null) return;
        var anchor = FindCaretAnchor(field) ?? field;
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        int s = Math.Clamp(Math.Min(_selAnchor, _caret), 0, value.Length);
        int e = Math.Clamp(Math.Max(_selAnchor, _caret), 0, value.Length);
        if (s == e) return;

        var (px, py) = PaintedTopLeft(anchor);
        var lh = FontService.LineHeightPx(anchor.Style);
        var ch = anchor.Style.FontSize * 1.1f;
        var left0 = px + anchor.ContentLeftInset;
        var top0 = py + anchor.ContentTopInset;
        var sel = new SKColor(0x2F, 0x6F, 0xED, 0x40); // translucent selection blue

        if (field.Element?.HasAttribute("data-multiline") != true)
        {
            var x1 = left0 + _fonts.MeasureText(anchor.Style, value[..s]);
            var x2 = left0 + _fonts.MeasureText(anchor.Style, value[..e]);
            list.Add(new FillRect(x1, top0 + (lh - ch) / 2f, x2 - x1, ch, 0f, sel));
            return;
        }
        // Per visual row (wrap-aware) highlight for a textarea. A selected newline at a logical
        // line's end shows a small trailing sliver.
        foreach (var row in BuildTextRows(anchor, value))
        {
            int re = row.Start + row.Text.Length;
            if (e <= row.Start || s > re) continue;
            int ca = Math.Clamp(s - row.Start, 0, row.Text.Length);
            int cb = Math.Clamp(e - row.Start, 0, row.Text.Length);
            var x1 = row.X + _fonts.MeasureText(anchor.Style, row.Text[..ca]);
            var x2 = row.X + _fonts.MeasureText(anchor.Style, row.Text[..cb]);
            var w = (x2 - x1) + (row.NewlineAfter && e > re ? 6f : 0f); // selected trailing newline → sliver
            if (w > 0) list.Add(new FillRect(x1, row.Y + (row.Height - ch) / 2f, w, ch, 0f, sel));
        }
    }

    /// <summary>A visual (post-wrap) row of a field's text: the buffer range it covers
    /// (<c>[Start,End]</c>, contiguous so no caret position falls in a gap), its visible text, and
    /// the absolute top-left it is PAINTED at (matching the painter's <c>AbsoluteBox(textNode)+line.X/Y</c>),
    /// so caret/selection/hit-testing line up with wrapped text instead of a synthetic line grid.</summary>
    private readonly record struct TextRow(int Start, int End, string Text, float X, float Y, float Height, bool NewlineAfter);

    private static List<TextRow> BuildTextRows(RenderNode anchor, string value)
    {
        var rows = new List<TextRow>();
        var logical = value.Split('\n');
        // The textarea renders one <div class="cupri-ta-line"> per logical line, in order.
        var lineDivs = anchor.Children.Where(c => c.Element is not null).ToList();
        var lh = FontService.LineHeightPx(anchor.Style);
        var offset = 0;
        for (var i = 0; i < logical.Length; i++)
        {
            var lineText = logical[i];
            var newlineAfter = i < logical.Length - 1;
            var div = i < lineDivs.Count ? lineDivs[i] : null;
            var textNode = div?.Children.FirstOrDefault(c => c.IsText);

            if (lineText.Length > 0 && textNode?.Lines is { Count: > 0 } lines)
            {
                var (tx, ty) = PaintedTopLeft(textNode);
                // Each visual row's start column in the logical line (skips whitespace consumed at wraps).
                var cols = new int[lines.Count];
                var cursor = 0;
                for (var r = 0; r < lines.Count; r++)
                {
                    var col = lineText.IndexOf(lines[r].Text, Math.Min(cursor, lineText.Length), StringComparison.Ordinal);
                    cols[r] = col < 0 ? Math.Min(cursor, lineText.Length) : col;
                    cursor = cols[r] + lines[r].Text.Length;
                }
                for (var r = 0; r < lines.Count; r++)
                {
                    // Cover contiguously up to the next row's start (or the line's end for the last
                    // row) so a caret in consumed/trailing whitespace still maps here — no gaps, so the
                    // caret/scroll never falls through to the bottom row.
                    var end = offset + (r + 1 < lines.Count ? cols[r + 1] : lineText.Length);
                    rows.Add(new TextRow(offset + cols[r], end, lines[r].Text,
                        tx + lines[r].X, ty + lines[r].Y, lines[r].Height, r == lines.Count - 1 && newlineAfter));
                }
            }
            else
            {
                // Empty logical line (or no laid-out text): a zero-width row at the line box.
                var (dx, dy) = PaintedTopLeft(div ?? anchor);
                rows.Add(new TextRow(offset, offset, "",
                    dx + (div?.ContentLeftInset ?? 0f), dy + (div?.ContentTopInset ?? 0f), lh, newlineAfter));
            }
            offset += lineText.Length + 1;
        }
        return rows;
    }

    // The visual row a caret sits in: the row whose [Start,End] contains it, else the nearest by
    // offset (never the last row by default, which would jump the caret/scroll to the bottom).
    private static TextRow RowForCaret(List<TextRow> rows, int caret)
    {
        var best = rows[0];
        var bestDist = int.MaxValue;
        foreach (var r in rows)
        {
            var d = caret < r.Start ? r.Start - caret : caret > r.End ? caret - r.End : 0;
            if (d <= bestDist) { bestDist = d; best = r; } // <= keeps the later row on a boundary tie
        }
        return best;
    }

    // The caret index in the focused field's buffer nearest the point (x,y) — click-to-place-caret.
    private int CaretFromPoint(RenderNode field, RenderNode anchorNode, float x, float y)
    {
        var value = _editBuffer ?? "";
        if (value.Length == 0) return 0;

        // Multi-line: pick the visual (wrap-aware) row nearest y, then the nearest column in it.
        if (field.Element?.HasAttribute("data-multiline") == true)
        {
            var rows = BuildTextRows(anchorNode, value);
            var row = rows[0];
            var bestDy = float.MaxValue;
            foreach (var r in rows)
            {
                var dy = y < r.Y ? r.Y - y : y > r.Y + r.Height ? y - (r.Y + r.Height) : 0f;
                if (dy < bestDy) { bestDy = dy; row = r; }
            }
            return row.Start + NearestColumn(anchorNode.Style, row.Text, x - row.X);
        }

        var (bx, _) = PaintedTopLeft(anchorNode);
        return NearestColumn(anchorNode.Style, value, x - (bx + anchorNode.ContentLeftInset));
    }

    // Painted top-left of a node: like HitTesting.AbsoluteBox but subtracts each scrollable ancestor's
    // clamped scroll offset, so caret/selection line up with where the Painter actually draws the text
    // (the Painter shifts a scrollable node's children up by its ScrollY).
    private static (float X, float Y) PaintedTopLeft(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X; y += n.Y;
            if (n.Parent is { IsScrollable: true } p) y -= Math.Clamp(p.ScrollY, 0, p.MaxScrollY);
            if (n.IsTopLayer) break;
        }
        return (x, y);
    }

    // The nearest scrollable ancestor of a node (or the node itself), for clipping/scrolling; null if none.
    private static RenderNode? ScrollableContainer(RenderNode? node)
    {
        for (var n = node; n is not null; n = n.Parent) if (n.IsScrollable) return n;
        return null;
    }

    // The scrollbar thumb rect (painted), mirroring the Painter; null if not scrollable.
    private (float X, float Y, float W, float H)? ThumbRect(RenderNode n)
    {
        if (!n.IsScrollable) return null;
        var (ax, ay) = PaintedTopLeft(n);
        var boxH = n.ContentBoxHeight;
        var thumbH = MathF.Max(28f, boxH * boxH / n.ScrollContentHeight);
        var thumbY = ay + n.ContentTopInset + Math.Clamp(n.ScrollY, 0, n.MaxScrollY) / n.MaxScrollY * (boxH - thumbH);
        var thumbX = ax + n.Width - n.BorderRightW - 8f;
        return (thumbX, thumbY, 5f, thumbH);
    }

    // The column in <paramref name="text"/> whose x-offset is nearest <paramref name="localX"/>.
    private int NearestColumn(ComputedStyle style, string text, float localX)
    {
        var bestCol = 0;
        var bestDist = MathF.Abs(localX);
        for (var i = 1; i <= text.Length; i++)
        {
            var d = MathF.Abs(localX - _fonts.MeasureText(style, text[..i]));
            if (d < bestDist) { bestDist = d; bestCol = i; }
        }
        return bestCol;
    }

    // Draw a keyboard focus ring around the focused control (only after Tab — "focus-visible").
    private void AppendFocusRing(DisplayList list)
    {
        if (!_focusVisible || _kbIndex < 0) return;
        var f = Focusables();
        if (_kbIndex >= f.Count) return;
        var (x, y, w, h) = HitTesting.AbsoluteBox(f[_kbIndex]);
        const float t = 2f, pad = 2f;                 // ring thickness + gap outside the border box
        var (rx, ry, rw, rh) = (x - pad, y - pad, w + 2 * pad, h + 2 * pad);
        var c = new SKColor(0x2F, 0x6F, 0xED);        // accessible focus blue
        list.Add(new FillRect(rx, ry, rw, t, 0f, c));           // top
        list.Add(new FillRect(rx, ry + rh - t, rw, t, 0f, c));  // bottom
        list.Add(new FillRect(rx, ry, t, rh, 0f, c));           // left
        list.Add(new FillRect(rx + rw - t, ry, t, rh, 0f, c));  // right
    }

    private static RenderNode? FindFocused(RenderNode n)
    {
        if (n.Element?.HasAttribute("data-focus") == true) return n;
        foreach (var c in n.Children) { var f = FindFocused(c); if (f is not null) return f; }
        return null;
    }

    // The descendant a component marks as the value-text node (data-caret-anchor); null → use the field itself.
    private static RenderNode? FindCaretAnchor(RenderNode n)
    {
        if (n.Element?.HasAttribute("data-caret-anchor") == true) return n;
        foreach (var c in n.Children) { var f = FindCaretAnchor(c); if (f is not null) return f; }
        return null;
    }

    /// <summary>Build the committed display-list snapshot without rasterising (the seam).</summary>
    public DisplayList BuildDisplayList(float width, float height)
    {
        _layout.Layout(_root, width, height);
        return _painter.Build(_root);
    }

    // ---- interaction (Layer 0 → hit-test → dispatch) -------------------------
    private readonly List<(string Selector, Action<CupriPointerEvent> Handler)> _clickHandlers = new();

    /// <summary>Register a click handler matched by CSS selector (bubbles from target up).</summary>
    public CupriDocument OnClick(string selector, Action<CupriPointerEvent> handler)
    {
        _clickHandlers.Add((selector, handler));
        return this;
    }

    public RenderNode? HitTest(float x, float y) => HitTesting.HitTest(_root, x, y);

    /// <summary>Build the platform-neutral semantics tree (§5) at the given size.</summary>
    public Accessibility.AccessibilityNode BuildAccessibilityTree(float width, float height)
    {
        _layout.Layout(_root, width, height);
        return Accessibility.AccessibilityTree.Build(_root);
    }

    /// <summary>
    /// Dispatch a click at (x,y): hit-test, run built-in control behaviour (switch
    /// toggle, slider set) and user handlers along the bubble path, write back to the
    /// bound model, and refresh. Returns true if anything handled it (→ needs repaint).
    /// </summary>
    public bool DispatchClick(float x, float y, int clickCount = 1)
    {
        _textDrag = false;
        var hit = HitTesting.HitTest(_root, x, y);
        if (hit is null) { _kbIndex = -1; return UpdateFocus(null); } // click on empty space blurs

        // Grabbing a scrollbar thumb starts a scroll-drag (takes priority; doesn't focus/blur).
        for (var n = hit; n is not null; n = n.Parent)
            if (ThumbRect(n) is { } tr && x >= tr.X - 6 && x <= tr.X + tr.W + 8 && y >= tr.Y && y <= tr.Y + tr.H)
            {
                _scrollDrag = n; _scrollDragY0 = y; _scrollDragScroll0 = Math.Clamp(n.ScrollY, 0, n.MaxScrollY);
                return true;
            }

        // Focus: a click inside a text/number field focuses it; elsewhere blurs.
        RenderNode? field = hit;
        while (field is not null && field.Element?.GetAttribute("role") is not ("textbox" or "spinbutton")) field = field.Parent;
        var focusChanged = UpdateFocus(field?.Element);

        // Sync keyboard focus to the clicked control (so Tab continues from here), but don't
        // show the focus ring for a mouse click — it appears on Tab/Shift-Tab (focus-visible).
        _kbIndex = IndexOfFocusable(hit);
        _focusVisible = false;

        // Built-in behaviour first (steppers, toggles, buttons, user handlers).
        var handled = ActivateFrom(hit, x, y);

        // Clicking a checkbox/radio/switch's text label toggles the control (like <label>).
        if (!handled && ActivateLabel(hit) is { } labelled)
        {
            _kbIndex = FocusableIndexOf(labelled); // continue Tab order from the toggled control
            handled = true;
        }

        // If the click landed in a focused text field and nothing else consumed it, position the
        // caret / select a word (double-click) or line (triple-click), and arm a drag-select.
        if (!handled && field is not null && _focusKey is not null)
        {
            var pos = CaretFromPoint(field, FindCaretAnchor(field) ?? field, x, y);
            var buf = _editBuffer ?? "";
            if (clickCount >= 3) { var (a, b) = LineAt(buf, pos); _selAnchor = a; _caret = b; }
            else if (clickCount == 2) { var (a, b) = WordAt(buf, pos); _selAnchor = a; _caret = b; }
            else { _caret = pos; _selAnchor = pos; _textDrag = true; }
            _caretMoved = true;
            if (focusChanged) Refresh(); // new field → rebuild; caret/selection alone just repaints
            ReconcileScope();
            return true;
        }

        if (handled || focusChanged) Refresh();
        ReconcileScope(); // a click may have opened/closed an overlay → update the focus scope
        return handled || focusChanged;
    }

    // Walk from a node up the ancestor chain, applying the first built-in control behaviour or
    // user click handler. Shared by mouse clicks and keyboard activation (Enter/Space).
    private bool ActivateFrom(RenderNode start, float x, float y)
    {
        for (var node = start; node is not null; node = node.Parent)
        {
            if (node.Element is not { } el) continue;

            // Number stepper: +/- button adjusts the nearest numeric field's bound value.
            if (el.GetAttribute("data-cupri-step") is { Length: > 0 } stepRaw) return StepNumber(node, stepRaw);

            // Generic "set a bound value" click (tabs, select options, tree selection). Also
            // closes any containing overlay so picking an option dismisses its dropdown.
            if (el.GetAttribute("data-set-path") is { Length: > 0 } setPath && _model is not null)
            {
                var ok = BindingEngine.TrySet(_model, setPath, el.GetAttribute("data-set-value") ?? "");
                SetNearestOpen(node, false);
                return ok;
            }

            // Overlay open/close: dismiss (backdrop/outside) and trigger toggle.
            if (el.HasAttribute("data-cupri-dismiss")) return SetNearestOpen(node, false);
            if (el.HasAttribute("data-cupri-toggle")) return ToggleNearestOpen(node);

            switch (el.GetAttribute("role"))
            {
                case "switch" or "checkbox" or "radio": return ActivateControl(el);
                case "slider": return StartSliderDrag(node, el, x);
                default:
                    var any = false;
                    foreach (var (selector, handler) in _clickHandlers)
                    {
                        if (!Matches(el, selector)) continue;
                        handler(new CupriPointerEvent(x, y, node, el));
                        any = true;
                    }
                    if (any) return true;
                    break;
            }
        }
        return false;
    }

    // ---- keyboard focus + tab order (a11y capability #4) ---------------------
    // The interactive roles/attributes Tab stops on; an element is focusable if it is one of
    // these AND has no focusable descendant (so we land on the actual control, not a wrapper).
    private static bool IsFocusableRole(IElement el) =>
        el.GetAttribute("role") is "switch" or "checkbox" or "radio" or "slider"
                                or "textbox" or "spinbutton" or "button"
        || el.HasAttribute("data-cupri-toggle")
        || el.HasAttribute("data-set-path")
        || el.HasAttribute("data-cupri-step");

    private bool IsFocusable(IElement el) =>
        IsFocusableRole(el) || _clickHandlers.Exists(h => Matches(el, h.Selector));

    // Focusable render nodes in DOM (pre-order) order; skips a matched node's subtree so a
    // control counts once. display:none subtrees are already absent from the render tree. When
    // an overlay is open its panel is a focus scope — Tab is trapped within it (a11y).
    private List<RenderNode> Focusables()
    {
        var scope = FocusScope() ?? _root;
        var list = new List<RenderNode>();
        void Walk(RenderNode n)
        {
            if (n.Element is { } el && IsFocusable(el)) { list.Add(n); return; }
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var c in scope.Children) Walk(c);
        return list;
    }

    // The top-most open overlay panel (marked data-focus-scope), or null when none is open.
    private RenderNode? FocusScope()
    {
        RenderNode? scope = null;
        void Walk(RenderNode n)
        {
            if (n.Element?.HasAttribute("data-focus-scope") == true) scope = n; // last wins → topmost
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        return scope;
    }

    // Keep keyboard focus consistent as overlays open/close: entering a scope focuses its first
    // control; leaving clears focus. Called after any state change that may open/close an overlay.
    private bool _overlayFocused;
    private void ReconcileScope()
    {
        var scoped = FocusScope() is not null;
        if (scoped != _overlayFocused) _kbIndex = scoped ? 0 : -1;
        _overlayFocused = scoped;
    }

    private RenderNode? CurrentFocusNode()
    {
        var f = Focusables();
        return _kbIndex >= 0 && _kbIndex < f.Count ? f[_kbIndex] : null;
    }

    private int IndexOfFocusable(RenderNode hit)
    {
        // The focusable ancestor of the hit node (or itself), matched against the current list.
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element is { } el && IsFocusable(el))
            {
                var f = Focusables();
                for (var i = 0; i < f.Count; i++) if (ReferenceEquals(f[i], n)) return i;
                return -1;
            }
        return -1;
    }

    // Index of the focusable whose element is `el` (for syncing Tab order after a label click).
    private int FocusableIndexOf(IElement el)
    {
        var f = Focusables();
        for (var i = 0; i < f.Count; i++) if (ReferenceEquals(f[i].Element, el)) return i;
        return -1;
    }

    /// <summary>Move keyboard focus to the next (dir=+1) or previous (dir=-1) control, wrapping.</summary>
    private bool MoveFocus(int dir)
    {
        var f = Focusables();
        if (f.Count == 0) return false;
        _kbIndex = _kbIndex < 0 || _kbIndex >= f.Count
            ? (dir > 0 ? 0 : f.Count - 1)
            : (_kbIndex + dir + f.Count) % f.Count;
        _focusVisible = true;
        // If the newly focused control is a text field, start editing it; otherwise blur text.
        var el = f[_kbIndex].Element;
        UpdateFocus(el?.GetAttribute("role") is "textbox" or "spinbutton" ? el : null);
        Refresh();
        return true;
    }

    // Activate the keyboard-focused control (Enter/Space) as if clicked at its centre.
    private bool ActivateFocused()
    {
        var f = Focusables();
        if (_kbIndex < 0 || _kbIndex >= f.Count) return false;
        var (bx, by, bw, bh) = HitTesting.AbsoluteBox(f[_kbIndex]);
        var handled = ActivateFrom(f[_kbIndex], bx + bw / 2, by + bh / 2);
        if (handled) { Refresh(); ReconcileScope(); }
        return handled;
    }

    // Arrow-key nav within a group: radios move+select among their group; everything else
    // (menu items, list options, general) moves focus to the previous/next focusable. Sliders
    // are handled separately (they nudge their value) before this is called.
    private bool ArrowMove(int dir)
    {
        if (CurrentFocusNode() is { } cur && cur.Element?.GetAttribute("role") == "radio")
            return RadioArrow(cur, dir);
        return MoveFocus(dir);
    }

    // Move to the previous/next radio in the same group and select it (ARIA radio pattern).
    private bool RadioArrow(RenderNode cur, int dir)
    {
        var group = cur.Element!.GetAttribute("data-bind-group");
        var f = Focusables();
        var radios = f.Where(n => n.Element?.GetAttribute("role") == "radio"
                                  && n.Element.GetAttribute("data-bind-group") == group).ToList();
        if (radios.Count < 2) return false;
        var pos = radios.FindIndex(n => ReferenceEquals(n, cur));
        var next = radios[(pos + dir + radios.Count) % radios.Count];
        _kbIndex = f.FindIndex(n => ReferenceEquals(n, next));
        _focusVisible = true;
        var ok = group is { Length: > 0 } && _model is not null
            && BindingEngine.TrySet(_model, group, next.Element!.GetAttribute("value"));
        Refresh();
        return ok;
    }

    // Arrow-nudge a focused slider by its step (or 1/20th of range), clamped to min/max.
    private bool NudgeSlider(RenderNode node, int dir)
    {
        var el = node.Element!;
        if (el.GetAttribute("data-bind-value") is not { Length: > 0 } path || _model is null) return false;
        var min = double.TryParse(el.GetAttribute("min"), out var mn) ? mn : 0;
        var max = double.TryParse(el.GetAttribute("max"), out var mx) ? mx : 100;
        var step = double.TryParse(el.GetAttribute("step"), out var s) && s > 0 ? s : Math.Max(1, (max - min) / 20);
        var cur = BindingEngine.Resolve(_model, path) is { } v && double.TryParse(v.ToString(), out var d) ? d : min;
        var next = Math.Clamp(cur + dir * step, min, max);
        var text = next == Math.Floor(next) ? ((long)next).ToString() : next.ToString();
        _focusVisible = true;
        var ok = BindingEngine.TrySet(_model, path, text);
        Refresh();
        return ok;
    }

    // Escape: close the top-most open overlay if any; otherwise blur a focused text field.
    private bool HandleEscape()
    {
        RenderNode? target = null;
        void Walk(RenderNode n)
        {
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } p
                && _model is not null && BindingEngine.Resolve(_model, p) as bool? == true) target = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        if (target?.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && _model is not null)
        {
            BindingEngine.TrySet(_model, path, false);
            _overlayFocused = false; _kbIndex = -1;
            Refresh();
            return true;
        }
        if (_focusKey is not null) { UpdateFocus(null); Refresh(); return true; }
        return false;
    }

    private bool UpdateFocus(IElement? field)
    {
        var key = field?.GetAttribute("data-bind-value") ?? field?.GetAttribute("id");
        if (key == _focusKey) return false;
        CommitBuffer(); // blur the previous field: validate + commit (or revert) its edit buffer
        _focusKey = key;
        _focusNumeric = field?.HasAttribute("data-numeric") == true;
        _focusMultiline = field?.HasAttribute("data-multiline") == true;
        _focusMin = double.TryParse(field?.GetAttribute("data-min"), out var mn) ? mn : null;
        _focusMax = double.TryParse(field?.GetAttribute("data-max"), out var mx) ? mx : null;
        _editBuffer = key is null ? null : BindingEngine.Resolve(_model, key)?.ToString() ?? "";
        _caret = _editBuffer?.Length ?? 0;
        _selAnchor = _caret;
        _caretMoved = true;
        return true;
    }

    /// <summary>
    /// Feed a keystroke to the focused text field: printable text via <paramref name="text"/>,
    /// or an editing key (backspace/arrows/…). Edits the bound string and refreshes.
    /// </summary>
    public bool DispatchKey(string? text, EditKey key, KeyMods mods = KeyMods.None)
    {
        ReconcileScope(); // reflect any overlay that opened/closed since the last event

        if (key == EditKey.Escape) return HandleEscape();
        // Tab moves keyboard focus regardless of edit state (trapped within an open overlay).
        if (key == EditKey.Tab) return MoveFocus(+1);
        if (key == EditKey.ShiftTab) return MoveFocus(-1);

        // With no text field focused, Enter/Space activate and arrows navigate groups/sliders.
        // (Hosts deliver space as either EditKey.Space or a " " character.)
        if (_focusKey is null)
        {
            var role = CurrentFocusNode()?.Element?.GetAttribute("role");
            switch (key)
            {
                case EditKey.Enter or EditKey.Space: return ActivateFocused();
                case EditKey.Up: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, +1) : ArrowMove(-1);
                case EditKey.Down: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, -1) : ArrowMove(+1);
                case EditKey.Left: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, -1) : ArrowMove(-1);
                case EditKey.Right: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, +1) : ArrowMove(+1);
            }
            return text == " " && ActivateFocused();
        }

        if (_model is null) return false;
        // Edit a permissive buffer (may hold an invalid value while typing); it shows a red
        // border and is validated/clamped on blur — never block the user mid-edit.
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var caret = Math.Clamp(_caret, 0, value.Length);
        var anchor = Math.Clamp(_selAnchor, 0, value.Length);
        var shift = mods.HasFlag(KeyMods.Shift);
        var ctrl = mods.HasFlag(KeyMods.Ctrl);
        int selS = Math.Min(anchor, caret), selE = Math.Max(anchor, caret);
        var hasSel = selS != selE;
        var edited = false;

        switch (key)
        {
            case EditKey.SelectAll: anchor = 0; caret = value.Length; break;

            case EditKey.Left:
                if (!shift && hasSel) caret = selS;                                        // collapse to left edge
                else caret = ctrl ? WordLeft(value, caret) : Math.Max(0, caret - 1);
                if (!shift) anchor = caret;
                break;
            case EditKey.Right:
                if (!shift && hasSel) caret = selE;
                else caret = ctrl ? WordRight(value, caret) : Math.Min(value.Length, caret + 1);
                if (!shift) anchor = caret;
                break;
            case EditKey.Home: caret = 0; if (!shift) anchor = caret; break;
            case EditKey.End: caret = value.Length; if (!shift) anchor = caret; break;

            case EditKey.Backspace:
                if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; edited = true; }
                else if (ctrl && caret > 0) { var w = WordLeft(value, caret); value = value.Remove(w, caret - w); caret = w; edited = true; }
                else if (caret > 0) { value = value.Remove(caret - 1, 1); caret--; edited = true; }
                anchor = caret;
                break;
            case EditKey.Delete:
                if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; edited = true; }
                else if (ctrl && caret < value.Length) { var w = WordRight(value, caret); value = value.Remove(caret, w - caret); edited = true; }
                else if (caret < value.Length) { value = value.Remove(caret, 1); edited = true; }
                anchor = caret;
                break;

            case EditKey.Enter:
                if (_focusMultiline)
                {
                    if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; }
                    value = value.Insert(caret, "\n"); caret++; anchor = caret; edited = true; break;
                }
                CommitBuffer(); _focusKey = null; Refresh(); return true; // validate + commit + blur

            default:
                if (!string.IsNullOrEmpty(text))
                {
                    if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; }
                    value = value.Insert(caret, text); caret += text.Length; anchor = caret; edited = true;
                }
                break;
        }

        _caret = caret;
        _selAnchor = anchor;
        _caretMoved = true;
        if (edited)
        {
            _editBuffer = value;
            // Live-commit only when the buffer is currently valid, so other bindings track it;
            // invalid text stays in the buffer (red border) and the model keeps its last good value.
            if (BufferValid(value)) BindingEngine.TrySet(_model, _focusKey, value);
            Refresh(); // buffer changed → rebuild so the field re-renders the new text
        }
        // A caret/selection-only change needs just a repaint (the caret + selection are drawn from
        // _caret/_selAnchor at render time), so we skip the rebuild — the host repaints on `true`.
        return true;
    }

    // ---- text: clipboard + word/line boundaries (host provides the actual clipboard I/O) ----

    /// <summary>The currently selected text in the focused field, or null if nothing is selected.</summary>
    public string? CopySelection()
    {
        if (_focusKey is null || _editBuffer is null || _selAnchor == _caret) return null;
        int s = Math.Clamp(Math.Min(_selAnchor, _caret), 0, _editBuffer.Length);
        int e = Math.Clamp(Math.Max(_selAnchor, _caret), 0, _editBuffer.Length);
        return e > s ? _editBuffer[s..e] : null;
    }

    /// <summary>Copy the selection and delete it (cut). Returns the cut text, or null.</summary>
    public string? CutSelection()
    {
        var t = CopySelection();
        if (t is not null) DispatchKey(null, EditKey.Delete);
        return t;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int WordLeft(string s, int i)
    {
        i = Math.Clamp(i, 0, s.Length);
        while (i > 0 && !IsWordChar(s[i - 1])) i--;
        while (i > 0 && IsWordChar(s[i - 1])) i--;
        return i;
    }

    private static int WordRight(string s, int i)
    {
        i = Math.Clamp(i, 0, s.Length);
        while (i < s.Length && !IsWordChar(s[i])) i++;
        while (i < s.Length && IsWordChar(s[i])) i++;
        return i;
    }

    // The word (or whitespace/punctuation run) around index i — for double-click selection.
    private static (int, int) WordAt(string s, int i)
    {
        if (s.Length == 0) return (0, 0);
        i = Math.Clamp(i, 0, s.Length);
        int a = i, b = i;
        if ((i < s.Length && IsWordChar(s[i])) || (i > 0 && IsWordChar(s[i - 1])))
        {
            while (a > 0 && IsWordChar(s[a - 1])) a--;
            while (b < s.Length && IsWordChar(s[b])) b++;
        }
        else
        {
            while (a > 0 && !IsWordChar(s[a - 1]) && s[a - 1] != '\n') a--;
            while (b < s.Length && !IsWordChar(s[b]) && s[b] != '\n') b++;
        }
        return (a, b);
    }

    // The whole line around index i (between newlines) — for triple-click selection.
    private static (int, int) LineAt(string s, int i)
    {
        i = Math.Clamp(i, 0, s.Length);
        int a = i, b = i;
        while (a > 0 && s[a - 1] != '\n') a--;
        while (b < s.Length && s[b] != '\n') b++;
        return (a, b);
    }

    // Toggle/select a checkbox, switch, or radio from its element alone (no RenderNode needed) —
    // shared by a direct click on the control and a click on its adjacent text label.
    private bool ActivateControl(IElement el) => el.GetAttribute("role") switch
    {
        "switch" or "checkbox" => ToggleSwitch(el),
        "radio" => el.GetAttribute("data-bind-group") is { Length: > 0 } gp && _model is not null
            ? BindingEngine.TrySet(_model, gp, el.GetAttribute("value"))
            : SetChecked(el, true),
        _ => false,
    };

    private static bool IsLabelableControl(IElement? el) =>
        el?.GetAttribute("role") is "switch" or "checkbox" or "radio";

    /// <summary>
    /// A click that hit no control but landed on a checkbox/radio/switch's text label activates
    /// that control (HTML <c>&lt;label&gt;</c> behaviour). Labels are authored as siblings of the
    /// control, so we look outward from the clicked node: the immediately-adjacent control,
    /// preferring the one BEFORE the label (the "[box] Label" pattern) then the one AFTER it
    /// (the "Label [switch]" pattern). A following control is only bound when it has no trailing
    /// label of its own, so a group heading like "Size" that precedes the first radio doesn't
    /// hijack it. Returns the activated control, or null.
    /// </summary>
    private IElement? ActivateLabel(RenderNode hit)
    {
        // The whole ancestor chain of `hit` is non-interactive here (ActivateFrom already ran and
        // found no control above the hit), so walking out to adjacent siblings stays on inert text.
        RenderNode? node = hit;
        for (var hops = 0; node is not null && hops < 5; node = node.Parent, hops++)
        {
            if (node.Element is not { } el) continue;
            if (el.PreviousElementSibling is { } prev && IsLabelableControl(prev) && ActivateControl(prev))
                return prev;
            if (el.NextElementSibling is { } next && IsLabelableControl(next)
                && !HasTrailingLabel(next) && ActivateControl(next))
                return next;
        }
        return null;
    }

    // True when a control already owns a text label on its far side — i.e. its next sibling is a
    // non-control element — so an element before it is a heading, not its label.
    private static bool HasTrailingLabel(IElement control) =>
        control.NextElementSibling is { } sib && !IsLabelableControl(sib);

    private bool ToggleSwitch(IElement el)
    {
        var path = el.GetAttribute("data-bind-checked");
        if (path is null || _model is null) return false;
        var current = BindingEngine.Resolve(_model, path) as bool? ?? false;
        return BindingEngine.TrySet(_model, path, !current);
    }

    private bool SetChecked(IElement el, bool value)
    {
        var path = el.GetAttribute("data-bind-checked");
        return path is not null && _model is not null && BindingEngine.TrySet(_model, path, value);
    }

    // Walk up to the nearest element with a two-way-bound `open` and set/toggle it.
    private bool SetNearestOpen(RenderNode node, bool value)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && _model is not null)
                return BindingEngine.TrySet(_model, path, value);
        return false;
    }

    private bool ToggleNearestOpen(RenderNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && _model is not null)
            {
                var current = BindingEngine.Resolve(_model, path) as bool? ?? false;
                return BindingEngine.TrySet(_model, path, !current);
            }
        return false;
    }

    // A stepper (+/-) inside a number field: nudge the field's bound value by step*delta, clamped.
    private bool StepNumber(RenderNode node, string stepRaw)
    {
        if (_model is null || !int.TryParse(stepRaw, out var dir)) return false;
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Element?.GetAttribute("data-bind-value") is not { Length: > 0 } path) continue;
            if (_focusKey == path) CommitBuffer(); // flush any in-progress edit before stepping
            var cur = BindingEngine.Resolve(_model, path) is { } v && double.TryParse(v.ToString(), out var d) ? d : 0;
            var step = double.TryParse(n.Element.GetAttribute("data-step"), out var s) ? s : 1;
            var next = cur + dir * step;
            if (double.TryParse(n.Element.GetAttribute("data-min"), out var min)) next = Math.Max(min, next);
            if (double.TryParse(n.Element.GetAttribute("data-max"), out var max)) next = Math.Min(max, next);
            // Keep integers integral so a bound int round-trips cleanly.
            var text = next == Math.Floor(next) ? ((long)next).ToString() : next.ToString();
            var ok = BindingEngine.TrySet(_model, path, text);
            if (_focusKey == path) { _editBuffer = text; _caret = text.Length; } // keep the buffer in sync with the step
            return ok;
        }
        return false;
    }

    // ---- field validation: permissive typing, validate on commit (blur/Enter) --------------
    // The philosophy across all fields: let the user type freely (invalid states show a red
    // border), and only validate/clamp when the field loses focus — so text stays editable.

    // Is the buffer currently valid for its field? Plain text: always. Numeric: parseable + in range.
    private bool BufferValid(string buf)
    {
        if (!_focusNumeric) return true;
        if (!double.TryParse(buf, out var n)) return false;
        return !(_focusMin is { } mn && n < mn) && !(_focusMax is { } mx && n > mx);
    }

    // The value to write on commit, or null to leave the model unchanged (revert an unparseable buffer).
    private string? BufferCommit(string buf)
    {
        if (!_focusNumeric) return buf;
        if (!double.TryParse(buf, out var n)) return null; // e.g. "" or "abc" — keep the last good value
        if (_focusMin is { } mn) n = Math.Max(mn, n);
        if (_focusMax is { } mx) n = Math.Min(mx, n);
        return n == Math.Floor(n) ? ((long)n).ToString() : n.ToString();
    }

    // Validate and commit the current edit buffer to the model, then clear it (blur).
    private void CommitBuffer()
    {
        if (_focusKey is not null && _editBuffer is not null && _model is not null
            && BufferCommit(_editBuffer) is { } committed)
            BindingEngine.TrySet(_model, _focusKey, committed);
        _editBuffer = null;
    }

    // Begin a slider interaction: cache its geometry so drag-moves don't need the node.
    private bool StartSliderDrag(RenderNode node, IElement el, float x)
    {
        var path = el.GetAttribute("data-bind-value");
        if (path is null || _model is null) return false;

        var box = HitTesting.AbsoluteBox(node);
        _dragging = true;
        _dragX0 = box.X;
        _dragPad = node.ContentLeftInset;
        _dragInnerW = box.W - node.HorizontalInsets;
        _dragMin = ParseAttr(el, "min", 0);
        _dragMax = ParseAttr(el, "max", 100);
        _dragPath = path;
        return ApplySliderValue(x);
    }

    private bool ApplySliderValue(float x)
    {
        if (_dragPath is null || _model is null) return false;
        var ratio = _dragInnerW > 0 ? Math.Clamp((x - _dragX0 - _dragPad) / _dragInnerW, 0, 1) : 0;
        var value = _dragMin + ratio * (_dragMax - _dragMin);
        return BindingEngine.TrySet(_model, _dragPath, Math.Round(value));
    }

    /// <summary>Pointer move: drag a slider, or update :hover. Returns true if a repaint is needed.</summary>
    public bool DispatchPointerMove(float x, float y)
    {
        if (_scrollDrag is { } sd)
        {
            var boxH = sd.ContentBoxHeight;
            var thumbH = MathF.Max(28f, boxH * boxH / sd.ScrollContentHeight);
            var travel = boxH - thumbH;
            if (travel > 0.5f)
                sd.ScrollY = Math.Clamp(_scrollDragScroll0 + (y - _scrollDragY0) / travel * sd.MaxScrollY, 0, sd.MaxScrollY);
            return true; // scroll is paint-time → repaint, no rebuild
        }
        if (_dragging)
        {
            if (!ApplySliderValue(x)) return false;
            Refresh();
            return true;
        }
        // Drag-select text: extend the selection caret to the pointer (anchor stays put).
        if (_textDrag && _focusKey is not null && FindFocused(_root) is { } field)
        {
            _caret = CaretFromPoint(field, FindCaretAnchor(field) ?? field, x, y);
            _caretMoved = true; // auto-scroll if the drag runs past the visible edge
            return true; // caret/selection only → repaint, no rebuild
        }
        return UpdateHover(x, y);
    }

    /// <summary>Pointer up: end any slider drag, scrollbar drag, or text drag-select.</summary>
    public void DispatchPointerUp(float x, float y) { _dragging = false; _textDrag = false; _scrollDrag = null; }

    /// <summary>Scroll wheel: scroll the nearest scrollable element under the pointer by pixels.</summary>
    public bool DispatchWheel(float x, float y, float pixelDelta)
    {
        var hit = HitTesting.HitTest(_root, x, y);
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (!n.IsScrollable) continue;
            var before = n.ScrollY;
            n.ScrollY = Math.Clamp(n.ScrollY + pixelDelta, 0, n.MaxScrollY);
            return Math.Abs(n.ScrollY - before) > 0.01f;
        }
        return false;
    }

    // Toggle data-hover on the hovered element + ancestors, then re-resolve styles (no full rebuild).
    private bool UpdateHover(float x, float y)
    {
        var hit = HitTesting.HitTest(_root, x, y);
        var target = hit?.Element;
        if (_hoverChain.Count > 0 && ReferenceEquals(_hoverChain[0], target)) return false;

        foreach (var e in _hoverChain) e.RemoveAttribute("data-hover");
        _hoverChain.Clear();
        for (var e = target; e is not null; e = e.ParentElement)
        {
            e.SetAttribute("data-hover", "");
            _hoverChain.Add(e);
        }
        ReStyle();
        return true;
    }

    /// <summary>Re-resolve styles on the existing DOM (for :hover/:active) without re-parsing/binding.</summary>
    private void ReStyle()
    {
        if (_dom is null) return;
        _root = new StyleResolver(_rules, _viewportWidth).BuildTree(_dom);
    }

    private static double ParseAttr(IElement el, string name, double fallback) =>
        double.TryParse(el.GetAttribute(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool Matches(IElement el, string selector)
    {
        try { return el.Matches(selector); }
        catch { return false; }
    }

    /// <summary>Convenience CPU-raster render to an image (headless/tests).</summary>
    public SKImage RenderToImage(int width, int height, SKColor? clear = null)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(clear ?? SKColors.White);
        Render(surface.Canvas, width, height);
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    public void Dispose()
    {
        _fonts.Dispose();
        _dom?.Dispose();
    }
}
