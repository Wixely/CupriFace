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
internal sealed class Teapot3dSurface : ISurfaceSource, IDisposable
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
            EnsureStarted();
            return _running;
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
        _thread.Join(2000);
        _frame?.Dispose(); _retired?.Dispose();
    }
}
