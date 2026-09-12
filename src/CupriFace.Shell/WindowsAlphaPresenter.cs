using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// Premultiplied BGRA presentation for a frameless Windows window, avoiding the WGL/DWM path that
/// composites zero alpha as black on some machines (#139). The caller retains the bitmap; this
/// retains one DIB until the size changes.
///
/// <para><b>Construction throws, presentation does not.</b> Failing to make the window layered at
/// startup is a real, permanent problem and the caller needs to know before it shows a window.
/// Failing to present ONE frame is not: <c>UpdateLayeredWindow</c> and <c>GetWindowRect</c> can both
/// fail transiently during ordinary desktop upheaval — a monitor change, a session lock, an RDP
/// transition, a race with minimise — and this runs on every frame. Throwing there turns a compositor
/// hiccup into a dead application, so a failed frame is dropped, reported once, and the loop carries
/// on. Same reasoning as the live-resize repaint in <c>SkiaWindow</c>: survivable, but never
/// silent.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsAlphaPresenter : IDisposable
{
    private readonly nint _hwnd;
    private nint _dc, _bitmap, _oldBitmap, _bits;
    private int _width, _height;
    private bool _presentFailureReported, _sizeFailureReported;

    /// <summary>Frames dropped because Windows refused to present them. Zero is the expected value;
    /// anything else belongs in a bug report alongside what the desktop was doing at the time.</summary>
    public int DroppedFrames { get; private set; }

    public WindowsAlphaPresenter(nint hwnd)
    {
        _hwnd = hwnd;
        if ((GetClassLongW(hwnd, -26) & 0x60) != 0)
            throw new InvalidOperationException("Per-pixel alpha requires a window class without CS_OWNDC or CS_CLASSDC.");
        var style = GetWindowLongW(hwnd, -20);
        SetWindowLongW(hwnd, -20, style | 0x80000);
        if ((GetWindowLongW(hwnd, -20) & 0x80000) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Layered style failed");
    }

    /// <summary>Present one frame. Returns false when Windows refused it — the frame is lost, the
    /// window keeps whatever it last showed, and the next frame tries again.</summary>
    public unsafe bool Present(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType != SKAlphaType.Premul)
            throw new ArgumentException("Expected premultiplied BGRA pixels.", nameof(bitmap));

        try
        {
            if (width != _width || height != _height)
            {
                Dispose();
                _dc = CreateCompatibleDC(0);
                if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed");
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, 0, 0);
                if (_bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _oldBitmap = SelectObject(_dc, _bitmap);
                if (_oldBitmap == 0 || _oldBitmap == -1)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SelectObject failed");
                _width = width; _height = height;
            }

            var rowBytes = checked(width * 4);
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy((byte*)bitmap.GetPixels() + (long)y * bitmap.RowBytes,
                    (byte*)_bits + (long)y * rowBytes, rowBytes, rowBytes);

            var size = new Point { X = width, Y = height };
            var source = new Point();
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (UpdateLayeredWindow(_hwnd, 0, 0, ref size, _dc, ref source, 0, ref blend, 2)) return true;

            ReportOnce(ref _presentFailureReported,
                $"UpdateLayeredWindow refused a frame (Win32 {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            ReportOnce(ref _presentFailureReported, $"{ex.GetType().Name}: {ex.Message}");
        }

        DroppedFrames++;
        return false;
    }

    /// <summary>
    /// The window's own size, or null when it cannot be established or the window is minimised.
    ///
    /// <para><c>UpdateLayeredWindow</c> sizes the whole HWND, and on a resized layered window the
    /// CLIENT rect can still describe the old surface until the next present — so the outer rect is
    /// the one to trust here (frameless only, where the two are the same thing).</para>
    /// </summary>
    public (int Width, int Height)? WindowSize
    {
        get
        {
            try
            {
                if (IsIconic(_hwnd)) return null;
                if (GetWindowRect(_hwnd, out var rect))
                    return (rect.Right - rect.Left, rect.Bottom - rect.Top);
                ReportOnce(ref _sizeFailureReported,
                    $"GetWindowRect failed (Win32 {Marshal.GetLastWin32Error()})");
            }
            catch (Exception ex)
            {
                ReportOnce(ref _sizeFailureReported, $"{ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>Say it the first time and then stop. A compositor that is refusing frames refuses
    /// many, and a per-frame message would bury the one line that mattered.</summary>
    private static void ReportOnce(ref bool reported, string detail)
    {
        if (reported) return;
        reported = true;
        Console.Error.WriteLine(
            $"[CupriFace] Windows alpha presentation problem ({detail}); frames may be dropped. "
            + "The window keeps its last presented content. Further occurrences are not reported.");
    }

    public void Dispose()
    {
        if (_dc != 0 && _oldBitmap != 0 && _oldBitmap != -1) SelectObject(_dc, _oldBitmap);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_dc != 0) DeleteDC(_dc);
        _dc = _bitmap = _oldBitmap = _bits = 0;
        _width = _height = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Op, Flags, Alpha, Format; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, Important;
    }

    // LibraryImport rather than DllImport: the Viewer publishes with -p:Aot=true, and #131 moved the
    // UIA bridge to source-generated interop for exactly that reason.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial uint GetClassLongW(nint hwnd, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowLongW(nint hwnd, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowLongW(nint hwnd, int index, int value);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint hwnd, nint dstDc, nint dst, ref Point size, nint srcDc, ref Point src, uint key, ref Blend blend, uint flags);
}
