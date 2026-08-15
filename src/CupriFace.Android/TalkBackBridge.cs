using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using CupriFace.Accessibility;

namespace CupriFace.Android;

/// <summary>
/// The fourth accessibility bridge — TalkBack, at the same altitude as UIA, AT-SPI and
/// NSAccessibility: the engine's platform-neutral semantics tree exposed as a VIRTUAL view
/// hierarchy through a raw <see cref="AccessibilityNodeProvider"/>. No AndroidX — the raw
/// provider is the whole dependency, exactly as the other three bridges speak their platform's
/// raw protocol.
///
/// Threading: the document lives on the GL thread, TalkBack asks on the UI thread. The bridge
/// therefore never touches the document to ANSWER — it answers from an immutable
/// <see cref="AccessibilityNode"/> tree the host publishes after any frame whose ContentVersion
/// moved (Animate bumps the version when a fling steps, so scrolled bounds republish too).
/// Actions cross back the other way, queued to the GL thread through the same door touch uses.
///
/// Identity: virtual ids are allocated per STRUCTURAL PATH and never reused — the identity that
/// survives the engine's per-keystroke rebuilds, the same scheme scroll restoration and the
/// desktop bridges key on.
///
/// Kill switch: `adb shell setprop debug.cupriface.talkback off` (read once per process) makes
/// the view report no provider — the escape hatch if a device's screen reader misbehaves.
/// </summary>
internal sealed class TalkBackBridge : AccessibilityNodeProvider
{
    private readonly CupriHostView _view;
    private readonly AndroidHost _host;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _idByPath = new();   // never shrinks: ids are forever
    private Dictionary<int, AccessibilityNode> _byId = new();
    private Dictionary<int, int> _parentOf = new();               // child id -> parent id (-1 = host)
    private AccessibilityNode? _root;
    private float _scale = 1f;
    private int _originX, _originY;                                // view's screen origin (OnLayout)
    private int _nextId = 1;
    private int _a11yFocusId = -1;                                 // TalkBack's green rectangle
    private int _hoverId = -1;                                     // explore-by-touch position
    private long _lastContentEvent;                                // throttle WINDOW_CONTENT_CHANGED

    private TalkBackBridge(CupriHostView view, AndroidHost host)
    {
        _view = view;
        _host = host;
    }

    /// <summary>Null when the kill switch is set — the view then reports no provider and Android
    /// falls back to treating it as one opaque view.</summary>
    internal static TalkBackBridge? Create(CupriHostView view, AndroidHost host) =>
        Killed() ? null : new TalkBackBridge(view, host);

    private static bool Killed()
    {
        try
        {
            using var cls = Java.Lang.Class.ForName("android.os.SystemProperties");
            var get = cls.GetMethod("get", Java.Lang.Class.ForName("java.lang.String"));
            var value = get.Invoke(null, new Java.Lang.Object[] { new Java.Lang.String("debug.cupriface.talkback") });
            return string.Equals(value?.ToString(), "off", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal void SetViewOrigin(int x, int y) { lock (_gate) { _originX = x; _originY = y; } }

    // ---- publish (GL thread) ------------------------------------------------------------------

    /// <summary>Swap in a freshly built semantics tree. Called on the GL thread after a frame;
    /// everything stored is immutable-after-publish, so UI-thread reads only need the lock for
    /// the swap itself.</summary>
    internal void Publish(AccessibilityNode root, float inputScale)
    {
        var byId = new Dictionary<int, AccessibilityNode>();
        var parentOf = new Dictionary<int, int>();
        var focusedId = -1;
        lock (_gate)
        {
            // The document root is the window itself — its CHILDREN are the top-level virtual
            // nodes, parented to the host view (id -1).
            foreach (var child in root.Children) Assign(child, -1, byId, parentOf, ref focusedId);
            _root = root;
            _byId = byId;
            _parentOf = parentOf;
            _scale = inputScale;
        }

        // Announce that the subtree changed (throttled — a fling steps every frame) and where
        // input focus went, if anywhere. Events must leave from the UI thread.
        var now = SystemClock.UptimeMillis();
        if (now - _lastContentEvent >= 100)
        {
            _lastContentEvent = now;
            SendEvent(EventTypes.WindowContentChanged, -1);
        }
        if (focusedId >= 0 && focusedId != _lastFocusEventId)
        {
            _lastFocusEventId = focusedId;
            SendEvent(EventTypes.ViewFocused, focusedId);
        }
        else if (focusedId < 0) _lastFocusEventId = -1;  // so refocusing the same node re-announces
    }

    private int _lastFocusEventId = -1;

    private void Assign(AccessibilityNode node, int parentId,
        Dictionary<int, AccessibilityNode> byId, Dictionary<int, int> parentOf, ref int focusedId)
    {
        if (!_idByPath.TryGetValue(node.Path, out var id))
        {
            id = _nextId++;
            _idByPath[node.Path] = id;
        }
        byId[id] = node;
        parentOf[id] = parentId;
        if (node.Focused) focusedId = id;
        foreach (var child in node.Children) Assign(child, id, byId, parentOf, ref focusedId);
    }

    // ---- the provider (UI thread) -------------------------------------------------------------

    public override AccessibilityNodeInfo? CreateAccessibilityNodeInfo(int virtualViewId)
    {
        lock (_gate)
        {
            if (virtualViewId == View.NoId) return CreateHostInfo();
            if (_byId.TryGetValue(virtualViewId, out var node)) return CreateVirtualInfo(virtualViewId, node);
            return null;
        }
    }

    private AccessibilityNodeInfo CreateHostInfo()
    {
        var info = AccessibilityNodeInfo.Obtain(_view)!;
        _view.OnInitializeAccessibilityNodeInfo(info);
        if (_root is { } root)
            foreach (var child in root.Children)
                if (_idByPath.TryGetValue(child.Path, out var id)) info.AddChild(_view, id);
        return info;
    }

    private AccessibilityNodeInfo CreateVirtualInfo(int id, AccessibilityNode node)
    {
        var info = AccessibilityNodeInfo.Obtain(_view, id)!;
        info.PackageName = _view.Context?.PackageName;
        info.ClassName = ClassNameFor(node.Role);

        // What gets announced: the VALUE is the text for textboxes (what's in the field), the
        // NAME rides content-description; for everything else the name IS the text.
        if (node.Role is "textbox" or "combobox" or "spinbutton")
        {
            info.Text = node.Value;
            info.ContentDescription = node.Name;
        }
        else
        {
            info.Text = node.Name;
        }

        if (node.Checked is { } isChecked)
        {
            info.Checkable = true;
            info.Checked = isChecked;
        }
        if (node.Selected is { } isSelected) info.Selected = isSelected;
        info.Enabled = !node.Disabled;
        info.Focusable = node.Focusable;
        info.Focused = node.Focused;
        info.VisibleToUser = !node.Offscreen;              // the clip-chain flag (PR #5), reused
        info.AccessibilityFocused = id == _a11yFocusId;

        info.SetBoundsInScreen(ScreenRect(node));

        if (_parentOf.TryGetValue(id, out var parentId) && parentId >= 0) info.SetParent(_view, parentId);
        else info.SetParent(_view);
        foreach (var child in node.Children)
            if (_idByPath.TryGetValue(child.Path, out var childId)) info.AddChild(_view, childId);

        info.AddAction(id == _a11yFocusId
            ? AccessibilityNodeInfo.AccessibilityAction.ActionClearAccessibilityFocus!
            : AccessibilityNodeInfo.AccessibilityAction.ActionAccessibilityFocus!);
        if (IsActivatable(node)) info.AddAction(AccessibilityNodeInfo.AccessibilityAction.ActionClick!);
        if (node.Focusable) info.AddAction(AccessibilityNodeInfo.AccessibilityAction.ActionFocus!);

        if (node.Role is "slider" && node is { Min: { } mn, Max: { } mx })
        {
            info.SetRangeInfo(AccessibilityNodeInfo.RangeInfo.Obtain(
                RangeType.Float, (float)mn, (float)mx, (float)(node.Now ?? mn)));
            info.AddAction(AccessibilityNodeInfo.AccessibilityAction.ActionSetProgress!);
        }
        return info;
    }

    private Rect ScreenRect(AccessibilityNode node)
    {
        var (x, y, w, h) = node.Bounds;
        return new Rect(
            _originX + (int)(x * _scale), _originY + (int)(y * _scale),
            _originX + (int)((x + w) * _scale), _originY + (int)((y + h) * _scale));
    }

    private static string ClassNameFor(string role) => role switch
    {
        "button" or "tab" or "menuitem" => "android.widget.Button",
        "switch" => "android.widget.Switch",
        "checkbox" => "android.widget.CheckBox",
        "radio" => "android.widget.RadioButton",
        "slider" => "android.widget.SeekBar",
        "progressbar" => "android.widget.ProgressBar",
        "textbox" or "spinbutton" or "combobox" => "android.widget.EditText",
        "heading" or "link" => "android.widget.TextView",
        "image" => "android.widget.ImageView",
        "list" or "listbox" or "tablist" or "menu" => "android.widget.ListView",
        "listitem" or "option" => "android.widget.TextView",
        _ => "android.view.View",
    };

    // The engine's focusable predicate IS "interactive" (it is the Tab-order predicate); roles
    // cover the activatable-but-not-focusable leftovers.
    private static bool IsActivatable(AccessibilityNode n) =>
        n.Focusable || n.Role is "button" or "link" or "switch" or "checkbox" or "radio"
                              or "tab" or "menuitem" or "option" or "textbox" or "combobox";

    public override bool PerformAction(int virtualViewId, [global::Android.Runtime.GeneratedEnum] global::Android.Views.Accessibility.Action action, Bundle? arguments)
    {
        string? path;
        lock (_gate)
        {
            if (!_byId.TryGetValue(virtualViewId, out var node)) return false;
            path = node.Path;
        }

        switch (action)
        {
            case global::Android.Views.Accessibility.Action.AccessibilityFocus:
                _a11yFocusId = virtualViewId;
                SendEvent(EventTypes.ViewAccessibilityFocused, virtualViewId);
                return true;
            case global::Android.Views.Accessibility.Action.ClearAccessibilityFocus:
                if (_a11yFocusId == virtualViewId) _a11yFocusId = -1;
                SendEvent(EventTypes.ViewAccessibilityFocusCleared, virtualViewId);
                return true;
            case global::Android.Views.Accessibility.Action.Click:
                // Queued to the document thread; the action lands as a real click at the node's
                // centre — activation rules, overlays and disabled handling all apply.
                _host.OnGlThread(() => { if (_host.Document.AccessibilityActivate(path)) _host.MarkDirty(); });
                SendEvent(EventTypes.ViewClicked, virtualViewId);
                return true;
            case global::Android.Views.Accessibility.Action.Focus:
                _host.OnGlThread(() => { if (_host.Document.AccessibilityFocus(path)) _host.MarkDirty(); });
                return true;
            default:
                if ((int)action == AccessibilityNodeInfo.AccessibilityAction.ActionSetProgress!.Id
                    && arguments?.GetFloat(AccessibilityNodeInfo.ActionArgumentProgressValue) is { } value)
                {
                    _host.OnGlThread(() => { if (_host.Document.AccessibilitySetValue(path, value)) _host.MarkDirty(); });
                    return true;
                }
                return false;
        }
    }

    // ---- explore-by-touch (UI thread) ---------------------------------------------------------

    /// <summary>Hover events are how TalkBack explores by touch: report which virtual node the
    /// finger is over and TalkBack moves its focus there. Returns true when handled.</summary>
    internal bool OnHover(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.HoverEnter:
            case MotionEventActions.HoverMove:
                int hit;
                lock (_gate) { hit = HitTest(e.GetX(), e.GetY()); }
                if (hit != _hoverId)
                {
                    if (_hoverId >= 0) SendEvent(EventTypes.ViewHoverExit, _hoverId);
                    if (hit >= 0) SendEvent(EventTypes.ViewHoverEnter, hit);
                    _hoverId = hit;
                }
                return true;
            case MotionEventActions.HoverExit:
                if (_hoverId >= 0) SendEvent(EventTypes.ViewHoverExit, _hoverId);
                _hoverId = -1;
                return true;
            default:
                return false;
        }
    }

    // Deepest node under the point wins — later siblings paint on top, so walk children in
    // reverse. View-relative px in, matched against logical bounds × scale.
    private int HitTest(float px, float py)
    {
        if (_root is not { } root || _scale <= 0) return -1;
        var (lx, ly) = (px / _scale, py / _scale);
        return Deepest(root, lx, ly);
    }

    private int Deepest(AccessibilityNode node, float x, float y)
    {
        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var c = node.Children[i];
            if (c.Offscreen) continue;
            var (bx, by, bw, bh) = c.Bounds;
            var inside = x >= bx && x <= bx + bw && y >= by && y <= by + bh;
            // A child OUTSIDE its own bounds can still contain the point through an overlay
            // (top-layer) descendant, so recurse regardless; prefer the deepest inside hit.
            var deep = Deepest(c, x, y);
            if (deep >= 0) return deep;
            if (inside && _idByPath.TryGetValue(c.Path, out var id)) return id;
        }
        return -1;
    }

    // ---- events -------------------------------------------------------------------------------

    private void SendEvent(EventTypes type, int virtualId)
    {
        void Send()
        {
            if (_view.Context?.GetSystemService(global::Android.Content.Context.AccessibilityService)
                    is not AccessibilityManager { IsEnabled: true }) return;
            var ev = AccessibilityEvent.Obtain(type)!;
            ev.PackageName = _view.Context?.PackageName;
            if (type == EventTypes.WindowContentChanged)
            {
                ev.ContentChangeTypes = ContentChangeTypes.Subtree;
                ev.SetSource(_view);
            }
            else if (virtualId >= 0) ev.SetSource(_view, virtualId);
            else ev.SetSource(_view);
            _view.Parent?.RequestSendAccessibilityEvent(_view, ev);
        }
        if (_view.Handler is { } h) h.Post(Send);        // publishes arrive on the GL thread
        else Send();
    }
}
