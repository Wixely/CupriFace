using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CupriFace.Media.Windows;

internal static partial class Win32VideoWindow
{
    private const string WindowClassName = "CupriFace.Media.Windows.VideoHost";
    private const int BlackBrush = 4;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WsChild = 0x40000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private static readonly object WindowClassLock = new();
    private static bool _windowClassRegistered;
    private static nint _moduleInstance;

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
    private static nint WindowProcedure(nint window, uint message, nuint wordParameter, nint longParameter) =>
        DefWindowProcW(window, message, wordParameter, longParameter);

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

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static partial ushort RegisterClassW(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wordParameter, nint longParameter);

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
