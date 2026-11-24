using System.Runtime.InteropServices;
using CupriFace.Hosting;
using CupriFace.Interaction;
using Silk.NET.SDL;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// A window beside the app's main one — the dev tools popped out, or a window the app opened for a
/// document of its own — drawn in software into a small SDL window.
///
/// <para><b>It has no loop of its own.</b> The main window drives it from its <c>Tick</c>, on the
/// app's thread, through <see cref="SdlChildWindows"/>. That is not a shortcut: everything it draws
/// (the app's render tree, or a second document on the same thread as the first) is only safe to
/// touch between the main window's frames. How its EVENTS arrive depends on which main window the
/// app got:</para>
/// <list type="bullet">
/// <item>the SDL software window drains SDL's one process-wide queue, so it hands these windows'
/// events over through <see cref="SdlSoftwareWindow.ForeignWindowEvent"/>;</item>
/// <item>the GLFW window has nothing to do with SDL, so <see cref="SdlChildWindows"/> initialises
/// SDL's video itself and polls the queue in its <c>Pump</c>.</item>
/// </list>
///
/// <para>What this class owns is the window: creating it at the monitor's scale, the surface it is
/// drawn into, presenting it, decoding SDL's events into logical client units and the engine's key
/// vocabulary, and the cursor. What it SHOWS, and what input means there, is the subclass's.</para>
/// </summary>
internal abstract unsafe class SdlChildWindow : IDisposable
{
    private const uint PixelFormatArgb8888 = 0x16362004;   // B,G,R,A in memory = Bgra8888

    /// <summary>SDL's "centre it on the display" position.</summary>
    public const int Centered = 0x2FFF0000;

    protected readonly Sdl Sdl = Sdl.GetApi();
    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;
    private SKBitmap? _bitmap;
    private int _w, _h;          // pixels
    private uint _id;
    private float _scale = 1f;   // device scale of the monitor it is on
    private bool _needsDraw = true;
    private float _lastX, _lastY;
    private bool _fullscreen;
    private readonly Dictionary<SystemCursor, nint> _cursors = new();
    private readonly List<string> _dropBatch = [];
    private bool _dropBatching;
    private readonly string? _dumpPath;

    /// <param name="dumpSuffix">Under <c>CUPRIFACE_FRAME_DUMP=out.png</c>, this window's pixels go
    /// to <c>out.&lt;suffix&gt;.png</c> beside the main window's — the only way to see them on a
    /// machine where a screen capture lies.</param>
    protected SdlChildWindow(string dumpSuffix)
    {
        _dumpPath = Environment.GetEnvironmentVariable("CUPRIFACE_FRAME_DUMP") is { Length: > 0 } p
            ? Path.ChangeExtension(p, "." + dumpSuffix + ".png") : null;
    }

    /// <summary>SDL's id for the window; 0 while closed. Events are routed by it.</summary>
    public uint Id => _id;

    public bool IsOpen => _window is not null;

    /// <summary>The device scale of the monitor it is on (1 everywhere but Windows; see <see cref="ReadScale"/>).</summary>
    protected float DeviceScale => _scale;

    /// <summary>Its client size in device pixels.</summary>
    protected (int W, int H) PixelSize => (_w, _h);

    /// <summary>Where it is and how big, in screen pixels; null while closed.</summary>
    public (int X, int Y, int W, int H)? Placement
    {
        get
        {
            if (_window is null) return null;
            int x = 0, y = 0, w = 0, h = 0;
            Sdl.GetWindowPosition(_window, ref x, ref y);
            Sdl.GetWindowSize(_window, ref w, ref h);
            return (x, y, w, h);
        }
    }

    /// <summary>The Win32 handle, for owning a file dialog; null elsewhere or while closed.</summary>
    public nint? Win32Hwnd
    {
        get
        {
            if (_window is null || !OperatingSystem.IsWindows()) return null;
            var info = new SysWMInfo();
            Sdl.GetVersion(&info.Version);
            if (!Sdl.GetWindowWMInfo(_window, &info)) return null;
            var hwnd = (nint)info.Info.Win.Hwnd;
            return hwnd == 0 ? null : hwnd;
        }
    }

    /// <summary>The NSWindow, for attaching a sheet; null elsewhere or while closed.</summary>
    public nint? CocoaWindow
    {
        get
        {
            if (_window is null || !OperatingSystem.IsMacOS()) return null;
            var info = new SysWMInfo();
            Sdl.GetVersion(&info.Version);
            if (!Sdl.GetWindowWMInfo(_window, &info)) return null;
            var window = (nint)info.Info.Cocoa.Window;
            return window == 0 ? null : window;
        }
    }

    /// <summary>
    /// Create the window. Width and height are LOGICAL units; when <paramref name="applyScale"/> the
    /// monitor's scale is applied here, once — false for a size remembered from last time, which is
    /// already the size the user left it. False when SDL refused.
    /// </summary>
    protected bool Create(string title, int x, int y, int width, int height, bool resizable, bool applyScale)
    {
        if (_window is not null) return true;
        _window = Sdl.CreateWindow(title, x, y, width, height, resizable ? (uint)WindowFlags.Resizable : 0u);
        if (_window is null)
        {
            Console.WriteLine($"[CupriFace] Could not open a window \"{title}\" ({Sdl.GetErrorS()}).");
            return false;
        }
        _id = Sdl.GetWindowID(_window);
        // The software renderer, like the app's own software window: these windows are a few
        // thousand pixels of document, and a GPU renderer here is one more thing that can fail on a VM.
        _renderer = Sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null)
        {
            Console.WriteLine($"[CupriFace] Could not open a window \"{title}\" ({Sdl.GetErrorS()}).");
            DestroyWindow();
            return false;
        }
        // The monitor's scale, so the text is the same physical size as the main window's.
        _scale = ReadScale();
        if (applyScale && _scale != 1f)
        {
            Sdl.SetWindowSize(_window, (int)(width * _scale), (int)(height * _scale));
            // A centred window was centred at its unscaled size; centre it again at the real one.
            if (x == Centered || y == Centered) Sdl.SetWindowPosition(_window, x, y);
        }
        KeepOnScreen();
        int cw = 0, ch = 0;
        Sdl.GetWindowSize(_window, ref cw, ref ch);
        EnsureSurface(cw, ch);
        _needsDraw = true;
        return true;
    }

    /// <summary>Destroy the native window and everything drawn into it. Safe to call twice.</summary>
    protected void DestroyWindow()
    {
        foreach (var c in _cursors.Values) Sdl.FreeCursor((Cursor*)c);
        _cursors.Clear();
        if (_texture is not null) { Sdl.DestroyTexture(_texture); _texture = null; }
        if (_renderer is not null) { Sdl.DestroyRenderer(_renderer); _renderer = null; }
        if (_window is not null) { Sdl.DestroyWindow(_window); _window = null; }
        _bitmap?.Dispose();
        _bitmap = null;
        _id = 0;
    }

    /// <summary>Something changed what it shows: draw it at the next <see cref="Pump"/>.</summary>
    public void Invalidate() => _needsDraw = true;

    public void SetTitle(string title)
    {
        if (_window is not null) Sdl.SetWindowTitle(_window, title);
    }

    /// <summary>Borderless fullscreen on the display it is on, or back.</summary>
    public void SetFullscreen(bool on)
    {
        if (_window is null) return;
        if (Sdl.SetWindowFullscreen(_window, on ? (uint)WindowFlags.FullscreenDesktop : 0u) == 0) _fullscreen = on;
    }

    public bool IsFullscreen => _fullscreen;

    public void MoveBy(int dx, int dy)
    {
        if (_window is null) return;
        int x = 0, y = 0;
        Sdl.GetWindowPosition(_window, ref x, ref y);
        Sdl.SetWindowPosition(_window, x + dx, y + dy);
    }

    /// <summary>Once per main-window tick: draw if anything changed. Subclasses add their own work
    /// (a close the app asked for, a frame their content says is due) around it.</summary>
    public virtual void Pump()
    {
        if (_window is null) return;
        if (WantsFrame()) _needsDraw = true;
        if (!_needsDraw) return;
        _needsDraw = false;
        Draw();
    }

    /// <summary>Whether the content has a frame due for reasons of its own — animation, posted work.</summary>
    protected virtual bool WantsFrame() => false;

    /// <summary>Draw the content into <paramref name="canvas"/>, which is <paramref name="pixelWidth"/> x
    /// <paramref name="pixelHeight"/> device pixels on a monitor of scale <paramref name="deviceScale"/>.</summary>
    protected abstract void Paint(SKCanvas canvas, int pixelWidth, int pixelHeight, float deviceScale);

    // ---- input, in logical client units; each returns whether it changed what is shown ----------
    protected virtual void OnCloseButton() { }
    protected virtual bool OnPointerDown(float x, float y, int clicks) => false;
    protected virtual bool OnPointerMove(float x, float y) => false;
    protected virtual bool OnPointerUp(float x, float y) => false;
    protected virtual bool OnPointerLeave() => false;
    protected virtual bool OnRightPointerDown(float x, float y) => false;
    protected virtual bool OnMiddlePointerDown(float x, float y) => false;
    protected virtual bool OnMiddlePointerUp(float x, float y) => false;
    /// <summary>Wheel notches, positive away from the user; <paramref name="mods"/> carries Ctrl (zoom).</summary>
    protected virtual bool OnWheel(float x, float y, float notches, KeyMods mods) => false;
    protected virtual bool OnKey(EditKey key, KeyMods mods) => false;
    protected virtual bool OnKeyUp(EditKey key) => false;
    protected virtual bool OnText(string text) => false;
    /// <summary>A Ctrl/Cmd + letter, or Ctrl + =/-/0 (page zoom) — the main windows' chord set.</summary>
    protected virtual bool OnShortcut(char ch, KeyMods mods) => false;
    protected virtual void OnFocusChanged(bool focused) { }
    protected virtual bool OnFilesDropped(float x, float y, string[] paths) => false;
    protected virtual Style.CursorType CursorAt(float x, float y) => Style.CursorType.Default;
    /// <summary>Input changed something; the host may have something of its own to redraw.</summary>
    protected virtual void OnChanged() { }

    /// <summary>The OS clipboard as text, through SDL (process-wide, so any window can use it).</summary>
    protected string? ClipboardText
    {
        get => Sdl.HasClipboardText() == SdlBool.True ? Sdl.GetClipboardTextS() : null;
        set { if (value is not null) Sdl.SetClipboardText(value); }
    }

    /// <summary>One event for this window. <see cref="SdlChildWindows"/> routes by window id.</summary>
    public void Handle(Event e)
    {
        if (_window is null || SdlSoftwareWindow.WindowOf(e) != _id) return;
        var changed = false;
        switch ((EventType)e.Type)
        {
            case EventType.Windowevent:
                switch ((WindowEventID)e.Window.Event)
                {
                    case WindowEventID.Close:
                        OnCloseButton();
                        return;
                    case WindowEventID.SizeChanged:
                        _scale = ReadScale();
                        EnsureSurface(e.Window.Data1, e.Window.Data2);
                        _needsDraw = true;
                        break;
                    case WindowEventID.Moved:
                        var s = ReadScale();
                        if (s != _scale) { _scale = s; _needsDraw = true; }
                        break;
                    case WindowEventID.Exposed or WindowEventID.Shown or WindowEventID.Restored:
                        _needsDraw = true;
                        break;
                    case WindowEventID.Leave:
                        changed = OnPointerLeave();
                        break;
                    // The keyboard follows the focused window: SDL addresses key events to it.
                    case WindowEventID.FocusGained:
                        OnFocusChanged(true);
                        break;
                    case WindowEventID.FocusLost:
                        OnFocusChanged(false);
                        break;
                }
                break;
            case EventType.Mousemotion:
                (_lastX, _lastY) = (e.Motion.X / _scale, e.Motion.Y / _scale);
                changed = OnPointerMove(_lastX, _lastY);
                SetCursor(CursorAt(_lastX, _lastY));
                break;
            case EventType.Mousebuttondown:
                (_lastX, _lastY) = (e.Button.X / _scale, e.Button.Y / _scale);
                changed = e.Button.Button switch
                {
                    1 => OnPointerDown(_lastX, _lastY, Math.Max(1, (int)e.Button.Clicks)),
                    2 => OnMiddlePointerDown(_lastX, _lastY),
                    3 => OnRightPointerDown(_lastX, _lastY),
                    _ => false,
                };
                SetCursor(CursorAt(_lastX, _lastY));
                break;
            case EventType.Mousebuttonup:
                (_lastX, _lastY) = (e.Button.X / _scale, e.Button.Y / _scale);
                changed = e.Button.Button switch
                {
                    1 => OnPointerUp(_lastX, _lastY),
                    2 => OnMiddlePointerUp(_lastX, _lastY),
                    _ => false,
                };
                SetCursor(CursorAt(_lastX, _lastY));
                break;
            case EventType.Mousewheel:
            {
                // The wheel event carries no modifier state; ask SDL at delivery time, as the main
                // window does. PreciseY keeps a trackpad's fractions where SDL reports them.
                var mods = ((ushort)Sdl.GetModState() & ((ushort)Keymod.Ctrl | (ushort)Keymod.Gui)) != 0
                    ? KeyMods.Ctrl : KeyMods.None;
                var notches = e.Wheel.PreciseY != 0 ? e.Wheel.PreciseY : e.Wheel.Y;
                changed = OnWheel(_lastX, _lastY, notches, mods);
                break;
            }
            case EventType.Keydown:
                changed = HandleKeyDown(e.Key);
                break;
            case EventType.Keyup:
            {
                var up = SdlSoftwareWindow.ToEditKey(e.Key.Keysym.Scancode, shift: false);
                if (up != EditKey.None) changed = OnKeyUp(up);
                break;
            }
            case EventType.Textinput:
            {
                var text = Marshal.PtrToStringUTF8((nint)e.Text.Text);
                if (!string.IsNullOrEmpty(text)) changed = OnText(text);
                break;
            }
            // A multi-file drop is Dropbegin, a Dropfile per file, then Dropcomplete: gathered and
            // delivered once, as the main window does, because dropping a selection is one gesture.
            case EventType.Dropbegin:
                _dropBatch.Clear();
                _dropBatching = true;
                break;
            case EventType.Dropfile:
            {
                var path = Marshal.PtrToStringUTF8((nint)e.Drop.File);
                Sdl.Free(e.Drop.File);
                if (path is { Length: > 0 }) _dropBatch.Add(path);
                if (!_dropBatching) changed = FlushDrop();
                break;
            }
            case EventType.Dropcomplete:
                _dropBatching = false;
                changed = FlushDrop();
                break;
            case EventType.Droptext:
                Sdl.Free(e.Drop.File);
                break;
        }
        if (changed)
        {
            _needsDraw = true;
            OnChanged();
        }
    }

    // The same chord rules as SdlSoftwareWindow's loop, so a second window treats the keyboard
    // exactly as the first: any Ctrl/Cmd + letter is a chord (clipboard, undo, or the app's own
    // OnShortcut); Ctrl + =/-/0 (and the keypad's) is page zoom; Tab, Escape and the F-keys do not
    // auto-repeat, because a held Tab would fly through the focus ring.
    private bool HandleKeyDown(KeyboardEvent key)
    {
        var mod = key.Keysym.Mod;
        var shift = (mod & (ushort)Keymod.Shift) != 0;
        var ctrl = (mod & ((ushort)Keymod.Ctrl | (ushort)Keymod.Gui)) != 0;
        var mods = (shift ? KeyMods.Shift : 0) | (ctrl ? KeyMods.Ctrl : 0);
        var sc = key.Keysym.Scancode;
        if (ctrl && sc is >= Scancode.ScancodeA and <= Scancode.ScancodeZ)
            return OnShortcut((char)('a' + (sc - Scancode.ScancodeA)), mods);
        if (ctrl)
        {
            var zoom = sc switch
            {
                Scancode.ScancodeEquals or Scancode.ScancodeKPPlus => '=',
                Scancode.ScancodeMinus or Scancode.ScancodeKPMinus => '-',
                Scancode.Scancode0 or Scancode.ScancodeKP0 => '0',
                _ => '\0',
            };
            if (zoom != '\0') return OnShortcut(zoom, mods);
        }
        var ek = SdlSoftwareWindow.ToEditKey(sc, shift);
        if (key.Repeat != 0 && ek is EditKey.Escape or EditKey.Tab or EditKey.ShiftTab
                or >= EditKey.F1 and <= EditKey.F12) return false;
        return ek != EditKey.None && OnKey(ek, mods);
    }

    private bool FlushDrop()
    {
        if (_dropBatch.Count == 0) return false;
        var paths = _dropBatch.ToArray();
        _dropBatch.Clear();
        return OnFilesDropped(_lastX, _lastY, paths);
    }

    private void Draw()
    {
        if (_bitmap is null || _texture is null) return;
        using (var canvas = new SKCanvas(_bitmap))
        {
            Paint(canvas, _w, _h, _scale);
            canvas.Flush();
        }
        Sdl.UpdateTexture(_texture, null, (void*)_bitmap.GetPixels(), _w * 4);
        if (_dumpPath is { } dump)
            using (var f = File.Create(dump)) _bitmap.Encode(f, SKEncodedImageFormat.Png, 90);
        Sdl.RenderClear(_renderer);
        Sdl.RenderCopy(_renderer, _texture, null, null);
        Sdl.RenderPresent(_renderer);
    }

    private void EnsureSurface(int w, int h)
    {
        if (w <= 0 || h <= 0 || (_bitmap is not null && w == _w && h == _h)) return;
        _w = w; _h = h;
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (_texture is not null) Sdl.DestroyTexture(_texture);
        _texture = Sdl.CreateTexture(_renderer, PixelFormatArgb8888, (int)TextureAccess.Streaming, w, h);
    }

    // The same source of D the app's software window uses, for the same reason: only Windows
    // reports it per monitor to an SDL window created without SDL_WINDOW_ALLOW_HIGHDPI.
    private float ReadScale()
    {
        if (!OperatingSystem.IsWindows() || !WindowsDpi.Enabled || _window is null) return 1f;
        return Win32Hwnd is { } hwnd ? HostScale.Sanitize(WindowsDpi.GetScaleForWindow(hwnd)) : _scale;
    }

    /// <summary>Pull the window back onto the display it is mostly on, if it was placed past an edge
    /// (the app maximised, so "to its right" is off the screen).</summary>
    private void KeepOnScreen()
    {
        var display = Sdl.GetWindowDisplayIndex(_window);
        if (display < 0) display = 0;
        var bounds = new Silk.NET.Maths.Rectangle<int>();
        if (Sdl.GetDisplayUsableBounds(display, ref bounds) != 0) return;
        int x = 0, y = 0, w = 0, h = 0;
        Sdl.GetWindowPosition(_window, ref x, ref y);
        Sdl.GetWindowSize(_window, ref w, ref h);
        var right = bounds.Origin.X + bounds.Size.X;
        var bottom = bounds.Origin.Y + bounds.Size.Y;
        var nx = Math.Clamp(x, bounds.Origin.X, Math.Max(bounds.Origin.X, right - w));
        var ny = Math.Clamp(y, bounds.Origin.Y, Math.Max(bounds.Origin.Y, bottom - h));
        if (h > bounds.Size.Y) Sdl.SetWindowSize(_window, w, bounds.Size.Y);
        if (nx != x || ny != y) Sdl.SetWindowPosition(_window, nx, ny);
    }

    private void SetCursor(Style.CursorType c)
    {
        var id = c switch
        {
            Style.CursorType.Pointer => SystemCursor.SystemCursorHand,
            Style.CursorType.Text => SystemCursor.SystemCursorIbeam,
            _ => SystemCursor.SystemCursorArrow,
        };
        if (!_cursors.TryGetValue(id, out var ptr)) _cursors[id] = ptr = (nint)Sdl.CreateSystemCursor(id);
        Sdl.SetCursor((Cursor*)ptr);
    }

    public virtual void Dispose() => DestroyWindow();
}

/// <summary>
/// Every <see cref="SdlChildWindow"/> of one host run, and the one place their events are routed.
///
/// <para>SDL has ONE event queue per process. Two windows each polling it would each eat the other's
/// events — a click in the dev tools landing nowhere because an app window drained it first — so
/// there is exactly one reader: the SDL main window when the app got one (it hands foreign events to
/// <see cref="Handle"/>), or this object when the main window is GLFW's
/// (<see cref="OwnsEvents"/>).</para>
/// </summary>
internal sealed unsafe class SdlChildWindows : IDisposable
{
    private readonly Sdl _sdl = Sdl.GetApi();
    private readonly List<SdlChildWindow> _windows = [];
    private bool _videoInited;

    /// <summary>True when nothing else reads SDL's queue (the GLFW main window): SDL's video is
    /// initialised here on first use and the queue polled in <see cref="Pump"/>.</summary>
    public bool OwnsEvents { get; set; }

    /// <summary>Make SDL ready to create a window. False (and said why) when it cannot be.</summary>
    public bool EnsureVideo()
    {
        if (!OwnsEvents || _videoInited) return true;
        if (_sdl.InitSubSystem(Sdl.InitVideo) != 0)
        {
            Console.WriteLine($"[CupriFace] Could not start SDL for a second window ({_sdl.GetErrorS()}).");
            return false;
        }
        _videoInited = true;
        // Text input is process-wide in SDL. The SDL main window starts it itself; with a GLFW main
        // window nothing else would, and typing into a second window would deliver no text.
        _sdl.StartTextInput();
        return true;
    }

    public void Add(SdlChildWindow window)
    {
        if (!_windows.Contains(window)) _windows.Add(window);
    }

    public void Remove(SdlChildWindow window) => _windows.Remove(window);

    /// <summary>One event addressed to a window that is not the main one.</summary>
    public void Handle(Event e)
    {
        if (SdlSoftwareWindow.WindowOf(e) is not { } id || id == 0) return;
        foreach (var w in _windows)
            if (w.Id == id) { w.Handle(e); return; }
    }

    /// <summary>Once per main-window tick: read the queue when it is ours, then let each window draw.</summary>
    public void Pump()
    {
        if (OwnsEvents && _videoInited)
        {
            var e = new Event();
            while (_sdl.PollEvent(ref e) != 0) Handle(e);
        }
        // A copy: a window may close itself (and leave the list) inside its own Pump.
        foreach (var w in _windows.ToArray()) w.Pump();
    }

    /// <summary>Before the main window is disposed: the SDL window's Dispose calls SDL_Quit, which
    /// would leave these holding destroyed windows.</summary>
    public void Dispose()
    {
        foreach (var w in _windows.ToArray()) w.Dispose();
        _windows.Clear();
        if (_videoInited) { _sdl.QuitSubSystem(Sdl.InitVideo); _videoInited = false; }
    }
}
