using System.Runtime.InteropServices;
using System.Text;
using Android.Content.PM;
using Android.Graphics;
using Android.Opengl;
using Javax.Microedition.Khronos.Opengles;
// Both Android.Opengl and Javax...Khronos.Egl define EGLConfig, and GLSurfaceView.IRenderer wants
// the Khronos one. Aliasing rather than dropping a using: Android.Opengl is where GLSurfaceView
// itself lives, so neither namespace can go.
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;

namespace CupriFace.Experiments.GlProbe.Android;

// The Android leg. Portable half identical to the other two (shared/Gltf.cs is linked into all
// three); what differs is, again, only how the GL entry points arrive — and Android is a THIRD
// answer:
//
//   web     — Emscripten's symbols are static, DirectPInvoke binds them at link time
//   desktop — opengl32 exports GL 1.1 only, everything modern is wglGetProcAddress function pointers
//   android — libGLESv3.so EXPORTS the GLES 3 entry points, so a plain DllImport binds them
//
// Three mechanisms, one call shape. That is the finding this leg is for: the seam a portable
// renderer needs is small and lives entirely in how the address is obtained.
//
// Note what is NOT imported here: an image codec. Android's own BitmapFactory decodes the glb's
// embedded JPEG, exactly as Skia does on the other two hosts. Every platform already has a decoder;
// a renderer that ships one has taken a dependency it never needed.

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
    private const string Gl = "libGLESv3.so";

    private static void Log(string m) => global::Android.Util.Log.Info(Tag, m);

    // Plain DllImports: libGLESv3.so exports these, so there is no proc-address dance at all.
    [DllImport(Gl)] private static extern byte* glGetString(uint name);
    [DllImport(Gl)] private static extern void glViewport(int x, int y, int w, int h);
    [DllImport(Gl)] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport(Gl)] private static extern void glClear(uint mask);
    [DllImport(Gl)] private static extern void glEnable(uint cap);
    [DllImport(Gl)] private static extern void glDepthFunc(uint f);
    [DllImport(Gl)] private static extern uint glCreateShader(uint type);
    [DllImport(Gl)] private static extern void glShaderSource(uint s, int c, byte** str, int* len);
    [DllImport(Gl)] private static extern void glCompileShader(uint s);
    [DllImport(Gl)] private static extern void glGetShaderiv(uint s, uint p, int* v);
    [DllImport(Gl)] private static extern void glGetShaderInfoLog(uint s, int max, int* len, byte* log);
    [DllImport(Gl)] private static extern uint glCreateProgram();
    [DllImport(Gl)] private static extern void glAttachShader(uint p, uint s);
    [DllImport(Gl)] private static extern void glLinkProgram(uint p);
    [DllImport(Gl)] private static extern void glGetProgramiv(uint p, uint n, int* v);
    [DllImport(Gl)] private static extern void glGetProgramInfoLog(uint p, int max, int* len, byte* log);
    [DllImport(Gl)] private static extern void glUseProgram(uint p);
    [DllImport(Gl)] private static extern void glGenVertexArrays(int n, uint* a);
    [DllImport(Gl)] private static extern void glBindVertexArray(uint a);
    [DllImport(Gl)] private static extern void glGenBuffers(int n, uint* b);
    [DllImport(Gl)] private static extern void glBindBuffer(uint t, uint b);
    [DllImport(Gl)] private static extern void glBufferData(uint t, nint size, void* d, uint usage);
    [DllImport(Gl)] private static extern void glVertexAttribPointer(uint i, int size, uint type, byte norm, int stride, void* ptr);
    [DllImport(Gl)] private static extern void glEnableVertexAttribArray(uint i);
    [DllImport(Gl)] private static extern void glBindAttribLocation(uint p, uint i, byte* name);
    [DllImport(Gl)] private static extern int glGetUniformLocation(uint p, byte* name);
    [DllImport(Gl)] private static extern void glUniformMatrix4fv(int loc, int n, byte tr, float* v);
    [DllImport(Gl)] private static extern void glUniform4f(int loc, float a, float b, float c, float d);
    [DllImport(Gl)] private static extern void glUniform1i(int loc, int v);
    [DllImport(Gl)] private static extern void glGenTextures(int n, uint* t);
    [DllImport(Gl)] private static extern void glBindTexture(uint target, uint t);
    [DllImport(Gl)] private static extern void glTexParameteri(uint target, uint p, int v);
    [DllImport(Gl)] private static extern void glTexImage2D(uint t, int lvl, int ifmt, int w, int h, int b, uint fmt, uint type, void* px);
    [DllImport(Gl)] private static extern void glGenerateMipmap(uint target);
    [DllImport(Gl)] private static extern void glDrawElements(uint mode, int count, uint type, void* idx);
    [DllImport(Gl)] private static extern void glReadPixels(int x, int y, int w, int h, uint f, uint t, void* p);
    [DllImport(Gl)] private static extern uint glGetError();

    private const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    private const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    private const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4;
    private const uint FLOAT = 0x1406, TRIANGLES = 0x0004, UNSIGNED_INT = 0x1405;
    private const uint RGBA = 0x1908, UNSIGNED_BYTE = 0x1401;
    private const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    private const uint VERSION = 0x1F02, RENDERER = 0x1F01, DEPTH_TEST = 0x0B71, LESS = 0x0201;
    private const uint TEXTURE_2D = 0x0DE1, RGBA8 = 0x8058;
    private const uint TEX_MIN_FILTER = 0x2801, TEX_MAG_FILTER = 0x2800, TEX_WRAP_S = 0x2802, TEX_WRAP_T = 0x2803;
    private const int LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, REPEAT = 0x2901;

    private Gltf? _model;
    private uint _prog;
    private int _mvpLoc, _hasTex;
    private int _w = 1, _h = 1;
    private int _frame;
    private bool _reported;

    private static string Str(byte* p) => p is null ? "(null)" : Marshal.PtrToStringUTF8((nint)p) ?? "(null)";

    private static void Source(uint shader, string src)
    {
        var bytes = Encoding.UTF8.GetBytes(src + "\0");
        fixed (byte* p = bytes)
        {
            byte** one = stackalloc byte*[1];
            one[0] = p;
            glShaderSource(shader, 1, one, null);
        }
    }

    private static uint Compile(uint type, string src, string label)
    {
        var s = glCreateShader(type);
        Source(s, src);
        glCompileShader(s);
        int ok; glGetShaderiv(s, COMPILE_STATUS, &ok);
        if (ok != 0) return s;
        var log = stackalloc byte[1024];
        glGetShaderInfoLog(s, 1024, null, log);
        Log($"FAIL {label} shader: {Str(log)}");
        return 0;
    }

    private static int Uniform(uint prog, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) return glGetUniformLocation(prog, p);
    }

    private static void Attrib(uint prog, uint index, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) glBindAttribLocation(prog, index, p);
    }

    private static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fovY / 2f);
        var m = new float[16];
        m[0] = f / aspect; m[5] = f;
        m[10] = (far + near) / (near - far); m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    private static float[] LookAt(float ex, float ey, float ez, float cx, float cy, float cz)
    {
        float zx = ex - cx, zy = ey - cy, zz = ez - cz;
        var zl = MathF.Sqrt(zx * zx + zy * zy + zz * zz); zx /= zl; zy /= zl; zz /= zl;
        float xx = zz, xy = 0f, xz = -zx;
        var xl = MathF.Sqrt(xx * xx + xy * xy + xz * xz); xx /= xl; xy /= xl; xz /= xl;
        float yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
        return new float[16]
        {
            xx, yx, zx, 0, xy, yy, zy, 0, xz, yz, zz, 0,
            -(xx * ex + xy * ey + xz * ez), -(yx * ex + yy * ey + yz * ez), -(zx * ex + zy * ey + zz * ez), 1,
        };
    }

    private byte[] ReadAsset(string name)
    {
        using var s = assets.Open(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
    {
        try
        {
            var bytes = ReadAsset("teapot.glb");
            _model = Gltf.Load(bytes);
            Log($"glb {bytes.Length:n0} bytes -> {_model.Vertices.Length / 8:n0} vertices, "
                + $"{_model.Indices.Length / 3:n0} triangles, uv={_model.HasUv}, "
                + $"texture={(_model.BaseColorImage is null ? "none" : $"{_model.BaseColorImage.Length:n0} encoded bytes")}");
        }
        catch (Exception ex) { Log($"FAIL load: {ex.Message}"); return; }

        Log($"GL_VERSION  = {Str(glGetString(VERSION))}");
        Log($"GL_RENDERER = {Str(glGetString(RENDERER))}");

        // GLSL ES 300 — the SAME shader source the web leg runs, because WebGL2 is GLES 3.0. The
        // desktop leg is the odd one out, needing #version 330 core.
        var vs = Compile(VERTEX_SHADER, """
            #version 300 es
            in vec3 aPos;
            in vec3 aNormal;
            in vec2 aUv;
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec2 vUv;
            void main() { vNormal = aNormal; vUv = aUv; gl_Position = uMvp * vec4(aPos, 1.0); }
            """, "vertex");
        var fs = Compile(FRAGMENT_SHADER, """
            #version 300 es
            precision highp float;
            in vec3 vNormal;
            in vec2 vUv;
            uniform vec4 uColor;
            uniform sampler2D uTex;
            uniform int uHasTex;
            out vec4 fragColor;
            void main() {
                vec3 n = normalize(vNormal);
                vec3 l = normalize(vec3(0.35, 0.75, 0.55));
                float d = max(dot(n, l), 0.0);
                vec3 albedo = uColor.rgb;
                if (uHasTex == 1) albedo *= texture(uTex, vUv).rgb;
                fragColor = vec4(albedo * (0.22 + 0.85 * d), uColor.a);
            }
            """, "fragment");
        if (vs == 0 || fs == 0) return;

        _prog = glCreateProgram();
        glAttachShader(_prog, vs); glAttachShader(_prog, fs);
        Attrib(_prog, 0, "aPos"); Attrib(_prog, 1, "aNormal"); Attrib(_prog, 2, "aUv");
        glLinkProgram(_prog);
        int linked; glGetProgramiv(_prog, LINK_STATUS, &linked);
        if (linked == 0)
        {
            var log = stackalloc byte[1024];
            glGetProgramInfoLog(_prog, 1024, null, log);
            Log($"FAIL link: {Str(log)}");
            return;
        }
        glUseProgram(_prog);

        var m = _model!;
        uint vao, vbo, ebo;
        glGenVertexArrays(1, &vao); glBindVertexArray(vao);
        glGenBuffers(1, &vbo); glBindBuffer(ARRAY_BUFFER, vbo);
        fixed (float* v = m.Vertices) glBufferData(ARRAY_BUFFER, m.Vertices.Length * sizeof(float), v, STATIC_DRAW);
        var stride = 8 * sizeof(float);
        glVertexAttribPointer(0, 3, FLOAT, 0, stride, (void*)0); glEnableVertexAttribArray(0);
        glVertexAttribPointer(1, 3, FLOAT, 0, stride, (void*)(3 * sizeof(float))); glEnableVertexAttribArray(1);
        glVertexAttribPointer(2, 2, FLOAT, 0, stride, (void*)(6 * sizeof(float))); glEnableVertexAttribArray(2);
        glGenBuffers(1, &ebo); glBindBuffer(ELEMENT_ARRAY_BUFFER, ebo);
        fixed (uint* i = m.Indices) glBufferData(ELEMENT_ARRAY_BUFFER, m.Indices.Length * sizeof(uint), i, STATIC_DRAW);

        // ---- the texture, decoded by the PLATFORM ------------------------------------------------
        // BitmapFactory, not a codec of ours. Bitmap hands back ARGB ints; GL wants RGBA bytes, so
        // the swizzle is explicit rather than hoped for — the desktop leg needed the mirror of this
        // (Skia decoded BGRA there and RGBA on the web), which is exactly the kind of difference
        // that silently swaps red and blue if assumed.
        if (m.BaseColorImage is { Length: > 0 } encoded)
        {
            using var bmp = BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length);
            if (bmp is null) Log("WARN the embedded image did not decode");
            else
            {
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
                Log($"texture decoded {tw}x{th} by BitmapFactory -> rgba8888");
                uint tex; glGenTextures(1, &tex);
                glBindTexture(TEXTURE_2D, tex);
                fixed (byte* p = rgba)
                    glTexImage2D(TEXTURE_2D, 0, (int)RGBA8, tw, th, 0, RGBA, UNSIGNED_BYTE, p);
                glGenerateMipmap(TEXTURE_2D);
                glTexParameteri(TEXTURE_2D, TEX_MIN_FILTER, LINEAR_MIPMAP_LINEAR);
                glTexParameteri(TEXTURE_2D, TEX_MAG_FILTER, LINEAR);
                glTexParameteri(TEXTURE_2D, TEX_WRAP_S, REPEAT);
                glTexParameteri(TEXTURE_2D, TEX_WRAP_T, REPEAT);
                _hasTex = 1;
            }
        }

        glEnable(DEPTH_TEST); glDepthFunc(LESS);
        _mvpLoc = Uniform(_prog, "uMvp");
        glUniform4f(Uniform(_prog, "uColor"), m.BaseColor[0], m.BaseColor[1], m.BaseColor[2], m.BaseColor[3]);
        glUniform1i(Uniform(_prog, "uTex"), 0);
        glUniform1i(Uniform(_prog, "uHasTex"), _hasTex);
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _w = Math.Max(1, width); _h = Math.Max(1, height);
        glViewport(0, 0, _w, _h);
    }

    public void OnDrawFrame(IGL10? gl)
    {
        if (_model is not { } m || _prog == 0) return;

        float cx = (m.Min[0] + m.Max[0]) / 2f, cy = (m.Min[1] + m.Max[1]) / 2f, cz = (m.Min[2] + m.Max[2]) / 2f;
        float dx = m.Max[0] - m.Min[0], dy = m.Max[1] - m.Min[1], dz = m.Max[2] - m.Min[2];
        var radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 2f;
        var fov = 45f * MathF.PI / 180f;
        var dist = radius / MathF.Sin(fov / 2f) * 1.25f;

        // Orbit, so the CI-style "did it change?" check has something to see and a human watching
        // the device can tell it is live rather than a still.
        var angle = 0.6f + _frame * 0.02f;
        var proj = Perspective(fov, (float)_w / _h, radius * 0.01f, dist + radius * 4f);
        var view = LookAt(cx + MathF.Sin(angle) * dist, cy + dist * 0.35f, cz + MathF.Cos(angle) * dist, cx, cy, cz);
        var mvp = Gltf.Multiply(proj, view);
        fixed (float* p = mvp) glUniformMatrix4fv(_mvpLoc, 1, 0, p);

        glClearColor(0.055f, 0.067f, 0.086f, 1f);
        glClear(COLOR_BUFFER_BIT | DEPTH_BUFFER_BIT);
        glDrawElements(TRIANGLES, m.Indices.Length, UNSIGNED_INT, (void*)0);

        // Report once, a few frames in so the surface has certainly settled. Same statistics the
        // other two legs print, so the three can be compared directly instead of each merely
        // "looking right" on its own.
        if (_frame++ != 5 || _reported) return;
        _reported = true;

        var err = glGetError();
        if (err != 0) { Log($"FAIL glGetError 0x{err:X}"); return; }

        var pixels = new byte[_w * _h * 4];
        fixed (byte* p = pixels) glReadPixels(0, 0, _w, _h, RGBA, UNSIGNED_BYTE, p);

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

        var ok = drawn > 5000 && shades > 20 && (_hasTex == 0 || tones > 30);
        Log(ok
            ? "PASS teapot.glb rendered, lit and textured on the android gles3 path"
            : "FAIL the model did not render the way a lit, textured mesh should");
    }
}
