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
public sealed class CupriDocument : IDisposable
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

    // Hover + drag state
    private readonly List<IElement> _hoverChain = new();
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
        _rasterizer.Paint(canvas, _painter.Build(_root));
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
        if (hit is null) return false;

        var handled = false;
        for (var node = hit; node is not null && !handled; node = node.Parent)
        {
            if (node.Element is not { } el) continue;

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

        if (handled) Refresh();
        return handled;
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
