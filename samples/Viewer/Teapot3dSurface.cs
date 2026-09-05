using System.Diagnostics;
using CupriFace.Demo.ThreeD;
using CupriFace.Dom;
using CupriFace.Paint;
using SkiaSharp;
using Silk.NET.Windowing;

namespace CupriFace.Samples.Viewer;

/// <summary>
/// The Showcase's 3D viewport on DESKTOP: a real glTF model rendered with OpenGL and published to
/// the engine as <see cref="ISurfaceSource"/> frames.
///
/// <para>This lives at the composition root, next to the video wiring and for the same reason — the
/// shared <c>ShowcaseApp</c> is compiled by the wasm and Android hosts too, and must not drag a
/// desktop GL stack into them. <c>ShowcaseApp</c> contributes only markup: an element with
/// <c>data-cupri-surface="showcase3d"</c>. A host that wires nothing shows the poster, which is the
/// engine's existing behaviour for a surface with no frames.</para>
///
/// <para><b>Desktop takes the PAINTED lane.</b> It owns a private GL context on a private thread and
/// hands the engine finished <see cref="SKImage"/>s, which the painter draws into the display list
/// like any other image — so the 3D is inside the frame, and ordinary UI composites over it with no
/// special handling. The web host cannot do this (no GPU context there at all) and takes the
/// host-composited lane instead. Same app, two strategies, chosen by what the host can do.</para>
///
/// <para><b>The cost, stated rather than hidden:</b> the frame goes GPU → CPU → GPU — glReadPixels
/// into an SKImage that Skia uploads again when it paints. Measured on this machine: draw ~0.13 ms,
/// readback ~0.54 ms, to-SKImage ~0.78 ms. Moving the frame costs about ten times rendering it. The
/// fix is a texture-backed SKImage over a context shared with the engine's, which needs the engine
/// to expose its <c>GRContext</c> — worth doing, and not needed for a demo.</para>
/// </summary>
internal sealed class Teapot3dSurface : IGpuSurfaceSource, CupriFace.Demo.IShowcase3dInfo, IDisposable
{
    private readonly int _w, _h;
    private readonly Gltf _model;
    private readonly Action<string> _log;
    private readonly SurfaceRegistry _registry;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile SKImage? _frame;
    private SKImage? _retired;

    /// <summary>Published so the page can report what actually happened rather than assert it.</summary>
    public volatile string Status = "starting";
    public string GlVersion = "(not started)";

    /// <summary>Vendor and renderer as the driver reports them, shown on the page.</summary>
    public string Detail { get; private set; } = "";
    public double LastDrawMs, LastReadbackMs, LastUploadMs;
    public long Frames;

    private Teapot3dSurface(Gltf model, SurfaceRegistry registry, int w, int h, Action<string> log)
    {
        _model = model; _registry = registry; _w = w; _h = h; _log = log;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "showcase-3d" };
        // NOT started here — see Ticking.
    }

    private int _started;

    /// <summary>
    /// Start the GL thread only once the ENGINE IS ALREADY RENDERING, which is what reading this
    /// property means: the registry's AnyTicking is polled from the host's frame loop, so by the
    /// time it is asked, the host window exists and its own GLFW init has finished.
    ///
    /// <para>That ordering is load-bearing, not tidiness. Starting in the constructor raced the
    /// host's window creation, and on Windows two concurrent <c>glfwInit</c> calls collide with
    /// "Failed to register window class: Class already exists". The loser is whoever comes second —
    /// and when that was the HOST, the whole app silently fell back to the SDL software window. A
    /// demo that quietly downgrades the application it is demonstrating is worse than a demo that
    /// does not run, and a fixed sleep would only have hidden the race. Deferring makes the order
    /// deterministic instead of likely.</para>
    /// </summary>
    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _thread.Start();
    }

    /// <summary>
    /// Wire the demo into a document, or leave it alone. Returns null when the model cannot be read,
    /// and never throws: a machine with no usable OpenGL must still run the Showcase — it simply
    /// shows the poster and a line of text saying no 3D was wired. Graceful degradation is not extra
    /// work here, it is the engine's existing behaviour for a surface with no frames.
    /// </summary>
    public static Teapot3dSurface? TryAttach(CupriDocument doc, Action<string>? log = null)
    {
        log ??= _ => { };
        try
        {
            var asm = typeof(Gltf).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("teapot.glb", StringComparison.OrdinalIgnoreCase));
            if (name is null) { log("3d: the teapot asset is not embedded"); return null; }
            using var s = asm.GetManifestResourceStream(name)!;
            var glb = new byte[s.Length];
            s.ReadExactly(glb);

            var surface = new Teapot3dSurface(Gltf.Load(glb), doc.Surfaces, 512, 512, log) { _doc = doc };
            doc.Surfaces.Register("showcase3d", surface);
            return surface;
        }
        catch (Exception ex)
        {
            // Never fatal. The Showcase is not a 3D app.
            log($"3d: not wired ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public SKImage? CurrentFrame => _frame;
    public (int W, int H)? NaturalSize => (_w, _h);
    /// <summary>
    /// Producing frames — but only while the viewport is actually on screen.
    ///
    /// <para>A bare <c>_running</c> is dishonest for a tabbed app: the registry folds this into the
    /// document's "something is animating" signal, so the host never idles and a GL thread keeps
    /// drawing a teapot nobody is looking at. The web half of this demo learned it the hard way — a
    /// permanently-true <c>Ticking</c> span the browser host's frame loop for ever, and the browser
    /// gate caught it as tabbing no longer reaching text fields.</para>
    ///
    /// <para><c>LaidOut</c> is the signal, not "did the painter ask about me": the display list is
    /// rebuilt every tick to compute damage, so the painter consults surfaces inside
    /// <c>display:none</c> sections too.</para>
    /// </summary>
    public bool Ticking
    {
        get
        {
            var onScreen = _doc is { } d && Find(d.Root) is { LaidOut: true };
            _onScreen = onScreen;          // the render loop parks itself when this goes false
            if (!onScreen) return false;

            // Prefer the host's GPU hook; only start the private context + readback thread if no
            // hook turns up. The host sets HasGpuFrameHook during its first Draw, which is AFTER
            // the first Ticking poll, so "false" means nothing for a frame or two - hence counting
            // rather than deciding immediately. A software window never sets it, and falls back.
            if (_gpuReady || _registry.HasGpuFrameHook) return true;
            if (++_polls > 10) EnsureStarted();
            return true;
        }
    }

    private volatile bool _onScreen;
    private CupriDocument? _doc;

    private static RenderNode? Find(RenderNode n)
    {
        if (n.SurfaceKey == "showcase3d") return n;
        foreach (var c in n.Children) if (Find(c) is { } found) return found;
        return null;
    }

    // ---- the zero-copy path (A2) ---------------------------------------------------------------

    private bool _gpuReady, _gpuFailed;
    private uint _gpuFbo, _gpuTex, _gpuDepth;
    private SceneRenderer? _gpuRenderer;
    private SKImage? _gpuFrame;
    private readonly System.Diagnostics.Stopwatch _gpuClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>CPU time to SUBMIT the frame's GL commands on the zero-copy path - not the time the
    /// GPU spends rendering it. Nothing here forces a sync, so the driver has queued the work and
    /// returned; the readback path's numbers look bigger partly because glReadPixels waits for that
    /// work to finish. The honest claim is about TRANSPORT, which this path does not do at all, not
    /// about rendering having become free.</summary>
    public double GpuSubmitMs;

    /// <summary>How many times Ticking has been asked. Used only to decide, once, that no GPU hook
    /// is coming: the host sets HasGpuFrameHook during its first Draw, which happens AFTER the first
    /// Ticking poll, so a couple of frames must pass before "false" means anything.</summary>
    private int _polls;

    // The host's GL context is current inside RenderOnGpu, but there is no Silk.NET window here to
    // ask for entry points, so they come from the platform loader directly. wglGetProcAddress
    // answers for extensions while the 1.1 core lives in opengl32 itself, which is why both are
    // consulted - the classic loader shape, and the reason a single lookup would half-work.
    [System.Runtime.InteropServices.DllImport("opengl32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint wglGetProcAddress(string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint LoadLibrary(string name);

    private static nint HostProc(string name)
    {
        var p = wglGetProcAddress(name);
        // wglGetProcAddress returns these sentinels rather than null for a name it does not know.
        if (p == 0 || p == 1 || p == 2 || p == 3 || p == -1)
            p = GetProcAddress(LoadLibrary("opengl32.dll"), name);
        return p;
    }

    /// <summary>
    /// Draw on the HOST'S context, and hand the engine a texture instead of pixels.
    ///
    /// <para>This is the whole of A2 from the sample's side: no readback, no row flip, no re-upload.
    /// The frame never leaves the GPU - SKImage.FromTexture wraps the same colour attachment the
    /// model was just rendered into, and Skia draws it directly.</para>
    ///
    /// <para>Nothing restores GL state here, on purpose: SurfaceRegistry calls
    /// GRContext.ResetContext() after every producer, which is what makes issuing raw GL on Skia's
    /// own context safe at all.</para>
    /// </summary>
    public unsafe void RenderOnGpu(GRContext context)
    {
        if (_gpuFailed) return;
        if (!_gpuReady)
        {
            if (!OperatingSystem.IsWindows()) { _gpuFailed = true; return; }   // the loader above is Win32
            Gl.Load(HostProc);
            if (Gl.Missing.Count > 0)
            {
                _gpuFailed = true;
                _log($"3d: zero-copy path unavailable ({Gl.Missing.Count} GL entry points missing)");
                return;
            }

            _gpuRenderer = new SceneRenderer(_model, glslEs: false);
            if (!_gpuRenderer.Initialise(DecodeWithSkia, m => _log("3d: " + m))) { _gpuFailed = true; return; }

            uint fbo, tex, depth;
            Gl.GenFramebuffers(1, &fbo); Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
            Gl.GenTextures(1, &tex); Gl.BindTexture(Gl.TEXTURE_2D, tex);
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, _w, _h, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MIN_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MAG_FILTER, Gl.LINEAR);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
            Gl.GenRenderbuffers(1, &depth); Gl.BindRenderbuffer(Gl.RENDERBUFFER, depth);
            Gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.DEPTH_COMPONENT24, _w, _h);
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, depth);
            if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
            {
                _gpuFailed = true; _log("3d: zero-copy framebuffer incomplete"); return;
            }
            _gpuFbo = fbo; _gpuTex = tex; _gpuDepth = depth;
            _gpuReady = true;
            GlVersion = Gl.Str(Gl.GetString(Gl.VERSION));
            Detail = $"{Gl.Str(Gl.GetString(Gl.RENDERER))} — {GlVersion}";
            // The page binds this once, and it is only knowable after the first GPU frame —
            // so ask for a rebind, or the "Drawn by" row stays hidden until something else
            // happens to rebuild the document. Safe here: producers run before anything is
            // recorded for the frame.
            _doc?.Refresh();
            Status = "painted, zero-copy (the engine draws our texture)";
            _log($"3d: zero-copy path up on the host context, GL_VERSION = {GlVersion}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gpuFbo);
        _gpuRenderer!.Draw(0.6f + (float)_gpuClock.Elapsed.TotalSeconds * 0.6f, _w, _h, 0f, 0f, 0f, 0f);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        GpuSubmitMs = sw.Elapsed.TotalMilliseconds;

        // Wrapped fresh each frame: an SKImage over a texture is a handle, not a copy. The previous
        // handle is retired after the swap, the same discipline the readback path keeps.
        var info = new GRGlTextureInfo(Gl.TEXTURE_2D, _gpuTex, Gl.RGBA8);
        using var backend = new GRBackendTexture(_w, _h, false, info);
        var img = SKImage.FromTexture(context, backend, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        if (img is null) return;
        var previous = _gpuFrame;
        _gpuFrame = img;
        _frame = img;
        previous?.Dispose();
        Frames++;

        // Reported once, warm, so the two transports can be compared rather than asserted. There is
        // no readback or upload line here because there is no readback or upload.
        if (Frames == 200)
            _log($"3d: zero-copy - submit {GpuSubmitMs * 1000:0} us, transport NONE " +
                 "(the engine draws our texture; GPU render time is not measured here, nothing syncs)");
    }

    private unsafe void RenderLoop()
    {
        IWindow? window = null;
        try
        {
            // A hidden 1x1 window purely to own a GL context: GLFW has no portable headless context,
            // and the real rendering goes to an FBO, so the window's own buffer is never used.
            //
            // Retried because window-system init can still be transiently busy even after the
            // deferral above (the host may create or recreate a window while this runs). A failure
            // here is never fatal — the page shows its poster and says no 3D was wired.
            var options = WindowOptions.Default with
            {
                Size = new(1, 1),
                IsVisible = false,
                Title = "showcase-3d",
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            };
            for (var attempt = 1; ; attempt++)
            {
                try { window = Window.Create(options); window.Initialize(); break; }
                catch (Exception ex) when (attempt < 5)
                {
                    _log($"3d: window attempt {attempt} failed ({ex.GetType().Name}), retrying");
                    window?.Dispose(); window = null;
                    Thread.Sleep(250 * attempt);
                }
            }
            window!.MakeCurrent();

            Gl.Load(n => window.GLContext!.TryGetProcAddress(n, out var p) ? p : 0);
            if (Gl.Missing.Count > 0)
            {
                Status = $"no 3D: {Gl.Missing.Count} GL entry points missing";
                _log($"3d: {Status}");
                return;
            }
            GlVersion = Gl.Str(Gl.GetString(Gl.VERSION));
            Detail = $"{Gl.Str(Gl.GetString(Gl.RENDERER))} — {GlVersion}";
            _log($"3d: offscreen context up, GL_VERSION = {GlVersion}");

            // glslEs: false — desktop GL, so the shader header is "#version 330 core". The web and
            // Android hosts pass true and get "#version 300 es" from the same source.
            var renderer = new SceneRenderer(_model, glslEs: false);
            if (!renderer.Initialise(DecodeWithSkia, m => _log("3d: " + m)))
            {
                Status = "no 3D: renderer init failed";
                return;
            }

            uint fbo, colour, depth;
            Gl.GenFramebuffers(1, &fbo); Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
            Gl.GenTextures(1, &colour); Gl.BindTexture(Gl.TEXTURE_2D, colour);
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, _w, _h, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MIN_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MAG_FILTER, Gl.LINEAR);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, colour, 0);
            Gl.GenRenderbuffers(1, &depth); Gl.BindRenderbuffer(Gl.RENDERBUFFER, depth);
            Gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.DEPTH_COMPONENT24, _w, _h);
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, depth);

            var status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            if (status != Gl.FRAMEBUFFER_COMPLETE)
            {
                Status = $"no 3D: framebuffer incomplete 0x{status:X}";
                _log($"3d: {Status}");
                return;
            }
            Status = "painted (desktop GL → SKImage)";

            var pixels = new byte[_w * _h * 4];
            var info = new SKImageInfo(_w, _h, SKColorType.Rgba8888, SKAlphaType.Premul);
            var clock = Stopwatch.StartNew();
            var sw = new Stopwatch();

            while (_running)
            {
                var angle = 0.6f + (float)clock.Elapsed.TotalSeconds * 0.6f;

                sw.Restart();
                Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
                // Transparent clear: the engine composites this over the page, so the model sits on
                // whatever CSS put behind it rather than on a plate of its own.
                renderer.Draw(angle, _w, _h, 0f, 0f, 0f, 0f);
                var err = Gl.GetError();
                if (err != 0) { Status = $"no 3D: gl error 0x{err:X}"; _log($"3d: {Status}"); return; }
                LastDrawMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                fixed (byte* p = pixels) Gl.ReadPixels(0, 0, _w, _h, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
                LastReadbackMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                SKImage img;
                // GL's first row is the BOTTOM of the image; Skia's is the top. Flipping on the CPU
                // is honest but is exactly the cost a shared-texture path removes — it would flip in
                // the sampler instead.
                fixed (byte* p = pixels)
                {
                    using var bmp = new SKBitmap(info);
                    var dst = (byte*)bmp.GetPixels();
                    var row = _w * 4;
                    for (var y = 0; y < _h; y++)
                        Buffer.MemoryCopy(p + (_h - 1 - y) * row, dst + y * row, row, row);
                    img = SKImage.FromBitmap(bmp);
                }
                LastUploadMs = sw.Elapsed.TotalMilliseconds;

                // Swap first, retire the previous frame after — never free an image the paint path
                // may still be reading. The same discipline LottiePlayer keeps.
                var previous = _frame;
                _frame = img;
                _retired?.Dispose();
                _retired = previous;
                Frames++;

                if (Frames == 200)
                    _log($"3d: readback - draw {LastDrawMs:0.00} ms, glReadPixels {LastReadbackMs:0.00} ms, " +
                         $"to-SKImage {LastUploadMs:0.00} ms (transport {LastReadbackMs + LastUploadMs:0.00} ms)");

                _registry.NotifyFrame();
                Thread.Sleep(16);       // ~60fps; a demo, not a frame pacer

                // Park while the viewport is off screen. Without this the GPU work continues at full
                // rate behind a section nobody is looking at — Ticking going false stops the HOST
                // repainting, which is not the same as stopping the RENDERER.
                while (_running && !_onScreen) Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            Status = $"no 3D: {ex.GetType().Name}";
            _log($"3d: {ex.GetType().Name}: {ex.Message}");
        }
        finally { window?.Dispose(); }
    }

    /// <summary>Skia decodes; the renderer only ever sees RGBA. The same boundary every host keeps,
    /// which is why the renderer itself needs no image library and compiles for wasm.</summary>
    private static (byte[] Pixels, int W, int H)? DecodeWithSkia(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888 ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(rgba.GetPixels(), bytes, 0, bytes.Length);
        return (bytes, rgba.Width, rgba.Height);
    }

    public void Dispose()
    {
        _running = false;
        if (_started == 1) _thread.Join(2000);
        _gpuFrame?.Dispose();
        _frame?.Dispose(); _retired?.Dispose();
    }
}
