using System.Diagnostics;
using System.Runtime.InteropServices;
using CupriFace.Interaction;
using Silk.NET.SDL;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// CUPRIFACE_KEY_DEBUG=&lt;file.txt&gt;: append one line per keyboard/focus event a window hands the
/// host — BOTH windows write here, because a diagnostic wired into one window once sent this
/// project chasing a "silent" GL window that simply wasn't the window under test. Exists for the
/// same reason as the frame dump: when a machine is reachable only through CI, the window must be
/// able to testify about what it actually received — the hosted-runner keyboard hunt burned five
/// blind runs before anything could say which link broke.
/// </summary>
internal static class KeyDiag
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("CUPRIFACE_KEY_DEBUG");
    public static void Log(string line)
    {
        if (LogPath is null) return;
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { /* diagnostics never throw */ }
    }
}

/// <summary>
/// Cross-platform no-GPU window (DESIGN.md §7.5). Renders to a CPU <see cref="SKBitmap"/>
/// and presents it through SDL's *software* renderer (a streaming texture), so it needs
/// no OpenGL — works on Windows, macOS, and Linux, including over remote sessions. This is
/// the sole CPU present path: it reaches SDL through managed Silk.NET bindings, so the
/// project ships **no hand-written P/Invoke** (only the `unsafe` pointers the SDL API needs).
/// </summary>
public sealed unsafe class SdlSoftwareWindow : IDisposable
{
    // SDL_PIXELFORMAT_ARGB8888 (0xAARRGGBB in a u32 → B,G,R,A in memory ⇒ Bgra8888).
    private const uint PixelFormatArgb8888 = 0x16362004;
    // SDL_PIXELFORMAT_ABGR8888 — R,G,B,A byte order in memory on little-endian (= SDL_PIXELFORMAT_RGBA32).
    private const uint PixelFormatAbgr8888 = 0x16762004;

    private readonly Sdl _sdl = Sdl.GetApi();
    private readonly string _title;
    private readonly FrameStats _stats = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;

    private int _width, _height;
    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private EventFilter? _resizeWatch; // kept alive: fires during the OS modal resize loop

    public event Action<RenderContext>? Render;

    /// <summary>Damage-aware alternative to <see cref="Render"/> (used when set): draw into the RETAINED
    /// bitmap and return the device rect that changed — typically <c>CupriDocument.RenderIncremental</c>'s
    /// result. Null = the frame is unchanged: nothing is uploaded or presented (unless the window was
    /// exposed). A rect uploads just that region of the streaming texture.</summary>
    public Func<RenderContext, SKRectI?>? RenderIncrementalFrame;
    private bool _presentDirty = true; // (re)present the current texture even without a re-render (expose/restore)
    public event Action<float, float, int>? PointerDown;    // x, y, click count (1/2/3)
    public event Action<float, float>? RightPointerDown;    // right-click → context menu
    public event Action<float, float>? PointerMove;
    public event Action<float, float>? PointerUp;
    public event Action<float, float, float, KeyMods>? PointerWheel; // x, y, deltaY (notches), mods — Ctrl+wheel is zoom
    public event Action<string>? TextEntered;               // printable text (IME-aware)
    public event Action<EditKey, KeyMods>? EditKeyPressed;  // key + Shift/Ctrl modifiers
    public event Action<char, KeyMods>? Shortcut;           // Ctrl/Cmd + letter (a/c/x/v …) or =/-/0 (zoom)
    public FrameStats Stats => _stats;

    /// <summary>Raised once per loop iteration (after the event pump, before the render decision),
    /// on the UI thread — the hook for host work that must run there every frame, e.g. draining
    /// the UIA action queue. Work done here can dirty this same frame.</summary>
    public event Action? Tick;

    /// <summary>The Win32 window handle once the window exists; null before <see cref="Run"/> and on
    /// every other OS. What the UIA bridge attaches to — this window is the path GL-less Windows
    /// boxes (RDP sessions, VMs, CI runners) actually take, so it serves UIA too.</summary>
    public nint? Win32Hwnd
    {
        get
        {
            if (_window is null || !OperatingSystem.IsWindows()) return null;
            var info = new SysWMInfo();
            _sdl.GetVersion(&info.Version);   // SDL refuses WM info without the caller's version
            if (!_sdl.GetWindowWMInfo(_window, &info)) return null;
            var hwnd = (nint)info.Info.Win.Hwnd;
            return hwnd == 0 ? null : hwnd;
        }
    }

    /// <summary>The NSWindow once the window exists; null before <see cref="Run"/> and on every
    /// other OS. What the NSAccessibility bridge subclasses the content view of — and, as on
    /// Windows, this software window is the path a GPU-less Mac actually takes, so it has to serve
    /// accessibility too.</summary>
    public nint? CocoaWindow
    {
        get
        {
            if (_window is null || !OperatingSystem.IsMacOS()) return null;
            var info = new SysWMInfo();
            _sdl.GetVersion(&info.Version);   // SDL refuses WM info without the caller's version
            if (!_sdl.GetWindowWMInfo(_window, &info)) return null;
            var window = (nint)info.Info.Cocoa.Window;
            return window == 0 ? null : window;
        }
    }

    /// <summary>Screen position of the client area's top-left (SDL positions windows by client
    /// area, matching the origin pointer coordinates are relative to).</summary>
    public (int X, int Y) ScreenPosition
    {
        get
        {
            int x = 0, y = 0;
            if (_window is not null) _sdl.GetWindowPosition(_window, ref x, ref y);
            return (x, y);
        }
    }

    /// <summary>Nudge the window by a delta, for a frameless window being dragged by an element that
    /// stands in for its missing title bar. A delta rather than a destination because that is what the
    /// engine can report — it knows how far the pointer travelled, not where the window sits.</summary>
    public void MoveBy(int dx, int dy)
    {
        if (_window is null) return;
        var (x, y) = ScreenPosition;
        _sdl.SetWindowPosition(_window, x + dx, y + dy);
    }

    /// <summary>True while the window is fullscreen (see <see cref="SetFullscreen"/>).</summary>
    public bool IsFullscreen { get; private set; }

    /// <summary>Enter/leave fullscreen. Uses SDL's desktop-fullscreen (borderless at the desktop
    /// resolution — instant, no display-mode switch); the size-changed event that follows resizes
    /// the surface and reflows the app like any other resize.</summary>
    public void SetFullscreen(bool on)
    {
        if (_window is null || IsFullscreen == on) return;
        if (_sdl.SetWindowFullscreen(_window, on ? (uint)WindowFlags.FullscreenDesktop : 0) == 0)
        {
            IsFullscreen = on;
            _presentDirty = true;
        }
    }

    /// <summary>Change the native always-on-top state while the window is running.</summary>
    public void SetTopMost(bool on)
    {
        if (_window is not null)
        {
            _sdl.SetWindowAlwaysOnTop(_window, (SdlBool)(on ? 1 : 0));
        }
    }

    /// <summary>OS clipboard text, for copy/cut/paste (SDL, via managed Silk bindings). SDL clipboard
    /// strings are UTF-8; marshal them explicitly (Silk's convenience *S/string overloads assume
    /// ANSI, which mangles non-ASCII like “—” into mojibake).</summary>
    public string? ClipboardText
    {
        get
        {
            var ptr = _sdl.GetClipboardText();               // UTF-8; caller must SDL_free it
            if (ptr is null) return null;
            var text = Marshal.PtrToStringUTF8((IntPtr)ptr);
            _sdl.Free(ptr);
            return text;
        }
        set
        {
            if (value is null) return;
            var utf8 = System.Text.Encoding.UTF8.GetBytes(value + '\0'); // null-terminated UTF-8
            fixed (byte* p = utf8) _sdl.SetClipboardText(p);
        }
    }

    private float _lastX, _lastY; // wheel events carry no position — use the last move

    // System cursors, created lazily and cached (SDL_CreateSystemCursor is not free), plus the last one
    // set so a move that doesn't change the cursor is a no-op.
    private readonly Dictionary<SystemCursor, nint> _cursors = new();
    private CupriFace.Style.CursorType _lastCursor = (CupriFace.Style.CursorType)(-1);

    /// <summary>Show the platform cursor matching the engine's <see cref="CupriFace.Style.CursorType"/>
    /// (from <c>CupriDocument.CursorAt</c>). Synonyms without a distinct SDL system cursor fall back to the
    /// closest one (grab → the move arrow, there being no hand-grab; the diagonal resizes → the two-headed arrows).</summary>
    public void SetCursor(CupriFace.Style.CursorType c)
    {
        if (c == _lastCursor) return;
        _lastCursor = c;
        var id = c switch
        {
            CupriFace.Style.CursorType.Pointer => SystemCursor.SystemCursorHand,
            // See SkiaWindow: no platform hand-grab cursor exists, and sharing Pointer's made a drag
            // handle indistinguishable from a link. The move arrow says "this can be dragged".
            CupriFace.Style.CursorType.Grab or CupriFace.Style.CursorType.Grabbing
                or CupriFace.Style.CursorType.Move => SystemCursor.SystemCursorSizeall,
            CupriFace.Style.CursorType.Text => SystemCursor.SystemCursorIbeam,
            CupriFace.Style.CursorType.Wait => SystemCursor.SystemCursorWait,
            CupriFace.Style.CursorType.Progress => SystemCursor.SystemCursorWaitarrow,
            CupriFace.Style.CursorType.Crosshair => SystemCursor.SystemCursorCrosshair,
            CupriFace.Style.CursorType.NotAllowed => SystemCursor.SystemCursorNo,
            CupriFace.Style.CursorType.EwResize => SystemCursor.SystemCursorSizewe,
            CupriFace.Style.CursorType.NsResize => SystemCursor.SystemCursorSizens,
            CupriFace.Style.CursorType.NwseResize => SystemCursor.SystemCursorSizenwse,
            CupriFace.Style.CursorType.NeswResize => SystemCursor.SystemCursorSizenesw,
            _ => SystemCursor.SystemCursorArrow, // Default / Auto / Help / None
        };
        if (!_cursors.TryGetValue(id, out var ptr)) _cursors[id] = ptr = (nint)_sdl.CreateSystemCursor(id);
        _sdl.SetCursor((Cursor*)ptr);
    }

    // Pending window icon (RGBA8888) — set before Run(), applied right after CreateWindow.
    private (byte[] Rgba, int W, int H)? _pendingIcon;

    /// <summary>Set the OS window/taskbar icon from raw RGBA8888 pixels. Call before <see cref="Run"/>.</summary>
    public void SetIcon(byte[] rgba, int width, int height) => _pendingIcon = (rgba, width, height);

    private void ApplyPendingIcon()
    {
        if (_pendingIcon is not { } icon) return;
        fixed (byte* px = icon.Rgba)
        {
            var surface = _sdl.CreateRGBSurfaceWithFormatFrom(px, icon.W, icon.H, 32, icon.W * 4, PixelFormatAbgr8888);
            if (surface is not null)
            {
                _sdl.SetWindowIcon(_window, surface);
                _sdl.FreeSurface(surface);
            }
        }
    }

    private readonly bool _frameless, _topMost, _darkWindowChrome;
    private readonly SKColor _windowChromeColor;

    // NOTE: the SDL software path is opaque — its streaming texture blits over the window with no
    // per-pixel alpha against the desktop, so `transparent` has no effect here (the GL path handles
    // transparency). Frameless / always-on-top do work through standard SDL window flags.
    public SdlSoftwareWindow(string title = "CupriFace", int width = 1024, int height = 768,
        bool transparent = false, bool frameless = false, bool topMost = false,
        bool darkWindowChrome = false, SKColor? windowChromeColor = null)
    {
        _title = title;
        _width = width;
        _height = height;
        _frameless = frameless;
        _topMost = topMost;
        _darkWindowChrome = darkWindowChrome;
        _windowChromeColor = windowChromeColor ?? new SKColor(0x20, 0x20, 0x20);
    }

    public void Run()
    {
        if (_sdl.Init(Sdl.InitVideo) != 0)
            throw new InvalidOperationException($"SDL_Init failed: {_sdl.GetErrorS()}");

        var flags = WindowFlags.Resizable;
        if (_frameless) flags |= WindowFlags.Borderless;
        if (_topMost) flags |= WindowFlags.AlwaysOnTop;
        _window = _sdl.CreateWindow(_title, Sdl.WindowposCentered, Sdl.WindowposCentered,
            _width, _height, (uint)flags);
        if (_window is null) throw new InvalidOperationException($"SDL_CreateWindow failed: {_sdl.GetErrorS()}");
        ApplyPendingIcon();
        if (_darkWindowChrome && Win32Hwnd is { } hwnd)
        {
            WindowChrome.TryEnableDarkMode(hwnd, _windowChromeColor);
        }

        _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null) throw new InvalidOperationException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");

        EnsureSurface(_width, _height);
        _sdl.StartTextInput(); // deliver Textinput events (handles IME composition)

        // Repaint DURING resize: Windows/macOS run a modal loop that blocks the main loop,
        // but SDL still dispatches size events to an event watch — so we render from there.
        _resizeWatch = ResizeWatch;
        _sdl.AddEventWatch(new PfnEventFilter(_resizeWatch), null);

        var running = true;
        var e = new Event();
        while (running)
        {
            while (_sdl.PollEvent(ref e) != 0)
            {
                switch ((EventType)e.Type)
                {
                    case EventType.Quit:
                        running = false;
                        break;
                    case EventType.Mousebuttondown:
                        if (e.Button.Button == Sdl.ButtonRight) RightPointerDown?.Invoke(e.Button.X, e.Button.Y);
                        else PointerDown?.Invoke(e.Button.X, e.Button.Y, e.Button.Clicks); // SDL tracks click count
                        break;
                    case EventType.Mousebuttonup:
                        PointerUp?.Invoke(e.Button.X, e.Button.Y);
                        break;
                    case EventType.Mousemotion:
                        _lastX = e.Motion.X; _lastY = e.Motion.Y;
                        PointerMove?.Invoke(e.Motion.X, e.Motion.Y);
                        break;
                    case EventType.Mousewheel:
                        // The wheel event itself carries no modifier state; ask SDL at delivery time.
                        var wheelMods = ((ushort)_sdl.GetModState() & ((ushort)Keymod.Ctrl | (ushort)Keymod.Gui)) != 0
                            ? KeyMods.Ctrl : KeyMods.None;
                        PointerWheel?.Invoke(_lastX, _lastY, e.Wheel.Y, wheelMods);
                        break;
                    case EventType.Textinput:
                    {
                        var text = Marshal.PtrToStringUTF8((IntPtr)e.Text.Text);
                        if (!string.IsNullOrEmpty(text)) TextEntered?.Invoke(text);
                        break;
                    }
                    case EventType.Windowevent when (WindowEventID)e.Window.Event == WindowEventID.FocusGained:
                        KeyDiag.Log("sdl focus-gained");
                        break;
                    case EventType.Windowevent when (WindowEventID)e.Window.Event == WindowEventID.FocusLost:
                        KeyDiag.Log("sdl focus-lost");
                        break;
                    case EventType.Keydown:
                    {
                        KeyDiag.Log($"sdl keydown sc={e.Key.Keysym.Scancode} mod=0x{e.Key.Keysym.Mod:x}");
                        var mod = e.Key.Keysym.Mod;
                        var shift = (mod & (ushort)Keymod.Shift) != 0;
                        var ctrl = (mod & ((ushort)Keymod.Ctrl | (ushort)Keymod.Gui)) != 0; // Gui = Cmd (macOS)
                        var mods = (shift ? KeyMods.Shift : 0) | (ctrl ? KeyMods.Ctrl : 0);
                        // Any Ctrl/Cmd + letter is forwarded as a chord — the six clipboard/undo ones the
                        // host consumes, and every other letter so an app's own OnShortcut (e.g. Ctrl+K
                        // for a command palette) can fire. Scancodes A..Z are contiguous, so one range
                        // test replaces a per-letter switch that silently dropped everything unlisted.
                        if (ctrl && e.Key.Keysym.Scancode is >= Scancode.ScancodeA and <= Scancode.ScancodeZ)
                        {
                            Shortcut?.Invoke((char)('a' + (e.Key.Keysym.Scancode - Scancode.ScancodeA)), mods);
                            continue;
                        }
                        // Ctrl/Cmd + =/-/0 is page zoom, browser-style; keypad +/-/0 are the same
                        // intent on other keys, so they normalise to the same three chords.
                        if (ctrl)
                        {
                            var zoomCh = e.Key.Keysym.Scancode switch
                            {
                                Scancode.ScancodeEquals or Scancode.ScancodeKPPlus => '=',
                                Scancode.ScancodeMinus or Scancode.ScancodeKPMinus => '-',
                                Scancode.Scancode0 or Scancode.ScancodeKP0 => '0',
                                _ => '\0',
                            };
                            if (zoomCh != '\0') { Shortcut?.Invoke(zoomCh, mods); continue; }
                        }
                        var ek = e.Key.Keysym.Scancode switch
                        {
                            Scancode.ScancodeBackspace => EditKey.Backspace,
                            Scancode.ScancodeDelete => EditKey.Delete,
                            Scancode.ScancodeLeft => EditKey.Left,
                            Scancode.ScancodeRight => EditKey.Right,
                            Scancode.ScancodeHome => EditKey.Home,
                            Scancode.ScancodeEnd => EditKey.End,
                            Scancode.ScancodeReturn or Scancode.ScancodeReturn2 => EditKey.Enter,
                            Scancode.ScancodeUp => EditKey.Up,
                            Scancode.ScancodeDown => EditKey.Down,
                            Scancode.ScancodeTab => shift ? EditKey.ShiftTab : EditKey.Tab,
                            Scancode.ScancodeEscape => EditKey.Escape,
                            _ => EditKey.None,
                        };
                        if (ek != EditKey.None) EditKeyPressed?.Invoke(ek, mods);
                        break;
                    }
                    case EventType.Windowevent when (WindowEventID)e.Window.Event == WindowEventID.SizeChanged:
                        EnsureSurface(e.Window.Data1, e.Window.Data2);
                        break;
                    case EventType.Windowevent when (WindowEventID)e.Window.Event
                        is WindowEventID.Exposed or WindowEventID.Shown or WindowEventID.Restored:
                        _presentDirty = true; // window contents may be stale — re-present the texture
                        break;
                }
            }
            Tick?.Invoke();
            RenderFrame();
            _sdl.Delay(16); // ~60 fps cap
        }
    }

    /// <summary>Frames drawn from inside the resize watch rather than the main loop — the ones that
    /// make a drag-resize stream instead of snapping on release. Zero of these after a drag means
    /// the watch below is not firing, whatever the window looked like.</summary>
    public int ResizeFrames { get; private set; }

    /// <summary>SDL delivers size events to an event watch synchronously, from INSIDE the OS's modal
    /// resize loop — which is the whole reason this exists, because <see cref="Run"/>'s loop gets no
    /// turn until the mouse is released.</summary>
    private int ResizeWatch(void* userData, Event* e)
    {
        if ((EventType)e->Type == EventType.Windowevent && (WindowEventID)e->Window.Event == WindowEventID.SizeChanged)
        {
            EnsureSurface(e->Window.Data1, e->Window.Data2);
            RenderFrame();
            ResizeFrames++;
            if (SkiaWindow.ResizeDebug)
                Console.Error.WriteLine($"[resize] frame {ResizeFrames} at {e->Window.Data1}x{e->Window.Data2}");
        }
        return 0;
    }

    /// <summary>Raised whenever the retained bitmap + texture were actually recreated (size change,
    /// first creation) and thus hold NOTHING — the damage-diff producer must forget its last frame
    /// and repaint in full, or only the next change's rect would be visible on black. The host wires
    /// this to <c>CupriDocument.InvalidateRetainedFrame</c>.</summary>
    public event Action? SurfaceRecreated;

    private void EnsureSurface(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        // Same size and alive: keep the retained pixels. This matters beyond thrift — SDL delivers
        // a size change both to the resize WATCH (which repaints) and again from the polled queue;
        // recreating on the echo would throw away the frame the watch just painted.
        if (_bitmap is not null && w == _width && h == _height) return;
        _width = w; _height = h;
        _canvas?.Dispose();
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        _texture = _sdl.CreateTexture(_renderer, PixelFormatArgb8888, (int)TextureAccess.Streaming, w, h);
        SurfaceRecreated?.Invoke();
    }

    private void RenderFrame()
    {
        if (_canvas is null || _bitmap is null) return;

        var delta = _clock.Elapsed.TotalSeconds - _last;
        _last = _clock.Elapsed.TotalSeconds;

        if (RenderIncrementalFrame is { } incremental)
        {
            // Damage-aware path: the bitmap retains last frame's pixels; the callback repaints only the
            // changed rect (or nothing). Upload just that region; skip presenting entirely when clean.
            _stats.BeginFrame(delta);
            var damage = incremental(new RenderContext(_canvas, _width, _height, _stats));
            _canvas.Flush();
            _stats.EndFrame();

            if (damage is { } d && d.Width > 0 && d.Height > 0)
            {
                var rect = new Silk.NET.Maths.Rectangle<int>(d.Left, d.Top, d.Width, d.Height);
                var pixels = (byte*)_bitmap.GetPixels() + d.Top * _width * 4 + d.Left * 4;
                _sdl.UpdateTexture(_texture, &rect, pixels, _width * 4); // pitch = the full row stride
                _presentDirty = true;
            }
            if (!_presentDirty) return; // unchanged and not exposed — don't even present
            _presentDirty = false;
        }
        else
        {
            _stats.BeginFrame(delta);
            Render?.Invoke(new RenderContext(_canvas, _width, _height, _stats));
            _canvas.Flush();
            _stats.EndFrame();
            _sdl.UpdateTexture(_texture, null, (void*)_bitmap.GetPixels(), _width * 4);
        }

        _sdl.RenderClear(_renderer);
        _sdl.RenderCopy(_renderer, _texture, null, null);
        // Throttled: a full read-back + PNG encode per present would starve the UI thread (and
        // with it the UIA provider). Every Nth present keeps the file ≲1 s stale.
        if (_frameDumpPath is { } dump && ++_presentCount % 15 == 0) DumpPresentedPixels(dump);
        _sdl.RenderPresent(_renderer);
    }

    // CUPRIFACE_FRAME_DUMP=<file.png>: periodically read the pixels BACK FROM THE RENDER TARGET
    // (not our bitmap — the texture upload is exactly what can silently go wrong) and overwrite
    // the file. Ground truth of what the window presents, for environments where OS-level screen
    // capture is unavailable (locked sessions, CI). Debug only, hence the env gate.
    private readonly string? _frameDumpPath = Environment.GetEnvironmentVariable("CUPRIFACE_FRAME_DUMP");
    private int _presentCount;

    private void DumpPresentedPixels(string path)
    {
        try
        {
            using var bmp = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (_sdl.RenderReadPixels(_renderer, null, PixelFormatArgb8888, (void*)bmp.GetPixels(), _width * 4) != 0) return;
            using var img = SKImage.FromBitmap(bmp);
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            using var f = File.Create(path);
            png.SaveTo(f);
        }
        catch { /* diagnostics must never take the window down */ }
    }

    public void Dispose()
    {
        foreach (var p in _cursors.Values) _sdl.FreeCursor((Cursor*)p);
        _cursors.Clear();
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);
        _sdl.Quit();
        _canvas?.Dispose();
        _bitmap?.Dispose();
    }
}
