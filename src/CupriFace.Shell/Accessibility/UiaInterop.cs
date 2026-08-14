using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CupriFace.Shell.Accessibility;

// The one file in CupriFace that talks to Windows directly.
//
// UI Automation is a COM contract defined by the OS: a screen reader asks our window for a
// provider via WM_GETOBJECT, and everything after that is COM calls on the interfaces below.
// There is no fully managed route — Microsoft's managed wrapper (UIAutomationProvider.dll)
// lives in the WindowsDesktop framework, and referencing that would pull the whole desktop
// runtime into a self-contained publish (~4x the size) for the sake of two small assemblies.
//
// So this file mirrors the relevant slice of UIAutomationCore.idl by hand instead. The rules
// that keep it correct:
//   - Member ORDER inside each interface is the COM vtable order. Never reorder, never insert.
//   - GUIDs come verbatim from the IDL.
//   - VARIANT returns marshal as object (UnmanagedType.Struct); the runtime does the rest.
// Everything is 64-bit only (SetWindowLongPtrW has no 32-bit export) — matching the RIDs we
// ship. Callers must be behind OperatingSystem.IsWindows().

internal enum NavigateDirection
{
    Parent = 0,
    NextSibling = 1,
    PreviousSibling = 2,
    FirstChild = 3,
    LastChild = 4,
}

[Flags]
internal enum ProviderOptions
{
    ServerSideProvider = 0x1,
    UseComThreading = 0x20,
}

internal enum ToggleState
{
    Off = 0,
    On = 1,
    Indeterminate = 2,
}

internal enum ExpandCollapseState
{
    Collapsed = 0,
    Expanded = 1,
    PartiallyExpanded = 2,
    LeafNode = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public double Left, Top, Width, Height;
}

[ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawElementProviderSimple
{
    ProviderOptions ProviderOptions { get; }

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object? GetPatternProvider(int patternId);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? GetPropertyValue(int propertyId);

    IRawElementProviderSimple? HostRawElementProvider { get; }
}

[ComImport, Guid("f7063da8-8359-439c-9297-bbc5299a7d87"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawElementProviderFragment
{
    IRawElementProviderFragment? Navigate(NavigateDirection direction);

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
    int[]? GetRuntimeId();

    UiaRect BoundingRectangle { get; }

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
    object[]? GetEmbeddedFragmentRoots();

    void SetFocus();

    IRawElementProviderFragmentRoot? FragmentRoot { get; }
}

[ComImport, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawElementProviderFragmentRoot
{
    IRawElementProviderFragment? ElementProviderFromPoint(double x, double y);

    IRawElementProviderFragment? GetFocus();
}

[ComImport, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInvokeProvider
{
    void Invoke();
}

[ComImport, Guid("56d00bd0-c4f4-433c-a836-1a52a57e0892"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IToggleProvider
{
    void Toggle();
    ToggleState ToggleState { get; }
}

[ComImport, Guid("36dc7aef-33e6-4691-afe1-2be7274b3d33"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRangeValueProvider
{
    void SetValue(double value);
    double Value { get; }
    // UIA's IDL uses 4-byte BOOL; COM's default for C# bool is 2-byte VARIANT_BOOL. The
    // MarshalAs must sit on the GETTER (return position) to actually apply to a property.
    bool IsReadOnly { [return: MarshalAs(UnmanagedType.Bool)] get; }
    double Maximum { get; }
    double Minimum { get; }
    double LargeChange { get; }
    double SmallChange { get; }
}

[ComImport, Guid("c7935180-6fb3-4201-b174-7df73adbf64a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IValueProvider
{
    void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
    string? Value { [return: MarshalAs(UnmanagedType.BStr)] get; }
    bool IsReadOnly { [return: MarshalAs(UnmanagedType.Bool)] get; }
}

[ComImport, Guid("2acad808-b2d4-452d-a407-91ff1ad167b2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISelectionItemProvider
{
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    bool IsSelected { [return: MarshalAs(UnmanagedType.Bool)] get; }
    IRawElementProviderSimple? SelectionContainer { get; }
}

[ComImport, Guid("fb8b03af-3bdf-48d4-bd36-1a65793be168"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISelectionProvider
{
    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
    object[]? GetSelection();
    bool CanSelectMultiple { [return: MarshalAs(UnmanagedType.Bool)] get; }
    bool IsSelectionRequired { [return: MarshalAs(UnmanagedType.Bool)] get; }
}

[ComImport, Guid("d847d3a5-cab0-4a98-8c32-ecb45c59ad24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExpandCollapseProvider
{
    void Expand();
    void Collapse();
    ExpandCollapseState ExpandCollapseState { get; }
}

[SupportedOSPlatform("windows")]
internal static class UiaNative
{
    public const uint WM_GETOBJECT = 0x003D;
    public const uint WM_DESTROY = 0x0002;
    public const int UiaRootObjectId = -25;
    public const int GWLP_WNDPROC = -4;

    internal delegate nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("uiautomationcore.dll")]
    public static extern nint UiaReturnRawElementProvider(
        nint hwnd, nuint wParam, nint lParam,
        [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple? el);

    [DllImport("uiautomationcore.dll")]
    public static extern int UiaHostProviderFromHwnd(
        nint hwnd, [MarshalAs(UnmanagedType.Interface)] out IRawElementProviderSimple provider);

    [DllImport("uiautomationcore.dll")]
    public static extern int UiaRaiseAutomationEvent(
        [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider, int eventId);

    [DllImport("uiautomationcore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UiaClientsAreListening();

    [DllImport("uiautomationcore.dll")]
    public static extern int UiaDisconnectProvider(
        [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    public static extern nint CallWindowProcW(nint prevWndProc, nint hWnd, uint msg, nuint wParam, nint lParam);
}

// UIA integer ids (UIAutomationClient.h). Only the ones the bridge uses.
internal static class UiaIds
{
    // Properties
    public const int ControlTypeProperty = 30003;
    public const int NameProperty = 30005;
    public const int HasKeyboardFocusProperty = 30008;
    public const int IsKeyboardFocusableProperty = 30009;
    public const int IsEnabledProperty = 30010;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int HelpTextProperty = 30013;
    public const int IsControlElementProperty = 30016;
    public const int IsContentElementProperty = 30017;
    public const int IsOffscreenProperty = 30022;
    public const int FrameworkIdProperty = 30024;

    // Control types
    public const int Button = 50000;
    public const int Calendar = 50001;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Hyperlink = 50005;
    public const int Image = 50006;
    public const int ListItem = 50007;
    public const int List = 50008;
    public const int MenuItem = 50011;
    public const int ProgressBar = 50012;
    public const int RadioButton = 50013;
    public const int Slider = 50015;
    public const int Tab = 50018;
    public const int Menu = 50009;
    public const int MenuBar = 50010;
    public const int Spinner = 50016;
    public const int TabItem = 50019;
    public const int Text = 50020;
    public const int ToolTip = 50022;
    public const int Tree = 50023;
    public const int TreeItem = 50024;
    public const int Custom = 50025;
    public const int Group = 50026;
    public const int Document = 50030;
    public const int Window = 50032;
    public const int Pane = 50033;
    public const int HeaderItem = 50035;
    public const int Table = 50036;
    public const int Separator = 50038;

    // Patterns
    public const int InvokePattern = 10000;
    public const int SelectionPattern = 10001;
    public const int ValuePattern = 10002;
    public const int RangeValuePattern = 10003;
    public const int ExpandCollapsePattern = 10005;
    public const int SelectionItemPattern = 10010;
    public const int TogglePattern = 10015;

    // Events
    public const int AutomationFocusChangedEvent = 20005;
    public const int InvokedEvent = 20009;

    // GetRuntimeId: first element of a non-root runtime id
    public const int AppendRuntimeId = 3;
}
