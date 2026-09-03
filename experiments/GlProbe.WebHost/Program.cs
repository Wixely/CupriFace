using System.Runtime.InteropServices;
using System.Text;
using CupriFace;
using CupriFace.Experiments;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Paint;
using CupriFace.Web;
using SkiaSharp;

// The WEB integration, and the last architectural unknown. On desktop the renderer hands the engine
// finished SKImages; that cannot work here, because CupriFace's web hosts render to an SKBitmap and
// present through putImageData — there is NO GPU context to share.
//
// So the web takes the other lane the engine already has: HOST COMPOSITING. The engine punches a
// transparent hole at the element's box and something else shows through it. That is exactly what
// <cupri-video> does on this host, and Painter.cs's comment for the branch already names the case —
// "a HOST-COMPOSITED surface (web underlay video) paints no frames at all… future 3D viewports".
//
// The finding: this needs NOTHING added to the engine either. Everything required is already public.
//   - ISurfaceSource.HostComposited => true          punches the hole, no frame needed
//   - CupriApp.Transparent => true                   selects the straight-alpha present the hole needs
//   - doc.Root / RenderNode.SurfaceKey               find the element
//   - HitTesting.ScreenBox(node)                     where it landed on screen
// An app can therefore own a WebGL canvas underneath the engine's, glued to a box the engine lays
// out, without the engine knowing what 3D is.

internal static unsafe partial class Program
{
    private static void Main() => WebHost.Run(new Gl3dApp());
}

/// <summary>The surface. It never produces a frame for the engine — its whole contract is "punch a
/// hole here and I will fill it myself".</summary>
internal sealed unsafe class Gl3dSurface : ISurfaceSource
{
    private const string Em = "emscripten";

    // ONE definition of where the underlay is. The host derives this id from the surface key
    // ("cupri-underlay-" + key), which is the contract that lets an app find the element it asked
    // for. It is a const because it was briefly not: after moving to the seam, Size() asked about
    // the new id while the context request still named the probe's old hand-rolled "#gl3d", so the
    // canvas existed, the size looked fine, and only the context creation failed — reported as
    // "no WebGL2 context", three steps from a stale selector.
    private const string Target = "#cupri-underlay-teapot3d";

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
    [DllImport(Em, EntryPoint = "emscripten_run_script")]
    private static extern void RunScript(byte* script);

    private static void Js(string script)
    {
        var b = Encoding.UTF8.GetBytes(script + "\0");
        fixed (byte* p = b) RunScript(p);
    }

    [DllImport(Em, EntryPoint = "emscripten_get_canvas_element_size")]
    private static extern int GetCanvasSize(byte* target, int* w, int* h);

    private static int CanvasWidth() => Size().W;
    private static int CanvasHeight() => Size().H;

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

    private readonly Gltf _scene;
    private readonly Action<string> _log;
    private SceneRenderer? _renderer;
    private nint _ctx;
    private bool _started, _failed;
    private int _waited;
    private (int W, int H) _lastSize = (-1, -1);
    private int _frames;

    public CupriDocument? Doc;
    public float Scale = 1f;
    public string Status = "not started";

    public Gl3dSurface(Gltf scene, Action<string> log) { _scene = scene; _log = log; }

    // No frame ever: the engine must punch, not draw.
    public SKImage? CurrentFrame => null;
    public (int W, int H)? NaturalSize => (512, 512);
    public bool HostComposited => true;

    // THE SEAM. The host creates a <canvas> beneath the hole and keeps it glued to this element's
    // box — through the clip chain and the transform chain, which is work the video path already
    // did and this file no longer has to. Before this existed the probe created its own canvas with
    // emscripten_run_script and synced a plain box, which broke the moment the page scrolled.
    public string? UnderlayElement => "canvas";

    /// <summary>The probe's per-frame hook. The engine polls this every frame to decide whether a
    /// render-on-demand host should keep going, so it is reliably called — but it is a PROPERTY, and
    /// driving rendering from one is a shortcut this file should own up to. A real integration would
    /// export a tick and drive the underlay from the page's own requestAnimationFrame, independent of
    /// the engine's loop; that is a JS plumbing question, not an architectural one, which is why the
    /// probe does not spend its evidence on it.</summary>
    public bool Ticking
    {
        get { Render(); return true; }
    }

    private void Render()
    {
        if (_failed || Doc is null) return;

        if (!_started)
        {
            // The host creates the underlay AFTER a frame is painted, and this runs DURING one — so on
            // the first poll the canvas does not exist yet and the context request fails. RETRY rather
            // than latch: the element appears within a frame or two. Latching is what made the first
            // attempt report "no WebGL2 context on the underlay" for ever, on a page that was fine.
            if (Size().W <= 0)
            {
                if (++_waited > 240) { _failed = true; Status = "no underlay canvas appeared"; _log("gl3d: FAIL " + Status); }
                return;
            }
            _started = true;
            ContextAttributes attrs;
            InitAttributes(&attrs);
            attrs.MajorVersion = 2; attrs.MinorVersion = 0;
            attrs.Alpha = 1; attrs.Depth = 1; attrs.Antialias = 1;
            var target = Encoding.UTF8.GetBytes(Target + "\0");
            fixed (byte* t = target) _ctx = CreateContext(t, &attrs);
            if (_ctx <= 0) { _failed = true; Status = "no WebGL2 context on the underlay"; _log("gl3d: FAIL " + Status); return; }
            if (MakeCurrent(_ctx) != 0) { _failed = true; Status = "context not current"; _log("gl3d: FAIL " + Status); return; }

            Gl.Load(Proc);
            if (Gl.Missing.Count > 0)
            {
                _failed = true; Status = $"{Gl.Missing.Count} GL entry points missing";
                _log("gl3d: FAIL " + Status); return;
            }
            var ver = Gl.Str(Gl.GetString(Gl.VERSION));
            _log($"gl3d: underlay context up, GL_VERSION = {ver}");
            // Assert the version rather than trust the request. emscripten_webgl_create_context
            // DOWNGRADES to WebGL1 instead of failing when the build was not linked with
            // -sMAX_WEBGL_VERSION=2, and the first symptom is otherwise a shader error blaming
            // "#version 300 es" - a diagnosis three steps from the cause.
            if (!ver.Contains("WebGL 2"))
            {
                _failed = true;
                Status = $"context downgraded to WebGL1 ({ver}) - build needs -sMAX_WEBGL_VERSION=2";
                _log("gl3d: FAIL " + Status);
                return;
            }

            _renderer = new SceneRenderer(_scene, glslEs: true);
            if (!_renderer.Initialise(DecodeWithSkia, m => _log("gl3d: " + m))) { _failed = true; Status = "renderer init"; return; }
            _log($"gl3d: {_renderer.DrawCalls} draw call(s), {_renderer.TextureCount} texture(s)");
            Status = "running";
        }

        if (_renderer is null) return;

        // No positioning here any more: the host owns it. This only has to draw at whatever size the
        // canvas currently is, which is the whole point of the seam.
        MakeCurrent(_ctx);
        var w = CanvasWidth(); var h = CanvasHeight();
        if (w <= 0 || h <= 0) return;
        if ((w, h) != _lastSize) { _lastSize = (w, h); _log($"gl3d: drawing at {w}x{h}"); }
        // Transparent clear: the page's own background shows around the model, through the hole.
        _renderer.Draw(0.6f + _frames * 0.01f, w, h, 0f, 0f, 0f, 0f);
        _frames++;
    }

    public int Frames => _frames;

    private static RenderNode? FindSurface(RenderNode n, string key)
    {
        if (n.SurfaceKey == key) return n;
        foreach (var c in n.Children) if (FindSurface(c, key) is { } f) return f;
        return null;
    }

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

internal sealed class Gl3dApp : CupriApp
{
    private Gl3dSurface? _surface;

    public override string Title => "CupriFace — 3D under the page";
    // Straight-alpha present, which is what makes a punched hole actually transparent to the page.
    // A public virtual the app opts into; nothing in the engine had to change for it.
    public override bool Transparent => true;

    public override void Configure(CupriDocument doc)
    {
        byte[] glb;
        try
        {
            var asm = typeof(Gl3dApp).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("teapot.glb", StringComparison.OrdinalIgnoreCase))!;
            using var s = asm.GetManifestResourceStream(name)!;
            glb = new byte[s.Length];
            s.ReadExactly(glb);
        }
        catch (Exception ex) { Console.WriteLine($"gl3d: FAIL asset: {ex.Message}"); return; }

        var scene = Gltf.Load(glb);
        Console.WriteLine($"gl3d: {scene.Primitives.Count} primitive(s), {scene.VertexCount:n0} vertices, "
            + $"{scene.TriangleCount:n0} triangles, {scene.Images.Count} image(s)");

        _surface = new Gl3dSurface(scene, Console.WriteLine) { Doc = doc, Scale = 1f };
        doc.Surfaces.Register("teapot3d", _surface);

        // The page reads these back to assert on, rather than a human squinting at the canvas.
        doc.OnRebuilt(_ => { });
    }

    public override string Html => """
        <body>
          <div class="wrap">
            <div class="title">3D under the page</div>
            <p class="sub">The engine punches a transparent hole where the box below is, and a real
              WebGL canvas underneath shows through it — the same lane a <b>cupri-video</b> takes on
              this host. Nothing in the engine was changed.</p>
            <!-- A REAL scroll container, not the body: this engine scrolls elements with
                 overflow:scroll and a fixed height, not the document. It is the harder test and the
                 right one — as the stage moves the underlay must both TRANSLATE and CLIP against
                 this box, and clipping is the half a hand-rolled "set left/top" version cannot do
                 at all. A DOM element under the engine's canvas knows nothing of engine overflow. -->
            <div class="scroller">
            <div class="stage">
              <div data-cupri-surface="teapot3d" class="viewport"></div>
              <!-- Painted AFTER the surface, and deliberately overlapping it. ClearHole uses
                   BlendMode.Src to replace with transparent, so anything drawn later composites on
                   top — this badge is the test of whether UI can sit in FRONT of the 3D. -->
              <div class="badge">opaque UI over the hole</div>
              <!-- Two DIFFERENT routes to partial alpha, tested separately because they take
                   different paths through the painter: an rgba() fill lands as one command with
                   alpha < 1, while `opacity` wraps its subtree in PushOpacity. Either could survive
                   the hole and the premultiplied-to-straight present; neither is obvious. -->
              <div class="glass">rgba() fill, 45%</div>
              <div class="faded">opacity: 0.5</div>
            </div>
            <p class="body">This paragraph is laid out by the engine and painted over the same canvas
              the hole is punched in. Text before and after the 3D composites correctly, which is what
              a hole (rather than an overlay) buys.</p>
            <!-- Enough content to SCROLL. That is the point of this page now: the underlay is
                 positioned by the host through the same clip and transform chain a video uses, so it
                 tracks the scroll. The probe's own hand-rolled version synced a plain box and slid
                 out of its hole the moment the page moved. -->
            <p class="body">Scroll this page. The 3D canvas is a real DOM element beneath the engine's,
              and the host re-sends its box, its clip against every overflow ancestor, and the engine's
              transform chain after every painted frame — so it stays in its hole.</p>
            <p class="body">Filler, so there is somewhere to scroll to. One. Two. Three. Four. Five.</p>
            <p class="body">Filler. Six. Seven. Eight. Nine. Ten. Eleven. Twelve. Thirteen.</p>
            <p class="body">Filler. Fourteen. Fifteen. Sixteen. Seventeen. Eighteen. Nineteen.</p>
            <p class="body">Filler. Twenty. Twenty-one. Twenty-two. Twenty-three. Twenty-four.</p>
            <p class="body">Filler. Twenty-five. Twenty-six. Twenty-seven. Twenty-eight.</p>
            <p class="body">The end of the scroller.</p>
            </div>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; background:#ffffff; color:#1e2430; }
        .wrap { padding:22px 26px; }
        .title { font-size:20px; font-weight:bold; }
        .sub { color:#48505c; font-size:13px; margin:8px 0 16px; max-width:560px; }
        /* CONTENT-box sizing, which is the engine's model (node.Width = contentW + insets): this
           320 was written with border-box muscle memory, so the stage came out 332x331 total and
           left 12px of spare background down the right and bottom of the 308px viewport — a
           lopsided frame that looked like a rendering artefact and was just arithmetic. 308 makes
           the content box exactly the viewport, so the 6px padding is the whole frame. */
        /* Deliberately SHORTER than the stage, so the viewport is clipped before anything scrolls.
           A version that only translated would look right at rest and wrong the moment it moved. */
        .scroller { height:260px; overflow:scroll; background:#f4f6f9; border-radius:12px; padding:10px; }
        .stage { width:308px; height:308px; background:#11141a; border-radius:10px; padding:6px; }
        .viewport { width:308px; height:308px; border-radius:14px; }
        /* Negative margin pulls it back over the viewport it follows. */
        .badge { margin-top:-232px; margin-left:14px; width:150px; background:#b87333; color:#ffffff;
                 font-size:12px; padding:7px 10px; border-radius:8px; }
        .glass { margin-top:6px; margin-left:14px; width:150px; background:rgba(184,115,51,0.45);
                 color:#ffffff; font-size:12px; padding:7px 10px; border-radius:8px; }
        .faded { margin-top:6px; margin-left:14px; width:150px; background:#b87333; opacity:0.5;
                 color:#ffffff; font-size:12px; padding:7px 10px; border-radius:8px; }
        .body { color:#48505c; font-size:13px; margin-top:16px; max-width:560px; }
        """;
}
