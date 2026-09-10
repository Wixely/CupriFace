using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>Premultiplied BGRA presentation for a frameless Windows software window.
/// Avoids the WGL/DWM transparency path that can composite zero alpha as black (#139).
/// The caller retains the bitmap, and this presenter retains one DIB until the size changes.</summary>
internal sealed class WindowsAlphaPresenter : IDisposable
{
    private readonly nint _hwnd;
    private nint _dc, _bitmap, _oldBitmap, _bits;
    private int _width, _height;

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

    public unsafe void Present(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType != SKAlphaType.Premul)
            throw new ArgumentException("Expected premultiplied BGRA pixels.", nameof(bitmap));
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
        for (int y = 0; y < height; y++)
            Buffer.MemoryCopy((byte*)bitmap.GetPixels() + y * bitmap.RowBytes,
                (byte*)_bits + y * rowBytes, rowBytes, rowBytes);
        var size = new Point { X = width, Y = height };
        var source = new Point();
        var blend = new Blend { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(_hwnd, 0, 0, ref size, _dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow failed");
    }

    public (int Width, int Height) WindowSize
    {
        get
        {
            if (IsIconic(_hwnd)) return (0, 0);
            // ULW sizes the entire HWND. On a resized layered window the client rect can still
            // describe the old surface until the next ULW, so use the outer rect (frameless only).
            if (!GetWindowRect(_hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
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
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, Important; }
    [DllImport("user32.dll")] private static extern uint GetClassLongW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint dstDc, nint dst, ref Point size, nint srcDc, ref Point src, uint key, ref Blend blend, uint flags);
}
