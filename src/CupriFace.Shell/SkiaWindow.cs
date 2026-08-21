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
    private readonly bool _darkWindowChrome;
    private readonly SKColor _windowChromeColor;

    private IWindow? _window;
    private IInputContext? _input;
    private GRGlInterface? _glInterface;
    private GRContext? _grContext;
    private GRBackendRenderTarget? _renderTarget;
    private SKSurface? _surface;
    private Vector2D<int> _fbSize;

    /// <summary>Raised each frame after the surface is ready. Draw here.</summary>
    public event Action<RenderContext>? Render;

    /// <summary>Raised once per loop iteration, drawn or skipped, on the UI thread — the hook for
    /// host work that must run there every frame (e.g. draining the UIA action queue). Fires
    /// before the render-or-skip decision, so work done here can dirty this same frame.</summary>
    public event Action? Tick;

    /// <summary>The Win32 window handle once the window exists; null before <see cref="Run"/> and
    /// on every other OS. What the UIA bridge attaches to.</summary>
    public nint? Win32Hwnd => _window?.Native?.Win32?.Hwnd;

    /// <summary>The NSWindow once the window exists; null before <see cref="Run"/> and on every
    /// other OS. What the NSAccessibility bridge subclasses the content view of.</summary>
    public nint? CocoaWindow => _window?.Native?.Cocoa;

    /// <summary>Screen position of the client area's top-left (GLFW reports the content area, which
    /// is exactly the origin pointer coordinates are relative to).</summary>
    public (int X, int Y) ScreenPosition => _window is { } w ? (w.Position.X, w.Position.Y) : (0, 0);

    /// <summary>True while the window is OS-fullscreen (see <see cref="SetFullscreen"/>).</summary>
    public bool IsFullscreen => _window?.WindowState == WindowState.Fullscreen;

    // The state to restore on exit — a maximized window must come back maximized, not Normal.
    private WindowState _beforeFullscreen = WindowState.Normal;

    /// <summary>Enter/leave fullscreen (the host maps <c>WindowCommandRequested</c> and the
    /// Escape-to-exit convention here). Resize events flow as normal, so the app reflows.</summary>
    public void SetFullscreen(bool on)
    {
        if (_window is null || IsFullscreen == on) return;
        if (on)
        {
            _beforeFullscreen = _window.WindowState;
            _window.WindowState = WindowState.Fullscreen;
        }
        else
        {
            _window.WindowState = _beforeFullscreen == WindowState.Fullscreen ? WindowState.Normal : _beforeFullscreen;
        }
        _forceRender = true;
    }

    /// <summary>Change the native always-on-top state while the window is running.</summary>
    public void SetTopMost(bool on)
    {
        if (_window is not null)
        {
            _window.TopMost = on;
        }
    }

    /// <summary>Raised on left-button press with client-area coordinates and the click count
    /// (1/2/3 = single/double/triple — for word/line text selection).</summary>
    public event Action<float, float, int>? PointerDown;
    public event Action<float, float>? RightPointerDown;     // right-click → context menu
    public event Action<float, float>? PointerMove;
    public event Action<float, float>? PointerUp;
    public event Action<float, float, float, KeyMods>? PointerWheel; // x, y, deltaY (notches), mods — Ctrl+wheel is zoom
    public event Action<string>? TextEntered;
    public event Action<EditKey, KeyMods>? EditKeyPressed;  // key + Shift/Ctrl modifiers
    public event Action<char, KeyMods>? Shortcut;           // Ctrl/Cmd + letter (a/c/x/v …) or =/-/0 (zoom)

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
        bool transparent = false, bool frameless = false, bool topMost = false,
        bool darkWindowChrome = false, SKColor? windowChromeColor = null)
    {
        _darkWindowChrome = darkWindowChrome;
        _windowChromeColor = windowChromeColor ?? new SKColor(0x20, 0x20, 0x20);
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

    /// <summary>Attempt GL bring-up end to end — window, context, usable GL, Skia interface,
    /// GRContext — on an invisible throwaway window, then tear it all down. Run this in a
    /// DISPOSABLE CHILD PROCESS: on a machine with no OpenGL at all (the paravirtual GPU of
    /// virtualised Macs) glfwCreateWindow fails without setting a GLFW error, Silk.NET then
    /// applies the default window position to the NULL handle, and release-build GLFW (asserts
    /// compiled out) dies with a native SIGSEGV inside glfwSetWindowPos. That cannot be caught —
    /// but it can be CONTAINED in a process whose whole job is to die so the real one doesn't.
    /// A managed failure (no context, unusable GL) throws instead; the child maps both to its
    /// exit code. See DesktopHost.GlProbeSurvives.</summary>
    public static void Probe()
    {
        var options = WindowOptions.Default with
        {
            Title = "cupriface-gl-probe",
            Size = new Vector2D<int>(64, 64),
            IsVisible = false,
            API = GraphicsAPI.Default,
            ShouldSwapAutomatically = false,
        };
        using var window = Window.Create(options);
        window.Load += () =>
        {
            var ctx = window.GLContext
                ?? throw new InvalidOperationException("Probe window was created without a GL context.");
            VerifyGlIsUsable(ctx);
            using var iface = GRGlInterface.Create(name =>
                name.StartsWith("gl", StringComparison.Ordinal) && ctx.TryGetProcAddress(name, out var addr)
                    ? addr : IntPtr.Zero)
                ?? throw new InvalidOperationException("Failed to assemble Skia GL interface.");
            using var gr = GRContext.CreateGl(iface)
                ?? throw new InvalidOperationException("Failed to create Skia GL context.");
            GlTrace("probe: GL bring-up OK");
            window.Close();
        };
        window.Run();
    }

    // CUPRIFACE_GL_DEBUG=1 traces GL bring-up to stderr: the context's version/vendor/renderer,
    // then every proc-address Skia requests. When a broken GL stack kills the process natively,
    // the last trace line names the exact call that did it — evidence obtainable no other way,
    // because the crash is a SIGSEGV inside Skia, past any managed catch.
    private static readonly bool GlDebug =
        Environment.GetEnvironmentVariable("CUPRIFACE_GL_DEBUG") is "1" or "true" or "TRUE";

    private static void GlTrace(string message)
    {
        if (!GlDebug) return;
        Console.Error.WriteLine($"[gl] {message}");
        Console.Error.Flush(); // the process may die on the very next native call
    }

    /// <summary>Throws unless the current context can actually answer <c>glGetString(GL_VERSION)</c>.
    /// Cheap, and it is the exact call whose null result crashes Skia's interface assembly.</summary>
    private static unsafe void VerifyGlIsUsable(Silk.NET.Core.Contexts.IGLContext ctx)
    {
        // If the loader cannot even produce glGetString there is certainly no GL here, and calling
        // through a null address would be its own segfault.
        if (!ctx.TryGetProcAddress("glGetString", out var addr) || addr == IntPtr.Zero)
            throw new InvalidOperationException("No usable OpenGL: glGetString could not be resolved.");

        // Call it through Silk rather than a hand-rolled function pointer: it already knows each
        // platform's calling convention, and this keeps the shell free of bespoke native interop.
        using var gl = Silk.NET.OpenGL.GL.GetApi(ctx);
        if (gl.GetString(Silk.NET.OpenGL.StringName.Version) is null)
            throw new InvalidOperationException(
                "No usable OpenGL: glGetString(GL_VERSION) returned null (GPU-less or virtual display?).");

        if (GlDebug)
        {
            GlTrace($"version : {gl.GetStringS(Silk.NET.OpenGL.StringName.Version)}");
            GlTrace($"vendor  : {gl.GetStringS(Silk.NET.OpenGL.StringName.Vendor)}");
            GlTrace($"renderer: {gl.GetStringS(Silk.NET.OpenGL.StringName.Renderer)}");
        }
    }

    private void OnLoad()
    {
        if (_darkWindowChrome && Win32Hwnd is { } hwnd)
        {
            WindowChrome.TryEnableDarkMode(hwnd, _windowChromeColor);
        }

        var ctx = _window!.GLContext
            ?? throw new InvalidOperationException("Window was created without a GL context.");

        // A context object is not the same as usable GL. On a GPU-less or virtual X server
        // (headless CI, many remote/VM sessions) GLFW hands back a context whose entry points
        // resolve but do nothing: glGetString(GL_VERSION) returns NULL. Skia's interface assembly
        // then parses that null pointer and takes the whole process down with SIGSEGV — captured
        // under gdb in CI, crashing inside gr_glinterface_assemble_interface.
        //
        // A native crash cannot be caught, so the fallback in DesktopHost never got a chance. Ask
        // the context the same question Skia is about to ask, and turn a silent kill into an
        // ordinary exception that the SDL software path already handles.
        VerifyGlIsUsable(ctx);

        // Feed Skia the window's GL loader so it resolves the same context. With CUPRIFACE_GL_DEBUG
        // every lookup is traced BEFORE it resolves — if assembling the interface dies natively, the
        // final trace line is the killer.
        GlTrace("assembling Skia GL interface…");
        _glInterface = GRGlInterface.Create(name =>
        {
            GlTrace($"proc? {name}");        // logged BEFORE the lookup: if the lookup itself dies, this line names it

            // Answer ONLY for OpenGL ("gl*") names. Skia also probes for EGL entry points
            // (eglQueryString, eglGetCurrentDisplay) through this same loader, and on X11 the
            // loader is glXGetProcAddressARB — which is SPECIFIED to fabricate a dispatch stub
            // for any name it does not recognise. It returned non-null garbage for the egl*
            // probes, Skia concluded EGL was present, called the stub, and the process died in
            // an uninitialised dispatch slot (the headless-Linux SIGSEGV; the runner's own GL
            // trace named these two probes as the killer). A null here is fully handled: Skia
            // just skips EGL-specific extension detection, which a GLX/WGL context hasn't got.
            if (!name.StartsWith("gl", StringComparison.Ordinal))
            {
                GlTrace("   -> null (non-GL name; loaders lie about these)");
                return IntPtr.Zero;
            }

            var found = ctx.TryGetProcAddress(name, out var addr);
            GlTrace($"   -> {(found ? $"0x{addr:x}" : "null")}");
            return found ? addr : IntPtr.Zero;
        }) ?? throw new InvalidOperationException("Failed to assemble Skia GL interface.");
        GlTrace("interface assembled; creating GRContext…");

        _grContext = GRContext.CreateGl(_glInterface)
            ?? throw new InvalidOperationException("Failed to create Skia GL context.");
        GlTrace("GRContext created.");

        _fbSize = _window.FramebufferSize;

        if (_pendingIcon is { } icon)
        {
            var raw = new Silk.NET.Core.RawImage(icon.W, icon.H, icon.Rgba);
            _window.SetWindowIcon(ref raw);
        }

        // Being restored/refocused can invalidate what's on screen — repaint on the next frame.
        _window.StateChanged += _ => _forceRender = true;
        _window.FocusChanged += f => { _forceRender = true; KeyDiag.Log(f ? "gl focus-gained" : "gl focus-lost"); };

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
            mouse.Scroll += (m, wheel) => PointerWheel?.Invoke(m.Position.X, m.Position.Y, wheel.Y,
                _input.Keyboards.Any(Ctrl) ? KeyMods.Ctrl : KeyMods.None);
        }
        foreach (var kb in _input.Keyboards)
        {
            // Skip control chars (Ctrl+letter): those are shortcuts, handled in KeyDown below.
            kb.KeyChar += (k, ch) => { if (!Ctrl(k) && !char.IsControl(ch)) TextEntered?.Invoke(ch.ToString()); };
            kb.KeyDown += (k, key, _) =>
            {
                KeyDiag.Log($"gl keydown key={key} ctrl={Ctrl(k)}");
                var shift = k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight);
                var mods = (shift ? KeyMods.Shift : 0) | (Ctrl(k) ? KeyMods.Ctrl : 0);
                // Any Ctrl/Cmd + letter is forwarded as a chord (see the SDL window for why): the host
                // consumes the clipboard/undo ones, the rest reach the app's own OnShortcut bindings.
                if (Ctrl(k) && key is >= Key.A and <= Key.Z)
                {
                    Shortcut?.Invoke((char)('a' + (key - Key.A)), mods);
                    return;
                }
                // Ctrl/Cmd + =/-/0 is page zoom, browser-style; keypad +/-/0 carry the same intent.
                if (Ctrl(k))
                {
                    var zoomCh = key switch
                    {
                        Key.Equal or Key.KeypadAdd => '=',
                        Key.Minus or Key.KeypadSubtract => '-',
                        Key.Number0 or Key.Keypad0 => '0',
                        _ => '\0',
                    };
                    if (zoomCh != '\0') { Shortcut?.Invoke(zoomCh, mods); return; }
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

    /// <summary>Frames drawn from inside a resize callback rather than the normal loop — the ones
    /// that make a drag-resize stream instead of snapping on release. Zero of these after a drag
    /// means the mechanism below is not working, whatever the window looked like.</summary>
    public int ResizeFrames { get; private set; }

    private bool _resizeFailureReported;

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _fbSize = size;
        // Surface is recreated lazily on the next frame at the new size.
        _surface?.Dispose(); _surface = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _forceRender = true;

        // Repaint NOW. Windows and macOS run a MODAL loop while a window edge is dragged: Run()'s
        // render loop does not get another turn until the mouse is released, so a frame left to
        // "next tick" arrives when the drag ENDS. GLFW still delivers this callback throughout, so
        // this is the only chance to draw mid-drag.
        try
        {
            _window?.DoRender();
            ResizeFrames++;
        }
        catch (Exception ex)
        {
            // Re-entrant rendering is refused on some platforms. That is survivable — the window
            // catches up on release — but it must not be SILENT: "resize is janky" needs to be
            // answerable from a log rather than by guesswork.
            if (!_resizeFailureReported)
            {
                _resizeFailureReported = true;
                Console.Error.WriteLine(
                    $"[CupriFace] live-resize repaint unavailable ({ex.GetType().Name}: {ex.Message}); " +
                    "the window will catch up when the drag ends.");
            }
        }

        if (ResizeDebug)
            Console.Error.WriteLine($"[resize] frame {ResizeFrames} at {size.X}x{size.Y}");
    }

    /// <summary>CUPRIFACE_RESIZE_DEBUG=1 traces every mid-drag repaint. One drag of a window edge
    /// then answers the question no CI machine can: does this actually stream?</summary>
    internal static readonly bool ResizeDebug =
        Environment.GetEnvironmentVariable("CUPRIFACE_RESIZE_DEBUG") is "1" or "true";

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
        Tick?.Invoke();

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
