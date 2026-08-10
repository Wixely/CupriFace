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
    private readonly Paint.ImageStore _images;

    private IDocument? _dom;
    private IDocument? _templateDom; // the template parsed once; each rebuild clones it (see Rebuild)
    private RenderNode _root = null!;
    private List<CssRule> _rules = new();   // reused by ReStyle (hover/active without a full rebuild)
    private Dictionary<string, List<Keyframe>> _keyframes = new();
    private List<CssRule>? _cachedRules;    // parsed once (CSS is immutable) and reused every rebuild
    private Dictionary<string, List<Keyframe>>? _cachedKeyframes;
    private float _viewportWidth = 1024f;
    // The last size laid out, and whether the CURRENT tree has been laid out at it. A rebuild/restyle
    // produces a fresh tree with no geometry; hosts lay out once per frame and then dispatch input, so
    // input arriving before that frame would hit-test a tree whose boxes are all zero — see EnsureLaidOut.
    private float _laidOutWidth, _laidOutHeight;
    private bool _layoutDirty = true;
    private bool _hasMedia;
    private readonly Style.TransitionEngine _transitions = new();

    // Hover + drag + text-focus state
    private readonly List<IElement> _hoverChain = new();
    private readonly List<IElement> _activeChain = new(); // :active — the pressed element + ancestors
    private string? _focusKey;  // the focused field's bound path (survives rebuilds)
    private bool _focusNumeric; // focused field is validated as a number
    private bool _focusMultiline; // focused field is a textarea (Enter inserts a newline)
    private bool _focusMask;    // focused field masks its text (data-mask, e.g. <cupri-password>)
    private int _maskRevealPos = -1;          // index of a masked char being briefly "peeked" after typing; -1 = none
    private double _maskRevealStart = double.NaN; // Animate() clock time the peek began (NaN = not yet stamped)
    private double? _focusMin, _focusMax; // numeric field bounds (for validation/clamping)
    private bool _focusRequired;          // focused field's validation rules (for the mid-edit red border)
    private string? _focusPattern;
    private int? _focusMinLen;
    private readonly HashSet<string> _touched = new(); // fields visited (blurred) → their error text may show
    private bool _validateAll;            // set by ValidateAll() (form submit) → show every field's error
    private string? _editBuffer; // raw text being edited (permissive); validated/committed on blur
    private int _caret;
    private int _selAnchor;      // selection anchor; selection is [min(anchor,caret), max]. anchor==caret ⇒ none.
    private int _listHi = -1;    // highlighted option index for a focused listbox field (combobox); -1 = none
    private int _gridHi = -1;    // highlighted cell index for an open [data-gridnav] overlay (datepicker); -1 = none
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
    // Each <cupri-virtual> list's scroll offset by its data-repeat path, so the next rebuild windows it to
    // the rows in view. Updated when a virtual list scrolls (which then rebuilds to re-window).
    private readonly Dictionary<string, double> _virtualScroll = new();
    private RenderNode? _resizeDrag;    // node whose resize grip is being dragged
    private float _resizeX0, _resizeY0, _resizeW0, _resizeH0;

    // Drag-to-reorder: the list being reordered, its item nodes, the source/target slot, the grab Y, each
    // item's original mid-line (for slot hit-testing), and the per-slot shift distance.
    // Drag-to-reorder — one list, or several columns of a kanban board (all .cupri-reorder lists under a
    // shared .cupri-board). The lifted card follows the pointer across columns; the source column closes
    // its gap and the target column opens one; on drop OnReorder carries the source + target lists.
    private RenderNode? _reorderList;      // the source column the drag started in
    private RenderNode? _reorderCard;      // the lifted card (Dragging = true)
    private List<RenderNode>? _reorderItems; // every card across the group's columns (for the ease pass)
    private readonly record struct ReorderCol(RenderNode List, List<RenderNode> Items, float[] Mids, float Left, float Right);
    private List<ReorderCol>? _reorderCols; // per-column geometry for target/gap computation
    private int _reorderFromCol, _reorderToCol, _reorderFrom, _reorderTo;
    private float _reorderX0, _reorderY0, _reorderShift;
    private double _reorderAnimT = double.NaN; // previous ease time, for frame-rate-independent smoothing
    private Action<ReorderEvent>? _onReorder;

    // Split pane: the two panels either side of the divider being dragged, the drag axis, the grab point,
    // and the pair's initial pixel sizes + total flex-grow (kept constant so other panels don't move).
    private RenderNode? _splitA, _splitB;
    private bool _splitVertical;
    private float _splitStart, _splitPA0, _splitPB0, _splitGSum;

    // The last pointer hit-test (from UpdateHover), reused by CursorAt so a host's move→cursor pair costs
    // one tree walk, not two. Valid only for the same coordinates on the SAME tree — _lastHitRoot guards
    // against a restyle/rebuild having replaced the nodes in between.
    private RenderNode? _lastHit, _lastHitRoot;
    private float _lastHitX = float.NaN, _lastHitY = float.NaN;

    // Table column resize: the bound width-list path, the dragged column, the grab origin (its content
    // width + pointer x), and the list as it stood when the drag began. All value types (the width list is
    // written to the model each move, which rebuilds), so nothing here dangles across the per-move rebuild.
    private string? _colPath;
    private int _colIndex;
    private float _colStartW, _colStartX, _colMaxW;
    private string[] _colList = [];

    /// <summary>Narrowest a dragged column may get — enough to stay grabbable and show a little content.</summary>
    private const float MinColumnW = 24f;

    /// <summary>A drag-to-reorder drop: the source list, the item's index in it (<see cref="From"/>), and the
    /// target index (<see cref="To"/>) in <see cref="ToList"/>. For a single list (or a within-column move)
    /// <see cref="ToList"/> equals <see cref="List"/>; across kanban columns they differ. Register a handler
    /// with <see cref="OnReorder"/>; it moves the item from the source list to the target.</summary>
    public readonly record struct ReorderEvent(IElement List, int From, int To, IElement ToList);
    public CupriDocument OnReorder(Action<ReorderEvent> handler) { _onReorder = handler; return this; }

    // Right-click context menu (Cut/Copy/Paste/Select-all) over a text field. The engine owns
    // opening/positioning/rendering/dismissing it; the host performs the chosen clipboard action.
    private bool _ctxOpen;
    private float _ctxX, _ctxY;
    private bool _ctxHasSelection;  // enables Cut/Copy
    private bool _ctxHasText;       // enables Select All
    // A custom <cupri-context-menu> region's menu, opened at (_ctxX,_ctxY). Identified by the host's
    // document-order index (stable across rebuilds; element identity and generated ids are not). -1 = none.
    private int _ctxCustomIndex = -1;

    /// <summary>Raised when a context-menu item is chosen. The host performs the clipboard action
    /// (via <see cref="CopySelection"/>/<see cref="CutSelection"/>/<see cref="DispatchKey"/> +
    /// its own clipboard), keeping platform clipboard code out of the engine.</summary>
    public event Action<Interaction.ContextCommand>? ContextRequested;

    /// <summary>Raised when a link (<c>&lt;a href&gt;</c>) is activated with a non-anchor href (see
    /// <see cref="Interaction.NavigateEvent"/>). In-page <c>#anchor</c> links are scrolled into view by the
    /// engine and do not raise this. Multicast: an app can route internal hrefs (e.g. switch a view) while a
    /// host opens external ones in a browser — the engine itself opens nothing (that's a host concern).</summary>
    public event Action<Interaction.NavigateEvent>? Navigated;

    // Engine-owned toast stack (doc.Toast). Each toast slides in, waits, then slides out and is removed —
    // driven by Animate. Entering/Leaving render off-screen; the flip to/from Shown is what the transition
    // engine animates (paint-only). Rendered bottom-right by InjectToaster + the ToasterComponent's CSS.
    private enum ToastPhase { Entering, Shown, Leaving }
    private sealed class ToastEntry { public string Msg = ""; public string Kind = ""; public ToastPhase Phase; public double T = double.NaN; }
    private readonly List<ToastEntry> _toasts = new();
    private const double ToastShowSeconds = 3.6, ToastExitSeconds = 0.36;
    private bool ToastsPending => _toasts.Count > 0;

    /// <summary>Raise a transient toast (bottom-right stack). <paramref name="kind"/> may be
    /// <c>"success"</c> or <c>"error"</c> to tint it. It slides in, sits a few seconds, then slides out.</summary>
    public void Toast(string message, string kind = "")
    {
        _toasts.Add(new ToastEntry { Msg = message, Kind = kind });
        Rebuild(); // render it (off-screen); Animate flips it in on the next frame
    }

    // Per-field undo/redo history (cleared on focus change). A snapshot of the edit buffer + caret.
    private readonly record struct EditState(string Buffer, int Caret, int Anchor);
    private readonly List<EditState> _undo = new();
    private readonly List<EditState> _redo = new();
    private bool _typingGroup;          // coalesce a run of printable chars into one undo step

    private CupriDocument(string html, string? css)
    {
        _templateHtml = html;
        _css = css;
        _fonts = new FontService();
        _images = new Paint.ImageStore();
        _layout = new LayoutEngine(_fonts, _images);
        _painter = new Painter(_images);
        _rasterizer = new SkiaRasterizer(_fonts);
    }

    /// <summary>Register the assembly used to resolve embedded image sources (e.g. a bare
    /// <c>src="Assets/logo.png"</c> on a <c>&lt;cupri-image&gt;</c>). Data URIs, URLs and file paths
    /// need no assembly.</summary>
    public CupriDocument UseImages(System.Reflection.Assembly assembly)
    {
        _images.SetAssembly(assembly);
        return this;
    }

    /// <summary>Policy for remote (<c>http(s)</c>) image URLs (https-only, size cap, host allow-list…).
    /// Defaults are strict; override to e.g. allow a specific host.</summary>
    public CupriDocument UseImageUrlOptions(Resources.CupriSourceOptions options)
    {
        _images.UrlOptions = options;
        return this;
    }

    /// <summary>True (once, then reset) if a background image load finished since the last call. A
    /// render-on-demand host repaints when this returns true, so an async remote image appears.</summary>
    public bool ConsumeImageArrived() => _images.TakeArrived();

    public RenderNode Root => _root;

    /// <summary>Dev aid: outline every element's box on top of the paint (scroll containers in blue),
    /// to inspect layout in the live window. Off by default.</summary>
    public bool DebugOverlay { get => _painter.DebugOutline; set => _painter.DebugOutline = value; }

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

    /// <summary>Register a font from raw TTF/OTF bytes (e.g. an embedded resource). Registered faces
    /// are consulted before platform fonts, and the first registered family becomes the target of the
    /// generic families (<c>sans-serif</c> etc.) — essential in the browser, where the wasm Skia build
    /// embeds only a monospace face. Register each style you use (Regular, Bold, …) before rendering.</summary>
    public CupriDocument LoadFont(byte[] fontData)
    {
        _fonts.RegisterFont(fontData);
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

        // A rebuild starts from a fresh DOM, so scroll offsets on the fresh tree would reset — carry
        // them over (keyed by structural path, since element identity isn't stable across rebuilds).
        var scroll = CaptureScroll();
        CancelOrphanedPointerDrags(); // any node the pointer was dragging is about to be orphaned

        _dom?.Dispose();
        // Parse the (immutable) template ONCE; every rebuild — each keystroke — deep-clones the parsed
        // DOM instead of re-tokenizing the HTML string, which is severalfold cheaper.
        _templateDom ??= new HtmlParser().ParseDocument(_templateHtml);
        var dom = (IDocument)_templateDom.Clone();
        Mark("parse-html");

        if (_model is not null)
            BindingEngine.Apply(dom, _model, key => _virtualScroll.GetValueOrDefault(key)); // window each <cupri-virtual>
        Mark("bind");

        // Expand custom elements after binding so components see concrete attribute values.
        _components?.Expand(dom);
        Mark("expand-components");

        // Re-apply text focus across the rebuild (typing rebuilds the DOM each keystroke), and
        // paint the raw edit buffer (which may be invalid) over the bound value, flagging
        // data-invalid so the field can show a red border while the user is mid-edit.
        var focusEl = _focusKey is not null ? dom.QuerySelector($"[data-bind-value=\"{_focusKey}\"]") : null;

        // Auto-focus a field that asked for it (e.g. a command palette's search) when no text field is
        // already focused — so opening the palette lands the caret in the search box, ready to type. Only
        // present while its overlay is open, so it never steals focus otherwise.
        if (focusEl is null && dom.QuerySelector("[data-autofocus][data-bind-value]") is { } af)
        {
            _focusKey = af.GetAttribute("data-bind-value");
            _editBuffer = null;
            var qlen = (_model is not null ? BindingEngine.Resolve(_model, _focusKey!)?.ToString() : null)?.Length ?? 0;
            _caret = _selAnchor = qlen; // caret at the end of any existing text
            focusEl = af;
        }

        if (focusEl is not null)
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
                    else // mask secret fields (data-mask): paint bullets, keep the plaintext in the buffer
                        anchor.TextContent = MaskText(_editBuffer);
                }
                if (!BufferValid(_editBuffer)) focusEl.SetAttribute("data-invalid", "");
            }
        }

        Mark("focus");

        // The context menu is engine-owned, so re-inject it on every rebuild (the DOM re-parses
        // from the template each time) while it's open — same approach as focus re-application.
        if (_ctxOpen) InjectContextMenu(dom);
        if (_ctxCustomIndex >= 0) RevealCustomContextMenu(dom); // a <cupri-context-menu>'s popup, opened at the pointer
        if (_toasts.Count > 0) InjectToaster(dom);              // the engine-owned toast stack (doc.Toast)

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

        ApplyValidation(dom); // inject inline error messages before the tree is built
        Mark("validate");

        _hoverChain.Clear();
        _root = new StyleResolver(_rules, _viewportWidth).BuildTree(dom);
        _layoutDirty = true; // fresh tree: no geometry until the next layout
        RestoreScroll(scroll);
        _transitions.Detect(_root); // (re)start transitions whose target value changed this rebuild
        Mark("style+tree");
        _hasActiveAnim = _keyframes.Count > 0 && AnyAnimated(_root);
        _dom = dom;
    }

    // Per-node interaction state preserved across a rebuild, keyed by structural path (child-index
    // chain from root) since the DOM — and thus element identity — is re-parsed each rebuild.
    private readonly record struct NodeState(float ScrollY, bool AtBottom, bool FollowTail, float? ResizeW, float? ResizeH, float ScrollX, float NaturalHeight, float DisplayHeight, float? SplitGrow);

    // A height transition needs the element's natural (auto) height at Detect time, which runs on the
    // freshly-rebuilt tree before it's laid out — so carry the last measured value across the rebuild.
    private static bool HasHeightTransition(Style.ComputedStyle s)
    {
        if (s.Transitions is not { Count: > 0 } specs) return false;
        foreach (var sp in specs) if (sp.Property is "height" or "all") return true;
        return false;
    }

    private Dictionary<string, NodeState>? CaptureScroll()
    {
        if (_root is null) return null;
        Dictionary<string, NodeState>? map = null;
        void Walk(RenderNode n)
        {
            var tail = n.Element?.HasAttribute("data-follow-tail") == true;
            var scroll = n.Style.Overflow == OverflowMode.Scroll && (n.ScrollY > 0.01f || tail);
            var hTrans = n.ContentNaturalHeight > 0 && HasHeightTransition(n.Style);
            if (scroll || n.ResizeW is not null || n.ResizeH is not null || n.ScrollX > 0.01f || hTrans || n.SplitGrow is not null)
            {
                // A node not laid out this cycle (a rebuild landed before the next layout) has a stale 0
                // height — carry its last real displayed height (PrevHeight) forward instead, so a height
                // transition doesn't think an open panel collapsed to nothing.
                var displayH = n.LaidOut ? n.Height : n.PrevHeight;
                (map ??= new())[PathOf(n)] = new NodeState(n.ScrollY, n.ScrollY >= n.MaxScrollY - 1f, tail, n.ResizeW, n.ResizeH, n.ScrollX, n.ContentNaturalHeight, displayH, n.SplitGrow);
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        return map;
    }

    private void RestoreScroll(Dictionary<string, NodeState>? map)
    {
        if (map is null) return;
        void Walk(RenderNode n)
        {
            if (map.TryGetValue(PathOf(n), out var s))
            {
                if (n.Style.Overflow == OverflowMode.Scroll)
                    n.ScrollY = s.FollowTail && s.AtBottom ? float.MaxValue : s.ScrollY; // MaxValue → new bottom (tail-follow)
                n.ResizeW = s.ResizeW;
                n.ResizeH = s.ResizeH;
                n.ScrollX = s.ScrollX;
                if (s.NaturalHeight > 0) n.ContentNaturalHeight = s.NaturalHeight; // seed a height transition's auto target
                n.PrevHeight = s.DisplayHeight;                                     // …and the height it animates from
                n.SplitGrow = s.SplitGrow;                                          // …and a dragged split ratio
            }
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

    /// <summary>Advance @keyframes animations and in-flight CSS transitions to the given elapsed time.
    /// Mostly paint-only; a <c>transition: height</c> writes a definite height that the following layout
    /// honours, so the element and its siblings reflow. Returns true if anything animated this frame.</summary>
    public bool Animate(double timeSeconds)
    {
        var any = false;
        // Advance the toast stack first: a phase flip rebuilds and Detects a new opacity/transform target,
        // so the transition below applies it the SAME frame — the toast starts from its off-screen state
        // instead of flashing at the target for one frame before the transition kicks in.
        if (_toasts.Count > 0 && StepToasts(timeSeconds)) any = true;
        if (_keyframes.Count > 0) { Animation.Apply(_root, _keyframes, timeSeconds); any = true; }
        if (_transitions.Apply(_root, timeSeconds)) any = true; // interpolate transitions over @keyframes
        if (_maskRevealPos >= 0) // a masked field is peeking its last-typed char — time it out
        {
            if (double.IsNaN(_maskRevealStart)) _maskRevealStart = timeSeconds;      // stamp on the first frame
            else if (timeSeconds - _maskRevealStart >= MaskPeekSeconds)              // window elapsed → re-mask
            {
                _maskRevealPos = -1; _maskRevealStart = double.NaN;
                Rebuild(); // re-bake the field text fully masked (paint-only content swap)
            }
            any = true;
        }
        if (EaseReorder(timeSeconds)) any = true; // slide reorder rows into their gap
        return any;
    }

    // True while a masked field is peeking its last-typed char — keeps the host's frame pump alive
    // (folded into both continuous-render signals below) so Animate() can time the peek out.
    private bool MaskPeeking => _maskRevealPos >= 0;

    public bool HasAnimations => _keyframes.Count > 0;

    /// <summary>True while a CSS transition is mid-flight (a continuous host should keep calling
    /// <see cref="Animate"/> and repainting until it settles). Also true while a masked field is
    /// peeking its last-typed char, so the host keeps ticking until <see cref="Animate"/> re-masks it.</summary>
    public bool HasActiveTransitions => _transitions.Active || MaskPeeking || ReorderEasing || ToastsPending;

    /// <summary>True only if a *visible* node is currently animating (display:none subtrees are
    /// absent from the render tree). Lets a host render continuously only when it must, instead
    /// of every frame — critical for the CPU-rendered web host. Cached per rebuild (the animated
    /// set only changes when the tree does), so a host may poll it every frame for free. Also true
    /// while a masked field peeks its last-typed char (see <see cref="HasActiveTransitions"/>).</summary>
    public bool HasActiveAnimations => _hasActiveAnim || _transitions.Active || MaskPeeking || ReorderEasing || ToastsPending;
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
        var list = BuildFrame(width, height);
        _rasterizer.Paint(canvas, list);
    }

    // The previously-presented frame's commands, for RenderIncremental's damage diff. Only that path
    // writes these — Render/RenderToImage stay stateless full repaints.
    private IReadOnlyList<Paint.PaintCommand>? _lastPresented;
    private float _lastPresentedW, _lastPresentedH;

    /// <summary>Render for a host whose canvas RETAINS its pixels between frames (the SDL software
    /// bitmap, the WASM staging bitmap): diffs this frame's display list against the last one presented,
    /// clips the repaint to the damaged rectangle, and returns that rectangle — or <c>null</c> when the
    /// frame is identical, in which case nothing was drawn and the host can skip presenting entirely.
    /// The very first call (and any viewport size change) repaints in full. Only valid when every frame
    /// lands on the SAME retained canvas; one-shot renders should use <see cref="Render"/>.
    /// The result is visually identical to a full render; anti-aliased pixels of primitives crossing the
    /// damage boundary may differ from a monolithic render by a couple of least-significant bits (Skia
    /// computes AA coverage against the clip).</summary>
    public SKRectI? RenderIncremental(SKCanvas canvas, float width, float height, SKColor background)
    {
        var list = BuildFrame(width, height);
        var prev = _lastPresentedW == width && _lastPresentedH == height ? _lastPresented : null;
        var damage = Paint.DamageDiff.Compute(prev, list.Commands, width, height);
        _lastPresented = list.Commands;
        _lastPresentedW = width; _lastPresentedH = height;
        if (damage.IsEmpty) return null;                       // identical frame — nothing to present

        var rect = SKRectI.Ceiling(damage);
        canvas.Save();
        canvas.ClipRect(rect);
        canvas.Clear(background);                              // Clear respects the clip
        _rasterizer.Paint(canvas, list);                       // full list; Skia rejects outside the clip
        canvas.Restore();
        return rect;
    }

    /// <summary>The UI-thread half of the commit-snapshot seam: lay out, scroll the caret into view,
    /// and paint the render tree (with caret/selection/focus-ring) into an immutable
    /// <see cref="DisplayList"/> — <b>without rasterising</b>. A threaded host hands this to a render
    /// thread (see <c>ThreadedPresenter</c>); <see cref="Render"/> just rasterises it inline.</summary>
    public DisplayList BuildFrame(float width, float height)
    {
        // @media depends on viewport width — re-resolve styles when it changes.
        if (_hasMedia && Math.Abs(width - _viewportWidth) > 0.5f)
        {
            _viewportWidth = width;
            Rebuild();
        }
        _layout.Layout(_root, width, height);
        _laidOutWidth = width; _laidOutHeight = height; _layoutDirty = false;
        ScrollCaretIntoView();  // after layout, before paint: keep the caret visible in a scrolled field
        ScrollCaretIntoViewX(); // and horizontally, in a single-line (nowrap) field

        var list = _painter.Build(_root);
        // Caret + selection are drawn outside the scrolled subtree, so clip them to the focused
        // field's scroll box — otherwise they'd draw over neighbours when the field is scrolled.
        var clip = CaretClipRect();
        if (clip is { } c) list.Add(new PushClip(c.X, c.Y, c.W, c.H, c.Radius));
        AppendSelection(list);
        AppendCaret(list);
        if (clip is not null) list.Add(new PopClip());
        AppendFocusRing(list);
        return list;
    }

    private const char MaskChar = '•'; // • — the bullet a masked (data-mask) field paints per character
    private const double MaskPeekSeconds = 1.4; // how long a masked field shows its last-typed char

    // Mask <paramref name="plain"/> for a data-mask field: one bullet per UTF-16 unit (so caret/
    // selection/scroll offsets line up 1:1 with the mask), except the one character being peeked
    // (_maskRevealPos) after a keystroke, which stays visible until Animate() expires the peek.
    private string MaskText(string plain)
    {
        if (!_focusMask) return plain;
        var arr = new char[plain.Length];
        for (var i = 0; i < plain.Length; i++) arr[i] = i == _maskRevealPos ? plain[i] : MaskChar;
        return new string(arr);
    }

    // The text to DISPLAY/measure for the focused field: the raw edit buffer (or the bound value),
    // masked when the field opted in via data-mask (e.g. <cupri-password>).
    private string FocusedDisplayText()
    {
        var value = _editBuffer ?? (_focusKey is null ? null : BindingEngine.Resolve(_model, _focusKey)?.ToString()) ?? "";
        return MaskText(value);
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
        var value = FocusedDisplayText();
        var caret = Math.Clamp(_caret, 0, value.Length);
        var t = RowForCaret(BuildTextRows(anchor, value), caret); // wrap-aware for textarea + wrapped field
        float rowY = t.Y, rowH = t.Height;

        var (_, scY) = PaintedTopLeft(sc);
        var bandTop = scY + sc.ContentTopInset;
        var bandBottom = bandTop + sc.ContentBoxHeight;
        var newScroll = sc.ScrollY;
        if (rowY < bandTop) newScroll = sc.ScrollY + (rowY - bandTop);              // scroll up to reveal
        else if (rowY + rowH > bandBottom) newScroll = sc.ScrollY + (rowY + rowH - bandBottom); // down
        sc.ScrollY = Math.Clamp(newScroll, 0, sc.MaxScrollY);
    }

    // Keep the caret horizontally visible in a single-line (white-space:nowrap) field by scrolling its
    // content — mirrors ScrollCaretIntoView on the X axis. Preserved across rebuilds via NodeState.
    private void ScrollCaretIntoViewX()
    {
        if (_focusKey is null) return;
        var field = FindFocused(_root);
        if (field is null || field.Style.WhiteSpace != WhiteSpaceMode.NoWrap) return;
        if (field.Element?.HasAttribute("data-multiline") == true) return;

        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        var caret = Math.Clamp(_caret, 0, value.Length);
        var caretX = _fonts.MeasureText(anchor.Style, value[..caret]); // from the text start
        var full = value.Length == 0 ? 0 : _fonts.MeasureText(anchor.Style, value);
        var boxW = field.ContentBoxWidth;

        var sx = field.ScrollX;
        if (caretX - sx < 0) sx = caretX;                    // caret ran off the left → reveal it
        else if (caretX - sx > boxW) sx = caretX - boxW;     // ran off the right → reveal it
        field.ScrollX = Math.Clamp(sx, 0, MathF.Max(0, full - boxW));
    }

    // The focused field's scroll-container content box (painted), for clipping caret/selection; null if none.
    private (float X, float Y, float W, float H, float Radius)? CaretClipRect()
    {
        if (_focusKey is null) return null;
        var focused = FindFocused(_root);
        // A single-line (nowrap) field scrolls horizontally under overflow:hidden — clip the caret and
        // selection to its content box so they never draw past the field edge when scrolled.
        var sc = ScrollableContainer(focused)
            ?? (focused is { } f && f.Style.WhiteSpace == WhiteSpaceMode.NoWrap ? f : null);
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
        var value = FocusedDisplayText();
        var caret = Math.Clamp(_caret, 0, value.Length);
        var ch = anchor.Style.FontSize * 1.1f;

        // Place the caret at its real painted position via wrap-aware visual rows — correct for a
        // textarea AND for a single-line field whose long value has soft-wrapped to several rows.
        var rows = BuildTextRows(anchor, value);
        var target = RowForCaret(rows, caret);
        var col = Math.Clamp(caret - target.Start, 0, target.Text.Length);
        var cx = target.X + _fonts.MeasureText(anchor.Style, target.Text[..col]);
        var cy = target.Y + (target.Height - ch) / 2f;
        list.Add(new FillRect(cx, cy, 2f, ch, 0f, anchor.Style.Color));
    }

    // Draw the text selection highlight (behind the text) for the focused field.
    private void AppendSelection(DisplayList list)
    {
        if (_focusKey is null || _selAnchor == _caret) return;
        var field = FindFocused(_root);
        if (field is null) return;
        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        int s = Math.Clamp(Math.Min(_selAnchor, _caret), 0, value.Length);
        int e = Math.Clamp(Math.Max(_selAnchor, _caret), 0, value.Length);
        if (s == e) return;

        var ch = anchor.Style.FontSize * 1.1f;
        var sel = new SKColor(0x2F, 0x6F, 0xED, 0x40); // translucent selection blue

        // Per visual row (wrap-aware) highlight — for both a textarea and a soft-wrapped single-line
        // field. A selected newline at a logical line's end shows a small trailing sliver.
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
            // Textarea: one line-<div> per logical line, each wrapping a text node. Single-line field:
            // no line divs — the value text node sits directly under the anchor span. Handle both.
            var textNode = (div ?? anchor).Children.FirstOrDefault(c => c.IsText);

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

        // Pick the visual (wrap-aware) row nearest y, then the nearest column within it. Works for
        // both a textarea and a single-line field whose long value soft-wraps to several rows.
        _ = field;
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

    // Painted top-left of a node: like HitTesting.AbsoluteBox but subtracts each scrollable ancestor's
    // clamped scroll offset, so caret/selection line up with where the Painter actually draws the text
    // (the Painter shifts a scrollable node's children up by its ScrollY).
    private static (float X, float Y) PaintedTopLeft(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X; y += n.Y;
            if (n.Parent is { } p)
            {
                if (p.IsScrollable) y -= Math.Clamp(p.ScrollY, 0, p.MaxScrollY);
                x -= p.ScrollX; // horizontal caret-follow shift (0 unless a single-line field)
            }
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

    // Is (x,y) over the resize grip (bottom-right corner) of a resizable node?
    private bool InResizeGrip(RenderNode n, float x, float y)
    {
        if (n.Style.Resize == ResizeMode.None) return false;
        var (ax, ay) = PaintedTopLeft(n);
        float right = ax + n.Width, bottom = ay + n.Height;
        const float hot = 18f;
        return x >= right - hot && x <= right + 2 && y >= bottom - hot && y <= bottom + 2;
    }

    // Clamp a dragged border-box size to a floor + the element's min/max-* (resolved approximately).
    private float ClampResize(RenderNode n, float value, bool horizontal)
    {
        var s = n.Style;
        float min = 24f, max = float.MaxValue;
        if (horizontal)
        {
            if (s.MinWidth.IsDefinite) min = MathF.Max(min, s.MinWidth.Resolve(_viewportWidth) + n.HorizontalInsets);
            if (s.MaxWidth.IsDefinite) max = s.MaxWidth.Resolve(_viewportWidth) + n.HorizontalInsets;
        }
        else
        {
            if (s.MinHeight.IsDefinite) min = MathF.Max(min, s.MinHeight.Resolve(_viewportWidth) + n.VerticalInsets);
            if (s.MaxHeight.IsDefinite) max = s.MaxHeight.Resolve(_viewportWidth) + n.VerticalInsets;
        }
        return Math.Clamp(value, min, MathF.Max(min, max));
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
    // Registered selector → handler. The selector is COMPILED once here (see Matches) rather than
    // re-parsed on every click, hover and cursor query.
    private readonly List<(string Selector, AngleSharp.Css.Dom.ISelector? Compiled, Action<CupriPointerEvent> Handler)> _clickHandlers = new();

    /// <summary>Register a click handler matched by CSS selector (bubbles from target up).</summary>
    public CupriDocument OnClick(string selector, Action<CupriPointerEvent> handler)
    {
        _clickHandlers.Add((selector, CompileSelector(selector), handler));
        return this;
    }

    private readonly Dictionary<string, Action> _shortcuts = new();

    /// <summary>Register a keyboard shortcut. <paramref name="mods"/> is usually <see cref="KeyMods.Ctrl"/>
    /// (Cmd maps to it on macOS); <paramref name="key"/> is the character, e.g. <c>"k"</c>. A Ctrl shortcut
    /// fires anywhere (even while editing a field); a plain-key one only fires when no field is focused.
    /// The host must deliver the chord — the built-in Web/Viewer hosts forward Ctrl/Cmd + letter.</summary>
    public CupriDocument OnShortcut(KeyMods mods, string key, Action handler)
    {
        _shortcuts[ShortcutKey(mods, key)] = handler;
        return this;
    }
    private static string ShortcutKey(KeyMods mods, string key) => (mods.HasFlag(KeyMods.Ctrl) ? "ctrl+" : "") + key.ToLowerInvariant();

    // Custom interaction primitives (extensibility): a data-* attribute → behaviour, alongside the
    // engine's built-in vocabulary (data-set-path / data-cupri-toggle / …). Fires on click AND on
    // keyboard activation (Enter/Space) since both route through ActivateFrom.
    private readonly List<(string Attr, Func<Interaction.CupriActionEvent, bool> Handler)> _actionHandlers = new();

    /// <summary>Register a custom activation primitive: when a clicked/activated element (or an
    /// ancestor) carries <paramref name="dataAttribute"/> (e.g. <c>"data-sort-by"</c>), the handler
    /// runs with the element, its attribute value and the model. Return true if it handled the event
    /// (stops bubbling + triggers a refresh). Lets components define new interactions without an engine
    /// change — the registration point the built-in <c>data-*</c> hooks were missing for third parties.</summary>
    public CupriDocument OnAction(string dataAttribute, Func<Interaction.CupriActionEvent, bool> handler)
    {
        _actionHandlers.Add((dataAttribute, handler));
        return this;
    }

    public RenderNode? HitTest(float x, float y) { EnsureLaidOut(); return HitTesting.HitTest(_root, x, y); }

    /// <summary>Lay out the current tree if it hasn't been, at the last size a frame used. A rebuild or
    /// restyle throws away the laid-out tree, and hosts render once per frame and dispatch input in
    /// between — so input arriving after a hover/model change but before the next frame would hit-test a
    /// tree whose boxes are all zero, and be silently swallowed (a click landing in the same frame as the
    /// hover that preceded it did nothing). Every hit-testing entry point calls this first. It's a no-op
    /// on the common path (the frame just laid out), and the work it does is what the next frame would
    /// have done anyway.</summary>
    private void EnsureLaidOut()
    {
        if (!_layoutDirty || _laidOutWidth <= 0 || _laidOutHeight <= 0) return;
        _layout.Layout(_root, _laidOutWidth, _laidOutHeight);
        _layoutDirty = false;
    }

    /// <summary>The cursor the host should show at (x, y). A drag in progress dictates it; otherwise the
    /// drag affordance under the pointer (a resize grip or a resizable table's column edge) wins, then the
    /// nearest explicit CSS <c>cursor</c>, then one inferred from the element (pointer over links / buttons /
    /// clickables, text over text fields). Hosts call this after each move and map it to a platform cursor.</summary>
    public Style.CursorType CursorAt(float x, float y)
    {
        EnsureLaidOut();
        // 1) An active drag owns the cursor regardless of what's under the pointer — the pointer routinely
        //    leaves the grabbed control mid-drag, and flickering to whatever it passes over reads as a
        //    broken drag. Every drag the document can be in is listed here.
        if (_colPath is not null) return Style.CursorType.EwResize;
        if (_splitA is not null) return _splitVertical ? Style.CursorType.NsResize : Style.CursorType.EwResize;
        if (_reorderItems is not null) return Style.CursorType.Grabbing;
        if (_scrollDrag is not null) return Style.CursorType.Default;  // scrollbar thumb
        if (_dragging) return Style.CursorType.Pointer;                // slider thumb
        if (_textDrag) return Style.CursorType.Text;                   // drag-selecting in a field
        if (_resizeDrag is { } rd) return rd.Style.Resize switch
        {
            Style.ResizeMode.Horizontal => Style.CursorType.EwResize,
            Style.ResizeMode.Vertical => Style.CursorType.NsResize,
            _ => Style.CursorType.NwseResize,
        };

        // Reuse the hit the move dispatch just recorded for these exact coordinates on this same tree —
        // hosts call CursorAt right after DispatchPointerMove, so this usually saves the second tree walk.
        // (A restyle/rebuild replaces the tree, which invalidates the cache by identity; EnsureLaidOut
        // above guarantees the fresh tree has geometry, so re-hit-testing it is correct.)
        var hit = ReferenceEquals(_lastHitRoot, _root) && _lastHitX == x && _lastHitY == y
            ? _lastHit
            : HitTesting.HitTest(_root, x, y);
        if (hit is null) return Style.CursorType.Default;

        // 2) Drag affordances under the pointer (a corner grip / a column boundary) — before CSS, so they
        //    beat an inherited cursor the way the grab itself takes priority over a normal click.
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (InResizeGrip(n, x, y)) return n.Style.Resize switch
            {
                Style.ResizeMode.Horizontal => Style.CursorType.EwResize,
                Style.ResizeMode.Vertical => Style.CursorType.NsResize,
                _ => Style.CursorType.NwseResize,
            };
            if (ColumnBoundaryAt(n, x) is not null) return Style.CursorType.EwResize;
        }

        // 3) The nearest explicit CSS cursor (Auto = unspecified → keep looking up the chain).
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Style.Cursor != Style.CursorType.Auto) return n.Style.Cursor;

        // 4) Inferred: disabled controls → not-allowed; text fields → text; anything that acts on a
        //    click → pointer. Checked innermost-out, so a disabled control inside a clickable row wins.
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (n.Element is not { } el) continue;
            if (IsDisabled(el)) return Style.CursorType.NotAllowed;
            if (el.GetAttribute("role") is "textbox" or "spinbutton") return Style.CursorType.Text;
            if (ActsOnClick(el)) return Style.CursorType.Pointer;
        }

        // 5) A checkbox/radio/switch's text label activates the control (HTML <label> behaviour), so it
        //    gets the control's cursor — same walk the click uses, so the two always agree.
        return LabelTargets(hit).Any() ? Style.CursorType.Pointer : Style.CursorType.Default;
    }

    /// <summary>Would a click on this element (not its ancestors) do something? The single definition
    /// behind the pointer cursor: the interactive roles, links, every built-in <c>data-*</c> activation
    /// hook, and — the ones easily forgotten — app-registered <see cref="OnClick"/> selectors and
    /// <see cref="OnAction"/> attributes, which <see cref="ActivateFrom"/> honours and so must show a
    /// pointer too (a sidebar nav row wired only by <c>OnClick</c> is the canonical case).</summary>
    private bool ActsOnClick(IElement el) =>
        el.GetAttribute("role") is "link" or "button" or "switch" or "checkbox" or "radio" or "slider"
        || el.LocalName is "a" or "button"
        || el.HasAttribute("data-set-path") || el.HasAttribute("data-set-toggle")
        || el.HasAttribute("data-cupri-toggle") || el.HasAttribute("data-cupri-dismiss")
        || el.HasAttribute("data-cupri-step")
        || _actionHandlers.Exists(h => el.HasAttribute(h.Attr))
        || _clickHandlers.Exists(h => Matches(el, h.Compiled));

    /// <summary>A control the author marked unavailable (<c>aria-disabled</c>, or the <c>disabled</c>
    /// class/attribute the components use, e.g. a pagination arrow on the first page). It keeps its role
    /// for a11y but drops its activation hooks, so the cursor must not promise a click.</summary>
    private static bool IsDisabled(IElement el) =>
        el.ClassList.Contains("disabled")
        || el.HasAttribute("disabled")
        || el.GetAttribute("aria-disabled") is "true";

    /// <summary>The CSS <c>cursor</c> keyword for a <see cref="Style.CursorType"/> — for web hosts that set
    /// <c>canvas.style.cursor</c> from <see cref="CursorAt"/>.</summary>
    public static string CursorCss(Style.CursorType c) => c switch
    {
        Style.CursorType.Pointer => "pointer",
        Style.CursorType.Text => "text",
        Style.CursorType.Wait => "wait",
        Style.CursorType.Progress => "progress",
        Style.CursorType.Help => "help",
        Style.CursorType.Crosshair => "crosshair",
        Style.CursorType.Move => "move",
        Style.CursorType.NotAllowed => "not-allowed",
        Style.CursorType.Grab => "grab",
        Style.CursorType.Grabbing => "grabbing",
        Style.CursorType.EwResize => "ew-resize",
        Style.CursorType.NsResize => "ns-resize",
        Style.CursorType.NeswResize => "nesw-resize",
        Style.CursorType.NwseResize => "nwse-resize",
        Style.CursorType.None => "none",
        _ => "default",
    };

    /// <summary>Build the platform-neutral semantics tree (§5) at the given size.</summary>
    public Accessibility.AccessibilityNode BuildAccessibilityTree(float width, float height)
    {
        _layout.Layout(_root, width, height);
        return Accessibility.AccessibilityTree.Build(_root);
    }

    /// <summary>Serialise the semantics tree to an ARIA HTML fragment for the web host's off-screen
    /// screen-reader mirror (the canvas is opaque to assistive tech). See <see cref="Accessibility.AriaHtml"/>.</summary>
    public string BuildAriaHtml(float width, float height) =>
        Accessibility.AriaHtml.Serialize(BuildAccessibilityTree(width, height));

    /// <summary>
    /// Dispatch a click at (x,y): hit-test, run built-in control behaviour (switch
    /// toggle, slider set) and user handlers along the bubble path, write back to the
    /// bound model, and refresh. Returns true if anything handled it (→ needs repaint).
    /// </summary>
    public bool DispatchClick(float x, float y, int clickCount = 1)
    {
        EnsureLaidOut();
        _textDrag = false;

        // An open context menu intercepts the next click: an item runs its command (without
        // blurring the underlying field, so the selection survives for Copy/Cut); a click
        // elsewhere (or on a disabled/separator row) just dismisses it. Either way it's swallowed.
        if (_ctxOpen)
        {
            var ctxHit = HitTesting.HitTest(_root, x, y);
            if (InContextMenu(ctxHit))
            {
                if (ContextCommandOf(ctxHit) is { } cmd) { _ctxOpen = false; ContextRequested?.Invoke(cmd); Refresh(); }
                return true; // inside but not actionable → keep the menu open
            }
            _ctxOpen = false; Refresh(); return true; // outside → dismiss
        }

        // A custom <cupri-context-menu>: a click outside dismisses it; a click on a leaf row closes it
        // AND runs the row's action via the normal flow below; a click on a submenu parent keeps it open.
        if (_ctxCustomIndex >= 0)
        {
            var ctxHit = HitTesting.HitTest(_root, x, y);
            if (!InCustomContextMenu(ctxHit)) { _ctxCustomIndex = -1; Refresh(); return true; } // outside → dismiss
            if (!OnLeafMenuItem(ctxHit)) return true;                                            // parent/padding → keep open
            _ctxCustomIndex = -1;                                                                // leaf → close, then fall through
        }

        var hit = HitTesting.HitTest(_root, x, y);

        // Click-away: close any open bound-flag popup (picker/select/popover) the click landed outside.
        // Doesn't consume the click, so it still does its normal thing; the refresh below applies it.
        var strayClosed = CloseStrayPopups(hit);

        if (hit is null) // click on empty space blurs (and closes any stray popup)
        {
            _kbIndex = -1;
            var blurred = UpdateFocus(null);
            if (strayClosed) Refresh();
            return blurred || strayClosed;
        }

        // Grabbing a resize grip (bottom-right corner) starts a resize-drag — corner takes priority.
        for (var n = hit; n is not null; n = n.Parent)
            if (InResizeGrip(n, x, y))
            {
                _resizeDrag = n; _resizeX0 = x; _resizeY0 = y;
                _resizeW0 = n.ResizeW ?? n.Width; _resizeH0 = n.ResizeH ?? n.Height;
                return true;
            }

        // Grabbing a resizable table's column boundary (a header cell's right edge) starts a column drag.
        for (var n = hit; n is not null; n = n.Parent)
            if (StartColumnResize(n, x)) return true;

        // Grabbing a scrollbar thumb starts a scroll-drag (takes priority; doesn't focus/blur).
        for (var n = hit; n is not null; n = n.Parent)
            if (ThumbRect(n) is { } tr && x >= tr.X - 6 && x <= tr.X + tr.W + 8 && y >= tr.Y && y <= tr.Y + tr.H)
            {
                _scrollDrag = n; _scrollDragScroll0 = Math.Clamp(n.ScrollY, 0, n.MaxScrollY); _scrollDragY0 = y;
                return true;
            }

        // Grabbing a drag-reorder handle starts a reorder drag (paint-time; doesn't focus/blur).
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("cupri-reorder-handle") == true && StartReorder(n, x, y))
                return true;

        // Grabbing a split-pane divider starts a split-resize drag.
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("cupri-split-divider") == true && StartSplit(n, x, y))
                return true;

        // :active press feedback — mark the pressed element chain (restyled below; cleared on pointer-up).
        SetActive(hit.Element);

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
            if (focusChanged || strayClosed) Refresh(); // new field or a dismissed popup → rebuild
            ReconcileScope();
            return true;
        }

        if (handled || focusChanged || strayClosed) Refresh();
        else if (_activeChain.Count > 0) ReStyle(); // show the :active press even if nothing else changed
        ReconcileScope(); // a click may have opened/closed an overlay → update the focus scope
        return handled || focusChanged || strayClosed || _activeChain.Count > 0;
    }

    // Walk from a node up the ancestor chain, applying the first built-in control behaviour or
    // user click handler. Shared by mouse clicks and keyboard activation (Enter/Space).
    private bool ActivateFrom(RenderNode start, float x, float y)
    {
        for (var node = start; node is not null; node = node.Parent)
        {
            if (node.Element is not { } el) continue;

            // Custom registered interaction primitives (extensibility) — checked before the built-ins
            // so a third party can define new data-* behaviours.
            foreach (var (attr, handler) in _actionHandlers)
                if (el.GetAttribute(attr) is { } av && handler(new Interaction.CupriActionEvent(node, el, av, _model, x, y)))
                    return true;

            // A link: an in-page #anchor scrolls its target into view here; any other href raises Navigated
            // (the app routes internal hrefs, a host opens external ones). Keyboard Enter routes here too.
            if (el.LocalName == "a" && el.GetAttribute("href") is { Length: > 0 } href)
            {
                if (href[0] == '#') return ScrollToAnchor(href[1..]);
                Navigated?.Invoke(new Interaction.NavigateEvent(href, IsExternalHref(href)));
                return true;
            }

            // Number stepper: +/- button adjusts the nearest numeric field's bound value.
            if (el.GetAttribute("data-cupri-step") is { Length: > 0 } stepRaw) return StepNumber(node, stepRaw);

            // Generic "set a bound value" click (tabs, select options, tree selection). Closes any
            // containing overlay so picking an option dismisses its dropdown — unless the element opts
            // out with data-set-keep (an in-place control that adjusts a value without dismissing, e.g.
            // the date picker's month navigation).
            if (el.GetAttribute("data-set-path") is { Length: > 0 } setPath && _model is not null)
            {
                var ok = BindingEngine.TrySet(_model, setPath, el.GetAttribute("data-set-value") ?? "");
                if (!el.HasAttribute("data-set-keep")) SetNearestOpen(node, false);
                return ok;
            }

            // Toggle a value in a bound comma-set (multi-select table rows): add it if absent, else remove it.
            if (el.GetAttribute("data-set-toggle") is { Length: > 0 } togPath && _model is not null)
            {
                var v = el.GetAttribute("data-toggle-value") ?? "";
                var set = (BindingEngine.Resolve(_model, togPath)?.ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!set.Remove(v)) set.Add(v);
                return BindingEngine.TrySet(_model, togPath, string.Join(",", set));
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
                    foreach (var (_, compiled, handler) in _clickHandlers)
                    {
                        if (!Matches(el, compiled)) continue;
                        handler(new CupriPointerEvent(x, y, node, el));
                        any = true;
                    }
                    if (any) return true;
                    break;
            }
        }
        return false;
    }

    // A href with a URL scheme (http:, https:, mailto:, tel:, …) or protocol-relative // is external;
    // a bare path (e.g. "charts") is internal in-app routing. (A "#anchor" is handled before this.)
    private static bool IsExternalHref(string href) =>
        href.StartsWith("//", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(href, "^[a-zA-Z][a-zA-Z0-9+.-]*:");

    // In-page anchor: scroll the element with this id to the top of its nearest scrollable ancestor.
    // Scroll offsets survive the rebuild (CaptureScroll), so the page stays put at the anchor.
    private bool ScrollToAnchor(string id)
    {
        if (id.Length > 0 && FindById(_root, id) is { } target)
            for (var s = target.Parent; s is not null; s = s.Parent)
            {
                if (!s.IsScrollable) continue;
                var offset = HitTesting.AbsoluteBox(target).Y - HitTesting.AbsoluteBox(s).Y; // unscrolled offset
                s.ScrollY = Math.Clamp(offset - 12f, 0, s.MaxScrollY);                        // 12px above
                break;
            }
        return true; // the link is handled whether or not the anchor exists / anything scrolls
    }

    private static RenderNode? FindById(RenderNode n, string id)
    {
        if (n.Element?.GetAttribute("id") == id) return n;
        foreach (var c in n.Children) { var f = FindById(c, id); if (f is not null) return f; }
        return null;
    }

    // ---- keyboard focus + tab order (a11y capability #4) ---------------------
    // The interactive roles/attributes Tab stops on; an element is focusable if it is one of
    // these AND has no focusable descendant (so we land on the actual control, not a wrapper).
    private static bool IsFocusableRole(IElement el) =>
        el.GetAttribute("role") is "switch" or "checkbox" or "radio" or "slider"
                                or "textbox" or "spinbutton" or "button"
        || (el.LocalName == "a" && el.HasAttribute("href"))
        || el.HasAttribute("data-cupri-toggle")
        || el.HasAttribute("data-set-path")
        || el.HasAttribute("data-set-toggle")
        || el.HasAttribute("data-cupri-step");

    private bool IsFocusable(IElement el) =>
        IsFocusableRole(el) || _clickHandlers.Exists(h => Matches(el, h.Compiled));

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

    // The option rows of the focused field's listbox (a data-listbox field, e.g. the combobox), for
    // keyboard nav; null if the focused field isn't a listbox.
    private List<IElement>? FocusedListbox()
    {
        if (_dom is null || _focusKey is null) return null;
        var input = _dom.QuerySelector($"[data-bind-value=\"{_focusKey}\"]");
        if (input is null || !input.HasAttribute("data-listbox") || input.ParentElement is not { } container) return null;
        var opts = new List<IElement>();
        foreach (var o in container.QuerySelectorAll("[role=\"option\"][data-set-value]")) opts.Add(o);
        return opts;
    }

    // An open [data-gridnav] overlay (the date picker's day grid) + its option cells and column count,
    // for arrow-key 2D navigation; null if none is open.
    private (List<IElement> Cells, int Cols)? FocusedGrid()
    {
        if (_dom is null || _dom.QuerySelector("[data-gridnav]") is not { } grid) return null;
        var cols = int.TryParse(grid.GetAttribute("data-gridnav"), out var c) ? Math.Max(1, c) : 7;
        var cells = new List<IElement>();
        foreach (var cell in grid.QuerySelectorAll("[data-set-value]")) cells.Add(cell);
        return cells.Count > 0 ? (cells, cols) : null;
    }

    private static int GridSelectedIndex(List<IElement> cells)
    {
        for (var i = 0; i < cells.Count; i++) if (cells[i].ClassList.Contains("selected")) return i;
        return 0;
    }

    private bool MoveGrid((List<IElement> Cells, int Cols) g, int delta)
    {
        _gridHi = Math.Clamp(_gridHi + delta, 0, g.Cells.Count - 1); // clamp keeps it within the shown month
        foreach (var cell in g.Cells) cell.RemoveAttribute("data-highlight");
        g.Cells[_gridHi].SetAttribute("data-highlight", "");
        ReStyle();
        return true;
    }

    private bool ActivateGrid((List<IElement> Cells, int Cols) g)
    {
        if (_gridHi < 0 || _gridHi >= g.Cells.Count || _model is null) return false;
        var cell = g.Cells[_gridHi];
        if (cell.GetAttribute("data-set-path") is not { Length: > 0 } path) return false;
        BindingEngine.TrySet(_model, path, cell.GetAttribute("data-set-value") ?? "");
        for (var e = cell; e is not null; e = e.ParentElement) // close the overlay (set its bound open=false)
            if (e.GetAttribute("data-bind-open") is { Length: > 0 } op) { BindingEngine.TrySet(_model, op, "false"); break; }
        _gridHi = -1;
        Refresh();
        return true;
    }

    private bool UpdateFocus(IElement? field)
    {
        var key = field?.GetAttribute("data-bind-value") ?? field?.GetAttribute("id");
        if (key == _focusKey) return false;
        CommitBuffer(); // blur the previous field: validate + commit (or revert) its edit buffer
        _focusKey = key;
        _focusNumeric = field?.HasAttribute("data-numeric") == true;
        _focusMultiline = field?.HasAttribute("data-multiline") == true;
        _focusMask = field?.HasAttribute("data-mask") == true;
        _maskRevealPos = -1; _maskRevealStart = double.NaN; // the last-typed peek is per-field
        _focusMin = double.TryParse(field?.GetAttribute("data-min"), out var mn) ? mn : null;
        _focusMax = double.TryParse(field?.GetAttribute("data-max"), out var mx) ? mx : null;
        _focusRequired = field?.HasAttribute("required") == true;
        _focusPattern = field?.GetAttribute("pattern");
        _focusMinLen = int.TryParse(field?.GetAttribute("minlength"), out var mlen) ? mlen : null;
        _editBuffer = key is null ? null : BindingEngine.Resolve(_model, key)?.ToString() ?? "";
        _caret = _editBuffer?.Length ?? 0;
        _selAnchor = _caret;
        _caretMoved = true;
        _listHi = -1; // listbox highlight is per-field
        _undo.Clear(); _redo.Clear(); _typingGroup = false; // undo history is per-field
        return true;
    }

    /// <summary>
    /// Feed a keystroke to the focused text field: printable text via <paramref name="text"/>,
    /// or an editing key (backspace/arrows/…). Edits the bound string and refreshes.
    /// </summary>
    public bool DispatchKey(string? text, EditKey key, KeyMods mods = KeyMods.None)
    {
        ReconcileScope(); // reflect any overlay that opened/closed since the last event

        // Any keystroke dismisses an open context menu; Escape only closes it (swallowed).
        if (_ctxOpen || _ctxCustomIndex >= 0) { _ctxOpen = false; _ctxCustomIndex = -1; Refresh(); if (key == EditKey.Escape) return true; }

        // A registered keyboard shortcut consumes the key. A Ctrl/Cmd chord fires anywhere; a plain-key
        // shortcut only when no text field is focused (so it doesn't eat normal typing). An unbound Ctrl
        // chord is swallowed too (returns false → the host can defer to the browser) rather than typed.
        if (text is { Length: 1 } && (mods.HasFlag(KeyMods.Ctrl) || _focusKey is null))
        {
            // Refresh: the handler almost certainly mutated the model (open a palette, toggle a panel) —
            // without the rebuild the change only became visible on the NEXT event's ReconcileScope,
            // which desktop's constant mouse-moves masked and the web host's quiet keyboard didn't.
            if (_shortcuts.TryGetValue(ShortcutKey(mods, text), out var shortcut)) { shortcut(); Refresh(); ReconcileScope(); return true; }
            if (mods.HasFlag(KeyMods.Ctrl)) return false; // never insert a Ctrl/Cmd + letter as text
        }

        // Normalize pasted/typed line endings to '\n' (a textarea's internal newline). Windows
        // clipboards deliver "\r\n"; the stray '\r' would otherwise render as a collapsed empty line.
        if (text is { } t && t.IndexOf('\r') >= 0) text = t.Replace("\r\n", "\n").Replace('\r', '\n');

        if (key == EditKey.Escape) return HandleEscape();
        // Tab moves keyboard focus regardless of edit state (trapped within an open overlay).
        if (key == EditKey.Tab) return MoveFocus(+1);
        if (key == EditKey.ShiftTab) return MoveFocus(-1);

        // With no text field focused, Enter/Space activate and arrows navigate groups/sliders.
        // (Hosts deliver space as either EditKey.Space or a " " character.)
        if (_focusKey is null)
        {
            // An open [data-gridnav] overlay (the date picker) captures the arrows for 2D day nav,
            // Enter/Space to pick the highlighted day, ±1 = day, ±cols = week.
            if (key is EditKey.Left or EditKey.Right or EditKey.Up or EditKey.Down or EditKey.Enter or EditKey.Space
                && FocusedGrid() is { } grid)
            {
                if (_gridHi < 0) _gridHi = GridSelectedIndex(grid.Cells);
                return key switch
                {
                    EditKey.Left => MoveGrid(grid, -1),
                    EditKey.Right => MoveGrid(grid, +1),
                    EditKey.Up => MoveGrid(grid, -grid.Cols),
                    EditKey.Down => MoveGrid(grid, +grid.Cols),
                    _ => ActivateGrid(grid), // Enter / Space
                };
            }

            var focused = CurrentFocusNode();
            var role = focused?.Element?.GetAttribute("role");

            // Reorder: ↑/↓ on a focused grip moves the row a slot (the keyboard equivalent of the drag).
            if (focused?.Element?.ClassList.Contains("cupri-reorder-handle") == true && key is EditKey.Up or EditKey.Down)
                return KeyboardReorder(focused, key == EditKey.Up ? -1 : +1);
            // Tree: →/← expand/collapse a focused tree item (ARIA tree pattern).
            if (focused?.Element?.ClassList.Contains("cupri-tree-twist") == true && key is EditKey.Left or EditKey.Right)
                return TreeExpand(focused, key == EditKey.Right);

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

        // Combobox / focused-listbox keyboard nav: Down/Up move a highlight over the suggestion rows,
        // Enter commits the highlighted one. (Applies only to a focused field marked data-listbox.)
        if (key is EditKey.Down or EditKey.Up or EditKey.Enter && FocusedListbox() is { } lb)
        {
            if (key is EditKey.Down or EditKey.Up)
            {
                if (lb.Count == 0) return false;
                foreach (var o in lb) o.RemoveAttribute("data-highlight");
                _listHi = key == EditKey.Down
                    ? Math.Min(_listHi < 0 ? 0 : _listHi + 1, lb.Count - 1)
                    : Math.Max(_listHi < 0 ? 0 : _listHi - 1, 0);
                lb[_listHi].SetAttribute("data-highlight", "");
                ReStyle();
                return true;
            }
            if (_listHi >= 0 && _listHi < lb.Count && lb[_listHi].GetAttribute("data-set-path") is { Length: > 0 } sp)
            {
                var row = lb[_listHi];                     // capture before the blur below resets _listHi
                var picked = row.GetAttribute("data-set-value") ?? "";
                UpdateFocus(null);                         // blur (commits the typed buffer)…
                BindingEngine.TrySet(_model, sp, picked);  // …then overwrite with the chosen suggestion
                // Close the enclosing overlay if the row lives in one (a command palette) — mirrors the
                // click path's SetNearestOpen; a plain combobox has no data-bind-open ancestor, so this no-ops.
                for (var e = row; e is not null; e = e.ParentElement)
                    if (e.GetAttribute("data-bind-open") is { Length: > 0 } op) { BindingEngine.TrySet(_model, op, false); break; }
                Refresh();
                return true;
            }
        }

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
        var (oValue, oCaret, oAnchor) = (value, caret, anchor); // pre-edit snapshot (for undo)

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
                    // A single-line field takes no hard line breaks (like <input>): a pasted multi-line
                    // string collapses its newlines to spaces so it stays one logical line.
                    if (!_focusMultiline && text.IndexOf('\n') >= 0) text = text.Replace('\n', ' ');
                    if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; }
                    value = value.Insert(caret, text); caret += text.Length; anchor = caret; edited = true;
                }
                break;
        }

        // Mobile-style peek: a masked field briefly shows the character you just typed, then re-masks
        // it (Animate expires the peek). Any other edit — delete, navigation, multi-char paste —
        // hides it immediately so only a single fresh keystroke is ever visible.
        if (_focusMask)
        {
            if (edited && key == EditKey.None && text is { Length: 1 }) { _maskRevealPos = caret - 1; _maskRevealStart = double.NaN; }
            else _maskRevealPos = -1;
        }

        if (edited)
        {
            // Record undo history. Coalesce a run of printable non-space chars into one step;
            // space/newline/delete/paste/any selection-replace start a new step.
            var typingChar = key == EditKey.None && text is { Length: 1 } && text[0] is not ('\n' or ' ') && !hasSel;
            if (!(typingChar && _typingGroup))
            {
                _undo.Add(new EditState(oValue, oCaret, oAnchor));
                if (_undo.Count > 300) _undo.RemoveAt(0);
                _redo.Clear();
            }
            _typingGroup = typingChar;
        }
        else _typingGroup = false;

        _caret = caret;
        _selAnchor = anchor;
        _caretMoved = true;
        if (edited)
        {
            _editBuffer = value;
            _listHi = -1; // the suggestion list re-filters on edit → drop the stale highlight
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

    /// <summary>The currently selected text in the focused field, or null if nothing is selected.
    /// A masked field (data-mask, e.g. an un-revealed &lt;cupri-password&gt;) never yields its
    /// plaintext to the clipboard — copy/cut are disabled until the value is revealed.</summary>
    public string? CopySelection()
    {
        if (_focusKey is null || _editBuffer is null || _selAnchor == _caret || _focusMask) return null;
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

    // ---- right-click context menu (Cut/Copy/Paste/Select-all on text fields) -----------------

    /// <summary>Open a context menu at (x,y) if it's over a text field. Focuses the field (a fresh
    /// focus has no selection, so Cut/Copy start disabled; right-clicking the already-focused field
    /// keeps its selection). Returns true if a menu opened or an open one closed (→ repaint).</summary>
    public bool DispatchContextMenu(float x, float y)
    {
        EnsureLaidOut();
        var hit = HitTesting.HitTest(_root, x, y);

        // A <cupri-context-menu> region under the pointer opens its own menu at the pointer.
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element is { } hostEl && hostEl.HasAttribute("data-cupri-ctx-host") && HostIndex(hostEl) is >= 0 and var idx)
            {
                _ctxOpen = false; _ctxCustomIndex = idx; _ctxX = x; _ctxY = y;
                Refresh(); // the rebuild reveals the menu at (x,y)
                return true;
            }

        RenderNode? field = hit;
        while (field is not null && field.Element?.GetAttribute("role") is not ("textbox" or "spinbutton"))
            field = field.Parent;

        if (field is null) // not over an editable or a custom region → close any open menu, else nothing to do
        {
            if (!_ctxOpen && _ctxCustomIndex < 0) return false;
            _ctxOpen = false; _ctxCustomIndex = -1; Refresh(); return true;
        }

        // Focus the field (does nothing if already focused — so an existing selection is kept).
        UpdateFocus(field.Element);
        _kbIndex = IndexOfFocusable(hit!); // hit is non-null here (field is hit or an ancestor)
        _focusVisible = false;

        _ctxOpen = true; _ctxX = x; _ctxY = y;
        _ctxHasSelection = CopySelection() is not null;
        _ctxHasText = !string.IsNullOrEmpty(_editBuffer);
        Refresh(); // rebuild injects the menu subtree
        return true;
    }

    // Is this node inside the open context menu? (walk up to the data-ctx-menu container)
    private static bool InContextMenu(RenderNode? n)
    {
        for (; n is not null; n = n.Parent)
            if (n.Element?.HasAttribute("data-ctx-menu") == true) return true;
        return false;
    }

    // The command a clicked menu row carries (data-cupri-context), searching up from the hit node.
    private static Interaction.ContextCommand? ContextCommandOf(RenderNode? n)
    {
        for (; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-cupri-context") is { Length: > 0 } cmd)
                return cmd switch
                {
                    "cut" => Interaction.ContextCommand.Cut,
                    "copy" => Interaction.ContextCommand.Copy,
                    "paste" => Interaction.ContextCommand.Paste,
                    "selectall" => Interaction.ContextCommand.SelectAll,
                    _ => (Interaction.ContextCommand?)null,
                };
        return null;
    }

    // Inject the menu as a position:fixed overlay at the pointer. Self-styled (inline) so it never
    // depends on the app's stylesheet or which component library is registered.
    private void InjectContextMenu(IDocument dom)
    {
        if (dom.Body is not { } body) return;
        var x = _ctxX.ToString(CultureInfo.InvariantCulture);
        var y = _ctxY.ToString(CultureInfo.InvariantCulture);

        static string Row(string cmd, string label, string hint, bool enabled)
        {
            var color = enabled ? "#1e2430" : "#b0b6c0";
            var attr = enabled ? $" role='menuitem' data-cupri-context='{cmd}'" : "";
            return $"<div{attr} style='display:flex;justify-content:space-between;gap:28px;"
                 + $"padding:7px 12px;border-radius:6px;color:{color};font-size:14px;'>"
                 + $"<span>{label}</span><span style='color:#9aa2b1'>{hint}</span></div>";
        }
        const string sep = "<div style='height:1px;background:#e6e9f0;margin:4px 6px;'></div>";

        var menu = $"<div data-ctx-menu data-ctx-clamp style='position:fixed;left:{x}px;top:{y}px;"
            + "background:#ffffff;border:1px #e6e9f0;border-radius:8px;padding:5px;min-width:180px;"
            + "font-family:sans-serif;z-index:60;'>"
            + Row("cut", "Cut", "Ctrl+X", _ctxHasSelection)
            + Row("copy", "Copy", "Ctrl+C", _ctxHasSelection)
            + Row("paste", "Paste", "Ctrl+V", true)
            + sep
            + Row("selectall", "Select All", "Ctrl+A", _ctxHasText)
            + "</div>";

        body.Insert(AngleSharp.Dom.AdjacentPosition.BeforeEnd, menu);
    }

    // ---- custom <cupri-context-menu> --------------------------------------------------------------

    // Document-order index of a context-menu host, so the open one can be re-found after a rebuild.
    private int HostIndex(IElement host)
    {
        if (_dom is null) return -1;
        var hosts = _dom.QuerySelectorAll("[data-cupri-ctx-host]");
        for (var i = 0; i < hosts.Length; i++) if (ReferenceEquals(hosts[i], host)) return i;
        return -1;
    }

    // Reveal the open custom menu at the pointer: flip its popup to display:block, place it at (x,y),
    // and mark it data-ctx-clamp so layout keeps it on-screen (same clamp the text menu uses).
    private void RevealCustomContextMenu(IDocument dom)
    {
        var hosts = dom.QuerySelectorAll("[data-cupri-ctx-host]");
        if (_ctxCustomIndex >= hosts.Length) { _ctxCustomIndex = -1; return; } // host went away since the click
        if (hosts[_ctxCustomIndex].QuerySelector(".cupri-ctx-menu") is not { } menu) { _ctxCustomIndex = -1; return; }
        var x = _ctxX.ToString(CultureInfo.InvariantCulture);
        var y = _ctxY.ToString(CultureInfo.InvariantCulture);
        menu.SetAttribute("data-ctx-clamp", "");
        menu.SetAttribute("style", $"display:block;left:{x}px;top:{y}px;"); // position:fixed + look come from .cupri-ctx-menu
    }

    private static bool InCustomContextMenu(RenderNode? n)
    {
        for (; n is not null; n = n.Parent) if (n.Element?.HasAttribute("data-cupri-ctx-menu") == true) return true;
        return false;
    }

    // The pointer is over an actionable (leaf) row — not a submenu parent or the popup's own padding.
    private static bool OnLeafMenuItem(RenderNode? n)
    {
        for (; n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("cupri-menu-item") == true)
                return !n.Element.ClassList.Contains("cupri-menu-parent");
        return false;
    }

    // ---- toast stack (doc.Toast) ------------------------------------------------------------------

    // Inject the current toasts at the body end. Entering/Leaving render off-screen (translated + faded);
    // the .cupri-toast-item transition animates the flip to/from the shown state as Animate advances phases.
    private void InjectToaster(IDocument dom)
    {
        if (dom.Body is not { } body || _toasts.Count == 0) return;
        var sb = new System.Text.StringBuilder("<div class='cupri-toaster'>");
        foreach (var t in _toasts)
        {
            var off = t.Phase != ToastPhase.Shown; // Entering + Leaving sit off to the right, transparent
            var kind = t.Kind.Length > 0 ? " " + t.Kind : "";
            sb.Append($"<div class='cupri-toast-item{kind}' role='status' style='opacity:{(off ? "0" : "1")};")
              .Append($"transform:translateX({(off ? "120%" : "0")})'>")
              .Append(EscapeHtml(t.Msg)).Append("</div>");
        }
        sb.Append("</div>");
        body.Insert(AngleSharp.Dom.AdjacentPosition.BeforeEnd, sb.ToString());
    }

    // Advance each toast: Entering → Shown (flip in) on its first tick; Shown → Leaving (flip out) once it
    // has sat for ToastShowSeconds; Leaving → removed once the exit slide has run. Rebuilds on any change so
    // the transition engine sees the new target. Returns true while any toast remains (keeps the loop alive).
    private bool StepToasts(double t)
    {
        var changed = false;
        for (var i = _toasts.Count - 1; i >= 0; i--)
        {
            var e = _toasts[i];
            switch (e.Phase)
            {
                case ToastPhase.Entering: e.Phase = ToastPhase.Shown; e.T = t; changed = true; break;
                case ToastPhase.Shown when t - e.T >= ToastShowSeconds: e.Phase = ToastPhase.Leaving; e.T = t; changed = true; break;
                case ToastPhase.Leaving when t - e.T >= ToastExitSeconds: _toasts.RemoveAt(i); changed = true; break;
            }
        }
        if (changed) Rebuild();
        return _toasts.Count > 0;
    }

    private static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Undo the last edit in the focused field (Ctrl+Z). Returns true if it changed anything.</summary>
    public bool Undo()
    {
        if (_focusKey is null || _undo.Count == 0) return false;
        _redo.Add(new EditState(_editBuffer ?? "", _caret, _selAnchor));
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        ApplyEditState(s);
        return true;
    }

    /// <summary>Redo the last undone edit (Ctrl+Y / Ctrl+Shift+Z). Returns true if it changed anything.</summary>
    public bool Redo()
    {
        if (_focusKey is null || _redo.Count == 0) return false;
        _undo.Add(new EditState(_editBuffer ?? "", _caret, _selAnchor));
        var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        ApplyEditState(s);
        return true;
    }

    private void ApplyEditState(EditState s)
    {
        _editBuffer = s.Buffer;
        _caret = Math.Clamp(s.Caret, 0, s.Buffer.Length);
        _selAnchor = Math.Clamp(s.Anchor, 0, s.Buffer.Length);
        _caretMoved = true;
        _typingGroup = false;
        if (_model is not null && _focusKey is not null && BufferValid(s.Buffer))
            BindingEngine.TrySet(_model, _focusKey, s.Buffer);
        Refresh();
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
    /// The controls a click at <paramref name="hit"/> could activate as a label, nearest first.
    /// Labels are authored as siblings of the control, so we look outward from the clicked node: the
    /// immediately-adjacent control, preferring the one BEFORE the label (the "[box] Label" pattern)
    /// then the one AFTER it (the "Label [switch]" pattern). A following control is only offered when
    /// it has no trailing label of its own, so a group heading like "Size" that precedes the first
    /// radio doesn't hijack it. (The whole ancestor chain of <paramref name="hit"/> is non-interactive
    /// when this runs — ActivateFrom already found no control above it — so walking out to adjacent
    /// siblings stays on inert text.) Enumerating candidates rather than acting lets <see cref="CursorAt"/>
    /// and <see cref="ActivateLabel"/> share ONE definition of "this text is a label", so the cursor
    /// can't promise a click the activation wouldn't honour.
    /// </summary>
    private static IEnumerable<IElement> LabelTargets(RenderNode hit)
    {
        RenderNode? node = hit;
        for (var hops = 0; node is not null && hops < 5; node = node.Parent, hops++)
        {
            if (node.Element is not { } el) continue;
            if (el.PreviousElementSibling is { } prev && IsLabelableControl(prev)) yield return prev;
            if (el.NextElementSibling is { } next && IsLabelableControl(next) && !HasTrailingLabel(next))
                yield return next;
        }
    }

    /// <summary>
    /// A click that hit no control but landed on a checkbox/radio/switch's text label activates
    /// that control (HTML <c>&lt;label&gt;</c> behaviour). Returns the activated control, or null —
    /// a candidate that declines to act (e.g. an unbound radio) falls through to the next.
    /// </summary>
    private IElement? ActivateLabel(RenderNode hit)
    {
        foreach (var control in LabelTargets(hit))
            if (ActivateControl(control)) return control;
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

    // Click-away dismissal: close any open anchored popup (select / date / time picker / popover) whose
    // open state is a bound flag, when the click lands outside both the popup and its trigger. Mutates
    // the flag like SetNearestOpen but does NOT consume the click, so it composes with whatever else the
    // click does (e.g. opening a different picker). A rebuild by the caller applies it.
    private bool CloseStrayPopups(RenderNode? hit)
    {
        if (_dom is null || _model is null) return false;
        var closed = false;
        foreach (var popup in _dom.QuerySelectorAll("[data-cupri-anchor]"))
        {
            var anchorId = popup.GetAttribute("data-cupri-anchor");
            var insideOrTrigger = false;
            for (var n = hit; n is not null; n = n.Parent)
                if (n.Element is { } el && (ReferenceEquals(el, popup) || el.GetAttribute("id") == anchorId)) { insideOrTrigger = true; break; }
            if (insideOrTrigger) continue;

            for (var e = popup; e is not null; e = e.ParentElement)
                if (e.GetAttribute("data-bind-open") is { Length: > 0 } path)
                { if (BindingEngine.TrySet(_model, path, false)) closed = true; break; }
        }
        return closed;
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
        if (_focusNumeric)
        {
            if (!double.TryParse(buf, out var n)) return false;
            if ((_focusMin is { } mn && n < mn) || (_focusMax is { } mx && n > mx)) return false;
        }
        // required / pattern / minlength drive the red border mid-edit too, not just numeric range.
        return FieldValidation.Check(_focusRequired, _focusPattern, _focusMinLen, null, null, buf, null) is null;
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

    // Validate and commit the current edit buffer to the model, then clear it (blur). Marks the field
    // "touched" so its inline error can show now that the user has left it.
    private void CommitBuffer()
    {
        if (_focusKey is not null && _editBuffer is not null && _model is not null
            && BufferCommit(_editBuffer) is { } committed)
            BindingEngine.TrySet(_model, _focusKey, committed);
        if (_focusKey is not null) _touched.Add(_focusKey);
        _editBuffer = null;
    }

    // Inline validation: for each bound field with rules that is invalid AND has been visited (or the form
    // was submitted), flag it invalid and inject an error message after it. The focused field is left to
    // its mid-edit red border (no message while you're still typing in it).
    private void ApplyValidation(IDocument dom)
    {
        foreach (var field in dom.QuerySelectorAll("[data-bind-value]"))
        {
            if (!FieldValidation.HasRules(field)) continue;
            var key = field.GetAttribute("data-bind-value")!;
            if (key == _focusKey || (!_validateAll && !_touched.Contains(key))) continue;
            if (FieldValidation.Evaluate(field, field.GetAttribute("value") ?? "") is not { } error) continue;

            field.SetAttribute("data-invalid", "");
            var msg = dom.CreateElement("div");
            msg.ClassName = "cupri-field-error";
            msg.TextContent = error;
            field.Parent?.InsertBefore(msg, field.NextSibling);
        }
    }

    /// <summary>Reveal every validated field's inline error (mark all fields touched) and re-render — call
    /// from a form's submit handler. Returns true when every field is currently valid.</summary>
    public bool ValidateAll()
    {
        _validateAll = true;
        Rebuild();
        if (_dom is null) return true;
        foreach (var field in _dom.QuerySelectorAll("[data-bind-value]"))
            if (FieldValidation.HasRules(field) && FieldValidation.Evaluate(field, field.GetAttribute("value") ?? "") is not null)
                return false;
        return true;
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
    // Grab a card by its handle: record the source column, every column in its board group (all
    // .cupri-reorder lists under a shared .cupri-board — else just this list), each column's cards and
    // their mid-lines + X-span for hit-testing which column/slot the pointer is over as it's dragged.
    private bool StartReorder(RenderNode handle, float x, float y)
    {
        RenderNode? item = handle;
        while (item is not null && item.Element?.ClassList.Contains("cupri-reorder-item") != true) item = item.Parent;
        if (item?.Parent is not { } list) return false;

        RenderNode? board = list;
        while (board is not null && board.Element?.ClassList.Contains("cupri-board") != true) board = board.Parent;
        var lists = new List<RenderNode>();
        if (board is not null) CollectReorderLists(board, lists); else lists.Add(list);

        var cols = new List<ReorderCol>();
        var all = new List<RenderNode>();
        int fromCol = -1, from = -1;
        for (var ci = 0; ci < lists.Count; ci++)
        {
            var items = new List<RenderNode>();
            foreach (var c in lists[ci].Children) if (c.Element?.ClassList.Contains("cupri-reorder-item") == true) items.Add(c);
            var mids = new float[items.Count];
            for (var i = 0; i < items.Count; i++) { var b = HitTesting.AbsoluteBox(items[i]); mids[i] = b.Y + b.H / 2f; }
            var lb = HitTesting.AbsoluteBox(lists[ci]);
            cols.Add(new ReorderCol(lists[ci], items, mids, lb.X, lb.X + lb.W));
            all.AddRange(items);
            var idx = items.IndexOf(item);
            if (idx >= 0) { fromCol = ci; from = idx; }
        }
        if (fromCol < 0) return false;

        _reorderCols = cols; _reorderItems = all; _reorderList = list; _reorderCard = item;
        _reorderFromCol = _reorderToCol = fromCol; _reorderFrom = _reorderTo = from;
        _reorderX0 = x; _reorderY0 = y;
        // How far the other cards slide to open/close the gap = the dragged card's own footprint (its
        // height + the list gap), NOT the distance between the first two cards — which overshoots when a
        // card wraps to a taller height, sliding the rest off the top of the column and leaving a gap below.
        var srcCol = cols[fromCol];
        var gap = srcCol.Items.Count >= 2
            ? MathF.Max(0f, (srcCol.Mids[1] - srcCol.Mids[0]) - (srcCol.Items[0].Height + srcCol.Items[1].Height) / 2f)
            : 8f;
        _reorderShift = item.Height + gap;
        _reorderAnimT = double.NaN;
        item.Dragging = true;
        return true;
    }

    // Every .cupri-reorder list under a board (a found list is a column — don't descend into its cards).
    private static void CollectReorderLists(RenderNode n, List<RenderNode> outp)
    {
        foreach (var c in n.Children)
            if (c.Element?.ClassList.Contains("cupri-reorder") == true) outp.Add(c);
            else CollectReorderLists(c, outp);
    }

    // Dragging: the lifted card follows the pointer (both axes, so it can cross columns); the column under
    // the pointer opens a gap at the drop slot and the source column closes the one the card left. Paint-time.
    private bool MoveReorder(float x, float y)
    {
        if (_reorderCols is not { } cols || _reorderCard is not { } card) return false;
        card.DragOffsetX = x - _reorderX0;
        card.DragOffsetY = y - _reorderY0;

        // Target column: the one whose X-span holds the pointer, else the nearest by centre.
        var toCol = _reorderFromCol; var best = float.MaxValue;
        for (var ci = 0; ci < cols.Count; ci++)
        {
            if (x >= cols[ci].Left && x < cols[ci].Right) { toCol = ci; best = -1; break; }
            var d = MathF.Abs(x - (cols[ci].Left + cols[ci].Right) / 2f);
            if (best >= 0 && d < best) { best = d; toCol = ci; }
        }

        // Target slot in that column, from the pointer's Y against the column's card mid-lines.
        var mids = cols[toCol].Mids;
        int to;
        if (toCol == _reorderFromCol)
        {
            to = _reorderFrom;                                                   // within the source column
            for (var i = _reorderFrom + 1; i < mids.Length && y > mids[i]; i++) to = i;
            for (var i = _reorderFrom - 1; i >= 0 && y < mids[i]; i--) to = i;
        }
        else { to = 0; for (var i = 0; i < mids.Length; i++) if (y > mids[i]) to = i + 1; } // into another column
        _reorderToCol = toCol; _reorderTo = to;

        ApplyReorderGaps();
        return true;
    }

    // Slide each card toward its gap target: within the source column the cards between the source and target
    // slots shift one place; across columns the source closes (cards after the origin move up) and the target
    // opens (cards at/after the drop move down). The lifted card is skipped — it tracks the pointer.
    private void ApplyReorderGaps()
    {
        if (_reorderCols is not { } cols) return;
        for (var ci = 0; ci < cols.Count; ci++)
        {
            var items = cols[ci].Items;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Dragging) continue;
                float t;
                if (ci == _reorderFromCol && ci == _reorderToCol)
                    t = _reorderTo > _reorderFrom && i > _reorderFrom && i <= _reorderTo ? -_reorderShift
                      : _reorderTo < _reorderFrom && i >= _reorderTo && i < _reorderFrom ? _reorderShift : 0f;
                else if (ci == _reorderFromCol) t = i > _reorderFrom ? -_reorderShift : 0f;   // source closes
                else if (ci == _reorderToCol)   t = i >= _reorderTo ? _reorderShift : 0f;      // target opens
                else t = 0f;
                items[i].DragTargetY = t;
            }
        }
    }

    // Ease each shifting row toward its target offset (the lifted row is skipped — it tracks the pointer).
    // Returns true while any row is still moving, which keeps the host's frame loop ticking mid-drag.
    private bool EaseReorder(double now)
    {
        if (_reorderItems is not { } items) return false;
        var dt = double.IsNaN(_reorderAnimT) ? 0 : Math.Clamp(now - _reorderAnimT, 0, 0.1);
        _reorderAnimT = now;
        var f = (float)(1 - Math.Exp(-dt * 18)); // frame-rate-independent smoothing
        var moving = false;
        foreach (var it in items)
        {
            if (it.Dragging) continue;
            var diff = it.DragTargetY - it.DragOffsetY;
            if (MathF.Abs(diff) < 0.4f) { it.DragOffsetY = it.DragTargetY; continue; }
            it.DragOffsetY += diff * f;
            moving = true;
        }
        return moving;
    }

    /// <summary>True while a reorder drag's rows are still sliding toward their gap — keeps the host
    /// rendering between pointer moves so the slide eases instead of snapping.</summary>
    public bool ReorderEasing
    {
        get
        {
            if (_reorderItems is not { } items) return false;
            foreach (var it in items) if (!it.Dragging && MathF.Abs(it.DragTargetY - it.DragOffsetY) > 0.4f) return true;
            return false;
        }
    }

    // A rebuild replaces every RenderNode, so any node the pointer was mid-drag on is now orphaned. The
    // paint-time drags (reorder / split / resize / scrollbar) hold references to those dead nodes — drop
    // them, or pointer-moves keep routing to a drag that no longer paints (the reorder row looks stuck or
    // stacked on its neighbours and :hover stops updating) until some unrelated event clears it. This is
    // the safety net for a missed pointer-up (released off-window) or a rebuild landing mid-drag. Any
    // committed size/ratio was already carried across by CaptureScroll, so nothing visible is lost. The
    // slider drag (_dragging) is deliberately NOT reset: it rebuilds on every move by design and re-finds
    // its node, so it must survive a rebuild.
    private void CancelOrphanedPointerDrags()
    {
        _reorderList = null; _reorderItems = null; _reorderCols = null; _reorderCard = null;
        _splitA = _splitB = null;
        _resizeDrag = null;
        _scrollDrag = null;
    }

    // Drop: clear the offsets and, if the card landed in a new column/slot, fire the reorder event (which
    // moves it in the model and rebuilds). A no-op drop just repaints the offsets away.
    private void EndReorder()
    {
        if (_reorderItems is { } items)
            foreach (var it in items) { it.DragOffsetX = 0f; it.DragOffsetY = 0f; it.DragTargetY = 0f; it.Dragging = false; }
        var cols = _reorderCols;
        var (fromCol, from, toCol, to) = (_reorderFromCol, _reorderFrom, _reorderToCol, _reorderTo);
        _reorderList = null; _reorderItems = null; _reorderCols = null; _reorderCard = null;
        if (cols is null) return;
        var fromEl = cols[fromCol].List.Element;
        var toEl = cols[toCol].List.Element;
        if ((toCol != fromCol || to != from) && fromEl is not null && toEl is not null)
        { _onReorder?.Invoke(new ReorderEvent(fromEl, from, to, toEl)); Refresh(); }
    }

    // Keyboard reorder: move the focused row's item one slot (↑ up / ↓ down), commit via OnReorder, and
    // keep focus on the moved row so repeated presses keep moving it — the keyboard equivalent of the drag.
    private bool KeyboardReorder(RenderNode handle, int dir)
    {
        RenderNode? item = handle;
        while (item is not null && item.Element?.ClassList.Contains("cupri-reorder-item") != true) item = item.Parent;
        if (item?.Parent is not { } list || list.Element is not { } listEl) return false;

        var items = new List<RenderNode>();
        foreach (var c in list.Children) if (c.Element?.ClassList.Contains("cupri-reorder-item") == true) items.Add(c);
        var from = items.IndexOf(item);
        var to = Math.Clamp(from + dir, 0, items.Count - 1);
        if (from < 0 || to == from) return true; // already at an edge — consume the key

        _onReorder?.Invoke(new ReorderEvent(listEl, from, to, listEl)); // keyboard reorder stays within one list
        Refresh();
        FocusReorderGrip(to);
        return true;
    }

    // After a keyboard move + rebuild, put focus back on the grip of the row now at slot `nth`.
    private void FocusReorderGrip(int nth)
    {
        RenderNode? container = null;
        void FindC(RenderNode n) { if (container is null && n.Element?.ClassList.Contains("cupri-reorder") == true) container = n; foreach (var c in n.Children) FindC(c); }
        FindC(_root);
        if (container is null) return;

        var rows = new List<RenderNode>();
        foreach (var c in container.Children) if (c.Element?.ClassList.Contains("cupri-reorder-item") == true) rows.Add(c);
        if (nth < 0 || nth >= rows.Count) return;

        RenderNode? grip = null;
        void FindG(RenderNode n) { if (grip is null && n.Element?.ClassList.Contains("cupri-reorder-handle") == true) grip = n; foreach (var c in n.Children) FindG(c); }
        FindG(rows[nth]);
        if (grip is null) return;

        var idx = Focusables().FindIndex(n => ReferenceEquals(n, grip));
        if (idx >= 0) { _kbIndex = idx; _focusVisible = true; }
    }

    // Tree: expand/collapse the focused item by toggling its twist, but only when it isn't already in the
    // requested state (→ expands a closed item, ← collapses an open one; otherwise the key is consumed).
    private bool TreeExpand(RenderNode twist, bool expand)
    {
        RenderNode? item = twist;
        while (item is not null && item.Element?.HasAttribute("aria-expanded") != true) item = item.Parent;
        if (item?.Element is not { } el) return true;
        var expanded = el.GetAttribute("aria-expanded") == "true";
        return expand == expanded || ActivateFocused(); // already there → consume; else toggle the twist
    }

    // Grab a split divider: find the panels either side of it, and cache their sizes + total grow so the
    // drag can trade size between just those two while the rest of the split stays put.
    private bool StartSplit(RenderNode divider, float x, float y)
    {
        if (divider.Parent is not { } split) return false;
        var kids = split.Children;
        var di = kids.IndexOf(divider);
        RenderNode? a = null, b = null;
        for (var i = di - 1; i >= 0; i--) if (kids[i].Element?.ClassList.Contains("cupri-split-panel") == true) { a = kids[i]; break; }
        for (var i = di + 1; i < kids.Count; i++) if (kids[i].Element?.ClassList.Contains("cupri-split-panel") == true) { b = kids[i]; break; }
        if (a is null || b is null) return false;

        _splitVertical = split.Element?.ClassList.Contains("vertical") == true;
        _splitA = a; _splitB = b;
        _splitStart = _splitVertical ? y : x;
        _splitPA0 = _splitVertical ? a.Height : a.Width;
        _splitPB0 = _splitVertical ? b.Height : b.Width;
        _splitGSum = (a.SplitGrow ?? a.Style.FlexGrow) + (b.SplitGrow ?? b.Style.FlexGrow);
        return true;
    }

    // Trade size between the two panels by the drag delta, clamped so neither collapses; re-express as
    // flex-grow (their sum unchanged) so layout redistributes and everything else holds.
    private bool MoveSplit(float x, float y)
    {
        if (_splitA is not { } a || _splitB is not { } b) return false;
        var total = _splitPA0 + _splitPB0;
        if (total <= 1f || _splitGSum <= 0f) return true;
        var delta = (_splitVertical ? y : x) - _splitStart;
        var newPA = Math.Clamp(_splitPA0 + delta, 40f, total - 40f);
        a.SplitGrow = _splitGSum * newPA / total;
        b.SplitGrow = _splitGSum - a.SplitGrow.Value;
        return true; // re-layout on repaint; no rebuild
    }

    // Grab a resizable table's column boundary: the press must be on a header cell (of a table marked
    // data-cupri-colresize), within ~7px of that cell's right edge, and not on the last column (which is
    // left flexible). Caches the column's content width so the drag is jump-free (flex-basis is content-box).
    private bool StartColumnResize(RenderNode cell, float x)
    {
        if (_model is null || ColumnBoundaryAt(cell, x) is not { } at) return false;
        var (table, col) = at;
        _colPath = table.Element!.GetAttribute("data-cupri-colresize")!;
        _colIndex = col; _colStartX = x;
        _colStartW = cell.ContentBoxWidth;                            // flex-basis we write is content-box
        _colList = (table.Element.GetAttribute("resize") ?? "").Split(',');

        // How wide this column may become: the table's content box, less what the OTHER columns need —
        // the ones to its left keep the width they have, the ones to its right keep at least the floor.
        // Without this a column could be dragged far past the table, collapsing every other column and
        // overflowing the container (dragging one boundary right made a 360px table 828px wide).
        var cells = cell.Parent!.Children.Where(c => c.Element?.LocalName == "cupri-cell").ToList();
        var before = 0f;
        for (var i = 0; i < col && i < cells.Count; i++) before += cells[i].Width;
        var after = Math.Max(0, cells.Count - col - 1);
        // Reserve in BORDER-box terms (what actually occupies the row), then convert the remainder back
        // to the content width we write as flex-basis. Cells share the component's padding, so this
        // cell's insets stand in for the others'.
        var insets = cell.Width - cell.ContentBoxWidth;               // this cell's own padding/border
        var free = table.ContentBoxWidth - before - after * (MinColumnW + insets);
        _colMaxW = Math.Max(MinColumnW, free - insets);
        return true;
    }

    // Is the pointer on a resizable table's column boundary — a non-last header cell's right edge (±7px)?
    // Returns the table node + column index if so. Shared by the grab (StartColumnResize) and CursorAt.
    private static (RenderNode Table, int Col)? ColumnBoundaryAt(RenderNode cell, float x)
    {
        if (cell.Element is not { LocalName: "cupri-cell" } ce) return null;
        if (ce.GetAttribute("data-col") is not { } cs || !int.TryParse(cs, out var col)) return null;
        if (cell.Parent?.Element?.ClassList.Contains("header") != true) return null;

        RenderNode? table = cell.Parent;
        while (table is not null && table.Element?.GetAttribute("data-cupri-colresize") is not { Length: > 0 }) table = table.Parent;
        if (table is null) return null;

        var lastCol = cell.Parent!.Children.Count(c => c.Element?.LocalName == "cupri-cell") - 1;
        if (col >= lastCol) return null;                              // the last column fills remaining width

        var box = HitTesting.AbsoluteBox(cell);
        return x >= box.X + box.W - 7f && x <= box.X + box.W + 7f ? (table, col) : null;
    }

    // Write the dragged column's new content width into the bound list and rebuild; Expand re-applies it to
    // every row's matching cell, so the whole column tracks the pointer in step (like the slider drag).
    private bool MoveColumnResize(float x)
    {
        if (_colPath is null || _model is null) return false;
        var w = Math.Clamp(_colStartW + (x - _colStartX), MinColumnW, _colMaxW);
        var list = _colList.ToList();
        while (list.Count <= _colIndex) list.Add("");
        list[_colIndex] = ((int)MathF.Round(w)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        BindingEngine.TrySet(_model, _colPath, string.Join(',', list));
        Refresh();
        return true;
    }

    public bool DispatchPointerMove(float x, float y)
    {
        EnsureLaidOut();
        if (_colPath is not null) return MoveColumnResize(x);
        if (_splitA is not null) return MoveSplit(x, y);
        if (_reorderItems is not null) return MoveReorder(x, y);
        if (_resizeDrag is { } rz)
        {
            var mode = rz.Style.Resize;
            if (mode is ResizeMode.Both or ResizeMode.Horizontal)
                rz.ResizeW = ClampResize(rz, _resizeW0 + (x - _resizeX0), horizontal: true);
            if (mode is ResizeMode.Both or ResizeMode.Vertical)
                rz.ResizeH = ClampResize(rz, _resizeH0 + (y - _resizeY0), horizontal: false);
            return true; // re-layout on repaint; no rebuild
        }
        if (_scrollDrag is { } sd)
        {
            var boxH = sd.ContentBoxHeight;
            var thumbH = MathF.Max(28f, boxH * boxH / sd.ScrollContentHeight);
            var travel = boxH - thumbH;
            if (travel > 0.5f)
                sd.ScrollY = Math.Clamp(_scrollDragScroll0 + (y - _scrollDragY0) / travel * sd.MaxScrollY, 0, sd.MaxScrollY);
            // If it's a virtual list, re-windowing rebuilds the tree — which runs CancelOrphanedPointerDrags
            // and drops _scrollDrag, so the drag would die after one frame. Re-link to the rebuilt scroller
            // by its stable data-virtual-key so the thumb keeps tracking the pointer.
            var vkey = sd.Element?.GetAttribute("data-virtual-key");
            if (RewindowVirtual(sd) && vkey is { Length: > 0 })
                _scrollDrag = FindByVirtualKey(_root, vkey);
            return true;          // scroll is paint-time → repaint (RewindowVirtual rebuilds if needed)
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
    /// <summary>Pointer released: end any drag and clear the :active press. Returns true if the press
    /// state cleared (→ repaint needed to un-press).</summary>
    public bool DispatchPointerUp(float x, float y)
    {
        if (_reorderItems is not null) { EndReorder(); return true; }
        if (_splitA is not null) { _splitA = null; _splitB = null; return true; }
        if (_colPath is not null) { _colPath = null; return true; }
        _dragging = false; _textDrag = false; _scrollDrag = null; _resizeDrag = null; return ClearActive();
    }

    /// <summary>Scroll wheel: scroll the nearest scrollable element under the pointer by pixels.</summary>
    public bool DispatchWheel(float x, float y, float pixelDelta)
    {
        EnsureLaidOut();
        if (_ctxOpen || _ctxCustomIndex >= 0) { _ctxOpen = false; _ctxCustomIndex = -1; Refresh(); } // scrolling dismisses the context menu
        var hit = HitTesting.HitTest(_root, x, y);
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (!n.IsScrollable) continue;
            var before = n.ScrollY;
            n.ScrollY = Math.Clamp(n.ScrollY + pixelDelta, 0, n.MaxScrollY);
            if (RewindowVirtual(n)) return true;                 // a virtual list re-windowed (rebuilt)
            if (Math.Abs(n.ScrollY - before) > 0.01f) return true;
            // This scroller is already at its edge in that direction — chain to the next scrollable
            // ancestor, as browsers do. Without this, a wheel over an inner scroller (a table with
            // overflow:scroll) went dead once it hit bottom instead of scrolling the page.
        }
        return false;
    }

    // A scrolled <cupri-virtual> list: once it has moved ~2 rows since its last window, record the offset and
    // rebuild so the newly-exposed rows are built (a 4-row buffer covers the rows in between). Returns true if
    // it rebuilt. Scroll offsets survive the rebuild (CaptureScroll), so the list stays put + re-windows.
    // Find the (rebuilt) virtual-list scroller by its stable key — used to re-link an in-flight
    // scrollbar drag across the re-window rebuild.
    private static RenderNode? FindByVirtualKey(RenderNode n, string key)
    {
        if (n.Element?.GetAttribute("data-virtual-key") == key) return n;
        foreach (var c in n.Children) { var f = FindByVirtualKey(c, key); if (f is not null) return f; }
        return null;
    }

    private bool RewindowVirtual(RenderNode n)
    {
        if (n.Element?.GetAttribute("data-virtual-key") is not { Length: > 0 } key) return false;
        var itemH = double.TryParse(n.Element.GetAttribute("item-height"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 40;
        if (Math.Abs(n.ScrollY - _virtualScroll.GetValueOrDefault(key)) < itemH * 2) return false;
        _virtualScroll[key] = n.ScrollY;
        Rebuild();
        return true;
    }

    // Toggle data-hover on the hovered element + ancestors, then re-resolve styles (no full rebuild).
    private bool UpdateHover(float x, float y)
    {
        var hit = HitTesting.HitTest(_root, x, y);
        _lastHit = hit; _lastHitRoot = _root; _lastHitX = x; _lastHitY = y; // reused by CursorAt
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

    // Mark data-active (CSS :active) on the pressed element + ancestors — set on pointer-down (the
    // caller restyles), cleared on pointer-up. Holds while the button is down, unlike hover. Just
    // mutates the DOM (no restyle here) so it doesn't disturb the click's in-progress hit logic.
    private void SetActive(IElement? target)
    {
        foreach (var e in _activeChain) e.RemoveAttribute("data-active");
        _activeChain.Clear();
        for (var e = target; e is not null; e = e.ParentElement)
        {
            e.SetAttribute("data-active", "");
            _activeChain.Add(e);
        }
    }

    private bool ClearActive()
    {
        if (_activeChain.Count == 0) return false;
        foreach (var e in _activeChain) e.RemoveAttribute("data-active");
        _activeChain.Clear();
        ReStyle();
        return true;
    }

    /// <summary>Re-resolve styles on the existing DOM (for :hover/:active) without re-parsing/binding.</summary>
    private void ReStyle()
    {
        if (_dom is null) return;
        // Restyle (hover/active) rebuilds the tree too, so preserve scroll offsets — otherwise any
        // mouse move over the page snaps a scrolled field back to the top.
        var scroll = CaptureScroll();
        _root = new StyleResolver(_rules, _viewportWidth).BuildTree(_dom);
        _layoutDirty = true; // fresh tree: no geometry until the next layout
        RestoreScroll(scroll);
        _transitions.Detect(_root); // hover/focus/class change → (re)start any transitions that flipped
    }

    private static double ParseAttr(IElement el, string name, double fallback) =>
        double.TryParse(el.GetAttribute(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // Selector matching for OnClick handlers.
    //
    // NOT `el.Matches(selector)`: that resolves a CSS selector parser off the element's browsing
    // context at call time, and in a TRIMMED WebAssembly publish that resolution fails — it threw, the
    // old catch swallowed it, and every selector-registered OnClick silently became a no-op on the web
    // host (dead sidebar nav, toast buttons, swatches…) while behaving perfectly on desktop. Compiling
    // the selector up front with CssSelectorParser is the same path the style resolver uses — which
    // demonstrably survives trimming, since the published build styles the page correctly — and it also
    // removes a per-click/per-mouse-move parse.
    private static readonly AngleSharp.Css.Parser.CssSelectorParser SelectorParser = new();

    private static AngleSharp.Css.Dom.ISelector? CompileSelector(string selector)
    {
        try { return SelectorParser.ParseSelector(selector); }
        catch { return null; } // unparseable selector: the handler simply never matches
    }

    private static bool Matches(IElement el, AngleSharp.Css.Dom.ISelector? compiled) =>
        compiled is not null && compiled.Match(el, null);

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

    /// <summary>Render a frame to an RGBA8888 byte buffer (<c>width*height*4</c>) — the canonical
    /// "embed me in another surface" entry point (HTML canvas, a game texture, …). Clears to
    /// <paramref name="clear"/> (default transparent, for overlays). Set <paramref name="straightAlpha"/>
    /// for consumers that want NON-premultiplied alpha (HTML <c>ImageData</c>, Unity <c>RGBA32</c>);
    /// leave it false for desktop compositors, which want premultiplied — Skia's native output.
    /// (A host needing zero per-frame allocation can instead call <see cref="Render"/> into its own
    /// surface and read its pixels directly.)</summary>
    public byte[] RenderToPixels(int width, int height, SKColor? clear = null, bool straightAlpha = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(clear ?? SKColors.Transparent);
        Render(surface.Canvas, width, height);
        surface.Canvas.Flush();
        using var img = surface.Snapshot();
        // Read back in the requested alpha type — Skia converts premultiplied → straight for us.
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888,
            straightAlpha ? SKAlphaType.Unpremul : SKAlphaType.Premul));
        img.ReadPixels(bmp.PeekPixels(), 0, 0);
        return bmp.Bytes;
    }

    public void Dispose()
    {
        _fonts.Dispose();
        _images.Dispose();
        _dom?.Dispose();
        _templateDom?.Dispose();
    }
}
