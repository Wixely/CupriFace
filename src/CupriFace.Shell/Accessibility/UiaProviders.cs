using System.Runtime.Versioning;
using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

// The COM objects a UIA client (Narrator, NVDA, FlaUI…) actually talks to. They are thin and
// thread-agnostic on purpose: every read resolves this provider's PATH against the bridge's
// current immutable snapshot (published by the UI thread after each drawn frame), and every
// action is POSTED to the UI thread — no UIA thread ever touches the live document. A path that
// no longer resolves (the control disappeared in a rebuild) degrades to empty answers, which
// UIA clients handle as "element gone".

/// <summary>The fragment root: represents the window's whole content (the "document" node).</summary>
[SupportedOSPlatform("windows")]
internal sealed class UiaRootProvider :
    IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot
{
    private readonly UiaBridge _bridge;
    public UiaRootProvider(UiaBridge bridge) => _bridge = bridge;

    public ProviderOptions ProviderOptions =>
        ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

    public object? GetPatternProvider(int patternId) => null;

    public object? GetPropertyValue(int propertyId) => propertyId switch
    {
        UiaIds.ControlTypeProperty => UiaIds.Pane,
        UiaIds.FrameworkIdProperty => "CupriFace",
        UiaIds.IsControlElementProperty => true,
        UiaIds.IsContentElementProperty => false,   // the window itself is chrome, not content
        _ => null,                                  // everything else comes from the HWND host
    };

    public IRawElementProviderSimple? HostRawElementProvider => _bridge.HostProvider;

    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        var root = _bridge.Current?.Root;
        if (root is null || root.Children.Count == 0) return null;
        return direction switch
        {
            NavigateDirection.FirstChild => _bridge.ProviderFor(root.Children[0]),
            NavigateDirection.LastChild => _bridge.ProviderFor(root.Children[^1]),
            _ => null,   // the root has no parent or siblings inside this fragment tree
        };
    }

    public int[]? GetRuntimeId() => null;   // the host provider supplies the window's id

    public UiaRect BoundingRectangle => _bridge.ToScreenRect(_bridge.Current?.Root.Bounds ?? default);

    public object[]? GetEmbeddedFragmentRoots() => null;

    public void SetFocus() { /* focusing the window is the OS's business; nothing to do */ }

    public IRawElementProviderFragmentRoot? FragmentRoot => this;

    public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y)
    {
        if (_bridge.Current is not { } snap) return null;
        // Screen physical px → CSS px, then the deepest node whose on-screen box contains the
        // point — walking children in order and letting later (painted-on-top) matches win,
        // mirroring how hit-testing resolves overlaps.
        var cx = (float)((x - snap.OriginX) / snap.Scale);
        var cy = (float)((y - snap.OriginY) / snap.Scale);
        AccessibilityNode? best = null;
        void Walk(AccessibilityNode n)
        {
            var (bx, by, bw, bh) = n.Bounds;
            if (cx >= bx && cx < bx + bw && cy >= by && cy < by + bh) best = n;
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var c in snap.Root.Children) Walk(c);
        return best is null ? null : _bridge.ProviderFor(best);
    }

    public IRawElementProviderFragment? GetFocus() =>
        _bridge.Current is { FocusedPath: { } fp } snap && snap.ByPath.TryGetValue(fp, out var n)
            ? _bridge.ProviderFor(n)
            : null;
}

/// <summary>One provider per semantic node, identified by structural path (stable across the
/// engine's per-keystroke rebuilds). Implements every pattern; <see cref="GetPatternProvider"/>
/// gates which ones a given role advertises.</summary>
[SupportedOSPlatform("windows")]
internal sealed class UiaNodeProvider :
    IRawElementProviderSimple, IRawElementProviderFragment,
    IInvokeProvider, IToggleProvider, IRangeValueProvider, IValueProvider,
    ISelectionItemProvider, ISelectionProvider, IExpandCollapseProvider
{
    private readonly UiaBridge _bridge;
    private readonly string _path;
    private readonly int _runtimeId;

    public UiaNodeProvider(UiaBridge bridge, string path, int runtimeId)
    {
        _bridge = bridge;
        _path = path;
        _runtimeId = runtimeId;
    }

    internal string Path => _path;

    private AccessibilityNode? Node =>
        _bridge.Current is { } snap && snap.ByPath.TryGetValue(_path, out var n) ? n : null;

    // ---- IRawElementProviderSimple ----------------------------------------------------------

    public ProviderOptions ProviderOptions =>
        ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

    public object? GetPatternProvider(int patternId) =>
        Node is { } n && UiaBridge.Supports(patternId, n) ? this : null;

    public object? GetPropertyValue(int propertyId)
    {
        if (Node is not { } n) return null;
        return propertyId switch
        {
            UiaIds.NameProperty => n.Name,
            UiaIds.ControlTypeProperty => UiaBridge.ControlTypeOf(n.Role),
            UiaIds.IsEnabledProperty => !n.Disabled,
            UiaIds.IsKeyboardFocusableProperty => n.Focusable && !n.Disabled,
            UiaIds.HasKeyboardFocusProperty => n.Focused,
            UiaIds.AutomationIdProperty => n.AutomationId,
            UiaIds.ClassNameProperty => n.Role,
            UiaIds.FrameworkIdProperty => "CupriFace",
            UiaIds.IsControlElementProperty => true,
            UiaIds.IsContentElementProperty => true,
            // Scrolled past, or clipped away by an overflow ancestor. Narrator uses this to skip a
            // control rather than read the whole document aloud.
            UiaIds.IsOffscreenProperty => n.Offscreen,
            _ => null,
        };
    }

    public IRawElementProviderSimple? HostRawElementProvider => null;

    // ---- IRawElementProviderFragment --------------------------------------------------------

    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        if (Node is not { } n) return null;
        switch (direction)
        {
            case NavigateDirection.Parent:
                return n.Parent is { } p && p.Parent is not null
                    ? _bridge.ProviderFor(p)
                    : _bridge.Root;   // a top-level node's parent is the fragment root
            case NavigateDirection.FirstChild:
                return n.Children.Count > 0 ? _bridge.ProviderFor(n.Children[0]) : null;
            case NavigateDirection.LastChild:
                return n.Children.Count > 0 ? _bridge.ProviderFor(n.Children[^1]) : null;
            case NavigateDirection.NextSibling:
            case NavigateDirection.PreviousSibling:
            {
                var siblings = n.Parent?.Children ?? _bridge.Current?.Root.Children;
                if (siblings is null) return null;
                var i = siblings.IndexOf(n);
                if (i < 0) return null;
                i += direction == NavigateDirection.NextSibling ? 1 : -1;
                return i >= 0 && i < siblings.Count ? _bridge.ProviderFor(siblings[i]) : null;
            }
            default:
                return null;
        }
    }

    public int[] GetRuntimeId() => new[] { UiaIds.AppendRuntimeId, _runtimeId };

    public UiaRect BoundingRectangle =>
        Node is { } n ? _bridge.ToScreenRect(n.Bounds) : default;

    public object[]? GetEmbeddedFragmentRoots() => null;

    public void SetFocus() => _bridge.Post(doc => doc.AccessibilityFocus(_path));

    public IRawElementProviderFragmentRoot? FragmentRoot => _bridge.Root;

    // ---- Patterns ---------------------------------------------------------------------------

    public void Invoke() => _bridge.Post(doc => doc.AccessibilityActivate(_path));

    public void Toggle() => _bridge.Post(doc => doc.AccessibilityActivate(_path));

    public ToggleState ToggleState => Node?.Checked switch
    {
        true => ToggleState.On,
        false => ToggleState.Off,
        null => ToggleState.Indeterminate,
    };

    public void SetValue(double value)
    {
        if (Node?.Role is not "slider") throw new InvalidOperationException("Value is read-only.");
        _bridge.Post(doc => doc.AccessibilitySetValue(_path, value));
    }

    public double Value => Node?.Now ?? 0;
    bool IRangeValueProvider.IsReadOnly => Node?.Role is not "slider";
    public double Maximum => Node?.Max ?? 100;
    public double Minimum => Node?.Min ?? 0;
    public double LargeChange => (Maximum - Minimum) / 10;
    public double SmallChange => 1;

    public void SetValue(string value) => throw new InvalidOperationException(
        "Text is entered through keyboard focus in this version.");   // honest: IsReadOnly says so

    string? IValueProvider.Value => Node?.Value;
    bool IValueProvider.IsReadOnly => true;

    public void Select() => _bridge.Post(doc => doc.AccessibilityActivate(_path));
    public void AddToSelection() => _bridge.Post(doc => doc.AccessibilityActivate(_path));
    public void RemoveFromSelection() { /* every selectable here is single-select */ }
    public bool IsSelected => Node is { } n && (n.Selected ?? n.Checked ?? false);
    public IRawElementProviderSimple? SelectionContainer =>
        Node?.Parent is { } p && p.Parent is not null ? _bridge.ProviderFor(p) : null;

    public object[]? GetSelection()
    {
        if (Node is not { } n) return null;
        var selected = new List<object>();
        foreach (var c in n.Children)
            if (c.Selected ?? c.Checked ?? false) selected.Add(_bridge.ProviderFor(c));
        return selected.Count > 0 ? selected.ToArray() : null;
    }
    public bool CanSelectMultiple => false;
    public bool IsSelectionRequired => false;

    public void Expand()
    {
        if (Node is { Expanded: false }) _bridge.Post(doc => doc.AccessibilityActivate(_path));
    }
    public void Collapse()
    {
        if (Node is { Expanded: true }) _bridge.Post(doc => doc.AccessibilityActivate(_path));
    }
    public ExpandCollapseState ExpandCollapseState => Node?.Expanded switch
    {
        true => ExpandCollapseState.Expanded,
        false => ExpandCollapseState.Collapsed,
        null => ExpandCollapseState.LeafNode,
    };
}
