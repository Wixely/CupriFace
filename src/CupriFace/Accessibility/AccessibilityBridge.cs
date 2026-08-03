namespace CupriFace.Accessibility;

/// <summary>
/// Bridges the platform-neutral <see cref="AccessibilityNode"/> tree to a native
/// assistive-technology API. One implementation per platform (DESIGN.md §5):
///   Windows → UI Automation · Linux → AT-SPI (D-Bus) · macOS → NSAccessibility ·
///   web → hidden DOM overlay.
/// </summary>
public interface IAccessibilityBridge
{
    /// <summary>Publish/refresh the semantics tree to the platform AT layer.</summary>
    void Update(AccessibilityNode root);
}

/// <summary>
/// Windows UI Automation bridge — SCAFFOLD. The role/state mapping below is the
/// contract; a complete implementation exposes each <see cref="AccessibilityNode"/> as
/// an IRawElementProviderFragment rooted on the window HWND and implements the control
/// patterns per role. That requires a live windowed host + a screen reader (Narrator)
/// to validate, so it is intentionally not wired up in this headless environment.
///
/// Role → UIA control type + pattern:
///   button       → Button        + Invoke
///   switch/checkbox → CheckBox   + Toggle (aria-checked → ToggleState)
///   slider       → Slider        + RangeValue (aria-valuemin/max/now)
///   progressbar  → ProgressBar   + RangeValue (read-only)
///   heading      → Text (with HeadingLevel)
///   link         → Hyperlink     + Invoke
///   image        → Image (Name = alt / aria-label)
/// Name comes from <see cref="AccessibilityNode.Name"/>; Bounds map to
/// BoundingRectangle; Focusable → IsKeyboardFocusable.
/// </summary>
public sealed class WindowsUiaBridge : IAccessibilityBridge
{
    private AccessibilityNode? _root;

    /// <summary>The most recently published tree (what a UIA client would traverse).</summary>
    public AccessibilityNode? Root => _root;

    public void Update(AccessibilityNode root) => _root = root;

    // A full provider would additionally:
    //  - respond to WM_GETOBJECT (UiaReturnRawElementProvider) on the window,
    //  - implement IRawElementProviderSimple/Fragment/FragmentRoot per node,
    //  - raise UIA events (property-changed, structure-changed) on model updates.
}
