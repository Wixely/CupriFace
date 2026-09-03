using System.Runtime.InteropServices;
using System.Text;
using CupriFace.Demo.ThreeD;
using CupriFace.Dom;
using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Samples.WebLlvm;

/// <summary>
/// The Showcase's 3D viewport in the BROWSER, and the interesting half of the demo.
///
/// <para>Desktop hands the engine finished <see cref="SKImage"/>s. That cannot work here: CupriFace's
/// web hosts render to an <c>SKBitmap</c> and present through <c>putImageData</c>, so there is no GPU
/// context to render into and nothing to share. The web takes the engine's OTHER lane instead —
/// <b>host compositing</b>. The surface reports <see cref="HostComposited"/>, so the painter punches
/// a transparent hole at the element's box (with its <c>border-radius</c>) and paints nothing into
/// it; a real WebGL canvas underneath the engine's shows through.</para>
///
/// <para><b>The host owns the positioning, and that is the whole point of the seam.</b> Declaring
/// <see cref="UnderlayElement"/> as <c>"canvas"</c> makes the host create
/// <c>#cupri-underlay-showcase3d</c> and then keep it glued to whatever box the engine laid out —
/// through the scroll offset, the clip against every <c>overflow</c> ancestor, and any transform on
/// the chain, re-sent after every painted frame. That is the same machinery <c>&lt;cupri-video&gt;</c>
/// has always used. Written by hand instead, this file previously synced a plain box and slid out of
/// its hole the moment the page scrolled.</para>
///
/// <para>Nothing in the engine was added for 3D. <c>ISurfaceSource</c>, <c>HostComposited</c> and the
/// underlay seam are all public API a video already uses.</para>
/// </summary>
internal sealed unsafe class Web3dSurface : ISurfaceSource
{
    private const string Em = "emscripten";

    /// <summary>ONE definition of where the underlay is: the host derives this id from the surface
    /// key, which is the contract that lets an app find the element it asked for. A const because it
    /// was briefly not — a stale selector left here after a refactor reported as "no WebGL2 context"
    /// while the canvas was present and correctly sized, three steps from the cause.</summary>
    private const string Key = "showcase3d";
    private const string Target = "#cupri-underlay-" + Key;

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextAttributes
    {
        public int Alpha, Depth, Stencil, Antialias, PremultipliedAlpha, PreserveDrawingBuffer;
        public int PowerPreference, FailIfMajorPerformanceCaveat;
        public int MajorVersion, MinorVersion;
        public int EnableExtensionsByDefault, ExplicitSwapControl;
        public int ProxyContextToMainThread, RenderViaOffscreenBackBuffer;
    }

    [DllImport(Em, EntryPoint = "emscripten_webgl_init_context_attributes")]
    private static extern void InitAttributes(ContextAttributes* attrs);
    [DllImport(Em, EntryPoint = "emscripten_webgl_create_context")]
    private static extern nint CreateContext(byte* target, ContextAttributes* attrs);
    [DllImport(Em, EntryPoint = "emscripten_webgl_make_context_current")]
    private static extern int MakeCurrent(nint context);
    [DllImport(Em, EntryPoint = "emscripten_GetProcAddress")]
    private static extern nint GetProcAddress(byte* name);
    [DllImport(Em, EntryPoint = "emscripten_get_canvas_element_size")]
    private static extern int GetCanvasSize(byte* target, int* w, int* h);

    private static (int W, int H) Size()
    {
        int w = 0, h = 0;
        var t = Encoding.UTF8.GetBytes(Target + "\0");
        fixed (byte* p = t) GetCanvasSize(p, &w, &h);
        return (w, h);
    }

    private static nint Proc(string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) return GetProcAddress(p);
    }

    private CupriDocument? _doc;
    private readonly Gltf _scene;
    private readonly Action<string> _log;
    private SceneRenderer? _renderer;
    private nint _ctx;
    private bool _started, _failed;
    private int _waited, _frames;

    public string Status = "not started";

    private Web3dSurface(Gltf scene, Action<string> log) { _scene = scene; _log = log; }

    /// <summary>Wire the demo in, or leave the page alone. Never throws: without this the element
    /// simply shows its poster, which is the engine's ordinary behaviour for a surface with no
    /// frames.</summary>
    public static Web3dSurface? TryAttach(CupriDocument doc, Action<string>? log = null)
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

            var surface = new Web3dSurface(Gltf.Load(glb), log) { _doc = doc };
            doc.Surfaces.Register(Key, surface);
            return surface;
        }
        catch (Exception ex)
        {
            log($"3d: not wired ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    // No frame ever reaches the engine: it must punch, not draw.
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => (512, 512);

    public bool HostComposited => true;

    /// <summary>
    /// Is our element actually on screen? Asked before doing ANY per-frame work.
    ///
    /// <para><b>This must be the layout, not the painter.</b> The obvious signal — "did the painter
    /// ask me for <see cref="HostComposited"/>?" — is wrong, and wrong in a way that looks right: the
    /// display list is rebuilt every tick to work out the damage, so the painter consults the surface
    /// for nodes in <c>display:none</c> sections too. Gating on it left the Showcase ticking for ever
    /// while parked on some other page, which is exactly the busy loop this check exists to avoid.
    /// <c>LaidOut</c> is the discriminator the video path already relies on for the same job.</para>
    /// </summary>
    private bool OnScreen => _doc is { } d && Find(d.Root) is { LaidOut: true };

    private static RenderNode? Find(RenderNode n)
    {
        if (n.SurfaceKey == Key) return n;
        foreach (var c in n.Children) if (Find(c) is { } found) return found;
        return null;
    }

    /// <summary>Ask the host for a <c>&lt;canvas&gt;</c> beneath the hole. A video returns null here
    /// and keeps owning its own element; both are then positioned by the same code.</summary>
    public string? UnderlayElement => "canvas";

    /// <summary>
    /// "I am producing frames" — and it must be answered honestly, which this first was not.
    ///
    /// <para>Returning a bare <c>true</c> looks harmless for a surface that never hands the engine a
    /// frame, and is not: the registry folds it into the document's "something is animating" signal,
    /// so a render-on-demand host NEVER IDLES. Nothing looked wrong — the paint count stayed flat,
    /// because there was no damage to paint — but the host span the frame loop for ever, and the
    /// browser gate caught it as a keyboard failure: tabbing stopped reaching text fields, so an IME
    /// would open at the page origin. A surface that quietly pins the host at 100% is a bad
    /// neighbour even when it draws correctly, and the Showcase spends most of its life on some
    /// other section.</para>
    ///
    /// <para>So: tick only while our element is actually being painted, which
    /// <see cref="HostComposited"/> tells us. Driving rendering from a property getter at all is
    /// still a shortcut this file owns up to — a production integration would export a tick and
    /// drive the underlay from the page's own requestAnimationFrame, independent of the engine's
    /// loop. That is JS plumbing rather than an architectural question, which is why the demo does
    /// not spend space on it.</para>
    /// </summary>
    public bool Ticking
    {
        get
        {
            if (!OnScreen) return false;   // parked on another section: let the host sleep
            Render();
            return true;                   // visible: keep frames coming, drawing or still acquiring
        }
    }

    private void Render()
    {
        if (_failed) return;

        if (!_started)
        {
            // Wait, indefinitely and without complaint, for the host to create the underlay.
            //
            // Two reasons it may not be there, and neither is an error. The host creates it AFTER a
            // frame is painted while this runs DURING one, so it is never there on the very first
            // poll. And in a TABBED app the element is not laid out at all until its section is
            // opened — which may be minutes, or never.
            //
            // This first had a retry budget, which is the wrong shape: it expired while the Showcase
            // sat on another tab, so opening 3D later found the surface permanently disabled and the
            // viewport empty. "Absent" here is a normal state with no timeout, not a slow failure.
            // A breadcrumb is logged once, after long enough that a genuinely unwired host leaves a
            // trace, but the surface keeps trying either way.
            if (Size().W <= 0)
            {
                if (++_waited == 600) _log("3d: the underlay canvas has not appeared while the viewport is on screen");
                return;
            }
            _started = true;

            ContextAttributes attrs;
            InitAttributes(&attrs);
            attrs.MajorVersion = 2; attrs.MinorVersion = 0;
            attrs.Alpha = 1; attrs.Depth = 1; attrs.Antialias = 1;
            var target = Encoding.UTF8.GetBytes(Target + "\0");
            fixed (byte* t = target) _ctx = CreateContext(t, &attrs);
            if (_ctx <= 0) { _failed = true; Status = "no WebGL2 context"; _log("3d: " + Status); return; }
            if (MakeCurrent(_ctx) != 0) { _failed = true; Status = "context not current"; _log("3d: " + Status); return; }

            Gl.Load(Proc);
            if (Gl.Missing.Count > 0)
            {
                _failed = true; Status = $"{Gl.Missing.Count} GL entry points missing";
                _log("3d: " + Status); return;
            }

            // Assert the version rather than trust the request. emscripten_webgl_create_context
            // DOWNGRADES to WebGL1 instead of failing when the build was not linked with
            // -sMAX_WEBGL_VERSION=2, and the first symptom is otherwise a shader error blaming
            // "#version 300 es" — a diagnosis three steps from the cause.
            var ver = Gl.Str(Gl.GetString(Gl.VERSION));
            if (!ver.Contains("WebGL 2"))
            {
                _failed = true;
                Status = $"downgraded to WebGL1 ({ver}) — the build needs -sMAX_WEBGL_VERSION=2";
                _log("3d: " + Status); return;
            }
            _log($"3d: underlay context up, GL_VERSION = {ver}");

            // glslEs: true — "#version 300 es", from the same shader source the desktop compiles
            // with a "#version 330 core" header.
            _renderer = new SceneRenderer(_scene, glslEs: true);
            if (!_renderer.Initialise(DecodeWithSkia, m => _log("3d: " + m)))
            {
                _failed = true; Status = "renderer init failed"; _log("3d: " + Status); return;
            }
            Status = "host-composited (WebGL2 underlay)";
        }

        if (_renderer is null) return;

        // No positioning here: the host owns it. This only draws at whatever size the canvas
        // currently is, which is the whole point of the seam.
        MakeCurrent(_ctx);
        var (w, h) = Size();
        if (w <= 0 || h <= 0) return;
        // Cleared to the STAGE COLOUR, not to transparent, and the reason is a real property of host
        // compositing rather than a style choice: the hole is punched with BlendMode.Src, so it
        // erases everything already painted at that box — including the element's own CSS
        // background. On desktop the model is drawn OVER that background and picks it up for free;
        // here there is nothing behind the canvas but the page. Clearing transparent left the web
        // viewport white while the desktop one was near-black, from identical markup. So the
        // underlay supplies the backdrop the hole took away, and both lanes look the same.
        // (#0b0f18 — .stage3d in ShowcaseApp.css.)
        _renderer.Draw(0.6f + _frames * 0.01f, w, h, 0x0b / 255f, 0x0f / 255f, 0x18 / 255f, 1f);
        _frames++;
    }

    /// <summary>Skia decodes; the renderer only ever sees RGBA — the boundary that keeps the shared
    /// renderer free of any image library and therefore compilable for wasm.</summary>
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
