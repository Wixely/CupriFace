using CupriFace.DevTools;
using CupriFace.Hosting;
using CupriFace.Interaction;
using Silk.NET.SDL;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// The dev tools panel popped out into a window of its own — the same <see cref="DevToolsPanel"/>,
/// drawn into a small SDL software window beside the app instead of docked inside it.
///
/// <para><b>It has no loop of its own.</b> The app's window drives it from its <c>Tick</c>, on the
/// app's thread, because the panel reads the app's render tree and that tree is only safe to read
/// between the app's frames. How its EVENTS arrive depends on which window the app got:</para>
/// <list type="bullet">
/// <item>the SDL software window drains SDL's one process-wide queue, so it hands this window's
/// events over through <see cref="SdlSoftwareWindow.ForeignWindowEvent"/> and this window must not
/// poll (<c>ownsEvents: false</c>);</item>
/// <item>the GLFW window has nothing to do with SDL, so this window initialises SDL's video for
/// itself and polls its own queue in <see cref="Pump"/> (<c>ownsEvents: true</c>).</item>
/// </list>
///
/// <para>A development tool: only created when <see cref="CupriDevTools.IsSupported"/>.</para>
/// </summary>
internal sealed unsafe class DevToolsWindow : IDisposable
{
    private const uint PixelFormatArgb8888 = 0x16362004;   // B,G,R,A in memory = Bgra8888

    private readonly Sdl _sdl = Sdl.GetApi();
    private readonly DevToolsPanel _panel;
    private readonly bool _ownsEvents;
    private readonly Action _changed;
    private bool _videoInited;
    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;
    private SKBitmap? _bitmap;
    private int _w, _h;          // pixels
    private uint _id;
    private float _scale = 1f;   // device scale of the monitor it is on
    private bool _needsDraw = true;
    private float _lastX, _lastY;
    private (int X, int Y, int W, int H)? _lastPlace;
    private readonly Dictionary<SystemCursor, nint> _cursors = new();

    /// <param name="panel">The panel to show.</param>
    /// <param name="ownsEvents">Poll SDL's queue in <see cref="Pump"/> — true when the app's window
    /// is not an SDL window, so nothing else will.</param>
    /// <param name="changed">Called when input here changed something the APP's window shows too —
    /// the element highlighted under a hovered tree row, say — so the host draws a frame.</param>
    public DevToolsWindow(DevToolsPanel panel, bool ownsEvents, Action changed)
    {
        _panel = panel;
        _ownsEvents = ownsEvents;
        _changed = changed;
    }

    public bool IsOpen => _window is not null;

    private static readonly string? DumpPath =
        Environment.GetEnvironmentVariable("CUPRIFACE_FRAME_DUMP") is { Length: > 0 } p
            ? Path.ChangeExtension(p, ".devtools.png") : null;

    /// <summary>Raised when the user closes the window with its own close button.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Open beside the app. <paramref name="appX"/>/<paramref name="appY"/> are the app window's
    /// client position and <paramref name="appRight"/> its right edge, in screen pixels; the panel is
    /// placed to the right of it, or where it was last time it was open. <paramref name="appLogicalHeight"/>
    /// is the app's height in LOGICAL units — the panel matches it, and the monitor's scale is applied
    /// here, once. False when SDL refused.
    /// </summary>
    public bool Open(int appX, int appY, int appRight, int appLogicalHeight)
    {
        if (_window is not null) return true;
        if (_ownsEvents && !_videoInited)
        {
            if (_sdl.InitSubSystem(Sdl.InitVideo) != 0)
            {
                Console.WriteLine($"[CupriFace] Dev tools: could not pop out ({_sdl.GetErrorS()}).");
                return false;
            }
            _videoInited = true;
        }

        // Width and height in LOGICAL units here; the monitor scale is applied once, below.
        var (x, y, w, h) = _lastPlace ?? (appRight + 8, appY, (int)DevToolsPanel.DefaultWidth, Math.Max(600, appLogicalHeight));
        _window = _sdl.CreateWindow("CupriFace DevTools", x, y, w, h, (uint)WindowFlags.Resizable);
        if (_window is null)
        {
            Console.WriteLine($"[CupriFace] Dev tools: could not pop out ({_sdl.GetErrorS()}).");
            return false;
        }
        _id = _sdl.GetWindowID(_window);
        // The software renderer, like the app's own software window: the panel is a few thousand
        // pixels of text, and a GPU renderer here is one more thing that can fail on a VM.
        _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null) { Close(); return false; }

        // The monitor's scale, so the panel's text is the same physical size as the app's. Only on
        // first opening: a remembered placement is already the size the user left it.
        // Text input is process-wide in SDL; the app's own SDL window has already started it, and
        // with a GLFW app window nothing else would.
        if (_ownsEvents) _sdl.StartTextInput();
        _scale = ReadScale();
        if (_lastPlace is null && _scale != 1f)
            _sdl.SetWindowSize(_window, (int)(w * _scale), (int)(h * _scale));
        KeepOnScreen();
        int cw = 0, ch = 0;
        _sdl.GetWindowSize(_window, ref cw, ref ch);
        EnsureSurface(cw, ch);
        _needsDraw = true;
        return true;
    }

    /// <summary>Close the window, remembering where it was.</summary>
    public void Close()
    {
        if (_window is not null)
        {
            int x = 0, y = 0, w = 0, h = 0;
            _sdl.GetWindowPosition(_window, ref x, ref y);
            _sdl.GetWindowSize(_window, ref w, ref h);
            if (w > 0 && h > 0) _lastPlace = (x, y, w, h);
        }
        foreach (var c in _cursors.Values) _sdl.FreeCursor((Cursor*)c);
        _cursors.Clear();
        if (_texture is not null) { _sdl.DestroyTexture(_texture); _texture = null; }
        if (_renderer is not null) { _sdl.DestroyRenderer(_renderer); _renderer = null; }
        if (_window is not null) { _sdl.DestroyWindow(_window); _window = null; }
        _bitmap?.Dispose();
        _bitmap = null;
        _id = 0;
    }

    /// <summary>The panel changed: draw it at the next <see cref="Pump"/>.</summary>
    public void Invalidate() => _needsDraw = true;

    /// <summary>Once per app tick: read this window's input (when it owns the queue) and draw if
    /// anything changed.</summary>
    public void Pump()
    {
        if (_window is null) return;
        if (_ownsEvents)
        {
            var e = new Event();
            while (_window is not null && _sdl.PollEvent(ref e) != 0)
                if (SdlSoftwareWindow.WindowOf(e) == _id) Handle(e);
        }
        if (_window is null || !_needsDraw) return;
        _needsDraw = false;
        Draw();
    }

    /// <summary>One event for this window. The app's SDL window calls this for events whose window
    /// id is this one's; <see cref="Pump"/> calls it for its own queue.</summary>
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
                        CloseRequested?.Invoke();
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
                        _panel.PanelPointerLeave();
                        changed = true;
                        break;
                    // The keyboard follows the focused window: this one's keys are the panel's.
                    case WindowEventID.FocusGained:
                        _panel.PanelHasFocus = true;
                        break;
                    case WindowEventID.FocusLost:
                        _panel.PanelHasFocus = false;
                        break;
                }
                break;
            case EventType.Mousemotion:
                (_lastX, _lastY) = (e.Motion.X / _scale, e.Motion.Y / _scale);
                changed = _panel.PanelPointerMove(_lastX, _lastY);
                SetCursor(_panel.PanelCursor(_lastX, _lastY));
                break;
            case EventType.Mousebuttondown when e.Button.Button == 1:
                (_lastX, _lastY) = (e.Button.X / _scale, e.Button.Y / _scale);
                changed = _panel.PanelPointerDown(_lastX, _lastY, Math.Max(1, (int)e.Button.Clicks));
                break;
            case EventType.Mousebuttonup when e.Button.Button == 1:
                changed = _panel.PanelPointerUp(e.Button.X / _scale, e.Button.Y / _scale);
                break;
            case EventType.Mousewheel:
                changed = _panel.PanelWheel(_lastX, _lastY, -e.Wheel.PreciseY * 50f);
                break;
            case EventType.Keydown:
            {
                _panel.PanelHasFocus = true;
                var mod = e.Key.Keysym.Mod;
                var shift = (mod & (ushort)Keymod.Shift) != 0;
                var ctrl = (mod & ((ushort)Keymod.Ctrl | (ushort)Keymod.Gui)) != 0;
                var mods = (shift ? KeyMods.Shift : 0) | (ctrl ? KeyMods.Ctrl : 0);
                var sc = e.Key.Keysym.Scancode;
                if (ctrl && sc is >= Scancode.ScancodeA and <= Scancode.ScancodeZ)
                {
                    changed = _panel.PanelShortcut((char)('a' + (sc - Scancode.ScancodeA)), mods,
                        () => _sdl.GetClipboardTextS(), v => _sdl.SetClipboardText(v));
                    break;
                }
                var key = SdlSoftwareWindow.ToEditKey(sc, shift);
                if (key != EditKey.None) changed = _panel.HandleKey(key, mods);
                break;
            }
            case EventType.Textinput:
            {
                var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)e.Text.Text);
                if (!string.IsNullOrEmpty(text)) changed = _panel.PanelText(text);
                break;
            }
        }
        if (changed)
        {
            _needsDraw = true;
            _changed();
        }
    }

    private void Draw()
    {
        if (_bitmap is null || _texture is null) return;
        using (var canvas = new SKCanvas(_bitmap))
        {
            canvas.Clear(new SKColor(0x1e, 0x1f, 0x24));
            canvas.Scale(_scale);
            _panel.Render(canvas, _w / _scale, _h / _scale);
            canvas.Flush();
        }
        _sdl.UpdateTexture(_texture, null, (void*)_bitmap.GetPixels(), _w * 4);
        // CUPRIFACE_FRAME_DUMP=out.png → out.devtools.png: this window's pixels beside the app's,
        // the only way to see it on a machine where a screen capture lies.
        if (DumpPath is { } dump)
            using (var f = File.Create(dump)) _bitmap.Encode(f, SKEncodedImageFormat.Png, 90);
        _sdl.RenderClear(_renderer);
        _sdl.RenderCopy(_renderer, _texture, null, null);
        _sdl.RenderPresent(_renderer);
    }

    private void EnsureSurface(int w, int h)
    {
        if (w <= 0 || h <= 0 || (_bitmap is not null && w == _w && h == _h)) return;
        _w = w; _h = h;
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        _texture = _sdl.CreateTexture(_renderer, PixelFormatArgb8888, (int)TextureAccess.Streaming, w, h);
    }

    // The same source of D the app's software window uses, for the same reason: only Windows
    // reports it per monitor to an SDL window created without SDL_WINDOW_ALLOW_HIGHDPI.
    private float ReadScale()
    {
        if (!OperatingSystem.IsWindows() || !WindowsDpi.Enabled || _window is null) return 1f;
        var info = new SysWMInfo();
        _sdl.GetVersion(&info.Version);
        if (!_sdl.GetWindowWMInfo(_window, &info) || info.Info.Win.Hwnd == null) return _scale;
        return HostScale.Sanitize(WindowsDpi.GetScaleForWindow((nint)info.Info.Win.Hwnd));
    }

    /// <summary>Pull the window back onto the display it is mostly on, if it was placed past an edge
    /// (the app maximised, so "to its right" is off the screen).</summary>
    private void KeepOnScreen()
    {
        var display = _sdl.GetWindowDisplayIndex(_window);
        if (display < 0) display = 0;
        var bounds = new Silk.NET.Maths.Rectangle<int>();
        if (_sdl.GetDisplayUsableBounds(display, ref bounds) != 0) return;
        int x = 0, y = 0, w = 0, h = 0;
        _sdl.GetWindowPosition(_window, ref x, ref y);
        _sdl.GetWindowSize(_window, ref w, ref h);
        var right = bounds.Origin.X + bounds.Size.X;
        var bottom = bounds.Origin.Y + bounds.Size.Y;
        var nx = Math.Clamp(x, bounds.Origin.X, Math.Max(bounds.Origin.X, right - w));
        var ny = Math.Clamp(y, bounds.Origin.Y, Math.Max(bounds.Origin.Y, bottom - h));
        if (h > bounds.Size.Y) _sdl.SetWindowSize(_window, w, bounds.Size.Y);
        if (nx != x || ny != y) _sdl.SetWindowPosition(_window, nx, ny);
    }

    private void SetCursor(Style.CursorType c)
    {
        var id = c switch
        {
            Style.CursorType.Pointer => SystemCursor.SystemCursorHand,
            Style.CursorType.Text => SystemCursor.SystemCursorIbeam,
            _ => SystemCursor.SystemCursorArrow,
        };
        if (!_cursors.TryGetValue(id, out var ptr)) _cursors[id] = ptr = (nint)_sdl.CreateSystemCursor(id);
        _sdl.SetCursor((Cursor*)ptr);
    }

    public void Dispose()
    {
        Close();
        if (_videoInited) { _sdl.QuitSubSystem(Sdl.InitVideo); _videoInited = false; }
    }
}
