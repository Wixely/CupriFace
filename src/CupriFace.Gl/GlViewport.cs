using System.Diagnostics;
using CupriFace.Dom;
using CupriFace.Gl.Internal;
using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Gl;

/// <summary>
/// An OpenGL viewport bound to one CupriFace element. Give it something that draws with GL and it
/// runs on a desktop, on a phone and in a browser.
///
/// <code>
/// // in the markup, anywhere:  &lt;div data-cupri-surface="scene" data-cupri-image="poster.png"&gt;&lt;/div&gt;
/// var viewport = GlViewport.Attach(doc, "scene", new MyScene());
/// </code>
///
/// <para><b>What it does for you</b> is the list of things that are different on each host and wrong
/// in ways that are hard to see: it acquires a context (the host's own where there is one), sizes the
/// render target to the element's box in DEVICE pixels and follows it through resizes, resets the
/// driver state that Skia leaves behind, hands the engine a texture rather than a copy of one where
/// that is possible, and — in a browser, where none of that is possible — asks the host for a real
/// <c>&lt;canvas&gt;</c> underneath and lets the engine punch a hole down to it.</para>
///
/// <para><b>What it does not do is render anything.</b> There is no scene, no camera, no material and
/// no model loader in this package. <see cref="IGlContent"/> is where an app's drawing goes, and the
/// only host-shaped thing it has to know about is <see cref="GlContext.ShaderHeader"/>.</para>
///
/// <para><b>Degrading is the normal path, not the error path.</b> A machine with no usable GL, a
/// software window, a browser whose WebGL2 was refused — each of these leaves the element showing its
/// <c>data-cupri-image</c> poster while <see cref="State"/> says <see cref="GlViewportState.Unavailable"/>
/// and <see cref="Diagnostic"/> says why. Nothing throws, and no host goes down because a viewport
/// could not start.</para>
/// </summary>
public sealed unsafe class GlViewport : IGpuSurfaceSource, IDisposable
{
    // The host sets SurfaceRegistry.HasGpuFrameHook during its FIRST DRAW, which happens after the
    // first Ticking poll — so "no hook yet" means nothing at all for a frame or two. Counting polls
    // rather than deciding immediately is what stops a GPU host being misread as a software one and
    // needlessly spinning up a private context. Ten frames is a sixth of a second and is not tuned:
    // anything above two would do, and this leaves room for a host that paints lazily.
    private const int GpuHookGrace = 10;

    private readonly CupriDocument _doc;
    private readonly SurfaceRegistry _registry;
    private readonly string _key;
    private readonly IGlContent _content;
    private readonly GlViewportOptions _opt;
    private readonly bool _web = OperatingSystem.IsBrowser();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private GlViewport(CupriDocument doc, string key, IGlContent content, GlViewportOptions opt)
    {
        _doc = doc;
        _registry = doc.Surfaces;
        _key = key;
        _content = content;
        _opt = opt;
        _naturalSize = opt.Size ?? (256, 256);
    }

    /// <summary>
    /// Bind a viewport to the element carrying <c>data-cupri-surface="<paramref name="surfaceKey"/>"</c>.
    ///
    /// <para>Returns immediately and never throws. No GL happens here: the context is acquired the
    /// first time the element is actually on screen, which in a tabbed app may be much later or
    /// never. Call <see cref="Dispose"/> when the viewport is finished with.</para>
    /// </summary>
    public static GlViewport Attach(CupriDocument doc, string surfaceKey, IGlContent content,
                                    GlViewportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(surfaceKey);
        ArgumentNullException.ThrowIfNull(content);

        var viewport = new GlViewport(doc, surfaceKey, content, options ?? new GlViewportOptions());
        doc.Surfaces.Register(surfaceKey, viewport);
        return viewport;
    }

    // ---- what the app can ask ---------------------------------------------------------------

    /// <summary>What the viewport is doing. See <see cref="GlViewportState"/> — the distinction that
    /// matters is <see cref="GlViewportState.Unavailable"/> (no GL here, carry on) against
    /// <see cref="GlViewportState.Failed"/> (GL was here and something in it broke).</summary>
    public GlViewportState State => _state;
    private volatile GlViewportState _state = GlViewportState.Waiting;

    /// <summary>Why, in the driver's own words where there are any. Null while nothing has gone
    /// wrong. Safe to show a user, and worth logging: it names the GPU.</summary>
    public string? Diagnostic { get; private set; }

    /// <summary>How this viewport's pixels reach the screen, once decided.</summary>
    public GlLane Lane { get; private set; } = GlLane.None;

    /// <summary>The live context, or null before one was acquired. Carries the driver strings and the
    /// shader header; do not call GL on it outside <see cref="IGlContent"/>.</summary>
    public GlContext? Context { get; private set; }

    /// <summary>Frames drawn.</summary>
    public long Frames => Interlocked.Read(ref _frames);
    private long _frames;

    /// <summary>The last size actually rendered, in device pixels — what the element's box worked out
    /// to, not what was asked for.</summary>
    public (int W, int H) RenderSize => (_lastW, _lastH);
    private volatile int _lastW, _lastH;

    // ---- ISurfaceSource -----------------------------------------------------------------------

    /// <summary>The latest frame. Always null on the browser lane, where the engine punches a hole
    /// instead of drawing anything.</summary>
    public SKImage? CurrentFrame => _frame;
    private volatile SKImage? _frame;
    private SKImage? _retired;

    /// <summary>Intrinsic size for layout, and deliberately CONSTANT. It would be tempting to report
    /// the live render size here; that would feed a box's own layout back into itself through the
    /// intrinsic-sizing path, which is a loop rather than a refinement.</summary>
    public (int W, int H)? NaturalSize => _naturalSize;
    private readonly (int W, int H) _naturalSize;

    /// <summary>True in a browser. The wasm hosts rasterise on the CPU and have no GPU context to
    /// share, so the only way to get real GL on a page is a canvas underneath the engine's output
    /// with a transparent hole punched down to it.</summary>
    public bool HostComposited => _web;

    /// <summary>Asks the browser host to create <c>#cupri-underlay-{key}</c> and keep it glued to the
    /// element's box through scrolling, clipping and transforms. Null elsewhere: the painted lanes
    /// have no underlay.</summary>
    public string? UnderlayElement => _web ? "canvas" : null;

    /// <summary>
    /// Producing frames — and answered honestly, which matters more here than it looks.
    ///
    /// <para>The registry folds this into the document's "something is animating" signal, so a
    /// permanent <c>true</c> means a render-on-demand host NEVER IDLES. That failure is close to
    /// invisible: the paint count stays flat because there is no damage, while the host spins a
    /// frame loop for ever behind a section nobody is looking at. On a phone it is a battery bug; in
    /// a browser it was first noticed as tabbing no longer reaching text fields.</para>
    ///
    /// <para>So the gate is <see cref="RenderNode.LaidOut"/> — is our element actually on screen —
    /// and NOT "did the painter ask about us". The display list is rebuilt every tick to compute
    /// damage, so the painter consults surfaces inside <c>display:none</c> sections too, and gating
    /// on that ticks for ever.</para>
    /// </summary>
    public bool Ticking
    {
        get
        {
            if (_disposed) return false;
            // A teardown that needs the host's context can only happen inside RenderOnGpu, so keep
            // asking for frames until it has run — see Dispose.
            if (_teardownPending) return true;
            if (_state is GlViewportState.Unavailable or GlViewportState.Failed) return false;

            // Everything that reads the document tree happens HERE, on the host's thread, and is
            // published to the other lanes as plain numbers.
            //
            // The private-context lane runs on its own thread, and a RenderNode walk from there is a
            // race against the host rebuilding the display list — which it does every tick to
            // compute damage. The symptom would be an intermittent "collection was modified" inside
            // a GL render loop, caught by the loop's own handler and reported as a viewport failure:
            // rare, unreproducible, and blaming the wrong thing entirely.
            var onScreen = FindNode() is { LaidOut: true };
            _onScreen = onScreen;
            if (onScreen && ElementSize() is { } size) { _wantW = size.W; _wantH = size.H; }

            // Driver strings become knowable only after a context comes up, and a page showing them
            // needs the document rebuilt to bind what was just learned. Deferred to here rather than
            // done at the point of discovery, because that point may be the private lane's thread
            // and Refresh is not the host's to call from there.
            if (_needsRefresh) { _needsRefresh = false; _doc.Refresh(); }

            if (!onScreen) return false;

            if (_web) { RenderWeb(); return true; }

            // Painted lanes. Prefer the host's own GPU context; fall back only once it is clear that
            // no hook is coming.
            if (Lane == GlLane.OffscreenReadback || _registry.HasGpuFrameHook) return true;
            if (++_polls > GpuHookGrace) BeginOffscreenLane();
            return _state is not (GlViewportState.Unavailable or GlViewportState.Failed);
        }
    }

    private volatile bool _onScreen;
    // The element's device size, measured on the host's thread and published for the lanes that
    // cannot safely walk the tree themselves. Zero until the first on-screen poll.
    private volatile int _wantW, _wantH;
    private volatile bool _needsRefresh;
    private int _polls;
    private volatile bool _disposed;
    private volatile bool _teardownPending;

    private RenderNode? FindNode() => Find(_doc.Root, _key);

    private static RenderNode? Find(RenderNode node, string key)
    {
        if (node.SurfaceKey == key) return node;
        foreach (var child in node.Children)
            if (Find(child, key) is { } found) return found;
        return null;
    }

    /// <summary>
    /// The size to render at, in device pixels: the element's laid-out box times the host's scale.
    ///
    /// <para>This is item 2 of the scoping document. The sample rendered a fixed 512×512 into
    /// whatever box the CSS gave it, which on a 3× phone is a third-resolution image upscaled into
    /// the panel — visible as softness with nothing in the markup to explain it.</para>
    /// </summary>
    internal (int W, int H)? ElementSize()
    {
        if (_opt.Size is { } fixedSize) return fixedSize;
        if (FindNode() is not { LaidOut: true } node) return null;

        var scale = _registry.DeviceScale;
        var w = (int)MathF.Ceiling(node.Width * scale);
        var h = (int)MathF.Ceiling(node.Height * scale);
        if (w <= 0 || h <= 0) return null;
        return (Math.Clamp(w, _opt.MinPixels, _opt.MaxPixels),
                Math.Clamp(h, _opt.MinPixels, _opt.MaxPixels));
    }

    // ---- the shared-GPU lane (desktop GL window, Android) -------------------------------------

    private readonly GlTarget _target = new();
    private int _gpuThreadId;

    /// <summary>
    /// Draw on the HOST'S context and publish a texture-backed image — no readback, no re-upload, no
    /// second context and no second thread.
    ///
    /// <para>Called by <c>SurfaceRegistry.RenderGpuFrames</c> with the context current and before
    /// anything is recorded for the frame. The registry calls <c>GRContext.ResetContext</c> after
    /// every producer, which is what makes issuing raw GL on Skia's own context safe; this method
    /// handles the other direction, putting the driver into a known state before the app's drawing
    /// code sees it.</para>
    /// </summary>
    public void RenderOnGpu(GRContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _gpuThreadId = Environment.CurrentManagedThreadId;

        if (_teardownPending) { TeardownGpu(); return; }
        if (_disposed || _web) return;
        if (Lane == GlLane.OffscreenReadback) return;      // committed to the other lane already
        if (_state is GlViewportState.Unavailable or GlViewportState.Failed) return;
        if (!_onScreen) return;

        if (Context is null && !AcquireHostContext()) return;
        var gl = Context!;
        var fn = gl.Fn;

        var size = _opt.Size ?? (_wantW > 0 ? (_wantW, _wantH) : _naturalSize);
        if (!_target.EnsureSize(fn, size.W, size.H, out var error)) { Fail(error!); return; }

        fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, _target.Fbo);
        DrawOneFrame(gl, size.W, size.H);
        fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);

        // An SKImage over a texture is a handle, not a copy, so it is made fresh each frame and the
        // PREVIOUS one retired after the swap — never free an image the paint path may still hold.
        var info = new GRGlTextureInfo(GlFunctions.TEXTURE_2D, _target.Texture, GlFunctions.RGBA8);
        using var backend = new GRBackendTexture(size.W, size.H, false, info);
        // BottomLeft: GL's first row is the bottom of the image and Skia's is the top. Saying so
        // costs nothing; flipping on the CPU is what the readback lane has to do instead.
        var image = SKImage.FromTexture(context, backend, GRSurfaceOrigin.BottomLeft,
                                        SKColorType.Rgba8888, SKAlphaType.Premul);
        if (image is null) return;

        var previous = _frame;
        _frame = image;
        previous?.Dispose();
        Interlocked.Increment(ref _frames);
    }

    private bool AcquireHostContext()
    {
        var proc = HostGl.ForHostContext(out var why);
        if (proc is null) { Unavailable(why ?? "no GL loader for this platform"); return false; }

        var fn = GlFunctions.Load(proc, out var missing);
        if (fn is null)
        {
            Unavailable($"{missing.Count} GL entry points missing: {string.Join(", ", missing)}");
            return false;
        }

        Lane = GlLane.SharedGpu;
        return StartContent(fn, proc, GlLane.SharedGpu);
    }

    /// <summary>Build the <see cref="GlContext"/>, tell the content to initialise, and record what
    /// the driver says it is. Shared by all three lanes, because all three need exactly this.</summary>
    private bool StartContent(GlFunctions fn, Func<string, nint> proc, GlLane lane)
    {
        var version = fn.Str(GlFunctions.VERSION);
        // Ask the driver rather than guess from the platform. "OpenGL ES 3.2 v1.r26" and
        // "WebGL 2.0 (OpenGL ES 3.0 Chromium)" both want #version 300 es; a desktop's "4.6.0 NVIDIA"
        // wants #version 330 core. Deriving it from the string is right even in the cases platform
        // detection would also get right, and it is right in the ones it would not — an ES context
        // on a desktop, or a desktop GL context inside an emulator.
        var dialect = version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase)
                   || version.Contains("WebGL", StringComparison.OrdinalIgnoreCase)
            ? GlDialect.GlEs300 : GlDialect.Gl330Core;

        var gl = new GlContext(dialect, lane, proc, fn)
        {
            Version = version,
            Renderer = fn.Str(GlFunctions.RENDERER),
            Vendor = fn.Str(GlFunctions.VENDOR),
        };

        bool ok;
        try { ok = _content.Initialise(gl); }
        catch (Exception ex) { Fail($"{ex.GetType().Name} initialising content: {ex.Message}"); return false; }
        if (!ok) { Fail("the content declined to initialise"); return false; }

        Context = gl;
        _contentStarted = true;
        _state = GlViewportState.Running;
        Log($"{lane} lane up on {gl.Renderer} — {gl.Version} ({dialect})");
        // Requested rather than performed: this runs on whichever thread owns the context, and for
        // the private-context lane that is not the host's. Ticking picks it up next frame.
        _needsRefresh = true;
        return true;
    }

    private bool _contentStarted;

    /// <summary>Reset, size, clear, draw. The four things every lane does identically, in the order
    /// that makes <see cref="IGlContent"/>'s promises true.</summary>
    private void DrawOneFrame(GlContext gl, int w, int h)
    {
        var fn = gl.Fn;
        if (_opt.ResetState) fn.ResetState();
        fn.Viewport(0, 0, w, h);
        var (r, g, b, a) = _opt.ClearColor;
        fn.ClearColor(r, g, b, a);
        fn.Clear(GlFunctions.COLOR_BUFFER_BIT | GlFunctions.DEPTH_BUFFER_BIT);

        _lastW = w; _lastH = h;
        var frame = new GlFrame(w, h, _clock.Elapsed.TotalSeconds, Frames);
        // A bad frame from the app's drawing code must not take the host down, and must not stop the
        // viewport: the previous frame stays on screen and the next one is tried.
        try { _content.Render(gl, in frame); }
        catch (Exception ex) { Log($"content threw during Render: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ---- the browser lane ---------------------------------------------------------------------

    private nint _webContext;
    private int _waitedForCanvas;

    private string UnderlayTarget => "#cupri-underlay-" + _key;

    private void RenderWeb()
    {
        if (_state is GlViewportState.Unavailable or GlViewportState.Failed) return;

        if (_webContext == 0)
        {
            // Wait, indefinitely and without complaint, for the host to create the underlay. It is
            // never there on the first poll (the host creates it AFTER a painted frame, and this runs
            // DURING one), and in a tabbed app it does not exist until the section is opened — which
            // may be minutes away, or never. A retry budget is the wrong shape here: it expires while
            // the app sits on another tab and leaves the viewport permanently dead.
            var (cw, ch) = Emscripten.CanvasSize(UnderlayTarget);
            if (cw <= 0 || ch <= 0)
            {
                if (++_waitedForCanvas == 600)
                    Log($"the underlay canvas {UnderlayTarget} has not appeared while the element is on screen");
                return;
            }

            var ctx = Emscripten.CreateContext(UnderlayTarget, alpha: true, antialias: _opt.Antialias);
            if (ctx <= 0) { Unavailable("the browser refused a WebGL2 context"); return; }
            if (!Emscripten.MakeCurrent(ctx)) { Unavailable("the WebGL2 context would not become current"); return; }

            var fn = GlFunctions.Load(Emscripten.ProcAddress, out var missing);
            if (fn is null)
            {
                Unavailable($"{missing.Count} GL entry points missing: {string.Join(", ", missing)}");
                return;
            }

            // Assert the version rather than trust the request. emscripten_webgl_create_context
            // DOWNGRADES a version-2 ask to WebGL1 instead of refusing it when the build was not
            // linked with -sMAX_WEBGL_VERSION=2, and the first symptom is otherwise a compile error
            // blaming "#version 300 es" — a diagnosis three steps from the cause. CupriFace.Web.NativeAot
            // sets that flag, so this fires mainly for a hand-rolled host.
            var version = fn.Str(GlFunctions.VERSION);
            if (!version.Contains("WebGL 2", StringComparison.OrdinalIgnoreCase))
            {
                Unavailable($"downgraded to WebGL1 ({version}) — the build needs -sMAX_WEBGL_VERSION=2");
                return;
            }

            // Paint the canvas NOW, before anything slow. The hole is already open and everything
            // below — shader compiles, texture decodes and uploads — can take a good half-second,
            // during which a never-drawn canvas is transparent and the bare page shows through it.
            var (r0, g0, b0, a0) = _opt.ClearColor;
            fn.ClearColor(r0, g0, b0, a0);
            fn.Clear(GlFunctions.COLOR_BUFFER_BIT | GlFunctions.DEPTH_BUFFER_BIT);

            _webContext = ctx;
            Lane = GlLane.HostComposited;
            if (!StartContent(fn, Emscripten.ProcAddress, GlLane.HostComposited)) return;
        }

        if (Context is null) return;
        Emscripten.MakeCurrent(_webContext);

        // The canvas's backing store is the truth here: the host sizes it from the element's DEVICE
        // rect, so there is nothing to compute and no scale to apply. Item 2 comes for free on this
        // lane and had to be built on the other two.
        var (w, h) = Emscripten.CanvasSize(UnderlayTarget);
        if (w <= 0 || h <= 0) return;
        DrawOneFrame(Context, w, h);
        Interlocked.Increment(ref _frames);
    }

    // ---- the private-context lane (software window, headless) ---------------------------------

    private Thread? _thread;
    private volatile bool _running = true;

    private void BeginOffscreenLane()
    {
        if (_opt.OffscreenContext is null)
        {
            Unavailable("this host shares no GPU context, and no OffscreenContext factory was supplied");
            return;
        }
        if (_thread is not null) return;

        Lane = GlLane.OffscreenReadback;
        _thread = new Thread(OffscreenLoop) { IsBackground = true, Name = $"cupri-gl-{_key}" };
        _thread.Start();
    }

    private void OffscreenLoop()
    {
        IGlOffscreenContext? ctx = null;
        GlFunctions? fn = null;
        try
        {
            ctx = _opt.OffscreenContext!();
            if (!ctx.MakeCurrent()) { Unavailable("the offscreen context would not become current"); return; }

            fn = GlFunctions.Load(ctx.GetProcAddress, out var missing);
            if (fn is null)
            {
                Unavailable($"{missing.Count} GL entry points missing: {string.Join(", ", missing)}");
                return;
            }
            if (!StartContent(fn, ctx.GetProcAddress, GlLane.OffscreenReadback)) return;

            var gl = Context!;
            byte[]? pixels = null;
            var frameMs = 1000.0 / Math.Clamp(_opt.MaxFramesPerSecond, 1, 240);

            while (_running && !_disposed)
            {
                // Park while off screen. Ticking going false stops the HOST repainting, which is not
                // the same as stopping this thread: without the park, a private context keeps a GPU
                // busy behind a section nobody is looking at.
                if (!_onScreen) { Thread.Sleep(100); continue; }

                var swept = Stopwatch.StartNew();
                // Published by Ticking on the host's thread — see the note there. Falls back to the
                // natural size only before the first on-screen poll has measured anything.
                var size = _opt.Size ?? (_wantW > 0 ? (_wantW, _wantH) : _naturalSize);
                if (!_target.EnsureSize(fn, size.W, size.H, out var error)) { Fail(error!); return; }

                fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, _target.Fbo);
                DrawOneFrame(gl, size.W, size.H);

                var bytes = size.W * size.H * 4;
                if (pixels is null || pixels.Length < bytes) pixels = new byte[bytes];
                fixed (byte* p = pixels) fn.ReadPixels(0, 0, size.W, size.H, GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, p);
                fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);

                var image = ToImage(pixels, size.W, size.H);
                if (image is not null)
                {
                    // Swap first, retire the PREVIOUS frame after: never free an image the paint path
                    // may still be reading.
                    var previous = _frame;
                    _frame = image;
                    _retired?.Dispose();
                    _retired = previous;
                    Interlocked.Increment(ref _frames);
                    _registry.NotifyFrame();
                }

                var rest = frameMs - swept.Elapsed.TotalMilliseconds;
                if (rest > 1) Thread.Sleep((int)rest);
            }
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The context is still current on this thread, which is the only moment any of this is
            // legal — hence doing it here rather than in Dispose.
            if (fn is not null)
            {
                if (_contentStarted && Context is not null)
                    try { _content.Shutdown(Context); } catch { /* teardown must not throw */ }
                _target.Delete(fn);
            }
            ctx?.Dispose();
        }
    }

    /// <summary>GL's first row is the bottom of the image and Skia's is the top, so the readback lane
    /// flips on the CPU. This is exactly the cost the shared-GPU lane does not pay: there the origin
    /// is declared and the sampler does it.</summary>
    private static SKImage? ToImage(byte[] pixels, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var dst = (byte*)bitmap.GetPixels();
        if (dst is null) return null;
        var row = w * 4;
        fixed (byte* src = pixels)
            for (var y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (h - 1 - y) * row, dst + y * row, row, row);
        return SKImage.FromBitmap(bitmap);
    }

    // ---- state reporting ----------------------------------------------------------------------

    private void Unavailable(string why)
    {
        Diagnostic = why;
        _state = GlViewportState.Unavailable;
        Log("unavailable: " + why);
    }

    private void Fail(string why)
    {
        Diagnostic = why;
        _state = GlViewportState.Failed;
        Log("failed: " + why);
    }

    private void Log(string message) => _opt.Log?.Invoke($"gl[{_key}]: {message}");

    // ---- teardown -------------------------------------------------------------------------------

    /// <summary>
    /// Stop the viewport and release its GL objects.
    ///
    /// <para>GL objects can only be deleted on the thread where their context is current, which for
    /// the shared lane is the host's render thread and not this one. So disposal from elsewhere ASKS
    /// for teardown and waits briefly for the next frame to perform it; disposal from the render
    /// thread itself does it inline. If no further frame arrives — a host already shutting down —
    /// the objects are left to die with the context, which is what happens to everything else on it.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _running = false;

        if (Lane == GlLane.SharedGpu && _contentStarted && !_web)
        {
            if (Environment.CurrentManagedThreadId == _gpuThreadId)
            {
                TeardownGpu();
            }
            else
            {
                _teardownPending = true;
                _registry.NotifyFrame();
                // Bounded: a host that will not paint again must not hold up an app's shutdown.
                _teardownDone.Wait(500);
                if (_teardownPending)
                    Log("no further frame arrived, so GL objects are left to the context's own teardown");
            }
        }

        _disposed = true;
        _teardownPending = false;
        _registry.Unregister(_key);
        _thread?.Join(2000);

        if (_web && _webContext != 0)
        {
            // The browser lane's context is on this (single) thread, so shutdown is legal here — but
            // only once it is actually current, since the page may have made another one current
            // since the last frame.
            Emscripten.MakeCurrent(_webContext);
            if (_contentStarted && Context is not null)
                try { _content.Shutdown(Context); } catch { /* teardown must not throw */ }
            Emscripten.DestroyContext(_webContext);
            _webContext = 0;
        }

        _frame?.Dispose();
        _retired?.Dispose();
        _frame = null;
        _retired = null;
        _teardownDone.Dispose();
    }

    private readonly ManualResetEventSlim _teardownDone = new(false);

    /// <summary>Release the shared lane's objects. Only legal on the host's render thread with its
    /// context current, which is why the only callers are <see cref="RenderOnGpu"/> and a
    /// <see cref="Dispose"/> that already knows it is on that thread.</summary>
    private void TeardownGpu()
    {
        _teardownPending = false;
        if (Context is { } gl)
        {
            if (_contentStarted)
                try { _content.Shutdown(gl); } catch { /* teardown must not throw */ }
            _target.Delete(gl.Fn);
        }
        _contentStarted = false;
        _teardownDone.Set();
    }
}
