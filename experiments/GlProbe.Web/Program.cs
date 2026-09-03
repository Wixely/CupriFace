using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CupriFace.Experiments;
using SkiaSharp;

// FEASIBILITY PROBE, not a library. Does a real exported model — geometry, interleaved accessors, a
// scene graph, indices, uvs and an embedded JPEG — survive the whole path to pixels under
// NativeAOT-LLVM, with no JavaScript and no bindings package?
//
// The renderer itself is shared/SceneRenderer.cs, identical to the one the desktop and CupriFace legs
// run. Only two things are web-specific and both are in this file: creating the WebGL2 context, and
// where GL function addresses come from.

internal static unsafe partial class Probe
{
    private const string Em = "emscripten";

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextAttributes
    {
        // Field order is the header's, exactly. Wrong order does not fail to build — it silently asks
        // for the wrong context, which looks like "WebGL2 is unavailable" when it is really "we asked
        // for version 0".
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

    // The web's answer to wglGetProcAddress. Emscripten ships it precisely so portable GL code does
    // not have to care that its symbols happen to be static here — which is what lets all three legs
    // share one renderer rather than each carrying a copy of the GL calls.
    [DllImport(Em, EntryPoint = "emscripten_GetProcAddress")]
    private static extern nint GetProcAddress(byte* name);

    private static nint Proc(string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) return GetProcAddress(p);
    }

    private const int W = 480, H = 480;

    private static byte[] LoadModel()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("teapot.glb", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("teapot.glb is not embedded in this assembly");
        using var s = asm.GetManifestResourceStream(name)!;
        var buf = new byte[s.Length];
        s.ReadExactly(buf);
        return buf;
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

    private static int Main()
    {
        Console.WriteLine("glprobe: start");

        Gltf scene;
        try
        {
            var bytes = LoadModel();
            scene = Gltf.Load(bytes);
            Console.WriteLine($"glprobe: glb {bytes.Length:n0} bytes -> {scene.Primitives.Count} primitive(s), "
                + $"{scene.VertexCount:n0} vertices, {scene.TriangleCount:n0} triangles, {scene.Images.Count} image(s)");
            foreach (var p in scene.Primitives)
                Console.WriteLine($"glprobe:   '{p.Name}' baseColor={p.BaseColor[0]:F2},{p.BaseColor[1]:F2},{p.BaseColor[2]:F2} "
                    + $"metallic={p.Metallic:F2} roughness={p.Roughness:F2} uv={p.HasUv} image={p.ImageIndex}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"glprobe: FAIL could not load the model: {ex.Message}");
            return 1;
        }

        ContextAttributes attrs;
        InitAttributes(&attrs);
        attrs.MajorVersion = 2; attrs.MinorVersion = 0;   // WebGL2 == GLES 3.0
        attrs.Alpha = 0; attrs.Depth = 1; attrs.Antialias = 1; attrs.PreserveDrawingBuffer = 1;

        nint ctx;
        var target = Encoding.UTF8.GetBytes("#glcanvas\0");
        fixed (byte* t = target) ctx = CreateContext(t, &attrs);
        if (ctx <= 0) { Console.WriteLine($"glprobe: FAIL no WebGL2 context ({ctx})"); return 1; }
        if (MakeCurrent(ctx) != 0) { Console.WriteLine("glprobe: FAIL context not current"); return 1; }

        Gl.Load(Proc);
        if (Gl.Missing.Count > 0)
        {
            Console.WriteLine($"glprobe: FAIL {Gl.Missing.Count} entry point(s) unresolved: {string.Join(", ", Gl.Missing)}");
            return 1;
        }
        Console.WriteLine($"glprobe: GL_VERSION = {Gl.Str(Gl.GetString(Gl.VERSION))}");

        var renderer = new SceneRenderer(scene, glslEs: true);
        if (!renderer.Initialise(DecodeWithSkia, m => Console.WriteLine("glprobe: " + m))) return 1;
        Console.WriteLine($"glprobe: {renderer.DrawCalls} draw call(s), {renderer.TextureCount} texture(s)");

        var pixels = new byte[W * H * 4];
        int DrawAt(float angle)
        {
            renderer.Draw(angle, W, H, 0.055f, 0.067f, 0.086f, 1f);
            var err = Gl.GetError();
            if (err != 0) { Console.WriteLine($"glprobe: FAIL glGetError 0x{err:X}"); return -1; }
            fixed (byte* p = pixels) Gl.ReadPixels(0, 0, W, H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
            return 0;
        }

        if (DrawAt(0.6f) != 0) return 1;

        // What the pixels must show:
        //   drawn  — something was rasterised at all
        //   shades — distinct LUMINANCE levels: lighting, i.e. real per-vertex normals
        //   tones  — distinct RED levels: texture sampling. Load-bearing once a texture is bound,
        //            because a flat-shaded model already varies in luminance and would pass `shades`
        //            with the texture silently ignored.
        var background = 0; var drawn = 0;
        long sr = 0, sg = 0, sb = 0;
        var levels = new bool[256]; var reds = new bool[256];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
            if (Math.Abs(r - 14) <= 3 && Math.Abs(g - 17) <= 3 && Math.Abs(b - 22) <= 3) { background++; continue; }
            drawn++; sr += r; sg += g; sb += b;
            levels[(r * 30 + g * 59 + b * 11) / 100] = true;
            reds[r] = true;
        }
        var shades = 0; foreach (var l in levels) if (l) shades++;
        var tones = 0; foreach (var t in reds) if (t) tones++;

        Console.WriteLine($"glprobe: model pixels = {drawn:n0} ({100.0 * drawn / (W * H):F1}% of frame), background = {background:n0}");
        Console.WriteLine($"glprobe: distinct luminance levels = {shades}, distinct red levels = {tones}");
        if (drawn > 0)
            Console.WriteLine($"glprobe: mean rgb over model pixels = {(double)sr / drawn:F1},{(double)sg / drawn:F1},{(double)sb / drawn:F1}");

        var first = new byte[pixels.Length];
        Array.Copy(pixels, first, pixels.Length);
        if (DrawAt(2.2f) != 0) return 1;
        var moved = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - first[i]) > 8 || Math.Abs(pixels[i + 1] - first[i + 1]) > 8) moved++;
        Console.WriteLine($"glprobe: pixels changed when the camera orbited = {moved:n0}");

        if (DrawAt(0.6f) != 0) return 1;      // leave the nicer angle up for the screenshot

        var hasTex = renderer.TextureCount > 0;
        var ok = drawn > 5000 && shades > 20 && moved > 5000 && (!hasTex || tones > 30);
        Console.WriteLine(ok
            ? "glprobe: PASS teapot.glb rendered, lit and textured from nativeaot-llvm via webgl2"
            : "glprobe: FAIL the model did not render the way a lit, textured, orbitable mesh should");
        return ok ? 0 : 1;
    }
}
