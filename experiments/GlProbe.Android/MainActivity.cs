using System.Runtime.InteropServices;
using Android.Content.PM;
using Android.Graphics;
using Android.Opengl;
using CupriFace.Demo.ThreeD;
using Javax.Microedition.Khronos.Opengles;
// Both Android.Opengl and Javax...Khronos.Egl define EGLConfig, and GLSurfaceView.IRenderer wants
// the Khronos one. Aliasing rather than dropping a using: Android.Opengl is where GLSurfaceView
// itself lives, so neither namespace can go.
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;

namespace CupriFace.Experiments.GlProbe.Android;

// The Android leg. Since the shared renderer landed this file contains NO GL calls — only a context
// (GLSurfaceView provides it), a proc-address source, and a platform image decoder.
//
//   web     Emscripten's symbols are static, and emscripten_GetProcAddress hands them out
//   desktop opengl32 exports GL 1.1 only; wglGetProcAddress hands out the rest
//   android libGLESv3.so exports them; dlsym hands them out
//
// Three lookups, one renderer — the same compiled SceneRenderer, not merely the same call sequence.
//
// Note what is NOT imported: an image codec. Android's own BitmapFactory decodes the glb's embedded
// JPEG, as Skia does on the other two hosts. Every platform already has a decoder; a renderer that
// ships one has taken a dependency it never needed.

[Activity(Label = "GlProbe", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var view = new GLSurfaceView(this);
        view.SetEGLContextClientVersion(3);
        view.SetEGLConfigChooser(8, 8, 8, 8, 16, 0);
        view.SetRenderer(new ProbeRenderer(Assets!));
        view.RenderMode = Rendermode.Continuously;
        SetContentView(view);
    }
}

internal sealed unsafe class ProbeRenderer(global::Android.Content.Res.AssetManager assets)
    : Java.Lang.Object, GLSurfaceView.IRenderer
{
    private const string Tag = "glprobe";
    private static void Log(string m) => global::Android.Util.Log.Info(Tag, m);

    // dlsym against libGLESv3.so rather than eglGetProcAddress. Deliberate: some drivers' EGL
    // implementations return a non-null stub for ANY name, which would make a missing entry point
    // look present and then crash on call — the same trap CupriFace's own GL loader documents for
    // glXGetProcAddressARB. dlsym answers about symbols that genuinely exist.
    [DllImport("libdl.so")] private static extern nint dlopen(string file, int mode);
    [DllImport("libdl.so")] private static extern nint dlsym(nint handle, string name);
    private const int RTLD_NOW = 2;

    private Gltf? _scene;
    private SceneRenderer? _renderer;
    private int _w = 1, _h = 1, _frame;
    private bool _reported, _failed;

    private byte[] ReadAsset(string name)
    {
        using var s = assets.Open(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>BitmapFactory decodes; the renderer only ever sees RGBA. Bitmap hands back ARGB
    /// ints, so the swizzle is explicit — the other legs needed the mirror of this (Skia gives BGRA
    /// on desktop, RGBA on the web), and assuming any one of them silently swaps red and blue.</summary>
    private static (byte[] Pixels, int W, int H)? DecodeWithBitmapFactory(byte[] encoded)
    {
        using var bmp = BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length);
        if (bmp is null) return null;
        int tw = bmp.Width, th = bmp.Height;
        var argb = new int[tw * th];
        bmp.GetPixels(argb, 0, tw, 0, 0, tw, th);
        var rgba = new byte[tw * th * 4];
        for (var i = 0; i < argb.Length; i++)
        {
            var p = argb[i];                       // 0xAARRGGBB
            rgba[i * 4 + 0] = (byte)(p >> 16);     // R
            rgba[i * 4 + 1] = (byte)(p >> 8);      // G
            rgba[i * 4 + 2] = (byte)p;             // B
            rgba[i * 4 + 3] = (byte)(p >> 24);     // A
        }
        return (rgba, tw, th);
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
    {
        try
        {
            var bytes = ReadAsset("teapot.glb");
            _scene = Gltf.Load(bytes);
            Log($"glb {bytes.Length:n0} bytes -> {_scene.Primitives.Count} primitive(s), "
                + $"{_scene.VertexCount:n0} vertices, {_scene.TriangleCount:n0} triangles, {_scene.Images.Count} image(s)");
            foreach (var p in _scene.Primitives)
                Log($"  '{p.Name}' baseColor={p.BaseColor[0]:F2},{p.BaseColor[1]:F2},{p.BaseColor[2]:F2} "
                    + $"metallic={p.Metallic:F2} roughness={p.Roughness:F2} uv={p.HasUv} image={p.ImageIndex}");
        }
        catch (Exception ex) { Log($"FAIL load: {ex.Message}"); _failed = true; return; }

        var lib = dlopen("libGLESv3.so", RTLD_NOW);
        if (lib == 0) { Log("FAIL could not dlopen libGLESv3.so"); _failed = true; return; }
        Gl.Load(n => dlsym(lib, n));
        if (Gl.Missing.Count > 0)
        {
            Log($"FAIL {Gl.Missing.Count} entry point(s) unresolved: {string.Join(", ", Gl.Missing)}");
            _failed = true;
            return;
        }
        Log("every entry point resolved through dlsym(libGLESv3.so)");
        Log($"GL_VERSION  = {Gl.Str(Gl.GetString(Gl.VERSION))}");
        Log($"GL_RENDERER = {Gl.Str(Gl.GetString(Gl.RENDERER))}");

        // glslEs: true — WebGL2 IS GLES 3.0, so this leg and the web leg compile the SAME shader
        // header; only the desktop one differs.
        _renderer = new SceneRenderer(_scene!, glslEs: true);
        if (!_renderer.Initialise(DecodeWithBitmapFactory, m => Log(m))) { _failed = true; return; }
        Log($"{_renderer.DrawCalls} draw call(s), {_renderer.TextureCount} texture(s)");
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _w = Math.Max(1, width); _h = Math.Max(1, height);
    }

    public void OnDrawFrame(IGL10? gl)
    {
        if (_failed || _renderer is null) return;

        // Orbit, so a human watching the device can tell it is live and the report has motion to see.
        var angle = 0.6f + _frame * 0.02f;
        _renderer.Draw(angle, _w, _h, 0.055f, 0.067f, 0.086f, 1f);

        // Report once, a few frames in so the surface has certainly settled. The same statistics the
        // other legs print, so the three can be compared directly rather than each merely looking
        // right on its own.
        if (_frame++ != 5 || _reported) return;
        _reported = true;

        var err = Gl.GetError();
        if (err != 0) { Log($"FAIL glGetError 0x{err:X}"); return; }

        var pixels = new byte[_w * _h * 4];
        fixed (byte* p = pixels) Gl.ReadPixels(0, 0, _w, _h, Gl.RGBA, Gl.UNSIGNED_BYTE, p);

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

        Log($"viewport {_w}x{_h}, model pixels = {drawn:n0} ({100.0 * drawn / (_w * _h):F1}% of frame)");
        Log($"distinct luminance levels = {shades}, distinct red levels = {tones}");
        if (drawn > 0)
            Log($"mean rgb over model pixels = {(double)sr / drawn:F1},{(double)sg / drawn:F1},{(double)sb / drawn:F1}");

        var hasTex = _renderer.TextureCount > 0;
        var ok = drawn > 5000 && shades > 20 && (!hasTex || tones > 30);
        Log(ok
            ? "PASS teapot.glb rendered, lit and textured on the android gles3 path"
            : "FAIL the model did not render the way a lit, textured mesh should");
    }
}
