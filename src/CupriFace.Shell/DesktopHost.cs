using System.Diagnostics;
using CupriFace;
using CupriFace.DevTools;
using CupriFace.Hosting;
using CupriFace.Interaction;
using CupriFace.Shell.Dialogs;

namespace CupriFace.Shell;

/// <summary>The GL window's GPU failed while it was open; carries the reason to the fallback.</summary>
internal sealed class GpuLostException(string reason) : Exception(reason);

/// <summary>
/// Runs a <see cref="CupriApp"/> in a desktop window: tries GPU (GL), falls back to the
/// SDL software window, and wires pointer input to the app's document. Its siblings render the
/// *same* app elsewhere: <c>WebHost.Run</c> (CupriFace.Web.Mono) to a browser &lt;canvas&gt;, and
/// <c>CupriActivity</c> (CupriFace.Android) to a GL surface on a phone.
/// </summary>
public static class DesktopHost
{
    private const int MiddleMousePointerId = -1;
    /// <summary>
    /// DIAGNOSTICS ONLY: the scale state the last drawn frame used — D, the framebuffer, the logical
    /// client size and T. Exposed so a probe app can display what the host actually computed rather
    /// than re-deriving it and agreeing with itself. Not a supported API for app logic: an app is
    /// told its size through <see cref="CupriApp.Present"/>, in logical units, on purpose.
    /// </summary>
    public static HostScale FrameScale { get; private set; }

    /// <summary>DIAGNOSTICS ONLY: what became of the Per-Monitor-V2 request at startup. "granted"
    /// means this process established it; "already set" means a manifest or the app got there first
    /// and won, which is correct behaviour rather than a failure.</summary>
    public static string DpiAwareness { get; private set; } = "not requested (non-Windows)";

    /// <param name="app">The portable application definition.</param>
    /// <param name="configure">Host-composition hook, run once after the document is built —
    /// where desktop-only capabilities attach (e.g. <c>d =&gt; d.UseVideo(new WebmVideoBackend())</c>
    /// from the optional CupriFace.Media package). Kept OUT of <see cref="CupriApp.Configure"/> on
    /// purpose: the app class is shared with hosts that must not reference desktop codecs.</param>
    public static void Run(CupriApp app, Action<CupriDocument>? configure = null)
        => Run(app, preferSoftware: false, configure: configure);

    /// <summary>Run with an explicit software-rendering preference. On Windows, frameless
    /// transparent apps use per-pixel layered presentation, bypassing WGL/DWM alpha issues.
    /// GPU-only surface producers must provide their software fallback for this mode.</summary>
    public static void Run(CupriApp app, bool preferSoftware, Action<CupriDocument>? configure = null)
        => RunCore(app, preferSoftware, layeredGpu: false, configure: configure);

    /// <summary>Windows-only GPU rendering with per-pixel alpha presentation. Draws on an
    /// off-screen Skia GPU surface and reads changed frames back for UpdateLayeredWindow.
    /// This is not zero-copy composition. Uses normal desktop rendering on other platforms
    /// or for apps that are not transparent and frameless. The Windows layered path needs working GL.
    /// CUPRIFACE_SOFTWARE=1 explicitly disables GPU rendering for troubleshooting.</summary>
    public static void RunWithLayeredGpu(CupriApp app, Action<CupriDocument>? configure = null)
    {
        if (!OperatingSystem.IsWindows() || !app.Transparent || !app.Frameless)
        {
            Console.WriteLine("[CupriFace] Layered GPU presentation requires Windows and a transparent, frameless app; using normal desktop rendering.");
            Run(app, configure);
            return;
        }
        RunCore(app, preferSoftware: false, layeredGpu: true, configure: configure);
    }

    private static void RunCore(CupriApp app, bool preferSoftware, bool layeredGpu, Action<CupriDocument>? configure)
    {
        // Per-Monitor-V2, before ANY window can exist — awareness is a process property that windows
        // inherit at creation, so this is the only moment it can be declared. Ahead of the GL probe
        // on purpose: the probe is a second process running this same line, and a probe window with
        // different awareness to the real one is not the thing being probed.
        //
        // A REQUEST, not a demand: if the executable's manifest already declares an awareness, or
        // the app set one before calling here, Windows refuses and that choice stands (#137).
        if (OperatingSystem.IsWindows())
        {
            DpiAwareness = !app.DpiAware ? "off (CupriApp.DpiAware = false)"
                : !WindowsDpi.Enabled ? "off (CUPRIFACE_DPI=0)"
                : WindowsDpi.TryDeclarePerMonitorV2() ? "Per-Monitor-V2 (granted)"
                : "already set by the app's manifest or the app itself — that choice wins";
        }

        // The GL-probe child (see GlProbeSurvives): attempt GL bring-up, report via exit code,
        // never open the real window. Checked before anything else so the probe stays invisible.
        if (Environment.GetCommandLineArgs().Contains("--cupriface-gl-probe"))
        {
            try { SkiaWindow.Probe(); Environment.Exit(0); }
            catch { Environment.Exit(1); }
        }

        var doc = app.CreateDocument();
        configure?.Invoke(doc);

        // The dev tools (DevTools/CupriDevTools): hot reload of the markup and stylesheet, the doctor
        // on every save, and the F12 panel docked beside the app. A Debug build of the APP only.
        // EVERY use below sits behind `CupriDevTools.IsSupported` itself — not a local copy of it —
        // because that is the test the trimmer folds, and how a Release publish drops all of it.
        HotReloadSession? reload = null;
        DevToolsPanel? devtools = null;
        if (CupriDevTools.IsSupported)
        {
            reload = new HotReloadSession(app, doc);
            // The threaded render path rasterises the app on another thread with no place to draw a
            // panel, so it gets hot reload and the doctor but no F12.
            if (!app.ThreadedRender)
                devtools = new DevToolsPanel(doc, reload, app.DevToolsShortcut)
                {
                    CanDetach = true,   // DevToolsWindow, beside the app
                    // CUPRIFACE_DEVTOOLS=open starts with the panel docked, =popout in its own window:
                    // for a session that is about the inspector, and for driving it from a script
                    // with no key to press.
                    IsOpen = Environment.GetEnvironmentVariable("CUPRIFACE_DEVTOOLS") is "open" or "popout",
                    Detached = Environment.GetEnvironmentVariable("CUPRIFACE_DEVTOOLS") is "popout",
                };
            Console.WriteLine(devtools is null
                ? "[CupriFace] Dev tools: no panel under ThreadedRender; hot reload is on."
                : app.DevToolsShortcut is { } chord
                ? $"[CupriFace] Dev tools: {chord} opens the inspector (CupriApp.DevToolsShortcut)."
                : "[CupriFace] Dev tools: no shortcut (CupriApp.DevToolsShortcut is null); hot reload is on.");
        }
        var devFrames = 0;              // the first doctor run waits for a laid-out frame
        var appClientW = 0f;            // where the app ends and the panel starts, logical client units
        var clientW = 0f;
        var devtoolsWasOpen = false;
        var panelPressed = false;       // a press that began in the panel keeps its drag there
        var pointerInPanel = false;
        DevToolsWindow? popout = null;  // the panel popped out (DevToolsPanel.Detached)
        var popoutDirty = false;
        // External links (http/mailto/…) open in the OS browser; internal routing + #anchors are the
        // app's / engine's concern. Both hosts do this, so links behave the same on desktop and web.
        doc.Navigated += e => { if (e.External) OpenExternal(e.Href); };
        // Decode the app icon once (any size PNG/JPEG → RGBA8888); both window kinds take raw pixels.
        (byte[] Rgba, int W, int H)? icon = null;
        if (app.Icon is { Length: > 0 } iconBytes)
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(iconBytes);
            if (bmp is not null)
            {
                using var rgba = bmp.Copy(SkiaSharp.SKColorType.Rgba8888);
                if (rgba is not null) icon = (rgba.Bytes, rgba.Width, rgba.Height);
            }
        }

        var clock = Stopwatch.StartNew();

        // Every window beside the main one — the dev tools popped out, and windows the app opens
        // through doc.Windows — and the one place their SDL events are routed. Disposed in each
        // window branch's finally, BEFORE that window: the SDL window's Dispose calls SDL_Quit.
        var childWindows = new SdlChildWindows();
        doc.Windows = new DesktopAppWindows(doc, childWindows, OpenExternal, clock);
        // The three scales, kept apart (#137). `scale` is P — the APPLICATION's factor, and the only
        // one pointer coordinates need, because both windows now hand this host logical client units
        // already. `effective` is T = D*P — the one the canvas, the surfaces, the damage rectangle
        // and the accessibility geometry all use. `deviceScale` reads D off whichever window opened.
        var scale = 1f;
        var effective = 1f;
        Func<float> deviceScale = () => 1f;
        var logicalW = 0f; var logicalH = 0f; // last presented logical size, for the a11y snapshot
        var lastRefresh = 0.0;
        var lastAnimFrame = double.NaN;   // NaN = nothing animated yet, so the first frame is due
        HostSurfaceCoordinator? hostSurfaces = null;

        // Render-on-demand (same model as the WASM host): input marks the doc dirty only when a
        // dispatch actually changed something; animation/refresh/image arrival wake it too. A static
        // window renders nothing at all.
        var dirty = true;
        void Mark(bool changed) { if (changed) dirty = true; }
        bool NeedsRender()
        {
            if (CupriDevTools.IsSupported)
            {
                // A file save or a C# hot reload asked for a reload: run it HERE, between frames.
                if (reload!.Pump()) dirty = true;
                if (++devFrames == 2) reload.Request();   // the first doctor run, on a laid-out document
                if (devtools is not null && devtools.ConsumeDirty()) { dirty = true; popoutDirty = true; }
            }
            // Periodic re-bind so live computed values (e.g. diagnostics) update on their own.
            if (app.RefreshIntervalSeconds > 0 &&
                clock.Elapsed.TotalSeconds - lastRefresh >= app.RefreshIntervalSeconds)
            {
                lastRefresh = clock.Elapsed.TotalSeconds;
                doc.Refresh();
                dirty = true;
            }
            if (doc.ConsumeImageArrived()) dirty = true;      // a background image finished loading
            // Continuous animation, at the app's ceiling (CupriApp.MaxFrameRateFor, default 120 on
            // every host). This host had NO ceiling before and the web hosts had a hard-coded 30 fps;
            // one shared knob replaces both. Only the animation DRIVE is throttled — input has
            // already set dirty above and paints on this frame regardless.
            //
            // The profile describes the monitor as of the LAST frame, which is what FrameScale holds
            // at this point in the loop. A ceiling is not worth a frame of latency to get exactly
            // right, and on the frame a window changes monitor the stale answer is used once.
            if (doc.HasActiveAnimations &&
                FrameCeiling.Due(clock.Elapsed.TotalSeconds, lastAnimFrame,
                    app.MaxFrameRateFor(new DeviceProfile(HostKind.Desktop, FrameScale.DeviceScale,
                        FrameScale.LogicalClientWidth, FrameScale.LogicalClientHeight))))
            {
                lastAnimFrame = clock.Elapsed.TotalSeconds;
                dirty = true;
            }
            var d = dirty;
            dirty = false;
            return d;
        }

        void Draw(RenderContext ctx)
        {
            // The framebuffer is PHYSICAL pixels; the application is asked about the LOGICAL window
            // it occupies. Handing an app framebuffer pixels was the bug: on a 150% monitor it laid
            // out as though it had half again as much room, so everything came out physically small
            // and did not keep its size when the window moved to another display (#137).
            var host = HostScale.ForFramebuffer(ctx.Width, ctx.Height, deviceScale());
            // With the dev tools docked, the app is laid out in what the panel leaves — the way a
            // browser's dev tools narrow the page, so its breakpoints can be watched firing.
            var p = app.Present(AppClientWidth(host.LogicalClientWidth), host.LogicalClientHeight);
            scale = p.Scale <= 0 ? 1f : p.Scale;
            var resolved = host.WithPresentScale(scale);
            effective = resolved.EffectiveScale;
            FrameScale = resolved;                      // diagnostics; see the property
            logicalW = p.LogicalWidth; logicalH = p.LogicalHeight;
            // Tell surfaces what a logical pixel is worth before asking any of them to draw. A
            // producer that rasterises to order — a GL viewport — sizes its buffer from this, and
            // without it renders at logical resolution and is upscaled into its box. It is T rather
            // than P: the monitor's contribution is exactly as real as the application's.
            doc.Surfaces.DeviceScale = effective;

            // GPU surface producers go FIRST, before a single command is recorded for this frame.
            // They issue raw GL on the same context Skia is about to use, so doing it mid-recording
            // would corrupt the state Skia believes the driver is in; the registry calls
            // ResetContext afterwards. A software window passes null here and producers fall back
            // to publishing ordinary SKImages. Present() above only computes — it records nothing —
            // so it is safely on this side of that line.
            if (ctx.Gpu is { } gpu) doc.Surfaces.RenderGpuFrames(gpu);

            // Transparent apps clear to a fully-transparent framebuffer so the desktop shows through
            // (premultiplied output is exactly what the OS compositor wants — no conversion needed).
            ctx.Canvas.Clear(app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background);
            if (doc.HasAnimations || doc.HasActiveTransitions)
                doc.Animate(clock.Elapsed.TotalSeconds); // drive @keyframes (spinner) + CSS transitions

            ctx.Canvas.Save();
            if (effective != 1f) ctx.Canvas.Scale(effective);
            doc.Render(ctx.Canvas, p.LogicalWidth, p.LogicalHeight);
            if (CupriDevTools.IsSupported && devtools is not null) devtools.PaintHighlight(ctx.Canvas);
            ctx.Canvas.Restore();
            if (CupriDevTools.IsSupported && devtools is not null && devtools.IsOpen && !devtools.Detached)
            {
                // The panel is chrome, not app: it takes the monitor's scale and none of the app's.
                ctx.Canvas.Save();
                ctx.Canvas.Scale(resolved.DeviceScale);
                ctx.Canvas.Translate(appClientW, 0);
                devtools.Render(ctx.Canvas, clientW - appClientW, host.LogicalClientHeight);
                ctx.Canvas.Restore();
            }
            hostSurfaces?.Sync(effective);
        }

        // The width the app is laid out in, given the window's — all of it, unless the dev tools
        // panel is docked. Also records the split, which is what pointer routing reads.
        float AppClientWidth(float windowW)
        {
            clientW = windowW;
            appClientW = windowW;
            if (CupriDevTools.IsSupported && devtools is not null && devtools.IsOpen && !devtools.Detached)
                appClientW = MathF.Max(160f, windowW - devtools.Width);
            return appClientW;
        }

        bool InPanel(float x) =>
            CupriDevTools.IsSupported && devtools is not null && devtools.IsOpen && !devtools.Detached && x >= appClientW;

        // The popped-out panel, kept in step with the panel's state once per tick of whichever window
        // the app got: opened beside the app when it is wanted, closed when it is not, and redrawn when
        // the panel changed. Its events and its drawing are the child-window hub's, like any other.
        void SyncPopout((int X, int Y) at)
        {
            if (!CupriDevTools.IsSupported || devtools is null) return;
            var want = devtools.IsOpen && devtools.Detached;
            if (want && popout is not { IsOpen: true })
            {
                if (popout is null)
                {
                    popout = new DevToolsWindow(devtools, childWindows, () => dirty = true);
                    // Its own close button closes the dev tools, as a browser's undocked ones do.
                    // Still popped out, so the next F12 opens the window again.
                    popout.CloseRequested += () => { devtools.IsOpen = false; dirty = true; };
                }
                var d = FrameScale.DeviceScale;
                if (!popout.Open(at.X, at.Y, at.X + (int)(clientW * d), (int)FrameScale.LogicalClientHeight))
                    devtools.Detached = false;   // could not open one: dock instead
                popoutDirty = true;
                dirty = true;                    // the app gets its full width back
            }
            else if (!want && popout is { IsOpen: true })
            {
                popout.Close();
                dirty = true;
            }
            if (popout is { IsOpen: true } && popoutDirty)
            {
                popoutDirty = false;
                popout.Invalidate();
            }
        }

        // Before the window it rides on is disposed: the SDL window's Dispose calls SDL_Quit, which
        // would leave this one holding a destroyed window.
        void DisposePopout()
        {
            if (!CupriDevTools.IsSupported || popout is null) return;
            popout.Dispose();
            popout = null;
        }

        // Pointer input for BOTH windows, routed: the dev tools panel and its element picker first
        // (Debug builds), then the app. x/y are the window's logical client units; the app's own
        // coordinates divide out its present scale, as they always have.
        bool PointerDownAt(float x, float y, int clicks)
        {
            if (CupriDevTools.IsSupported && devtools is not null && devtools.IsOpen)
            {
                if (InPanel(x)) { panelPressed = true; return devtools.PanelPointerDown(x - appClientW, y, clicks); }
                if (devtools.AppPointerDown(x / scale, y / scale)) return true;
            }
            return DocumentInput.PointerDown(doc, x / scale, y / scale, clicks);
        }

        bool PointerMoveAt(float x, float y)
        {
            if (CupriDevTools.IsSupported && devtools is not null && devtools.IsOpen)
            {
                if (panelPressed || InPanel(x))
                {
                    pointerInPanel = true;
                    return devtools.PanelPointerMove(x - appClientW, y);
                }
                if (pointerInPanel) { pointerInPanel = false; devtools.PanelPointerLeave(); }
                if (devtools.AppPointerMove(x / scale, y / scale)) return true;
            }
            return DocumentInput.PointerMove(doc, x / scale, y / scale);
        }

        bool PointerUpAt(float x, float y)
        {
            if (CupriDevTools.IsSupported && devtools is not null)
            {
                if (panelPressed) { panelPressed = false; return devtools.PanelPointerUp(x - appClientW, y); }
                if (devtools.AppPointerUp(x / scale, y / scale)) return true;
            }
            return DocumentInput.PointerUp(doc, x / scale, y / scale);
        }

        Style.CursorType CursorFor(float x, float y)
        {
            if (CupriDevTools.IsSupported && devtools is not null && devtools.IsOpen)
            {
                if (panelPressed || InPanel(x)) return devtools.PanelCursor(x - appClientW, y);
                if (devtools.AppCursor is { } pick) return pick;
            }
            return doc.CursorAt(x / scale, y / scale);
        }

        // The dev tools see a key before the app does — the shortcut, Escape while picking, and the
        // arrows while the panel has focus. True when they took it.
        bool DevToolsKey(EditKey k, KeyMods mods)
        {
            if (!CupriDevTools.IsSupported || devtools is null) return false;
            // A key in the APP's window while the panel is popped out is not the panel's — its own
            // window has the keyboard when it is the panel's turn. Only the shortcut applies here.
            if (devtools.Detached) devtools.PanelHasFocus = false;
            if (!devtools.HandleKey(k, mods)) return false;
            dirty = true;
            return true;
        }

        // Typed text and Ctrl/Cmd chords, while the docked panel has the keyboard: the HTML editor's,
        // or the element tree's (copy / cut / paste an element, undo an edit, save).
        bool DevToolsText(string t)
        {
            if (!CupriDevTools.IsSupported || devtools is null || devtools.Detached || !devtools.PanelText(t)) return false;
            dirty = true;
            return true;
        }

        bool DevToolsShortcut(char ch, KeyMods mods, Func<string?> read, Action<string> write)
        {
            if (!CupriDevTools.IsSupported || devtools is null || devtools.Detached) return false;
            return devtools.PanelShortcut(ch, mods, read, write);
        }

        // Escape hatch: CUPRIFACE_SOFTWARE=1 skips the GL attempt entirely and goes straight to
        // the SDL software window, which renders the same pixels a little slower. The GL path's
        // known failure modes are handled these days (a broken GL stack raises an ordinary
        // exception and falls through to SDL below) — but an explicit override beats debugging.
        var forceSoftware = preferSoftware || Environment.GetEnvironmentVariable("CUPRIFACE_SOFTWARE") is "1" or "true" or "TRUE";
        // Why, when it is forced — for doc.Renderer, which is where a slow window explains itself.
        string? forcedWhy = preferSoftware ? "the app asked for software rendering"
            : forceSoftware ? "software rendering was forced with CUPRIFACE_SOFTWARE" : null;
        // The SDL window with a real GL context (#143): touch and GPU rendering together. Opt-in
        // for now — making touchscreen machines pick it automatically is a policy decision, and
        // CUPRIFACE_SOFTWARE stays the kill switch above it, so software always wins a tie.
        var sdlGl = !forceSoftware && Environment.GetEnvironmentVariable("CUPRIFACE_SDL_GL") is "1" or "true" or "TRUE";

        // macOS with no OpenGL at all (the paravirtual GPU of virtualised Macs — CI runners, UTM
        // guests) kills the process NATIVELY inside GLFW before any managed guard can run: window
        // creation fails without setting a GLFW error, Silk.NET applies the default position to
        // the NULL handle, and release-build GLFW segfaults in glfwSetWindowPos (the macOS CI
        // crash report named that exact frame, window argument = 0). Uncatchable in-process — so
        // a throwaway child process takes the risk first. Real Macs (GL present) pay one
        // invisible ~200 ms probe at startup and then get the GPU window as before.
        if (!forceSoftware && OperatingSystem.IsMacOS() && !GlProbeSurvives())
        {
            Console.WriteLine("[CupriFace] GL probe failed (no OpenGL here); using the SDL software window.");
            forceSoftware = true;
            forcedWhy = "this Mac has no OpenGL (the GL probe process did not survive)";
        }

        try
        {
            if (forceSoftware || layeredGpu || sdlGl)
                throw new InvalidOperationException("Software rendering requested; skipping the GL window.");

            var window = new SkiaWindow(
                app.Title,
                app.Width,
                app.Height,
                app.Transparent,
                app.Frameless,
                app.TopMost,
                app.DarkWindowChrome,
                app.Background,
                app.DpiAware,
                app.TrackMonitorDpi);
            if (icon is { } ic) window.SetIcon(ic.Rgba, ic.W, ic.H);
            using var nativeSurfaces = new HostSurfaceCoordinator(
                doc, new DesktopHostSurfaceContext(() => window.Win32Hwnd, () => dirty = true));
            hostSurfaces = nativeSurfaces;
            deviceScale = () => window.DeviceScale;
            // A monitor change resizes every raster-backed surface and invalidates the retained
            // frame: what was cached was rasterised for the old scale and is the wrong pixel count
            // for the new one.
            window.DeviceScaleChanged += _ => { doc.InvalidateRetainedFrame(); dirty = true; };
            window.ShouldRender = NeedsRender; // GL: skip draw + swap entirely on clean frames
            // GLFW has nothing to do with SDL, so a second window's events are the hub's to read.
            childWindows.OwnsEvents = true;
            if (CupriDevTools.IsSupported) window.Tick += () => SyncPopout(window.ScreenPosition);
            window.Tick += childWindows.Pump;
            doc.FileDialogs = DesktopFileDialogs.Create(() => window.Win32Hwnd, () => window.CocoaWindow, doc.Post);
            // What it draws with, once the context exists (it is made inside Run). Reported on
            // the first tick rather than here, which is before the window has opened.
            var rendererReported = false;
            window.Tick += () =>
            {
                if (rendererReported) return;
                rendererReported = true;
                doc.ReportRenderer(new RendererInfo(RendererKind.Gpu,
                    SkiaWindow.GlRenderer is { Length: > 0 } r ? $"OpenGL — {r}" : "OpenGL"));
            };
            window.Render += Draw;

            // Accessibility: whichever bridge this OS has (UIA on Windows, AT-SPI on Linux),
            // attached on the first tick that can (Windows needs the HWND), draining queued AT
            // actions on this UI thread, and publishing a semantics snapshot after each drawn
            // frame — the subscription order after Draw is what sequences that. No-ops on a
            // platform without a bridge, under its kill switch, or if attaching failed.
            using var monitorMaximize = new WindowsMonitorMaximize();
            window.Tick += () => monitorMaximize.Attach(window.Win32Hwnd);
            using var a11y = new Accessibility.PlatformAccessibility(doc, () => dirty = true, app.Title);
            window.Tick += () => { if (a11y.Tick(() => OperatingSystem.IsMacOS() ? window.CocoaWindow : window.Win32Hwnd)) dirty = true; };
            // T, not P: an AT is told where things are in PHYSICAL screen pixels, so the monitor's
            // scale belongs in the same multiply the canvas used. Publishing P alone put every
            // bounding rectangle at two-thirds size on a 150% display.
            window.Render += _ => a11y.Publish(logicalW, logicalH, effective, window.ScreenPosition);
            using var tray = new WindowsTrayIcon(app.CloseToTray, app.Title, app.TrayCloseLabel);
            var topMost = app.TopMost;
            window.Tick += () =>
            {
                if (app.TopMost != topMost)
                {
                    topMost = app.TopMost;
                    window.SetTopMost(topMost);
                }
                tray.Attach(window.Win32Hwnd);
            };

            var middleMouseCaptured = false;
            window.PointerDown += (x, y, clicks) =>
            {
                Mark(PointerDownAt(x, y, clicks));
                window.SetCursor(CursorFor(x, y));
            };
            window.RightPointerDown += (x, y) => { if (!InPanel(x)) Mark(doc.DispatchContextMenu(x / scale, y / scale)); };
            window.MiddlePointerDown += (x, y) =>
            {
                if (!InPanel(x))
                    Mark(middleMouseCaptured = doc.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Down, x / scale, y / scale));
            };
            window.PointerMove += (x, y) =>
            {
                Mark(PointerMoveAt(x, y));
                if (middleMouseCaptured) Mark(doc.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Move, x / scale, y / scale));
                window.SetCursor(CursorFor(x, y));
            };
            window.PointerUp += (x, y) =>
            {
                Mark(PointerUpAt(x, y));
                window.SetCursor(CursorFor(x, y));
            };
            window.MiddlePointerUp += (x, y) =>
            {
                if (middleMouseCaptured) Mark(doc.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Up, x / scale, y / scale));
                middleMouseCaptured = false;
            };
            window.PointerWheel += (x, y, dy, mods) =>
            {
                if (InPanel(x)) { Mark(devtools!.PanelWheel(x - appClientW, y, -dy * 50f)); return; }
                // Ctrl+wheel is zoom, a plain wheel scrolls — DocumentInput.Wheel, as in every window.
                Mark(DocumentInput.Wheel(doc, x / scale, y / scale, dy, mods));
            };
            // A drop from the OS. The window already converted to logical client units and found
            // the pointer; dividing out the app's own present scale is this host's usual last step,
            // the same one every pointer event above takes.
            window.FilesDropped += (x, y, paths) =>
                Mark(!InPanel(x) && doc.DispatchFileDrop(x / scale, y / scale,
                    Array.ConvertAll(paths, DroppedFile.FromPath)));
            window.TextEntered += t =>
            {
                if (DevToolsText(t)) return;
                Mark(doc.DispatchKey(t, EditKey.None));
            };
            window.EditKeyPressed += (k, mods) =>
            {
                if (DevToolsKey(k, mods)) return;
                var handled = window.IsFullscreen && doc.DispatchFullscreenVideoPlaybackShortcut(k, mods);
                if (!handled) handled = doc.DispatchKey(null, k, mods);
                Mark(handled);
                // Escape the document didn't consume (no overlay open) exits fullscreen — the OS
                // convention. Overlays keep winning: dismissing one returns handled above.
                if (!handled && k == EditKey.Escape && window.IsFullscreen) SyncFullscreen(doc, window, false);
            };
            // Releases, so directional navigation can tell "pressed together" from "pressed in turn"
            // by what is still DOWN rather than by how fast they arrived. Forgetting them on focus
            // loss matters just as much: the key-up for anything held when the window loses focus is
            // delivered to whoever gains it, so without this the key stays held here for ever.
            doc.ReportsKeyUp = true;   // this host forwards releases; see DiagonalNavigation
            window.EditKeyReleased += k => Mark(doc.DispatchKeyUp(k));
            window.FocusLost += doc.ReleaseAllKeys;
            // The controller. doc.Gamepad rather than a driver of this host's own: a driver holds
            // which direction is currently held, so two of them turn one push into two moves — which
            // is exactly what an application with its own evdev reader had to work around by
            // switching one path off. Shared, the duplicate collapses by itself.
            //
            // These go through the document's own pad entry points rather than DispatchKey, and the
            // reason is not tidiness: they apply HostGamepadInput (an app may take the sticks and
            // leave the D-pad) and mark the event as a PAD event, which is what stops
            // KeyboardNavigation applying to a controller. Four hosts deriving that themselves is
            // four chances to disagree about it.
            window.GamepadStick += (x, y) => Mark(doc.DispatchGamepadStick(x, y));
            window.GamepadKey += (k, down) => Mark(doc.DispatchGamepadKey(k, down));
            window.GamepadConnected += doc.ReportGamepadConnected;
            window.GamepadDisconnected += doc.ReportGamepadDisconnected;
            // The ENGINE's verdict, into the same file the window writes what it RECEIVED into.
            // Side by side they answer different questions, and the pair is what makes a controller
            // bring-up readable: a window line with no engine line after it means the event never
            // reached the document, which until now looked exactly like one the document ignored.
            if (KeyDiag.Enabled) doc.InputObserved += o => KeyDiag.Log("engine " + o);
            window.Shortcut += (ch, mods) =>
            {
                if (!DevToolsShortcut(ch, mods, () => window.ClipboardText, v => window.ClipboardText = v))
                    DocumentInput.Shortcut(doc, ch, mods, () => window.ClipboardText, v => window.ClipboardText = v);
                dirty = true;
            };
            doc.ContextRequested += cmd => { DocumentInput.ContextAction(doc, cmd, () => window.ClipboardText, v => window.ClipboardText = v); dirty = true; };
            // A copy button (data-cupri-copy) supplies its own text rather than copying a selection.
            doc.ClipboardWriteRequested += v => window.ClipboardText = v;
            doc.WindowCommandRequested += cmd => SyncFullscreen(doc, window, cmd switch
            {
                WindowCommand.EnterFullscreen => true,
                WindowCommand.ExitFullscreen => false,
                _ => !window.IsFullscreen,
            });
            // A frameless window has no title bar to grab, so an element marked data-window-drag
            // becomes one. Wired on BOTH window types — a host feature added to only one of them is
            // how the GL and SDL paths have drifted before.
            doc.WindowMoveRequested += m => window.MoveBy(m.Dx, m.Dy);
            Action<string> clipboardWriter = value => window.ClipboardText = value;
            app.ClipboardWriteRequested += clipboardWriter;
            if (CupriDevTools.IsSupported && devtools is not null) devtools.ClipboardWriteRequested += clipboardWriter;
            try { window.Run(); }
            finally
            {
                hostSurfaces = null;
                app.ClipboardWriteRequested -= clipboardWriter;
                if (CupriDevTools.IsSupported && devtools is not null) devtools.ClipboardWriteRequested -= clipboardWriter;
                DisposePopout();
                // A GPU failure hands over to the software window below, and the app's other
                // windows go on living there; any other way out is the end of the run.
                if (window.GpuFailure is null) childWindows.Dispose();
            }

            // The GPU failed while the window was open: it has closed itself, and the software
            // window below takes over with the same document — state, scroll and focus intact.
            if (window.GpuFailure is { } lost)
            {
                // Producers drawing on the window's context move to their software lane.
                doc.Surfaces.GpuContextLost();
                window.Dispose();
                throw new GpuLostException(lost);
            }
        }
        catch (Exception ex)
        {
            // The message, not just the type: a bare "InvalidOperationException" here was read as a
            // driverless machine when it was a harness forcing the software path, and a bare
            // "PlatformNotSupportedException" as a session limit when it was the trimmer removing
            // Silk.NET's backends (#125, #126). The line is the only witness a fallback leaves.
            Console.WriteLine(sdlGl
                ? "[CupriFace] SDL window with a GL context requested (CUPRIFACE_SDL_GL=1): GPU rendering with touch."
                : layeredGpu && !forceSoftware
                ? "[CupriFace] Off-screen GPU rendering requested; using Windows layered presentation."
                : forceSoftware
                ? "[CupriFace] Software rendering requested; using the SDL software window."
                : ex is GpuLostException
                ? "[CupriFace] Opening the SDL software window after the GPU failure above."
                : $"[CupriFace] GPU unavailable ({ex.GetType().Name}: {ex.Message}); using the SDL software window.");
            using var window = new SdlSoftwareWindow(
                app.Title,
                app.Width,
                app.Height,
                app.Transparent,
                app.Frameless,
                app.TopMost,
                app.DarkWindowChrome,
                app.Background,
                app.DpiAware,
                app.TrackMonitorDpi);
            window.UseLayeredGpu = layeredGpu && !forceSoftware;
            window.UseGl = sdlGl;
            if (icon is { } ic) window.SetIcon(ic.Rgba, ic.W, ic.H);
            using var nativeSurfaces = new HostSurfaceCoordinator(
                doc, new DesktopHostSurfaceContext(() => window.Win32Hwnd, () => dirty = true));
            hostSurfaces = nativeSurfaces;
            deviceScale = () => window.DeviceScale;

            // The retained surface was recreated (blank): the doc's damage diff must restart from
            // a full repaint, and the frame must actually render even if nothing else is dirty.
            // A monitor change goes through here too — PollDeviceScale recreates the surface at the
            // new pixel size, so the same invalidation covers both.
            window.SurfaceRecreated += () => { doc.InvalidateRetainedFrame(); dirty = true; };
            // This window drains SDL's one queue; the other windows' events are handed to the hub.
            childWindows.OwnsEvents = false;
            window.ForeignWindowEvent = childWindows.Handle;
            if (CupriDevTools.IsSupported) window.Tick += () => SyncPopout(window.ScreenPosition);
            window.Tick += childWindows.Pump;
            doc.FileDialogs = DesktopFileDialogs.Create(() => window.Win32Hwnd, () => window.CocoaWindow, doc.Post);

            // What it draws with, and why it is not the GL window when it is not. The window knows
            // what it set up; only this host knows why it is here at all.
            var whyNotGlWindow = sdlGl || (layeredGpu && !forceSoftware) ? null
                : forceSoftware ? forcedWhy
                : ex is GpuLostException
                ? $"the GPU stopped working while the GL window was open: {ex.Message}"
                : $"the GL window could not be opened ({ex.GetType().Name}: {ex.Message})";
            var softwareReported = false;
            window.Tick += () =>
            {
                if (softwareReported) return;
                softwareReported = true;
                var info = window.Renderer;
                if (info.Kind == RendererKind.Cpu && info.Reason is null && whyNotGlWindow is not null)
                    info = info with { Reason = whyNotGlWindow };
                doc.ReportRenderer(info);
            };

            // The same bridge on the software window — this is the path GL-less machines (RDP,
            // VMs, CI runners, and every headless Linux box) actually take, so assistive tech
            // must work here, not only on GL.
            using var monitorMaximize = new WindowsMonitorMaximize();
            window.Tick += () => monitorMaximize.Attach(window.Win32Hwnd);
            using var a11y = new Accessibility.PlatformAccessibility(doc, () => dirty = true, app.Title);
            window.Tick += () => { if (a11y.Tick(() => OperatingSystem.IsMacOS() ? window.CocoaWindow : window.Win32Hwnd)) dirty = true; };
            using var tray = new WindowsTrayIcon(app.CloseToTray, app.Title, app.TrayCloseLabel);
            var topMost = app.TopMost;
            window.Tick += () =>
            {
                if (app.TopMost != topMost)
                {
                    topMost = app.TopMost;
                    window.SetTopMost(topMost);
                }
                tray.Attach(window.Win32Hwnd);
            };

            // Commit-snapshot render thread (opt-in): build the display list on this UI thread and let
            // a background thread rasterise it; present the latest completed frame each vsync. Targets
            // the physical surface (scale 1), so it composes with the responsive present.
            // ThreadedRender rasterises on a background thread into the CPU bitmap; the layered GPU
            // path draws on the GL context and reads back on this thread, so the two cannot both own
            // the frame. Saying so beats dropping an opt-in silently — an ignored setting is
            // indistinguishable from a broken one, which is how #137's ThreadedRender bug survived.
            if (app.ThreadedRender && (window.UseLayeredGpu || window.UseGl))
                Console.WriteLine("[CupriFace] ThreadedRender is ignored under layered GPU presentation; "
                    + "drawing stays on the UI thread.");
            using var presenter = app.ThreadedRender && !window.UseLayeredGpu && !window.UseGl ? new CupriFace.Threading.ThreadedPresenter() : null;
            void DrawThreaded(RenderContext ctx)
            {
                presenter!.Present(ctx.Canvas); // draw the previous frame the render thread finished
                if (CupriDevTools.IsSupported)
                {
                    reload!.Pump();
                    if (++devFrames == 2) reload.Request();
                }
                if (app.RefreshIntervalSeconds > 0 && clock.Elapsed.TotalSeconds - lastRefresh >= app.RefreshIntervalSeconds)
                { lastRefresh = clock.Elapsed.TotalSeconds; doc.Refresh(); }
                if (doc.HasAnimations || doc.HasActiveTransitions) doc.Animate(clock.Elapsed.TotalSeconds);

                // This path used never to call app.Present at all — so it ignored Hybrid and Zoom as
                // well as DPI, and published accessibility geometry at a hard-coded scale 1. It now
                // computes exactly what the inline paths do; only the rasterisation is elsewhere.
                var host = HostScale.ForFramebuffer(ctx.Width, ctx.Height, deviceScale());
                var p = app.Present(host.LogicalClientWidth, host.LogicalClientHeight);
                scale = p.Scale <= 0 ? 1f : p.Scale;
                var resolved = host.WithPresentScale(scale);
                effective = resolved.EffectiveScale;
                FrameScale = resolved;
                doc.Surfaces.DeviceScale = effective;

                // The list is built in LOGICAL units and the render thread scales it into the device
                // surface, so the glyphs are rasterised at the size they are shown at.
                var list = doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
                hostSurfaces?.Sync(effective);
                presenter.Submit(list, ctx.Width, ctx.Height,
                                 app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background, effective);
                a11y.Publish(p.LogicalWidth, p.LogicalHeight, effective, window.ScreenPosition);
            }

            if (window.UseLayeredGpu || window.UseGl)
            {
                // Reuse the GL host's draw contract, including same-context GPU surface producers.
                // The retained GPU surface and bitmap make expose-only frames free of readback.
                window.RenderIncrementalFrame = ctx =>
                {
                    if (!NeedsRender()) return null;
                    Draw(ctx);
                    a11y.Publish(logicalW, logicalH, effective, window.ScreenPosition);
                    return new SkiaSharp.SKRectI(0, 0, ctx.Width, ctx.Height);
                };
            }
            else if (presenter is not null)
                window.Render += DrawThreaded; // threaded path keeps its own pipeline (no damage/skip)
            else
            {
                // Damage-aware render-on-demand: repaint only the changed rect of the retained bitmap;
                // a clean frame renders, uploads, and presents nothing.
                window.RenderIncrementalFrame = ctx =>
                {
                    if (!NeedsRender()) return null;
                    // The dev tools panel: while it is docked (and on the frame it closes) draw the
                    // whole window the way the GL path does. A damage rectangle is the app's, and
                    // knows nothing of a panel beside it.
                    if (CupriDevTools.IsSupported && devtools is not null && (devtools.IsOpen || devtoolsWasOpen))
                    {
                        devtoolsWasOpen = devtools.IsOpen;
                        Draw(ctx);
                        doc.InvalidateRetainedFrame();
                        a11y.Publish(logicalW, logicalH, effective, window.ScreenPosition);
                        return new SkiaSharp.SKRectI(0, 0, ctx.Width, ctx.Height);
                    }
                    // Same split as the GL path: the app is asked about its LOGICAL window, and the
                    // canvas, the damage rect and the a11y geometry all use T = D*P (#137).
                    var host = HostScale.ForFramebuffer(ctx.Width, ctx.Height, deviceScale());
                    var p = app.Present(host.LogicalClientWidth, host.LogicalClientHeight);
                    scale = p.Scale <= 0 ? 1f : p.Scale;
                    var resolved = host.WithPresentScale(scale);
                    effective = resolved.EffectiveScale;
                    FrameScale = resolved;
                    doc.Surfaces.DeviceScale = effective;
                    if (doc.HasAnimations || doc.HasActiveTransitions) doc.Animate(clock.Elapsed.TotalSeconds);
                    var bg = app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background;

                    // Scale the canvas, then damage-clip inside it: the engine's clip is interpreted
                    // in the scaled space (= logical space), so only the rectangle it returns needs
                    // converting to device pixels. Previously any scale but 1 repainted in full,
                    // which on a HiDPI or fractionally-scaled display is every frame (#99).
                    ctx.Canvas.Save();
                    if (effective != 1f) ctx.Canvas.Scale(effective);
                    var logical = doc.RenderIncremental(ctx.Canvas, p.LogicalWidth, p.LogicalHeight, bg);
                    ctx.Canvas.Restore();
                    hostSurfaces?.Sync(effective);
                    SkiaSharp.SKRectI? damage = logical is { } lg
                        ? CupriDocument.ScaleDamageToDevice(lg, effective, ctx.Width, ctx.Height)
                        : null;
                    // A drawn frame is the moment the tree is laid out and current — publish then.
                    if (damage is not null) a11y.Publish(p.LogicalWidth, p.LogicalHeight, effective, window.ScreenPosition);
                    return damage;
                };
            }
            var middleMouseCaptured = false;
            window.PointerDown += (x, y, clicks) =>
            {
                Mark(PointerDownAt(x, y, clicks));
                window.SetCursor(CursorFor(x, y));
            };
            window.RightPointerDown += (x, y) => { if (!InPanel(x)) Mark(doc.DispatchContextMenu(x / scale, y / scale)); };
            window.MiddlePointerDown += (x, y) =>
            {
                if (!InPanel(x))
                    Mark(middleMouseCaptured = doc.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Down, x / scale, y / scale));
            };
            // Touch (#143). Only the SDL window can carry this: GLFW exposes no touch API at
            // all, which is why a tap reaches nothing on a Wayland desktop today — X11 emulates a
            // core pointer from touch and Wayland does not, so one build looks fine in a desktop
            // session and is inert in Game Mode.
            window.TouchPointer += (pointerId, phase, x, y) =>
                Mark(!InPanel(x) && DocumentInput.Finger(doc, pointerId, phase, x / scale, y / scale));
            window.PointerMove += (x, y) =>
            {
                Mark(PointerMoveAt(x, y));
                if (middleMouseCaptured) Mark(doc.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Move, x / scale, y / scale));
                window.SetCursor(CursorFor(x, y));
            };
            window.PointerUp += (x, y) =>
            {
                Mark(PointerUpAt(x, y));
                window.SetCursor(CursorFor(x, y));
            };
            window.MiddlePointerUp += (x, y) =>
            {
                if (middleMouseCaptured) Mark(doc.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Up, x / scale, y / scale));
                middleMouseCaptured = false;
            };
            window.PointerWheel += (x, y, dy, mods) =>
            {
                if (InPanel(x)) { Mark(devtools!.PanelWheel(x - appClientW, y, -dy * 50f)); return; }
                // Ctrl+wheel is zoom, a plain wheel scrolls — DocumentInput.Wheel, as in every window.
                Mark(DocumentInput.Wheel(doc, x / scale, y / scale, dy, mods));
            };
            // A drop from the OS. The window already converted to logical client units and found
            // the pointer; dividing out the app's own present scale is this host's usual last step,
            // the same one every pointer event above takes.
            window.FilesDropped += (x, y, paths) =>
                Mark(!InPanel(x) && doc.DispatchFileDrop(x / scale, y / scale,
                    Array.ConvertAll(paths, DroppedFile.FromPath)));
            window.TextEntered += t =>
            {
                if (DevToolsText(t)) return;
                Mark(doc.DispatchKey(t, EditKey.None));
            };
            window.EditKeyPressed += (k, mods) =>
            {
                if (DevToolsKey(k, mods)) return;
                var handled = window.IsFullscreen && doc.DispatchFullscreenVideoPlaybackShortcut(k, mods);
                if (!handled) handled = doc.DispatchKey(null, k, mods);
                Mark(handled);
                if (!handled && k == EditKey.Escape && window.IsFullscreen) SyncFullscreen(doc, window, false);
            };
            // Releases, so directional navigation can tell "pressed together" from "pressed in turn"
            // by what is still DOWN rather than by how fast they arrived. Forgetting them on focus
            // loss matters just as much: the key-up for anything held when the window loses focus is
            // delivered to whoever gains it, so without this the key stays held here for ever.
            doc.ReportsKeyUp = true;   // this host forwards releases; see DiagonalNavigation
            window.EditKeyReleased += k => Mark(doc.DispatchKeyUp(k));
            window.FocusLost += doc.ReleaseAllKeys;
            // The controller. doc.Gamepad rather than a driver of this host's own: a driver holds
            // which direction is currently held, so two of them turn one push into two moves — which
            // is exactly what an application with its own evdev reader had to work around by
            // switching one path off. Shared, the duplicate collapses by itself.
            //
            // These go through the document's own pad entry points rather than DispatchKey, and the
            // reason is not tidiness: they apply HostGamepadInput (an app may take the sticks and
            // leave the D-pad) and mark the event as a PAD event, which is what stops
            // KeyboardNavigation applying to a controller. Four hosts deriving that themselves is
            // four chances to disagree about it.
            window.GamepadStick += (x, y) => Mark(doc.DispatchGamepadStick(x, y));
            window.GamepadKey += (k, down) => Mark(doc.DispatchGamepadKey(k, down));
            window.GamepadConnected += doc.ReportGamepadConnected;
            window.GamepadDisconnected += doc.ReportGamepadDisconnected;
            // The ENGINE's verdict, into the same file the window writes what it RECEIVED into.
            // Side by side they answer different questions, and the pair is what makes a controller
            // bring-up readable: a window line with no engine line after it means the event never
            // reached the document, which until now looked exactly like one the document ignored.
            if (KeyDiag.Enabled) doc.InputObserved += o => KeyDiag.Log("engine " + o);
            window.Shortcut += (ch, mods) =>
            {
                if (!DevToolsShortcut(ch, mods, () => window.ClipboardText, v => window.ClipboardText = v))
                    DocumentInput.Shortcut(doc, ch, mods, () => window.ClipboardText, v => window.ClipboardText = v);
                dirty = true;
            };
            doc.ContextRequested += cmd => { DocumentInput.ContextAction(doc, cmd, () => window.ClipboardText, v => window.ClipboardText = v); dirty = true; };
            // A copy button (data-cupri-copy) supplies its own text rather than copying a selection.
            doc.ClipboardWriteRequested += v => window.ClipboardText = v;
            doc.WindowCommandRequested += cmd => SyncFullscreen(doc, window, cmd switch
            {
                WindowCommand.EnterFullscreen => true,
                WindowCommand.ExitFullscreen => false,
                _ => !window.IsFullscreen,
            });
            // A frameless window has no title bar to grab, so an element marked data-window-drag
            // becomes one. Wired on BOTH window types — a host feature added to only one of them is
            // how the GL and SDL paths have drifted before.
            doc.WindowMoveRequested += m => window.MoveBy(m.Dx, m.Dy);
            Action<string> clipboardWriter = value => window.ClipboardText = value;
            app.ClipboardWriteRequested += clipboardWriter;
            if (CupriDevTools.IsSupported && devtools is not null) devtools.ClipboardWriteRequested += clipboardWriter;
            try { window.Run(); }
            finally
            {
                hostSurfaces = null;
                app.ClipboardWriteRequested -= clipboardWriter;
                if (CupriDevTools.IsSupported && devtools is not null) devtools.ClipboardWriteRequested -= clipboardWriter;
                DisposePopout();
                childWindows.Dispose();
            }
        }
    }

    /// <summary>
    /// Change fullscreen and tell the DOCUMENT what actually happened.
    ///
    /// <para>The engine element-fullscreens a video and asks the host to follow; the host owns the
    /// window and can refuse, or can be taken out of fullscreen by a route the engine never sees —
    /// Escape, the window manager, the title bar. Only the host knows the answer, so it reports the
    /// state it ended in rather than the state it was asked for. Without that the two drift apart
    /// and you get a window still fullscreen around a video that has gone back into the layout, or
    /// the reverse (#304).</para>
    ///
    /// <para>Reporting the OBSERVED state also makes this idempotent: asking for a state the window
    /// is already in changes nothing and confirms the same answer.</para>
    /// </summary>
    private static void SyncFullscreen(CupriDocument doc, SkiaWindow window, bool on)
    {
        window.SetFullscreen(on);
        doc.NotifyHostFullscreen(window.IsFullscreen);
    }

    /// <inheritdoc cref="SyncFullscreen(CupriDocument, SkiaWindow, bool)"/>
    private static void SyncFullscreen(CupriDocument doc, SdlSoftwareWindow window, bool on)
    {
        window.SetFullscreen(on);
        doc.NotifyHostFullscreen(window.IsFullscreen);
    }

    // Launch ourselves with --cupriface-gl-probe and read the verdict off the exit code: 0 means the
    // child brought GL up end to end; anything else — a managed throw, a native SIGSEGV, a hang —
    // means this machine doesn't get the GL window. Any doubt (no process path, spawn failure,
    // timeout) counts as failure: the fallback is a working window either way.
    private static bool GlProbeSurvives()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;
            // Under `dotnet run` / `dotnet Viewer.dll` the process path is the dotnet host, which
            // would swallow the flag — re-exec the entry assembly through it. Published apps
            // (apphost or single-file) re-exec themselves.
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            var viaHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                          && entry is { Length: > 0 };
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (viaHost) { psi.ArgumentList.Add("exec"); psi.ArgumentList.Add(entry!); }
            psi.ArgumentList.Add("--cupriface-gl-probe");

            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // Open an allowed external link in the OS default browser/handler. Re-check at the host boundary
    // so no future call site can hand file:, javascript:, or a custom executable protocol to the shell.
    private static void OpenExternal(string href)
    {
        if (!ExternalLinkPolicy.IsAllowed(href)) return;
        try { Process.Start(new ProcessStartInfo(href) { UseShellExecute = true }); }
        catch { /* no handler / malformed url — ignore */ }
    }
}
