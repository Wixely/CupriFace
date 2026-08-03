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
    private bool _dragging;
    private float _dragX0, _dragInnerW, _dragPad;
    private double _dragMin, _dragMax;
    private string? _dragPath;

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

    private void Rebuild()
    {
        _dom?.Dispose();
        var dom = new HtmlParser().ParseDocument(_templateHtml);

        if (_model is not null)
            BindingEngine.Apply(dom, _model);

        // Expand custom elements after binding so components see concrete attribute values.
        _components?.Expand(dom);

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

        // Component defaults first (low priority), then author CSS, then <style> tags.
        var rules = new List<CssRule>();
        if (_components is not null) rules.AddRange(CssParser.Parse(_components.AggregatedCss));
        rules.AddRange(CssParser.Parse(_css));
        foreach (var styleEl in dom.QuerySelectorAll("style"))
            rules.AddRange(CssParser.Parse(styleEl.TextContent));

        // @keyframes (parsed from the same sources; not matched as normal rules).
        _keyframes = Animation.Parse(_css);
        if (_components is not null)
            foreach (var (k, frames) in Animation.Parse(_components.AggregatedCss)) _keyframes[k] = frames;
        foreach (var styleEl in dom.QuerySelectorAll("style"))
            foreach (var (k, frames) in Animation.Parse(styleEl.TextContent)) _keyframes[k] = frames;

        // Reassign a global source order so later stylesheets win ties (author > component).
        for (var i = 0; i < rules.Count; i++) rules[i].Order = i;

        _hasMedia = rules.Exists(r => r.Media is not null);
        _rules = rules;
        _hoverChain.Clear();
        _root = new StyleResolver(rules, _viewportWidth).BuildTree(dom);
        _dom = dom;
    }

    /// <summary>Advance @keyframes animations to the given elapsed time (paint-only).</summary>
    public bool Animate(double timeSeconds)
    {
        if (_keyframes.Count == 0) return false;
        Animation.Apply(_root, _keyframes, timeSeconds);
        return true;
    }

    public bool HasAnimations => _keyframes.Count > 0;

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
        var list = _painter.Build(_root);
        AppendCaret(list);
        _rasterizer.Paint(canvas, list);
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
        var box = HitTesting.AbsoluteBox(anchor);
        var lh = FontService.LineHeightPx(anchor.Style);
        var ch = anchor.Style.FontSize * 1.1f;

        // Multi-line (textarea): the caret's line is the count of newlines before it, and its
        // column is the text since the last newline. Single-line fields have one line at index 0.
        var upto = value[..caret];
        var lineIndex = 0;
        var col = upto;
        if (field.Element?.HasAttribute("data-multiline") == true)
        {
            var nl = upto.LastIndexOf('\n');
            lineIndex = upto.Count(c => c == '\n');
            col = nl >= 0 ? upto[(nl + 1)..] : upto;
        }
        var cx = box.X + anchor.ContentLeftInset + _fonts.MeasureText(anchor.Style, col);
        var cy = box.Y + anchor.ContentTopInset + lineIndex * lh + (lh - ch) / 2f;
        list.Add(new FillRect(cx, cy, 2f, ch, 0f, anchor.Style.Color));
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
    public bool DispatchClick(float x, float y)
    {
        var hit = HitTesting.HitTest(_root, x, y);
        if (hit is null) return UpdateFocus(null); // click on empty space blurs

        // Focus: a click inside a text/number field focuses it (caret at end); elsewhere blurs.
        RenderNode? field = hit;
        while (field is not null && field.Element?.GetAttribute("role") is not ("textbox" or "spinbutton")) field = field.Parent;
        var focusChanged = UpdateFocus(field?.Element);

        var handled = false;
        for (var node = hit; node is not null && !handled; node = node.Parent)
        {
            if (node.Element is not { } el) continue;

            // Number stepper: +/- button adjusts the nearest numeric field's bound value.
            if (el.GetAttribute("data-cupri-step") is { Length: > 0 } stepRaw) { handled = StepNumber(node, stepRaw); break; }

            // Generic "set a bound value" click (tabs, select options, tree selection). Also
            // closes any containing overlay so picking an option dismisses its dropdown.
            if (el.GetAttribute("data-set-path") is { Length: > 0 } setPath && _model is not null)
            {
                handled = BindingEngine.TrySet(_model, setPath, el.GetAttribute("data-set-value") ?? "");
                SetNearestOpen(node, false);
                break;
            }

            // Overlay open/close: dismiss (backdrop/outside) and trigger toggle.
            if (el.HasAttribute("data-cupri-dismiss")) { handled = SetNearestOpen(node, false); break; }
            if (el.HasAttribute("data-cupri-toggle")) { handled = ToggleNearestOpen(node); break; }

            switch (el.GetAttribute("role"))
            {
                case "switch" or "checkbox": handled = ToggleSwitch(el); break;
                case "radio":
                    handled = el.GetAttribute("data-bind-group") is { Length: > 0 } gp && _model is not null
                        ? BindingEngine.TrySet(_model, gp, el.GetAttribute("value"))
                        : SetChecked(el, true);
                    break;
                case "slider": handled = StartSliderDrag(node, el, x); break;
                default:
                    foreach (var (selector, handler) in _clickHandlers)
                    {
                        if (!Matches(el, selector)) continue;
                        handler(new CupriPointerEvent(x, y, node, el));
                        handled = true;
                    }
                    break;
            }
        }

        if (handled || focusChanged) Refresh();
        return handled || focusChanged;
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
        return true;
    }

    /// <summary>
    /// Feed a keystroke to the focused text field: printable text via <paramref name="text"/>,
    /// or an editing key (backspace/arrows/…). Edits the bound string and refreshes.
    /// </summary>
    public bool DispatchKey(string? text, EditKey key)
    {
        if (_focusKey is null || _model is null) return false;
        // Edit a permissive buffer (may hold an invalid value while typing); it shows a red
        // border and is validated/clamped on blur — never block the user mid-edit.
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var caret = Math.Clamp(_caret, 0, value.Length);
        var edited = false;

        switch (key)
        {
            case EditKey.Backspace: if (caret > 0) { value = value.Remove(caret - 1, 1); caret--; edited = true; } break;
            case EditKey.Delete: if (caret < value.Length) { value = value.Remove(caret, 1); edited = true; } break;
            case EditKey.Left: caret = Math.Max(0, caret - 1); break;
            case EditKey.Right: caret = Math.Min(value.Length, caret + 1); break;
            case EditKey.Home: caret = 0; break;
            case EditKey.End: caret = value.Length; break;
            case EditKey.Enter:
                if (_focusMultiline) { value = value.Insert(caret, "\n"); caret++; edited = true; break; } // newline
                CommitBuffer(); _focusKey = null; Refresh(); return true; // validate + commit + blur
            default:
                if (!string.IsNullOrEmpty(text)) { value = value.Insert(caret, text); caret += text.Length; edited = true; }
                break;
        }

        _caret = caret;
        if (edited)
        {
            _editBuffer = value;
            // Live-commit only when the buffer is currently valid, so other bindings track it;
            // invalid text stays in the buffer (red border) and the model keeps its last good value.
            if (BufferValid(value)) BindingEngine.TrySet(_model, _focusKey, value);
        }
        Refresh();
        return true;
    }

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
        if (_dragging)
        {
            if (!ApplySliderValue(x)) return false;
            Refresh();
            return true;
        }
        return UpdateHover(x, y);
    }

    /// <summary>Pointer up: end any slider drag.</summary>
    public void DispatchPointerUp(float x, float y) => _dragging = false;

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
