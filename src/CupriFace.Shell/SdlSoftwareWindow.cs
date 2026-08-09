using System.Diagnostics;
using System.Runtime.InteropServices;
using CupriFace.Interaction;
using Silk.NET.SDL;
using SkiaSharp;

namespace CupriFace.Shell;

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
    public event Action<float, float, float>? PointerWheel; // x, y, deltaY (notches)
    public event Action<string>? TextEntered;               // printable text (IME-aware)
    public event Action<EditKey, KeyMods>? EditKeyPressed;  // key + Shift/Ctrl modifiers
    public event Action<char, KeyMods>? Shortcut;           // Ctrl/Cmd + letter (a/c/x/v …)
    public FrameStats Stats => _stats;

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
    /// closest one (grab → hand; the diagonal resizes → the two-headed arrows).</summary>
    public void SetCursor(CupriFace.Style.CursorType c)
    {
        if (c == _lastCursor) return;
        _lastCursor = c;
        var id = c switch
        {
            CupriFace.Style.CursorType.Pointer or CupriFace.Style.CursorType.Grab or CupriFace.Style.CursorType.Grabbing => SystemCursor.SystemCursorHand,
            CupriFace.Style.CursorType.Text => SystemCursor.SystemCursorIbeam,
            CupriFace.Style.CursorType.Wait => SystemCursor.SystemCursorWait,
            CupriFace.Style.CursorType.Progress => SystemCursor.SystemCursorWaitarrow,
            CupriFace.Style.CursorType.Crosshair => SystemCursor.SystemCursorCrosshair,
            CupriFace.Style.CursorType.Move => SystemCursor.SystemCursorSizeall,
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

    private readonly bool _frameless, _topMost;

    // NOTE: the SDL software path is opaque — its streaming texture blits over the window with no
    // per-pixel alpha against the desktop, so `transparent` has no effect here (the GL path handles
    // transparency). Frameless / always-on-top do work through standard SDL window flags.
    public SdlSoftwareWindow(string title = "CupriFace", int width = 1024, int height = 768,
        bool transparent = false, bool frameless = false, bool topMost = false)
    {
        _title = title;
        _width = width;
        _height = height;
        _frameless = frameless;
        _topMost = topMost;
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
                        PointerWheel?.Invoke(_lastX, _lastY, e.Wheel.Y);
                        break;
                    case EventType.Textinput:
                    {
                        var text = Marshal.PtrToStringUTF8((IntPtr)e.Text.Text);
                        if (!string.IsNullOrEmpty(text)) TextEntered?.Invoke(text);
                        break;
                    }
                    case EventType.Keydown:
                    {
                        var mod = e.Key.Keysym.Mod;
                        var shift = (mod & (ushort)Keymod.Shift) != 0;
                        var ctrl = (mod & ((ushort)Keymod.Ctrl | (ushort)Keymod.Gui)) != 0; // Gui = Cmd (macOS)
                        var mods = (shift ? KeyMods.Shift : 0) | (ctrl ? KeyMods.Ctrl : 0);
                        if (ctrl)
                            switch (e.Key.Keysym.Scancode)
                            {
                                case Scancode.ScancodeA: Shortcut?.Invoke('a', mods); continue;
                                case Scancode.ScancodeC: Shortcut?.Invoke('c', mods); continue;
                                case Scancode.ScancodeX: Shortcut?.Invoke('x', mods); continue;
                                case Scancode.ScancodeV: Shortcut?.Invoke('v', mods); continue;
                                case Scancode.ScancodeZ: Shortcut?.Invoke('z', mods); continue;
                                case Scancode.ScancodeY: Shortcut?.Invoke('y', mods); continue;
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
            RenderFrame();
            _sdl.Delay(16); // ~60 fps cap
        }
    }

    private int ResizeWatch(void* userData, Event* e)
    {
        if ((EventType)e->Type == EventType.Windowevent && (WindowEventID)e->Window.Event == WindowEventID.SizeChanged)
        {
            EnsureSurface(e->Window.Data1, e->Window.Data2);
            RenderFrame();
        }
        return 0;
    }

    private void EnsureSurface(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        _width = w; _height = h;
        _canvas?.Dispose();
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        _texture = _sdl.CreateTexture(_renderer, PixelFormatArgb8888, (int)TextureAccess.Streaming, w, h);
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
        _sdl.RenderPresent(_renderer);
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
