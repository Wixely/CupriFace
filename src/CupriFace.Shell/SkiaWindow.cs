using System.Diagnostics;
using CupriFace.Interaction;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// M0 shell (DESIGN.md Layer 0 + Layer 4 bootstrap). Owns an OS window (Silk.NET),
/// an OpenGL context, and a Skia GPU surface bound to the default framebuffer.
/// Raises <see cref="Render"/> once per vsync'd frame with a ready-to-draw
/// <see cref="RenderContext"/>.
///
/// SCOPE NOTE: this is the single-threaded foundation. The render-thread /
/// commit-snapshot split (DESIGN.md §7.2) is the next increment of M0 and layers
/// on top of this without changing the public draw contract.
/// </summary>
public sealed class SkiaWindow : IDisposable
{
    private readonly WindowOptions _options;
    private readonly FrameStats _stats = new();

    private IWindow? _window;
    private IInputContext? _input;
    private GRGlInterface? _glInterface;
    private GRContext? _grContext;
    private GRBackendRenderTarget? _renderTarget;
    private SKSurface? _surface;
    private Vector2D<int> _fbSize;

    /// <summary>Raised each frame after the surface is ready. Draw here.</summary>
    public event Action<RenderContext>? Render;

    /// <summary>Raised on left-button press with client-area coordinates and the click count
    /// (1/2/3 = single/double/triple — for word/line text selection).</summary>
    public event Action<float, float, int>? PointerDown;
    public event Action<float, float>? RightPointerDown;     // right-click → context menu
    public event Action<float, float>? PointerMove;
    public event Action<float, float>? PointerUp;
    public event Action<float, float, float>? PointerWheel; // x, y, deltaY (notches)
    public event Action<string>? TextEntered;
    public event Action<EditKey, KeyMods>? EditKeyPressed;  // key + Shift/Ctrl modifiers
    public event Action<char, KeyMods>? Shortcut;           // Ctrl/Cmd + letter (a/c/x/v …)

    /// <summary>OS clipboard text, for copy/cut/paste (Silk keyboard, no P/Invoke).</summary>
    public string? ClipboardText
    {
        get => _input?.Keyboards is { Count: > 0 } ks ? ks[0].ClipboardText : null;
        set { if (value is not null && _input is not null) foreach (var kb in _input.Keyboards) kb.ClipboardText = value; }
    }

    // Click-count tracking (Silk MouseDown carries no count, unlike SDL): rapid clicks near
    // the same point escalate 1→2→3 for word/line selection.
    private readonly Stopwatch _clickClock = Stopwatch.StartNew();
    private double _lastClickMs; private float _lastClickX, _lastClickY; private int _clickCount;

    /// <summary>
    /// Optional per-frame predicate to request window close (used by the headless
    /// smoke test so a run terminates instead of blocking on the render loop).
    /// </summary>
    public Func<FrameStats, bool>? ShouldClose { get; set; }

    /// <summary>Render-on-demand gate: consulted each frame; false skips drawing AND swapping, so a
    /// static UI costs ~nothing (the front buffer stays on screen). Resize/state/focus changes force
    /// the next frame regardless. Null = render every frame (the old behaviour).</summary>
    public Func<bool>? ShouldRender { get; set; }
    private bool _forceRender = true; // first frame, resize, restore, focus — must repaint

    // Pending window icon (RGBA8888) — set before Run(), applied in OnLoad when the window exists.
    private (byte[] Rgba, int W, int H)? _pendingIcon;

    /// <summary>Set the OS window/taskbar icon from raw RGBA8888 pixels (any square size; the
    /// platform scales). Call before <see cref="Run"/>.</summary>
    public void SetIcon(byte[] rgba, int width, int height) => _pendingIcon = (rgba, width, height);

    public FrameStats Stats => _stats;

    public SkiaWindow(string title = "CupriFace", int width = 1024, int height = 768,
        bool transparent = false, bool frameless = false, bool topMost = false)
    {
        _options = WindowOptions.Default with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
            VSync = true,               // pace to the display (§7.1 target)
            API = GraphicsAPI.Default,  // OpenGL, double-buffered
            // Cross-platform (GLFW) window traits — no OS-specific code. Transparency needs a
            // compositing window manager (universal on Win8+/macOS/modern Linux); it degrades to
            // an opaque black background where none is present — the host environment's concern.
            TransparentFramebuffer = transparent,
            WindowBorder = frameless ? WindowBorder.Hidden : WindowBorder.Resizable,
            TopMost = topMost,
            // Render-on-demand: we swap manually, ONLY on frames we actually drew — a skipped frame
            // must not flip an undrawn back buffer onto the screen.
            ShouldSwapAutomatically = false,
        };
    }

    public void Run()
    {
        _window = Window.Create(_options);
        _window.Load += OnLoad;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Render += OnRender;
        _window.Closing += DisposeGpu;
        _window.Run();
    }

    private void OnLoad()
    {
        var ctx = _window!.GLContext
            ?? throw new InvalidOperationException("Window was created without a GL context.");

        // Feed Skia the window's GL loader so it resolves the same context.
        _glInterface = GRGlInterface.Create(name =>
            ctx.TryGetProcAddress(name, out var addr) ? addr : IntPtr.Zero)
            ?? throw new InvalidOperationException("Failed to assemble Skia GL interface.");

        _grContext = GRContext.CreateGl(_glInterface)
            ?? throw new InvalidOperationException("Failed to create Skia GL context.");

        _fbSize = _window.FramebufferSize;

        if (_pendingIcon is { } icon)
        {
            var raw = new Silk.NET.Core.RawImage(icon.W, icon.H, icon.Rgba);
            _window.SetWindowIcon(ref raw);
        }

        // Being restored/refocused can invalidate what's on screen — repaint on the next frame.
        _window.StateChanged += _ => _forceRender = true;
        _window.FocusChanged += _ => _forceRender = true;

        _input = _window.CreateInput();
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += (m, btn) =>
            {
                if (btn == MouseButton.Left) PointerDown?.Invoke(m.Position.X, m.Position.Y, NextClickCount(m.Position.X, m.Position.Y));
                else if (btn == MouseButton.Right) RightPointerDown?.Invoke(m.Position.X, m.Position.Y);
            };
            mouse.MouseUp += (m, btn) => { if (btn == MouseButton.Left) PointerUp?.Invoke(m.Position.X, m.Position.Y); };
            mouse.MouseMove += (m, pos) => PointerMove?.Invoke(pos.X, pos.Y);
            mouse.Scroll += (m, wheel) => PointerWheel?.Invoke(m.Position.X, m.Position.Y, wheel.Y);
        }
        foreach (var kb in _input.Keyboards)
        {
            // Skip control chars (Ctrl+letter): those are shortcuts, handled in KeyDown below.
            kb.KeyChar += (k, ch) => { if (!Ctrl(k) && !char.IsControl(ch)) TextEntered?.Invoke(ch.ToString()); };
            kb.KeyDown += (k, key, _) =>
            {
                var shift = k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight);
                var mods = (shift ? KeyMods.Shift : 0) | (Ctrl(k) ? KeyMods.Ctrl : 0);
                // Any Ctrl/Cmd + letter is forwarded as a chord (see the SDL window for why): the host
                // consumes the clipboard/undo ones, the rest reach the app's own OnShortcut bindings.
                if (Ctrl(k) && key is >= Key.A and <= Key.Z)
                {
                    Shortcut?.Invoke((char)('a' + (key - Key.A)), mods);
                    return;
                }
                var ek = key switch
                {
                    Key.Backspace => EditKey.Backspace,
                    Key.Delete => EditKey.Delete,
                    Key.Left => EditKey.Left,
                    Key.Right => EditKey.Right,
                    Key.Home => EditKey.Home,
                    Key.End => EditKey.End,
                    Key.Enter or Key.KeypadEnter => EditKey.Enter,
                    Key.Up => EditKey.Up,
                    Key.Down => EditKey.Down,
                    Key.Tab => shift ? EditKey.ShiftTab : EditKey.Tab,
                    Key.Escape => EditKey.Escape,
                    _ => EditKey.None,
                };
                if (ek != EditKey.None) EditKeyPressed?.Invoke(ek, mods);
            };
        }
    }

    private CupriFace.Style.CursorType _lastCursor = (CupriFace.Style.CursorType)(-1);

    /// <summary>Show the standard cursor matching the engine's <see cref="CupriFace.Style.CursorType"/>
    /// (from <c>CupriDocument.CursorAt</c>). Synonyms fold onto the nearest GLFW standard cursor
    /// (grab → hand; wait/progress/help have no distinct shape → the default arrow).</summary>
    public void SetCursor(CupriFace.Style.CursorType c)
    {
        if (c == _lastCursor || _input?.Mice is not { Count: > 0 } mice) return;
        _lastCursor = c;
        var shape = c switch
        {
            CupriFace.Style.CursorType.Pointer or CupriFace.Style.CursorType.Grab or CupriFace.Style.CursorType.Grabbing => StandardCursor.Hand,
            CupriFace.Style.CursorType.Text => StandardCursor.IBeam,
            CupriFace.Style.CursorType.Crosshair => StandardCursor.Crosshair,
            CupriFace.Style.CursorType.Move => StandardCursor.ResizeAll,
            CupriFace.Style.CursorType.NotAllowed => StandardCursor.NotAllowed,
            CupriFace.Style.CursorType.EwResize => StandardCursor.HResize,
            CupriFace.Style.CursorType.NsResize => StandardCursor.VResize,
            CupriFace.Style.CursorType.NwseResize => StandardCursor.NwseResize,
            CupriFace.Style.CursorType.NeswResize => StandardCursor.NeswResize,
            _ => StandardCursor.Default,
        };
        foreach (var m in mice) { m.Cursor.Type = Silk.NET.Input.CursorType.Standard; m.Cursor.StandardCursor = shape; }
    }

    private static bool Ctrl(IKeyboard k) =>
        k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight) ||
        k.IsKeyPressed(Key.SuperLeft) || k.IsKeyPressed(Key.SuperRight); // Cmd on macOS

    private int NextClickCount(float x, float y)
    {
        var now = _clickClock.Elapsed.TotalMilliseconds;
        _clickCount = (now - _lastClickMs <= 400 && Math.Abs(x - _lastClickX) < 5 && Math.Abs(y - _lastClickY) < 5)
            ? _clickCount + 1 : 1;
        _lastClickMs = now; _lastClickX = x; _lastClickY = y;
        return _clickCount;
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _fbSize = size;
        // Surface is recreated lazily on the next frame at the new size.
        _surface?.Dispose(); _surface = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _forceRender = true;
        // Repaint immediately so a drag-resize streams rather than snapping on release.
        try { _window?.DoRender(); } catch { /* reentrancy on some platforms — ignore */ }
    }

    private void EnsureSurface()
    {
        if (_surface is not null || _grContext is null) return;
        if (_fbSize.X <= 0 || _fbSize.Y <= 0) return;

        const uint GL_RGBA8 = 0x8058;
        var fbInfo = new GRGlFramebufferInfo(fboId: 0, format: GL_RGBA8);
        _renderTarget = new GRBackendRenderTarget(
            _fbSize.X, _fbSize.Y, sampleCount: 0, stencilBits: 8, fbInfo);
        _surface = SKSurface.Create(
            _grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
    }

    private void OnRender(double deltaSeconds)
    {
        EnsureSurface();
        if (_surface is null) return;

        var render = _forceRender || (ShouldRender?.Invoke() ?? true);
        _forceRender = false;
        if (render)
        {
            _stats.BeginFrame(deltaSeconds);

            Render?.Invoke(new RenderContext(_surface.Canvas, _fbSize.X, _fbSize.Y, _stats));

            _grContext!.Flush(); // push the recorded draws to the GL framebuffer before swap

            _stats.EndFrame();
            _window!.SwapBuffers(); // manual swap: only drawn frames reach the screen
        }
        else
        {
            // Nothing changed: the front buffer stays as-is. The vsync wait lives in SwapBuffers,
            // which we skipped — sleep briefly so an idle window doesn't spin the render loop.
            System.Threading.Thread.Sleep(8);
        }

        if (ShouldClose?.Invoke(_stats) == true)
            _window!.Close();
    }

    private void DisposeGpu()
    {
        _surface?.Dispose(); _surface = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _grContext?.Dispose(); _grContext = null;
        _glInterface?.Dispose(); _glInterface = null;
    }

    public void Dispose()
    {
        DisposeGpu();
        _input?.Dispose();
        _input = null;
        _window?.Dispose();
        _window = null;
    }
}

/// <summary>Everything a frame draw callback needs for one frame.</summary>
public readonly record struct RenderContext(SKCanvas Canvas, int Width, int Height, FrameStats Stats);
