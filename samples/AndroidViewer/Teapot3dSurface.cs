using System.Runtime.InteropServices;
using CupriFace.Demo.ThreeD;
using CupriFace.Dom;
using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.AndroidViewer;

/// <summary>
/// The Showcase's 3D viewport on ANDROID — and the shortest of the three host integrations, because
/// the phone is a GPU host like the desktop GL window.
///
/// <para>The host renders through an <c>SKGLSurfaceView</c>, which owns a real <c>GRContext</c>, so
/// this takes the same zero-copy path the desktop does: draw on the host's own context inside
/// <see cref="RenderOnGpu"/>, publish a texture-backed <see cref="SKImage"/>, and the engine paints
/// it with no copy. There is no private context, no offscreen EGL and no readback anywhere in this
/// file. Before <c>IGpuSurfaceSource</c> existed, Android looked like the hard host precisely
/// because all of that would have been needed.</para>
///
/// <para>Two things differ from the desktop, and only two:</para>
/// <list type="bullet">
/// <item><description>entry points come from <c>dlsym</c> against <c>libGLESv3.so</c>, not
/// <c>wglGetProcAddress</c>;</description></item>
/// <item><description>the shader header is <c>#version 300 es</c> (<c>glslEs: true</c>), which is
/// the same dialect the browser build compiles, because WebGL2 IS GLES 3.0.</description></item>
/// </list>
/// </summary>
internal sealed class Teapot3dSurface : IGpuSurfaceSource
{
    // dlsym against libGLESv3.so rather than eglGetProcAddress, and deliberately: some drivers'
    // EGL implementations return a non-null stub for ANY name, which makes a missing entry point
    // look present and then crash on the call. dlsym answers about symbols that genuinely exist.
    [DllImport("libdl.so")] private static extern nint dlopen(string file, int mode);
    [DllImport("libdl.so")] private static extern nint dlsym(nint handle, string name);
    private const int RTLD_NOW = 2;

    private readonly Gltf _model;
    private readonly Action<string> _log;
    private readonly int _w, _h;
    private CupriDocument? _doc;

    private bool _ready, _failed;
    private uint _fbo, _tex, _depth;
    private SceneRenderer? _renderer;
    private SKImage? _frame;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public long Frames;
    public string Status = "starting";

    private Teapot3dSurface(Gltf model, int w, int h, Action<string> log)
    { _model = model; _w = w; _h = h; _log = log; }

    /// <summary>Wire the demo into a document, or leave it alone. Never throws — a phone whose
    /// driver refuses is a phone that shows the panel and a line of text, not one that crashes.</summary>
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

            var surface = new Teapot3dSurface(Gltf.Load(glb), 512, 512, log) { _doc = doc };
            doc.Surfaces.Register("showcase3d", surface);
            return surface;
        }
        catch (Exception ex)
        {
            log($"3d: not wired ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public SKImage? CurrentFrame => _frame;
    public (int W, int H)? NaturalSize => (_w, _h);

    /// <summary>Only while the viewport is on screen. LaidOut, not "did the painter ask about me":
    /// the display list is rebuilt every tick to compute damage, so the painter consults surfaces
    /// inside <c>display:none</c> sections too — and the Showcase spends most of its life on some
    /// other page. A surface that pins a phone's GPU at 100% behind a page nobody is looking at is
    /// a battery bug, not a demo.</summary>
    public bool Ticking => !_failed && _doc is { } d && Find(d.Root) is { LaidOut: true };

    private static RenderNode? Find(RenderNode n)
    {
        if (n.SurfaceKey == "showcase3d") return n;
        foreach (var c in n.Children) if (Find(c) is { } found) return found;
        return null;
    }

    public unsafe void RenderOnGpu(GRContext context)
    {
        if (_failed || !Ticking) return;

        if (!_ready)
        {
            var lib = dlopen("libGLESv3.so", RTLD_NOW);
            if (lib == 0) { _failed = true; _log("3d: libGLESv3.so did not load"); return; }
            Gl.Load(n => dlsym(lib, n));
            if (Gl.Missing.Count > 0)
            {
                _failed = true;
                _log($"3d: {Gl.Missing.Count} GL entry points missing: {string.Join(", ", Gl.Missing)}");
                return;
            }

            // glslEs: true - "#version 300 es", the same dialect the browser build compiles.
            _renderer = new SceneRenderer(_model, glslEs: true);
            if (!_renderer.Initialise(DecodeWithSkia, m => _log("3d: " + m))) { _failed = true; return; }

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
                _failed = true; _log("3d: framebuffer incomplete"); return;
            }
            _fbo = fbo; _tex = tex; _depth = depth;
            _ready = true;
            Status = "painted, zero-copy (the engine draws our texture)";
            // The gate greps for this line, so it names the host and the driver rather than just
            // saying "ok" - a PASS that cannot say what it ran on is worth very little.
            _log($"cupri-gate: 3d ready GL_VERSION={Gl.Str(Gl.GetString(Gl.VERSION))}");
        }

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        _renderer!.Draw(0.6f + (float)_clock.Elapsed.TotalSeconds * 0.6f, _w, _h, 0f, 0f, 0f, 0f);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);

        var info = new GRGlTextureInfo(Gl.TEXTURE_2D, _tex, Gl.RGBA8);
        using var backend = new GRBackendTexture(_w, _h, false, info);
        var img = SKImage.FromTexture(context, backend, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        if (img is null) return;
        var previous = _frame;
        _frame = img;
        previous?.Dispose();
        Frames++;
        if (Frames == 60) _log($"cupri-gate: 3d frames={Frames} status={Status}");
    }

    /// <summary>Skia decodes; the renderer only ever sees RGBA. The same boundary every host keeps,
    /// which is why the shared renderer needs no image library of its own.</summary>
    private static (byte[] Pixels, int W, int H)? DecodeWithSkia(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888 ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        Marshal.Copy(rgba.GetPixels(), bytes, 0, bytes.Length);
        return (bytes, rgba.Width, rgba.Height);
    }
}
