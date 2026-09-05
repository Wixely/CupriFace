using CupriFace.Demo.ThreeD;
using Silk.NET.Windowing;
using SkiaSharp;

// The desktop leg. Since the shared renderer landed, this file contains NO GL calls at all — the
// entire body of it is: get a context, say where function addresses come from, hand over.
//
//   web     Emscripten's symbols are static, and emscripten_GetProcAddress hands them out
//   desktop opengl32 exports GL 1.1 only; wglGetProcAddress hands out the rest
//   android libGLESv3.so exports them; eglGetProcAddress hands them out
//
// One renderer, three lookups. That the difference shrank to a single lambda is the portability
// result — it was three separate copies of the GL calls before this, which proved much less.

internal static unsafe class DesktopProbe
{
    private const int W = 480, H = 480;

    private static (byte[] Pixels, int W, int H)? DecodeWithSkia(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null) return null;
        // Skia decodes to the PLATFORM's layout — BGRA here, RGBA on the web. Converting rather than
        // assuming is what keeps red and blue the right way round across hosts.
        using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888 ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(rgba.GetPixels(), bytes, 0, bytes.Length);
        return (bytes, rgba.Width, rgba.Height);
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("glprobe: start (desktop)");

        var glb = Path.Combine(AppContext.BaseDirectory, "teapot.glb");
        if (!File.Exists(glb)) { Console.WriteLine($"glprobe: FAIL asset missing: {glb}"); return 1; }

        Gltf scene;
        try
        {
            var bytes = File.ReadAllBytes(glb);
            scene = Gltf.Load(bytes);
            Console.WriteLine($"glprobe: glb {bytes.Length:n0} bytes -> {scene.Primitives.Count} primitive(s), "
                + $"{scene.VertexCount:n0} vertices, {scene.TriangleCount:n0} triangles, {scene.Images.Count} image(s)");
            foreach (var p in scene.Primitives)
                Console.WriteLine($"glprobe:   '{p.Name}' baseColor={p.BaseColor[0]:F2},{p.BaseColor[1]:F2},{p.BaseColor[2]:F2} "
                    + $"metallic={p.Metallic:F2} roughness={p.Roughness:F2} uv={p.HasUv} image={p.ImageIndex}");
        }
        catch (Exception ex) { Console.WriteLine($"glprobe: FAIL load: {ex.Message}"); return 1; }

        // Hidden by default: a headless check whose window steals focus is one nobody runs twice.
        var opts = WindowOptions.Default with
        {
            Size = new(W, H),
            Title = "glprobe",
            IsVisible = args.Contains("--show"),
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
        };

        IWindow window;
        try
        {
            window = Window.Create(opts);
            window.Initialize();
        }
        catch (Exception ex)
        {
            // The honest outcome on a machine with no usable GL, which this repo already knows is
            // common: virtualised GPUs, RDP sessions and CI runners all land here. Exit 2, not 1 —
            // an environment fact rather than a code failure.
            Console.WriteLine($"glprobe: NO-GL could not create an OpenGL window: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        try
        {
            window.MakeCurrent();
            Gl.Load(n => window.GLContext!.TryGetProcAddress(n, out var p) ? p : 0);
            if (Gl.Missing.Count > 0)
            {
                Console.WriteLine($"glprobe: FAIL {Gl.Missing.Count} entry point(s) unresolved: {string.Join(", ", Gl.Missing)}");
                return 1;
            }
            Console.WriteLine("glprobe: every entry point resolved through wglGetProcAddress/glXGetProcAddress");
            Console.WriteLine($"glprobe: GL_VERSION  = {Gl.Str(Gl.GetString(Gl.VERSION))}");
            Console.WriteLine($"glprobe: GL_RENDERER = {Gl.Str(Gl.GetString(Gl.RENDERER))}");

            var renderer = new SceneRenderer(scene, glslEs: false);
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

            // --- stress: how does it scale? -------------------------------------------------------
            // Correctness at 4,032 triangles says nothing about viability. This walks the instance
            // count up and reports the per-frame cost, so "is this a usable renderer" stops being a
            // matter of opinion. glFinish before each stop, because GL is asynchronous and timing
            // without it measures how fast commands are QUEUED, not how fast they are drawn.
            if (args.Contains("--stress"))
            {
                Console.WriteLine("glprobe: instances  draws  triangles      ms/frame     fps");
                foreach (var n in new[] { 1, 10, 50, 100, 250, 500, 1000 })
                {
                    for (var warm = 0; warm < 5; warm++) renderer.DrawInstances(0.6f, W, H, n, 0.055f, 0.067f, 0.086f, 1f);
                    Gl.Finish();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    const int frames = 30;
                    for (var f = 0; f < frames; f++) renderer.DrawInstances(0.6f + f * 0.01f, W, H, n, 0.055f, 0.067f, 0.086f, 1f);
                    Gl.Finish();
                    var ms = sw.Elapsed.TotalMilliseconds / frames;
                    Console.WriteLine($"glprobe: {n,9}  {n * renderer.DrawCalls,5}  {n * scene.TriangleCount,9:n0}  {ms,11:F2}  {1000 / ms,6:F0}");
                }
            }

            if (DrawAt(0.6f) != 0) return 1;

            var drawn = 0; long sr = 0, sg = 0, sb = 0;
            var levels = new bool[256]; var reds = new bool[256];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
                if (Math.Abs(r - 14) <= 3 && Math.Abs(g - 17) <= 3 && Math.Abs(b - 22) <= 3) continue;
                drawn++; sr += r; sg += g; sb += b;
                levels[(r * 30 + g * 59 + b * 11) / 100] = true;
                reds[r] = true;
            }
            var shades = 0; foreach (var l in levels) if (l) shades++;
            var tones = 0; foreach (var t in reds) if (t) tones++;

            Console.WriteLine($"glprobe: model pixels = {drawn:n0} ({100.0 * drawn / (W * H):F1}% of frame)");
            Console.WriteLine($"glprobe: distinct luminance levels = {shades}, distinct red levels = {tones}");
            if (drawn > 0)
                // The same statistic every leg prints, so the three can be compared directly rather
                // than each merely looking right on its own.
                Console.WriteLine($"glprobe: mean rgb over model pixels = {(double)sr / drawn:F1},{(double)sg / drawn:F1},{(double)sb / drawn:F1}");

            var first = new byte[pixels.Length];
            Array.Copy(pixels, first, pixels.Length);
            if (DrawAt(2.2f) != 0) return 1;
            var moved = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                if (Math.Abs(pixels[i] - first[i]) > 8 || Math.Abs(pixels[i + 1] - first[i + 1]) > 8) moved++;
            Console.WriteLine($"glprobe: pixels changed when the camera orbited = {moved:n0}");

            var hasTex = renderer.TextureCount > 0;
            var ok = drawn > 5000 && shades > 20 && moved > 5000 && (!hasTex || tones > 30);
            Console.WriteLine(ok
                ? "glprobe: PASS teapot.glb rendered, lit and textured on the desktop GL path"
                : "glprobe: FAIL the model did not render the way a lit, textured, orbitable mesh should");
            return ok ? 0 : 1;
        }
        finally { window.Dispose(); }
    }
}
