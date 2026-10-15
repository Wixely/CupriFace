using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CupriFace.Media.Windows;

internal readonly record struct PopOutBounds(int X, int Y, int Width, int Height, bool Maximized = false, bool Fullscreen = false);

internal static partial class Win32VideoWindow
{
    private const string WindowClassName = "CupriFace.Media.Windows.VideoHost";
    private const int BlackBrush = 4;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WsChild = 0x40000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsExTopmost = 0x00000008;
    private const int SwHide = 0;
    private const int SwShowMaximized = 3;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private static readonly object WindowClassLock = new();
    private static bool _windowClassRegistered;
    private static nint _moduleInstance;
    private static readonly Dictionary<nint, PopOutRegistration> PopOutChildren = [];

    private sealed record PopOutRegistration(nint Child, Action CloseRequested);

    internal static nint Create(nint parent)
    {
        EnsureWindowClass();
        var window = CreateWindowExW(
            0,
            WindowClassName,
            "",
            WsChild | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            parent,
            0,
            _moduleInstance,
            0);
        if (window == 0) throw new InvalidOperationException("Could not create the video child window.");
        EnableWindow(window, false);
        return window;
    }

    private static unsafe void EnsureWindowClass()
    {
        if (Volatile.Read(ref _windowClassRegistered)) return;

        lock (WindowClassLock)
        {
            if (_windowClassRegistered) return;

            _moduleInstance = GetModuleHandleW(0);
            fixed (char* className = WindowClassName)
            {
                var windowClass = new WindowClass
                {
                    WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                    Instance = _moduleInstance,
                    Background = GetStockObject(BlackBrush),
                    ClassName = (nint)className,
                };

                var atom = RegisterClassW(ref windowClass);
                var error = atom == 0 ? Marshal.GetLastPInvokeError() : 0;
                if (atom == 0 && error != ErrorClassAlreadyExists)
                {
                    throw new InvalidOperationException($"Could not register the video child-window class (Win32 error {error}).");
                }
            }

            Volatile.Write(ref _windowClassRegistered, true);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wordParameter, nint longParameter)
    {
        if (Win32PopOutChrome.HandleParentMessage(window, message, wordParameter, longParameter)) return 0;
        const uint windowSize = 0x0005;
        const uint windowClose = 0x0010;
        PopOutRegistration? registration;
        lock (PopOutChildren) PopOutChildren.TryGetValue(window, out registration);
        var child = registration?.Child ?? 0;
        if (child != 0 && message == windowSize)
        {
            FillClient(child, window);
            return 0;
        }
        // Closing the shell docks the one live decoder; it must never destroy playback.
        if (registration is not null && message == windowClose)
        {
            registration.CloseRequested();
            return 0;
        }
        return DefWindowProcW(window, message, wordParameter, longParameter);
    }

    internal static nint CreatePopOut(
        nint parent,
        nint child,
        float x,
        float y,
        float width,
        float height,
        PopOutBounds? restoredBounds,
        Action closeRequested,
        PopOutPlayerControls controls)
    {
        EnsureWindowClass();
        var origin = new Point { X = (int)MathF.Round(x), Y = (int)MathF.Round(y) };
        ClientToScreen(parent, ref origin);
        var wanted = restoredBounds ?? new PopOutBounds(
            origin.X,
            origin.Y,
            Math.Max(320, (int)MathF.Round(width)),
            Math.Max(180, (int)MathF.Round(height)));
        wanted = ClampToVisibleWorkArea(wanted);
        // Windows Snap Layouts report a full-monitor zone as a normal (not zoomed) window. Promote
        // that geometry to a real maximized restore so the replacement keeps squared monitor edges
        // rather than becoming a normal bordered window a few pixels inside them.
        if (!wanted.Maximized && FillsAvailableMonitorBounds(wanted))
            wanted = wanted with { Maximized = true };
        var window = CreateWindowExW(
            WsExTopmost,
            WindowClassName,
            "Luma player",
            WsOverlappedWindow,
            wanted.X,
            wanted.Y,
            wanted.Width,
            wanted.Height,
            0,
            0,
            _moduleInstance,
            0);
        if (window == 0) throw new InvalidOperationException("Could not create the picture-in-picture window.");
        ApplyLumaTheme(window);
        lock (PopOutChildren) PopOutChildren[window] = new PopOutRegistration(child, closeRequested);
        SetParent(child, window);
        FillClient(child, window);
        ShowWindow(window, wanted.Maximized ? SwShowMaximized : SwShowNoActivate);
        Win32PopOutChrome.Attach(window, controls, wanted.Fullscreen);
        return window;
    }

    internal static void PlaceInPopOut(nint child, nint popOut) => FillClient(child, popOut);

    internal static PopOutBounds? DestroyPopOut(nint popOut, nint child, nint parent)
    {
        if (popOut == 0) return null;
        PopOutBounds? bounds = Win32PopOutChrome.CaptureBounds(popOut);
        if (bounds is { Fullscreen: false } captured)
        {
            var rect = new Rect { Left = captured.X, Top = captured.Y, Right = captured.X + captured.Width, Bottom = captured.Y + captured.Height };
            bounds = captured with { Maximized = captured.Maximized || FillsAvailableMonitorBounds(rect) };
        }
        Win32PopOutChrome.Detach(popOut);
        lock (PopOutChildren) PopOutChildren.Remove(popOut);
        ShowWindow(popOut, SwHide);
        SetParent(child, parent);
        DestroyWindow(popOut);
        return bounds;
    }

    private static PopOutBounds ClampToVisibleWorkArea(PopOutBounds bounds)
    {
        var probe = new Rect
        {
            Left = bounds.X,
            Top = bounds.Y,
            Right = bounds.X + Math.Max(1, bounds.Width),
            Bottom = bounds.Y + Math.Max(1, bounds.Height),
        };
        var monitor = MonitorFromRect(ref probe, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
            return bounds with { Width = Math.Max(320, bounds.Width), Height = Math.Max(180, bounds.Height) };

        var workWidth = Math.Max(320, info.Work.Right - info.Work.Left);
        var workHeight = Math.Max(180, info.Work.Bottom - info.Work.Top);
        var width = Math.Clamp(bounds.Width, 320, workWidth);
        var height = Math.Clamp(bounds.Height, 180, workHeight);
        var x = Math.Clamp(bounds.X, info.Work.Left, info.Work.Right - width);
        var y = Math.Clamp(bounds.Y, info.Work.Top, info.Work.Bottom - height);
        return new PopOutBounds(x, y, width, height, bounds.Maximized);
    }

    private static bool FillsAvailableMonitorBounds(PopOutBounds bounds) => FillsAvailableMonitorBounds(new Rect
    {
        Left = bounds.X,
        Top = bounds.Y,
        Right = bounds.X + bounds.Width,
        Bottom = bounds.Y + bounds.Height,
    });

    private static bool FillsAvailableMonitorBounds(Rect rect)
    {
        var monitor = MonitorFromRect(ref rect, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return false;

        const int tolerance = 12; // DWM's invisible resize border varies with monitor DPI.
        return Matches(rect, info.Work, tolerance) || Matches(rect, info.Monitor, tolerance);

        static bool Matches(Rect actual, Rect target, int tolerance) =>
            Math.Abs(actual.Left - target.Left) <= tolerance
            && Math.Abs(actual.Top - target.Top) <= tolerance
            && Math.Abs(actual.Right - target.Right) <= tolerance
            && Math.Abs(actual.Bottom - target.Bottom) <= tolerance;
    }

    private static void ApplyLumaTheme(nint window)
    {
        // Windows 11 DWM colours use COLORREF (0x00bbggrr). Unsupported attributes are simply
        // ignored on older builds, leaving the normal system chrome rather than failing pop-out.
        var enabled = 1;
        _ = DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int));
        var caption = 0x00201610; // #101620
        var text = 0x00FBF6F3;    // #F3F6FB
        _ = DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
    }

    private static void FillClient(nint child, nint parent)
    {
        if (!GetClientRect(parent, out var bounds)) return;
        SetWindowRgn(child, 0, true);
        SetWindowPos(child, 0, 0, 0, Math.Max(1, bounds.Right), Math.Max(1, bounds.Bottom), SwpNoActivate | SwpNoZOrder);
        ShowWindow(child, SwShowNoActivate);
    }

    internal static void Place(
        nint window,
        float x,
        float y,
        float width,
        float height,
        float clipTop,
        float clipRight,
        float clipBottom,
        float clipLeft,
        IReadOnlyList<CupriFace.Paint.HostSurfaceOcclusion>? occlusions)
    {
        var px = (int)MathF.Round(x);
        var py = (int)MathF.Round(y);
        var pw = Math.Max(1, (int)MathF.Round(width));
        var ph = Math.Max(1, (int)MathF.Round(height));
        SetWindowPos(window, 0, px, py, pw, ph, SwpNoActivate | SwpNoZOrder);

        var left = Math.Clamp((int)MathF.Round(clipLeft), 0, pw);
        var top = Math.Clamp((int)MathF.Round(clipTop), 0, ph);
        var right = Math.Clamp(pw - (int)MathF.Round(clipRight), left, pw);
        var bottom = Math.Clamp(ph - (int)MathF.Round(clipBottom), top, ph);
        var region = CreateRectRgn(left, top, right, bottom);
        if (region != 0 && occlusions is not null)
        {
            foreach (var occlusion in occlusions)
            {
                var cutLeft = Math.Clamp((int)MathF.Floor(occlusion.X), left, right);
                var cutTop = Math.Clamp((int)MathF.Floor(occlusion.Y), top, bottom);
                var cutRight = Math.Clamp((int)MathF.Ceiling(occlusion.X + occlusion.Width), cutLeft, right);
                var cutBottom = Math.Clamp((int)MathF.Ceiling(occlusion.Y + occlusion.Height), cutTop, bottom);
                if (cutRight <= cutLeft || cutBottom <= cutTop) continue;
                var cutout = CreateRectRgn(cutLeft, cutTop, cutRight, cutBottom);
                if (cutout == 0) continue;
                CombineRgn(region, region, cutout, RegionDifference);
                DeleteObject(cutout);
            }
        }
        if (region != 0 && SetWindowRgn(window, region, true) == 0) DeleteObject(region);
        ShowWindow(window, SwShowNoActivate);
    }

    internal static void Hide(nint window) => ShowWindow(window, SwHide);
    internal static void Destroy(nint window) => DestroyWindow(window);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X; internal int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static partial ushort RegisterClassW(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wordParameter, nint longParameter);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref Rect rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int objectIndex);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll")]
    private static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    private const int RegionDifference = 4;

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint destination, nint source1, nint source2, int combineMode);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint value);
}
