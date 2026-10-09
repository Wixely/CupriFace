using System.Runtime.InteropServices;

namespace CupriFace.Shell;

/// <summary>Keeps native maximize bounds on the monitor that contains the window. GLFW and SDL can
/// otherwise inherit primary-monitor bounds when Windows asks their HWND for <c>WM_GETMINMAXINFO</c>,
/// which moves a window from a secondary display when its title-bar maximize button is pressed.</summary>
internal sealed partial class WindowsMonitorMaximize : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    private WndProc? _windowProcedure;
    private nint _window;
    private nint _previousWindowProcedure;

    public void Attach(nint? window)
    {
        if (!OperatingSystem.IsWindows() || _window != 0 || window is not { } hwnd || hwnd == 0)
            return;

        _windowProcedure = HandleWindowMessage;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtrW(
            hwnd,
            GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            _windowProcedure = null;
            return;
        }

        _window = hwnd;
        _previousWindowProcedure = previous;
    }

    private nint HandleWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        var result = Forward(window, message, wParam, lParam);
        if (message != WmGetMinMaxInfo || lParam == 0)
            return result;

        try
        {
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo))
                return result;

            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            bounds.MaxPosition.X = Math.Abs(monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left);
            bounds.MaxPosition.Y = Math.Abs(monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top);
            bounds.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
            bounds.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
            Marshal.StructureToPtr(bounds, lParam, false);
        }
        catch
        {
            // Exceptions cannot cross a native window-procedure callback. The platform's original
            // maximize behavior remains available through the procedure that already ran above.
        }

        return result;
    }

    private nint Forward(nint window, uint message, nuint wParam, nint lParam) =>
        _previousWindowProcedure != 0
            ? CallWindowProcW(_previousWindowProcedure, window, message, wParam, lParam)
            : DefWindowProcW(window, message, wParam, lParam);

    public void Dispose()
    {
        if (_window != 0)
            _ = SetWindowLongPtrW(_window, GwlpWndProc, _previousWindowProcedure);
        _window = 0;
        _previousWindowProcedure = 0;
        _windowProcedure = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint previous, nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);
}
