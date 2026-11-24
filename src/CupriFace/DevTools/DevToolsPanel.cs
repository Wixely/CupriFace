using System.Diagnostics;
using System.Globalization;
using AngleSharp.Dom;
using CupriFace.Diagnostics;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.DevTools;

/// <summary>
/// The F12 panel: an element tree of the running document, the box model, computed values, matched
/// rules, bindings and attributes of whatever is selected, an element picker, and the doctor's
/// findings from the last hot reload — all docked beside the app in the same window.
///
/// <para><b>It is drawn by CupriFace itself.</b> The panel is an ordinary document, so it needs no
/// second window and nothing from the host but a rectangle to draw into and the input that lands
/// there. The host lays the app out in what is left, so docking it narrows the app the way a
/// browser's dev tools do, and a layout's breakpoints can be watched firing.</para>
///
/// <para><b>Host contract.</b> Everything here is in LOGICAL units. The app's own coordinates are
/// the ones the host already hands the app (pointer / present scale); the panel's are relative to
/// its own left edge. Call <see cref="HandleKey"/> before the app sees a key, the <c>App…</c>
/// pointer methods before the app sees a pointer (the picker takes them while it is on), the
/// <c>Panel…</c> methods for input inside the panel, <see cref="PaintHighlight"/> on the app's
/// canvas after the app has drawn, and <see cref="Render"/> for the panel itself.
/// <see cref="ConsumeDirty"/> says whether any of it needs a frame.</para>
///
/// <para>A development tool: hosts create one only when <see cref="CupriDevTools.IsSupported"/>.</para>
/// </summary>
public sealed partial class DevToolsPanel : IDisposable
{
    /// <summary>The panel's width when it opens, in logical pixels.</summary>
    public const float DefaultWidth = 440;

    private const int MaxRows = 3000;

    private readonly CupriDocument _target;
    private readonly HotReloadSession? _reload;
    private readonly CupriDocument _ui;
    private readonly InspectorModel _m = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<int[]> _rowPaths = [];
    private bool _seeded;
    private int[]? _selected;
    private int[]? _hover;
    private RenderNode? _lastRoot;
    private long _lastBuild;
    private bool _dirty = true;
    private bool _stale = true;
    private bool _revealSelected;
    private bool _pickSwallowsUp;
    private bool _open;
    private bool _picking;

    /// <param name="target">The app's document — the one being inspected.</param>
    /// <param name="reload">The hot-reload session for it, if there is one: the panel shows its
    /// status and the doctor's findings, and its Reload button asks it to reload.</param>
    /// <param name="shortcut">The chord that opens and closes the panel (<see cref="HandleKey"/>).
    /// Null: only code opens it.</param>
    public DevToolsPanel(CupriDocument target, HotReloadSession? reload = null, KeyChord? shortcut = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _reload = reload;
        Shortcut = shortcut;
        _ui = CupriDocument.Load(DevToolsMarkup.Html, DevToolsMarkup.Css)
            .UseComponents(Components.ComponentRegistry.Default())   // cupri-textarea, for editing
            .Bind(_m);
        _ui.OnClick(".dt-pick", _ => Picking = !Picking);
        _ui.OnClick(".dt-outlines", _ => { _target.DebugOverlay = !_target.DebugOverlay; MarkStale(); });
        _ui.OnClick(".dt-reload", _ => _reload?.Request());
        _ui.OnClick(".dt-close", _ => IsOpen = false);
        _ui.OnClick(".dt-dock", _ => Detached = !Detached);
        _ui.OnClick(".dt-open", _ => OpenSelectedInEditor());
        _ui.OnClick(".dt-row", e => RowClicked(e));
        _ui.OnClick(".dt-problem", e => ProblemClicked(e));
        WireEditing();
        if (_reload is not null) _reload.Reloaded += OnReloaded;
    }

    /// <summary>The chord <see cref="HandleKey"/> toggles the panel on.</summary>
    public KeyChord? Shortcut { get; set; }

    /// <summary>Whether the panel is showing. The host narrows the app by <see cref="Width"/> while
    /// it is.</summary>
    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            // Closing hands the keyboard back: reopened, the arrows are the app's until the panel
            // is clicked again.
            if (!value) { Picking = false; _hover = null; PanelHasFocus = false; }
            MarkStale();
        }
    }

    /// <summary>
    /// Popped out into a window of its own rather than docked beside the app. The HOST acts on it —
    /// it stops reserving the panel's width and draws the panel in a second window instead — and
    /// only a host that can (<see cref="CanDetach"/>) shows the button. Kept while the panel is
    /// closed, so the next F12 opens it the way it was left.
    /// </summary>
    public bool Detached
    {
        get => _detached;
        set
        {
            if (_detached == value) return;
            _detached = value;
            MarkStale();
        }
    }
    private bool _detached;

    /// <summary>Whether the host can pop the panel out; the Pop out / Dock button shows only then.</summary>
    public bool CanDetach
    {
        get => _canDetach;
        set { _canDetach = value; MarkStale(); }
    }
    private bool _canDetach;

    /// <summary>The panel's width in logical pixels.</summary>
    public float Width { get; set; } = DefaultWidth;

    /// <summary>Element-picker mode: the app's pointer input goes to the panel, hovering highlights
    /// and a click selects. One click picks and leaves the mode, as a browser's does.</summary>
    public bool Picking
    {
        get => _picking;
        set
        {
            if (_picking == value) return;
            _picking = value;
            if (!value) _hover = null;
            MarkStale();
        }
    }

    /// <summary>The panel's own document — for tests, and for a host that wants its cursor.</summary>
    public CupriDocument Document => _ui;

    /// <summary>The selected node in the app's current render tree, or null.</summary>
    public RenderNode? SelectedNode => Resolve(_selected);

    /// <summary>The node under the picker or the hovered tree row, or null.</summary>
    public RenderNode? HoveredNode => Resolve(_hover);

    /// <summary>Whether anything the panel draws, or the highlight it paints over the app, has
    /// changed since the last call. Also notices a rebuild of the app's tree, which the panel has
    /// to follow.</summary>
    public bool ConsumeDirty()
    {
        if (_open && !ReferenceEquals(_target.Root, _lastRoot)) _stale = true;
        var d = _dirty || (_open && _stale);
        _dirty = false;
        return d;
    }

    /// <summary>Select an app node (it is revealed in the tree and its details shown).</summary>
    public void Select(RenderNode? node)
    {
        _selected = node is null ? null : PathOf(node);
        _revealSelected = true;
        MarkStale();
    }

    // ---- keys ------------------------------------------------------------------------------

    /// <summary>
    /// Offer a key to the dev tools before the app sees it. True when they took it: the shortcut
    /// (always, open or closed), Escape while picking, and the arrow keys while the panel was the
    /// last thing clicked. Everything else is left for the app.
    /// </summary>
    public bool HandleKey(EditKey key, KeyMods mods)
    {
        if (Shortcut is { } chord && chord.Matches(key, mods))
        {
            IsOpen = !IsOpen;
            return true;
        }
        if (!_open) return false;
        if (key == EditKey.Escape && _picking) { Picking = false; return true; }
        if (!PanelHasFocus) return false;
        // The HTML editor is open: every key is the text field's — Escape closes it.
        if (_mode != EditorMode.None)
        {
            if (key == EditKey.Escape) { CloseEditor(); return true; }
            _dirty |= _ui.DispatchKey(null, key, mods);
            return true;
        }
        switch (key)
        {
            case EditKey.Up: MoveSelection(-1); return true;
            case EditKey.Down: MoveSelection(+1); return true;
            case EditKey.Right: SetExpanded(true); return true;
            case EditKey.Left: SetExpanded(false); return true;
            case EditKey.Delete: DeleteSelected(); return true;
            case EditKey.Enter: OpenEditor(EditorMode.Edit); return true;
            default: return true;   // the panel has the keyboard; the app does not get its keys
        }
    }

    /// <summary>Whether the panel has the keyboard: it was the last thing clicked, or — popped out —
    /// its window is the focused one. While it has, keys, text and the clipboard chords are the
    /// panel's (<see cref="HandleKey"/>, <see cref="PanelText"/>, <see cref="PanelShortcut"/>), and a
    /// click in the app hands them back.</summary>
    public bool PanelHasFocus { get; set; }

    // ---- input over the app ----------------------------------------------------------------

    /// <summary>Pointer moved over the app, in the app's logical coordinates. True when the picker
    /// took it (the app must not see it).</summary>
    public bool AppPointerMove(float x, float y)
    {
        if (!_open || !_picking) return false;
        var hit = HitApp(x, y);
        var path = hit is null ? null : PathOf(hit);
        if (!SamePath(path, _hover)) { _hover = path; _dirty = true; }
        return true;
    }

    /// <summary>Pointer pressed over the app. True when the picker took it: the node under it is
    /// selected and picking ends.</summary>
    public bool AppPointerDown(float x, float y)
    {
        PanelHasFocus = false;
        if (!_open || !_picking) return false;
        Select(HitApp(x, y));
        Picking = false;
        _pickSwallowsUp = true;
        return true;
    }

    /// <summary>Pointer released over the app. True when it ends a press the picker took.</summary>
    public bool AppPointerUp(float x, float y)
    {
        if (!_pickSwallowsUp) return false;
        _pickSwallowsUp = false;
        return true;
    }

    /// <summary>The cursor to show over the app, or null for the app's own.</summary>
    public CursorType? AppCursor => _open && _picking ? CursorType.Crosshair : null;

    // ---- input over the panel --------------------------------------------------------------

    /// <summary>A press inside the panel, relative to its top-left corner.</summary>
    public bool PanelPointerDown(float x, float y, int clicks = 1)
    {
        PanelHasFocus = true;
        var changed = _ui.DispatchPointer(0, PointerPhase.Down, x, y) || _ui.DispatchClick(x, y, clicks);
        _dirty |= changed;
        return true;
    }

    /// <summary>The pointer moving inside the panel. Hovering a tree row highlights its element in
    /// the app.</summary>
    public bool PanelPointerMove(float x, float y)
    {
        var changed = _ui.DispatchPointer(0, PointerPhase.Move, x, y) || _ui.DispatchPointerMove(x, y);
        var row = RowIndexAt(x, y);
        var path = row is { } i && i < _rowPaths.Count ? _rowPaths[i] : null;
        if (!_picking && !SamePath(path, _hover)) { _hover = path; changed = true; }
        _dirty |= changed;
        return true;
    }

    /// <summary>The pointer leaving the panel.</summary>
    public void PanelPointerLeave()
    {
        if (!_picking && _hover is not null) { _hover = null; _dirty = true; }
    }

    public bool PanelPointerUp(float x, float y)
    {
        _dirty |= _ui.DispatchPointer(0, PointerPhase.Up, x, y) || _ui.DispatchPointerUp(x, y);
        return true;
    }

    /// <summary>A wheel over the panel. <paramref name="deltaY"/> as the document's
    /// <see cref="CupriDocument.DispatchWheel"/> takes it.</summary>
    public bool PanelWheel(float x, float y, float deltaY)
    {
        _dirty |= _ui.DispatchWheel(x, y, deltaY);
        return true;
    }

    public CursorType PanelCursor(float x, float y) => _ui.CursorAt(x, y);

    // ---- drawing ---------------------------------------------------------------------------

    /// <summary>Draw the panel at the canvas origin, <paramref name="width"/> by
    /// <paramref name="height"/> logical pixels.</summary>
    public void Render(SKCanvas canvas, float width, float height)
    {
        Refresh();
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, width, height));
        using (var bg = new SKPaint { Color = new SKColor(0x1e, 0x1f, 0x24) })
            canvas.DrawRect(0, 0, width, height, bg);
        _ui.Render(canvas, width, height);
        if (FocusEditorIfAsked()) _ui.Render(canvas, width, height);
        if (_revealSelected && RevealSelectedRow())
        {
            _revealSelected = false;
            _ui.Render(canvas, width, height);   // the scroll moved: draw it where it now is
        }
        canvas.Restore();
    }

    /// <summary>
    /// Outline the hovered (or else the selected) element over the app: content, padding and margin
    /// tinted as a browser's inspector does, and a label with its name and size. Call with the
    /// canvas set up exactly as it was for the app's own render — same scale, same origin.
    /// </summary>
    public void PaintHighlight(SKCanvas canvas)
    {
        if (!_open) return;
        var node = HoveredNode ?? SelectedNode;
        if (node is null || node.IsText) node = node?.Parent;
        if (node is null) return;

        var zoom = _target.Zoom;
        var (bx, by, bw, bh) = HitTesting.ScreenBox(node);
        SKRect R(float l, float t, float r, float b) => new(l * zoom, t * zoom, r * zoom, b * zoom);
        var border = R(bx, by, bx + bw, by + bh);
        var margin = R(bx - node.MarginLeft, by - node.MarginTop, bx + bw + node.MarginRight, by + bh + node.MarginBottom);
        var padding = R(bx + node.BorderLeftW, by + node.BorderTopW, bx + bw - node.BorderRightW, by + bh - node.BorderBottomW);
        var content = R(bx + node.ContentLeftInset, by + node.ContentTopInset,
                        bx + bw - node.BorderRightW - node.PadRight, by + bh - node.BorderBottomW - node.PadBottom);

        canvas.Save();
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        void Ring(SKRect outer, SKRect inner, SKColor c)
        {
            fill.Color = c;
            canvas.Save();
            canvas.ClipRect(inner, SKClipOperation.Difference);
            canvas.DrawRect(outer, fill);
            canvas.Restore();
        }
        Ring(margin, border, new SKColor(0xf6, 0xb2, 0x6b, 0x66));   // margin: orange
        Ring(border, padding, new SKColor(0xfd, 0xdd, 0x9b, 0x88));  // border: yellow
        Ring(padding, content, new SKColor(0x93, 0xc4, 0x7d, 0x77)); // padding: green
        fill.Color = new SKColor(0x6f, 0xa8, 0xdc, 0x77);            // content: blue
        canvas.DrawRect(content, fill);
        using (var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0x3b, 0x6f, 0xd8), IsAntialias = false })
            canvas.DrawRect(border, edge);

        var label = $"{CupriDocument.DescribeNode(node)}  {Num(bw)} × {Num(bh)}";
        using var font = new SKFont(SKTypeface.Default, 12);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var tw = font.MeasureText(label);
        var ly = border.Top - 22 >= 0 ? border.Top - 22 : border.Bottom + 4;
        var lx = Math.Max(0, border.Left);
        fill.Color = new SKColor(0x24, 0x26, 0x2c, 0xee);
        canvas.DrawRoundRect(new SKRect(lx, ly, lx + tw + 12, ly + 18), 3, 3, fill);
        canvas.DrawText(label, lx + 6, ly + 13, font, text);
        canvas.Restore();
    }

    // ---- the model -------------------------------------------------------------------------

    private void MarkStale() { _stale = true; _dirty = true; }

    /// <summary>Rebuild the panel's model from the app's current tree, when anything it shows may
    /// have changed — at most four times a second for a tree that is merely being rebuilt by hover,
    /// immediately for a click in the panel.</summary>
    private void Refresh()
    {
        var rootMoved = !ReferenceEquals(_target.Root, _lastRoot);
        if (!_stale && !rootMoved) return;
        var now = Stopwatch.GetTimestamp();
        if (!_stale && Stopwatch.GetElapsedTime(_lastBuild, now).TotalMilliseconds < 250) { _dirty = true; return; }
        _stale = false;
        _lastBuild = now;
        _lastRoot = _target.Root;

        _m.PickCss = _picking ? "on" : "";
        _m.OutlinesCss = _target.DebugOverlay ? "on" : "";
        _m.DockDisplay = _canDetach ? "flex" : "none";
        _m.DockLabel = _detached ? "Dock" : "Pop out";
        _m.Status = _reload is null ? "Hot reload is off" : _reload.LastError is { } err ? $"{_reload.Status}: {err}" : _reload.Status;
        _m.StatusCss = _reload?.LastError is null ? "" : "bad";
        BuildTree();
        BuildDetails();
        BuildProblems();
        BuildEditing();
        _ui.Refresh();
    }

    private void BuildTree()
    {
        var root = _target.Root;
        if (!_seeded)
        {
            _seeded = true;
            Seed(root, [], 0);
        }
        // Everything above the selection is opened, so a picked element is never hidden in a
        // collapsed branch.
        if (_selected is { } sel)
            for (var i = 0; i < sel.Length; i++) _expanded.Add(Key(sel.AsSpan(0, i)));

        var rows = new List<TreeRow>();
        _rowPaths.Clear();
        var path = new List<int>();
        Walk(root, 0);
        _m.Rows = rows;
        _m.TreeNote = rows.Count >= MaxRows ? $"Showing the first {MaxRows} rows." : "";

        void Walk(RenderNode n, int depth)
        {
            if (rows.Count >= MaxRows) return;
            var p = path.ToArray();
            var kids = VisibleChildren(n);
            var open = _expanded.Contains(Key(p));
            var flag = "";
            if (!n.IsText)
            {
                if (BoxOverflow.IsEmptyBoxWithContent(n)) flag = "empty box";
                else if (BoxOverflow.Overshoot(n) is { } over) flag = $"overflows {Num(over)}";
            }
            var css = n.IsText ? "text" : "";
            if (SamePath(p, _selected)) css += " sel";
            if (flag.Length > 0) css += " flagged";
            rows.Add(new TreeRow
            {
                Index = rows.Count,
                Indent = 4 + depth * 12,
                Toggle = kids.Count == 0 ? "" : open ? "▾" : "▸",
                Label = CupriDocument.DescribeNode(n),
                Size = n.IsText ? "" : $"{Num(n.Width)}×{Num(n.Height)}",
                Flag = flag,
                RowCss = css.Trim(),
            });
            _rowPaths.Add(p);
            if (!open) return;
            foreach (var (child, index) in kids)
            {
                path.Add(index);
                Walk(child, depth + 1);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private void Seed(RenderNode n, List<int> path, int depth)
    {
        if (depth > 2) return;
        _expanded.Add(Key(path.ToArray()));
        foreach (var (c, i) in VisibleChildren(n))
        {
            path.Add(i);
            Seed(c, path, depth + 1);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>The children worth a row: every element, and text that is not just whitespace.</summary>
    private static List<(RenderNode Node, int Index)> VisibleChildren(RenderNode n)
    {
        var list = new List<(RenderNode, int)>(n.Children.Count);
        for (var i = 0; i < n.Children.Count; i++)
        {
            var c = n.Children[i];
            if (c.IsText && string.IsNullOrWhiteSpace(c.Text)) continue;
            list.Add((c, i));
        }
        return list;
    }

    private void BuildDetails()
    {
        var node = SelectedNode;
        if (node is { IsText: true }) node = node.Parent;
        if (node is null)
        {
            _m.DetailsDisplay = "none";
            _m.EmptyDisplay = "flex";
            return;
        }
        _m.DetailsDisplay = "flex";
        _m.EmptyDisplay = "none";
        var el = node.Element;
        _m.Selected = CupriDocument.DescribeNode(node);
        _m.Classes = el?.GetAttribute("class") is { Length: > 0 } cls ? "." + string.Join(" .", cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : "";

        var (x, y, w, h) = HitTesting.ScreenBox(node);
        _m.Box = $"{Num(w)} × {Num(h)} at {Num(x)}, {Num(y)}";
        _m.Margin = Sides(node.MarginTop, node.MarginRight, node.MarginBottom, node.MarginLeft);
        _m.Border = Sides(node.BorderTopW, node.BorderRightW, node.BorderBottomW, node.BorderLeftW);
        _m.Padding = Sides(node.PadTop, node.PadRight, node.PadBottom, node.PadLeft);

        var s = node.Style;
        var computed = new List<NameValue>
        {
            new() { Name = "display", Value = Kebab(s.Display.ToString()) },
            new() { Name = "position", Value = Kebab(s.Position.ToString()) },
        };
        if (s.Display == DisplayType.Flex)
        {
            computed.Add(new() { Name = "flex-direction", Value = Kebab(s.FlexDirection.ToString()) });
            computed.Add(new() { Name = "flex-wrap", Value = Kebab(s.FlexWrap.ToString()) });
            computed.Add(new() { Name = "justify-content", Value = Kebab(s.JustifyContent.ToString()) });
            computed.Add(new() { Name = "align-items", Value = Kebab(s.AlignItems.ToString()) });
            computed.Add(new() { Name = "gap", Value = $"{Num(s.RowGap)} {Num(s.ColumnGap)}" });
        }
        computed.Add(new() { Name = "overflow", Value = Kebab(s.Overflow.ToString()) });
        computed.Add(new() { Name = "font", Value = $"{s.FontWeight} {Num(s.FontSize)}px {s.FontFamily}" });
        computed.Add(new() { Name = "color", Value = Hex(s.Color) });
        computed.Add(new() { Name = "background", Value = Hex(s.Background) });
        if (s.Opacity < 1f) computed.Add(new() { Name = "opacity", Value = Num(s.Opacity, "0.##") });
        if (s.ZIndex != 0) computed.Add(new() { Name = "z-index", Value = s.ZIndex.ToString(CultureInfo.InvariantCulture) });
        _m.Computed = computed;

        var bindings = new List<NameValue>();
        var attrs = new List<NameValue>();
        if (el is not null)
            foreach (var a in el.Attributes)
            {
                if (a.Name.StartsWith("data-bind-", StringComparison.Ordinal))
                    bindings.Add(new() { Name = a.Name["data-bind-".Length..], Value = "{{" + a.Value + "}}" });
                else if (a.Name is not ("class" or "id" or CupriDocument.SourceAttribute))
                    attrs.Add(new() { Name = a.Name, Value = a.Value });
            }
        _m.Bindings = bindings;
        _m.BindingsDisplay = bindings.Count > 0 ? "flex" : "none";
        _m.Attributes = attrs;
        _m.AttributesDisplay = attrs.Count > 0 ? "flex" : "none";

        _m.Rules = el is null ? [] : MatchedRules(el);
        _m.RulesDisplay = _m.Rules.Count > 0 ? "flex" : "none";

        var src = Locate();
        _m.SourceText = src is { } loc ? $"{Path.GetFileName(loc.File)}:{loc.Line}" : "";
        _m.SourceDisplay = src is null ? "none" : "flex";
    }

    /// <summary>The rules that match an element, most specific first — what a browser's Styles pane
    /// shows, minus the strike-through: the same property under two rules is decided by the
    /// higher one.</summary>
    private List<RuleLine> MatchedRules(IElement el)
    {
        var lines = new List<RuleLine>();
        if (el.GetAttribute("style") is { Length: > 0 } inline)
        {
            lines.Add(new() { Text = "style=\"…\"", LineCss = "selector" });
            foreach (var decl in inline.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = decl.IndexOf(':');
                if (colon > 0) lines.Add(new() { Text = decl[..colon].Trim(), Value = decl[(colon + 1)..].Trim() });
            }
        }
        var w = _target.ViewportWidth;
        var h = _target.ViewportHeight;
        var matched = new List<CssRule>();
        foreach (var rule in _target.ActiveRules)
        {
            if (rule.Pseudo is not null || rule.Compiled is null) continue;
            if (rule.Media is { } media && !media.Matches(w, h)) continue;
            bool hit;
            try { hit = rule.Compiled.Match(el, null); }
            catch { hit = false; }
            if (hit) matched.Add(rule);
        }
        matched.Sort((a, b) => a.Specificity != b.Specificity ? b.Specificity.CompareTo(a.Specificity) : b.Order.CompareTo(a.Order));
        foreach (var rule in matched)
        {
            lines.Add(new() { Text = rule.Selector + (rule.Media is not null ? "  @media" : ""), LineCss = "selector" });
            foreach (var (k, v) in rule.Declarations) lines.Add(new() { Text = k, Value = v });
        }
        return lines;
    }

    private void BuildProblems()
    {
        var report = _reload?.LastReport;
        if (report is null || report.IsClean)
        {
            _m.Problems = [];
            _m.ProblemsDisplay = report is null ? "none" : "flex";
            _m.ProblemSummary = "none";
            _m.ProblemsWrapCss = "clean";
            return;
        }
        _m.ProblemsDisplay = "flex";
        _m.ProblemsWrapCss = "";
        _m.ProblemSummary = HotReloadSession.Summary(report);
        var rows = new List<ProblemRow>();
        foreach (var f in report.Findings)
        {
            var (file, line) = _reload!.Locate(f);
            var sev = f.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "info" };
            rows.Add(new ProblemRow
            {
                Index = rows.Count,
                Severity = sev,
                Code = f.Code,
                Message = f.Message,
                Where = $"{Path.GetFileName(file)}:{line}",
                ProblemCss = sev,
            });
        }
        _m.Problems = rows;
    }

    // ---- actions ---------------------------------------------------------------------------

    private void RowClicked(CupriPointerEvent e)
    {
        if (!int.TryParse(e.Element.GetAttribute("data-index"), out var i) || i >= _rowPaths.Count) return;
        var path = _rowPaths[i];
        // A click on the disclosure triangle opens or closes; anywhere else on the row selects.
        var onToggle = false;
        for (var n = e.Target; n is not null && !ReferenceEquals(n.Element, e.Element); n = n.Parent)
            if (n.Element?.ClassList.Contains("dt-toggle") == true) { onToggle = true; break; }
        var key = Key(path);
        if (onToggle)
        {
            if (!_expanded.Remove(key)) _expanded.Add(key);
        }
        else _selected = path;
        MarkStale();
    }

    private void ProblemClicked(CupriPointerEvent e)
    {
        if (_reload?.LastReport is not { } report) return;
        if (!int.TryParse(e.Element.GetAttribute("data-index"), out var i) || i >= report.Findings.Count) return;
        var (file, line) = _reload.Locate(report.Findings[i]);
        EditorLauncher.Open(file, line);
    }

    private void OpenSelectedInEditor()
    {
        if (Locate() is { } loc) EditorLauncher.Open(loc.File, loc.Line);
    }

    /// <summary>Where the selected element was written: the markup file and line, found by the
    /// element's id, its first class, or its tag — the nearest ancestor that can be found, for
    /// the insides of a component, which are generated rather than written.</summary>
    private (string File, int Line)? Locate()
    {
        if (_reload?.MarkupFile is not { } file) return null;
        var node = SelectedNode;
        var html = _target.TemplateHtml;
        for (var n = node; n is not null; n = n.Parent)
            if (n.Element is { } el && SourceLocator.LineOf(html, el) is > 0 and var line)
                return (file, line);
        return null;
    }

    private void MoveSelection(int delta)
    {
        if (_rowPaths.Count == 0) return;
        var at = _selected is null ? -1 : _rowPaths.FindIndex(p => SamePath(p, _selected));
        var next = Math.Clamp(at < 0 ? 0 : at + delta, 0, _rowPaths.Count - 1);
        _selected = _rowPaths[next];
        _revealSelected = true;
        MarkStale();
    }

    private void SetExpanded(bool open)
    {
        if (_selected is null) return;
        var key = Key(_selected);
        if (open) _expanded.Add(key);
        else if (!_expanded.Remove(key) && _selected.Length > 0) _selected = _selected[..^1]; // already shut: go to the parent
        MarkStale();
    }

    /// <summary>Scroll the tree so the selected row is in view. True when it moved.</summary>
    private bool RevealSelectedRow()
    {
        if (_selected is null) return false;
        var index = _rowPaths.FindIndex(p => SamePath(p, _selected));
        if (index < 0) return false;
        var tree = Find(_ui.Root, n => n.Element?.ClassList.Contains("dt-tree") == true);
        if (tree is null || tree.Children.Count <= index) return false;
        var row = tree.Children[index];
        var top = row.Y - tree.ContentTopInset;
        var view = tree.ContentBoxHeight;
        var scroll = tree.ScrollY;
        if (top < scroll) tree.ScrollY = top;
        else if (top + row.Height > scroll + view) tree.ScrollY = top + row.Height - view;
        else return false;
        _ui.InvalidateRetainedFrame();
        return true;
    }

    private int? RowIndexAt(float x, float y)
    {
        for (var n = HitTesting.HitTest(_ui.Root, x, y); n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("dt-row") == true
                && int.TryParse(n.Element.GetAttribute("data-index"), out var i))
                return i;
        return null;
    }

    private RenderNode? HitApp(float x, float y)
    {
        var zoom = _target.Zoom <= 0 ? 1 : _target.Zoom;
        var hit = HitTesting.HitTest(_target.Root, x / zoom, y / zoom);
        return hit is { IsText: true } ? hit.Parent : hit;
    }

    // ---- paths: a node's identity across rebuilds --------------------------------------------

    // The app's tree is rebuilt on every interaction, so a RenderNode reference goes stale within a
    // click. Its PATH — child indices from the root — survives, and points at the same element as
    // long as the structure above it has not changed.
    private RenderNode? Resolve(int[]? path)
    {
        if (path is null) return null;
        var n = _target.Root;
        foreach (var i in path)
        {
            if (i >= n.Children.Count) return null;
            n = n.Children[i];
        }
        return n;
    }

    private int[] PathOf(RenderNode node)
    {
        var path = new List<int>();
        for (var n = node; n.Parent is { } p; n = p)
            path.Add(p.Children.IndexOf(n));
        path.Reverse();
        return [.. path];
    }

    private static bool SamePath(int[]? a, int[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static string Key(ReadOnlySpan<int> path)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in path) sb.Append(i).Append('/');
        return sb.ToString();
    }

    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
    {
        if (match(n)) return n;
        foreach (var c in n.Children) if (Find(c, match) is { } f) return f;
        return null;
    }

    private static string Num(float v, string format = "0.#") => v.ToString(format, CultureInfo.InvariantCulture);

    private static string Sides(float t, float r, float b, float l) =>
        t == r && r == b && b == l ? Num(t) : $"{Num(t)} {Num(r)} {Num(b)} {Num(l)}";

    private static string Hex(SKColor c) =>
        c.Alpha == 0 ? "transparent"
        : c.Alpha == 255 ? $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}"
        : $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}{c.Alpha:x2}";

    /// <summary><c>InlineFlex</c> → <c>inline-flex</c>, the way the value is written in CSS.</summary>
    private static string Kebab(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var ch = pascal[i];
            if (char.IsUpper(ch) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_reload is not null) _reload.Reloaded -= OnReloaded;
        _ui.Dispose();
    }
}
