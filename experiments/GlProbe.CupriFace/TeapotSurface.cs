using CupriFace.Demo.ThreeD;
using System.Diagnostics;
using CupriFace.Paint;
using SkiaSharp;
using Silk.NET.Windowing;

namespace CupriFace.Experiments.GlProbe.Host;

/// <summary>
/// A 3D viewport as an engine surface. <see cref="ISurfaceSource"/>'s own docstring says it is for
/// "a video player, later a 3D viewport or camera" — this is that, and the point of building it is
/// to find out whether the seam the engine already has is enough, or whether embedding a renderer
/// needs the engine changed.
///
/// <para>It needs nothing changed. The contract explicitly allows a producer to publish frames FROM
/// ANY THREAD, so the renderer owns a private GL context on a private thread and hands the engine
/// finished <see cref="SKImage"/>s. Nothing here touches Skia's context, which matters more than it
/// looks: issuing raw GL on the context Skia is mid-draw on would corrupt its state tracking, and the
/// fix for that (GRContext.ResetContext) needs a handle the engine does not expose.</para>
///
/// <para><b>The cost this pays, stated rather than hidden:</b> the frame goes GPU to CPU and back —
/// glReadPixels into an SKImage, which Skia then uploads again when it paints. Both halves are timed
/// below so the zero-copy alternative can be argued about with numbers. That alternative is a
/// texture-backed SKImage over a shared GL context (GRBackendTexture), which needs the engine to
/// expose its GRContext and its GL context to be shared with this one. Worth doing; not needed to
/// know whether embedding works at all.</para>
/// </summary>
public sealed class TeapotSurface : ISurfaceSource, IDisposable
{
    private readonly int _w, _h;
    private readonly Gltf _model;
    private readonly Action<string> _log;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile SKImage? _frame;
    private SKImage? _retired;
    private readonly SurfaceRegistry _registry;

    // Timings, published for the report rather than guessed at.
    public double LastDrawMs, LastReadbackMs, LastUploadMs;
    public long Frames;
    public string GlVersion = "(not started)";
    public volatile string Status = "starting";

    public TeapotSurface(Gltf model, SurfaceRegistry registry, int w, int h, Action<string> log)
    {
        _model = model; _registry = registry; _w = w; _h = h; _log = log;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "teapot-gl" };
        _thread.Start();
    }

    public SKImage? CurrentFrame => _frame;
    public (int W, int H)? NaturalSize => (_w, _h);
    public bool Ticking => _running;

    private unsafe void RenderLoop()
    {
        IWindow? window = null;
        try
        {
            // A hidden window purely to own a GL context. GLFW has no headless context on every
            // platform, and a 1x1 invisible window is the portable way to get one — the actual
            // rendering goes to an FBO, so the window's own buffer is never used.
            window = Window.Create(WindowOptions.Default with
            {
                Size = new(1, 1),
                IsVisible = false,
                Title = "teapot-gl",
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            });
            window.Initialize();
            window.MakeCurrent();

            Gl.Load(n => window.GLContext!.TryGetProcAddress(n, out var p) ? p : 0);
            if (Gl.Missing.Count > 0)
            {
                Status = $"failed: {Gl.Missing.Count} GL entry points missing";
                _log($"teapot: FAIL missing {string.Join(", ", Gl.Missing)}");
                return;
            }
            GlVersion = Gl.Str(Gl.GetString(Gl.VERSION));
            _log($"teapot: offscreen context up, GL_VERSION = {GlVersion}");

            // glslEs: false — this is desktop GL, so the shader header is "#version 330 core".
            // The web and Android legs pass true and get "#version 300 es" from the same source.
            var renderer = new SceneRenderer(_model, glslEs: false);
            if (!renderer.Initialise(DecodeWithSkia, m => _log("teapot: " + m)))
            {
                Status = "failed: renderer init";
                return;
            }

            // The offscreen target. Colour as a texture (so a zero-copy path could later hand this
            // very texture to Skia), depth as a renderbuffer since nothing samples it.
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
                Status = $"failed: framebuffer incomplete 0x{status:X}";
                _log($"teapot: FAIL framebuffer incomplete 0x{status:X}");
                return;
            }
            _log($"teapot: offscreen framebuffer {_w}x{_h} complete");
            Status = "running";

            var pixels = new byte[_w * _h * 4];
            var info = new SKImageInfo(_w, _h, SKColorType.Rgba8888, SKAlphaType.Premul);
            var clock = Stopwatch.StartNew();
            var sw = new Stopwatch();

            while (_running)
            {
                var angle = 0.6f + (float)clock.Elapsed.TotalSeconds * 0.6f;

                sw.Restart();
                Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
                // Transparent clear: the engine composites this over the page, so the teapot should
                // sit on whatever CSS put behind it rather than on a plate of its own.
                renderer.Draw(angle, _w, _h, 0f, 0f, 0f, 0f);
                var err = Gl.GetError();
                if (err != 0) { _log($"teapot: glGetError 0x{err:X}"); Status = $"gl error 0x{err:X}"; return; }
                LastDrawMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                fixed (byte* p = pixels) Gl.ReadPixels(0, 0, _w, _h, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
                LastReadbackMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                SKImage img;
                // GL's first row is the BOTTOM of the image; Skia's is the top. Flipping on the CPU
                // here is honest but is precisely the sort of cost a shared-texture path removes —
                // it would flip in the sampler instead.
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
                // may still be reading. Exactly the discipline LottiePlayer keeps.
                var previous = _frame;
                _frame = img;
                _retired?.Dispose();
                _retired = previous;
                Frames++;

                _registry.NotifyFrame();
                Thread.Sleep(16);       // ~60fps; this probe is about correctness, not a frame pacer
            }
        }
        catch (Exception ex)
        {
            Status = $"failed: {ex.GetType().Name}: {ex.Message}";
            _log($"teapot: FAIL {ex}");
        }
        finally { window?.Dispose(); }
    }

    /// <summary>Skia decodes; the renderer sees RGBA. The same boundary all three probe legs keep.</summary>
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
