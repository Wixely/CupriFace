using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Windowing;
using SkiaSharp;

// The desktop leg of the same question. The PORTABLE half — glb parsing, the scene graph, the
// shaders, the draw — is byte-identical to the web probe (experiments/shared/Gltf.cs is linked into
// both, and the GL calls below are the same sequence). Only one thing differs, and it is the whole
// reason this leg exists:
//
//   On the web, Emscripten's GL symbols are STATIC and DirectPInvoke resolves them at link time.
//   On Windows, opengl32.dll exports OpenGL 1.1 and nothing newer. glCreateShader, glGenBuffers,
//   glDrawElements — every modern entry point — must be fetched at run time through
//   wglGetProcAddress, whose result is only valid for the context that was current when it was
//   asked. A DllImport cannot express that, so the bindings become function pointers.
//
// If a single renderer is going to serve both, that difference has to live behind one seam. This
// proves the seam is small: a table of delegate* filled once after the context is current.

internal static unsafe class DesktopProbe
{
    private const int W = 480, H = 480;

    // ---- the function-pointer table ------------------------------------------------------------
    // Deliberately raw delegate*, not a generated binding: it is the same call shape the web probe
    // gets from DirectPInvoke, so the two legs differ in HOW the address arrives and in nothing else.
    private static delegate* unmanaged<uint, byte*> glGetString;
    private static delegate* unmanaged<int, int, int, int, void> glViewport;
    private static delegate* unmanaged<float, float, float, float, void> glClearColor;
    private static delegate* unmanaged<uint, void> glClear;
    private static delegate* unmanaged<uint, void> glEnable;
    private static delegate* unmanaged<uint, void> glDepthFunc;
    private static delegate* unmanaged<uint, uint> glCreateShader;
    private static delegate* unmanaged<uint, int, byte**, int*, void> glShaderSource;
    private static delegate* unmanaged<uint, void> glCompileShader;
    private static delegate* unmanaged<uint, uint, int*, void> glGetShaderiv;
    private static delegate* unmanaged<uint, int, int*, byte*, void> glGetShaderInfoLog;
    private static delegate* unmanaged<uint> glCreateProgram;
    private static delegate* unmanaged<uint, uint, void> glAttachShader;
    private static delegate* unmanaged<uint, void> glLinkProgram;
    private static delegate* unmanaged<uint, uint, int*, void> glGetProgramiv;
    private static delegate* unmanaged<uint, int, int*, byte*, void> glGetProgramInfoLog;
    private static delegate* unmanaged<uint, void> glUseProgram;
    private static delegate* unmanaged<int, uint*, void> glGenVertexArrays;
    private static delegate* unmanaged<uint, void> glBindVertexArray;
    private static delegate* unmanaged<int, uint*, void> glGenBuffers;
    private static delegate* unmanaged<uint, uint, void> glBindBuffer;
    private static delegate* unmanaged<uint, nint, void*, uint, void> glBufferData;
    private static delegate* unmanaged<uint, int, uint, byte, int, void*, void> glVertexAttribPointer;
    private static delegate* unmanaged<uint, void> glEnableVertexAttribArray;
    private static delegate* unmanaged<uint, uint, byte*, void> glBindAttribLocation;
    private static delegate* unmanaged<uint, byte*, int> glGetUniformLocation;
    private static delegate* unmanaged<int, int, byte, float*, void> glUniformMatrix4fv;
    private static delegate* unmanaged<int, float, float, float, float, void> glUniform4f;
    private static delegate* unmanaged<int, int, void> glUniform1i;
    private static delegate* unmanaged<int, uint*, void> glGenTextures;
    private static delegate* unmanaged<uint, uint, void> glBindTexture;
    private static delegate* unmanaged<uint, uint, int, void> glTexParameteri;
    private static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> glTexImage2D;
    private static delegate* unmanaged<uint, void> glGenerateMipmap;
    private static delegate* unmanaged<uint, int, uint, void*, void> glDrawElements;
    private static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> glReadPixels;
    private static delegate* unmanaged<uint> glGetError;

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

    private static readonly List<string> Missing = [];

    private static nint Proc(IWindow w, string name)
    {
        // GLFW answers for core entry points on every platform; the ARB fallback is for drivers that
        // only publish the extension spelling.
        if (w.GLContext!.TryGetProcAddress(name, out var p) && p != 0) return p;
        if (w.GLContext.TryGetProcAddress(name + "ARB", out p) && p != 0) return p;
        Missing.Add(name);
        return 0;
    }

    private static string Str(byte* p) => p is null ? "(null)" : Marshal.PtrToStringUTF8((nint)p) ?? "(null)";

    private static void Load(IWindow w)
    {
        glGetString = (delegate* unmanaged<uint, byte*>)Proc(w, "glGetString");
        glViewport = (delegate* unmanaged<int, int, int, int, void>)Proc(w, "glViewport");
        glClearColor = (delegate* unmanaged<float, float, float, float, void>)Proc(w, "glClearColor");
        glClear = (delegate* unmanaged<uint, void>)Proc(w, "glClear");
        glEnable = (delegate* unmanaged<uint, void>)Proc(w, "glEnable");
        glDepthFunc = (delegate* unmanaged<uint, void>)Proc(w, "glDepthFunc");
        glCreateShader = (delegate* unmanaged<uint, uint>)Proc(w, "glCreateShader");
        glShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Proc(w, "glShaderSource");
        glCompileShader = (delegate* unmanaged<uint, void>)Proc(w, "glCompileShader");
        glGetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)Proc(w, "glGetShaderiv");
        glGetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc(w, "glGetShaderInfoLog");
        glCreateProgram = (delegate* unmanaged<uint>)Proc(w, "glCreateProgram");
        glAttachShader = (delegate* unmanaged<uint, uint, void>)Proc(w, "glAttachShader");
        glLinkProgram = (delegate* unmanaged<uint, void>)Proc(w, "glLinkProgram");
        glGetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)Proc(w, "glGetProgramiv");
        glGetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc(w, "glGetProgramInfoLog");
        glUseProgram = (delegate* unmanaged<uint, void>)Proc(w, "glUseProgram");
        glGenVertexArrays = (delegate* unmanaged<int, uint*, void>)Proc(w, "glGenVertexArrays");
        glBindVertexArray = (delegate* unmanaged<uint, void>)Proc(w, "glBindVertexArray");
        glGenBuffers = (delegate* unmanaged<int, uint*, void>)Proc(w, "glGenBuffers");
        glBindBuffer = (delegate* unmanaged<uint, uint, void>)Proc(w, "glBindBuffer");
        glBufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)Proc(w, "glBufferData");
        glVertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)Proc(w, "glVertexAttribPointer");
        glEnableVertexAttribArray = (delegate* unmanaged<uint, void>)Proc(w, "glEnableVertexAttribArray");
        glBindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)Proc(w, "glBindAttribLocation");
        glGetUniformLocation = (delegate* unmanaged<uint, byte*, int>)Proc(w, "glGetUniformLocation");
        glUniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)Proc(w, "glUniformMatrix4fv");
        glUniform4f = (delegate* unmanaged<int, float, float, float, float, void>)Proc(w, "glUniform4f");
        glUniform1i = (delegate* unmanaged<int, int, void>)Proc(w, "glUniform1i");
        glGenTextures = (delegate* unmanaged<int, uint*, void>)Proc(w, "glGenTextures");
        glBindTexture = (delegate* unmanaged<uint, uint, void>)Proc(w, "glBindTexture");
        glTexParameteri = (delegate* unmanaged<uint, uint, int, void>)Proc(w, "glTexParameteri");
        glTexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)Proc(w, "glTexImage2D");
        glGenerateMipmap = (delegate* unmanaged<uint, void>)Proc(w, "glGenerateMipmap");
        glDrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)Proc(w, "glDrawElements");
        glReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)Proc(w, "glReadPixels");
        glGetError = (delegate* unmanaged<uint>)Proc(w, "glGetError");
    }

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
        Console.WriteLine($"glprobe: FAIL {label} shader: {Str(log)}");
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
            xx, yx, zx, 0,
            xy, yy, zy, 0,
            xz, yz, zz, 0,
            -(xx * ex + xy * ey + xz * ez),
            -(yx * ex + yy * ey + yz * ez),
            -(zx * ex + zy * ey + zz * ez), 1,
        };
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("glprobe: start (desktop)");

        var glb = Path.Combine(AppContext.BaseDirectory, "teapot.glb");
        if (!File.Exists(glb)) { Console.WriteLine($"glprobe: FAIL asset missing: {glb}"); return 1; }

        Gltf model;
        try
        {
            var bytes = File.ReadAllBytes(glb);
            model = Gltf.Load(bytes);
            Console.WriteLine($"glprobe: glb {bytes.Length:n0} bytes -> {model.Vertices.Length / 8:n0} vertices, "
                + $"{model.Indices.Length / 3:n0} triangles, uv={model.HasUv}, "
                + $"texture={(model.BaseColorImage is null ? "none" : $"{model.BaseColorImage.Length:n0} encoded bytes")}");
        }
        catch (Exception ex) { Console.WriteLine($"glprobe: FAIL load: {ex.Message}"); return 1; }

        // Hidden window: this is a headless check, and a window that steals focus on a dev machine
        // is a probe nobody runs twice. The context is real either way.
        var opts = WindowOptions.Default with
        {
            Size = new(W, H),
            Title = "glprobe",
            IsVisible = args.Contains("--show"),
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
        };

        IWindow? window = null;
        var exit = 1;
        try
        {
            window = Window.Create(opts);
            window.Initialize();
        }
        catch (Exception ex)
        {
            // The honest outcome on a machine with no usable GL, which this repo already knows is
            // common: virtualised GPUs, RDP sessions, and CI runners all land here.
            Console.WriteLine($"glprobe: NO-GL could not create an OpenGL window: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        try
        {
            window.MakeCurrent();
            Load(window);
            if (Missing.Count > 0)
            {
                Console.WriteLine($"glprobe: FAIL {Missing.Count} entry point(s) did not resolve: {string.Join(", ", Missing)}");
                return 1;
            }
            Console.WriteLine($"glprobe: all {37} entry points resolved through wglGetProcAddress/glXGetProcAddress");
            Console.WriteLine($"glprobe: GL_VERSION  = {Str(glGetString(VERSION))}");
            Console.WriteLine($"glprobe: GL_RENDERER = {Str(glGetString(RENDERER))}");

            // GLSL 330 core is the desktop spelling of the same shader the web leg runs as
            // GLSL ES 300. Identical logic; the version line and the precision qualifier differ,
            // which is exactly the portability tax a real engine would have to pay here.
            var vs = Compile(VERTEX_SHADER, """
                #version 330 core
                in vec3 aPos;
                in vec3 aNormal;
                in vec2 aUv;
                uniform mat4 uMvp;
                out vec3 vNormal;
                out vec2 vUv;
                void main() { vNormal = aNormal; vUv = aUv; gl_Position = uMvp * vec4(aPos, 1.0); }
                """, "vertex");
            var fs = Compile(FRAGMENT_SHADER, """
                #version 330 core
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
            if (vs == 0 || fs == 0) return 1;

            var prog = glCreateProgram();
            glAttachShader(prog, vs); glAttachShader(prog, fs);
            Attrib(prog, 0, "aPos"); Attrib(prog, 1, "aNormal"); Attrib(prog, 2, "aUv");
            glLinkProgram(prog);
            int linked; glGetProgramiv(prog, LINK_STATUS, &linked);
            if (linked == 0)
            {
                var log = stackalloc byte[1024];
                glGetProgramInfoLog(prog, 1024, null, log);
                Console.WriteLine($"glprobe: FAIL link: {Str(log)}");
                return 1;
            }
            glUseProgram(prog);

            uint vao, vbo, ebo;
            glGenVertexArrays(1, &vao); glBindVertexArray(vao);
            glGenBuffers(1, &vbo); glBindBuffer(ARRAY_BUFFER, vbo);
            fixed (float* v = model.Vertices)
                glBufferData(ARRAY_BUFFER, model.Vertices.Length * sizeof(float), v, STATIC_DRAW);
            var stride = 8 * sizeof(float);
            glVertexAttribPointer(0, 3, FLOAT, 0, stride, (void*)0); glEnableVertexAttribArray(0);
            glVertexAttribPointer(1, 3, FLOAT, 0, stride, (void*)(3 * sizeof(float))); glEnableVertexAttribArray(1);
            glVertexAttribPointer(2, 2, FLOAT, 0, stride, (void*)(6 * sizeof(float))); glEnableVertexAttribArray(2);
            glGenBuffers(1, &ebo); glBindBuffer(ELEMENT_ARRAY_BUFFER, ebo);
            fixed (uint* i = model.Indices)
                glBufferData(ELEMENT_ARRAY_BUFFER, model.Indices.Length * sizeof(uint), i, STATIC_DRAW);

            var hasTex = 0;
            if (model.BaseColorImage is { Length: > 0 } encoded)
            {
                using var decoded = SKBitmap.Decode(encoded);
                if (decoded is null) Console.WriteLine("glprobe: WARN the embedded image did not decode");
                else
                {
                    using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888
                        ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
                    Console.WriteLine($"glprobe: texture decoded {rgba.Width}x{rgba.Height} "
                        + $"(source {decoded.Info.ColorType}) -> rgba8888");
                    uint tex; glGenTextures(1, &tex);
                    glBindTexture(TEXTURE_2D, tex);
                    glTexImage2D(TEXTURE_2D, 0, (int)RGBA8, rgba.Width, rgba.Height, 0, RGBA, UNSIGNED_BYTE,
                                 (void*)rgba.GetPixels());
                    glGenerateMipmap(TEXTURE_2D);
                    glTexParameteri(TEXTURE_2D, TEX_MIN_FILTER, LINEAR_MIPMAP_LINEAR);
                    glTexParameteri(TEXTURE_2D, TEX_MAG_FILTER, LINEAR);
                    glTexParameteri(TEXTURE_2D, TEX_WRAP_S, REPEAT);
                    glTexParameteri(TEXTURE_2D, TEX_WRAP_T, REPEAT);
                    hasTex = 1;
                }
            }

            glEnable(DEPTH_TEST); glDepthFunc(LESS);
            glViewport(0, 0, W, H);

            float cx = (model.Min[0] + model.Max[0]) / 2f, cy = (model.Min[1] + model.Max[1]) / 2f;
            float cz = (model.Min[2] + model.Max[2]) / 2f;
            float dx = model.Max[0] - model.Min[0], dy = model.Max[1] - model.Min[1], dz = model.Max[2] - model.Min[2];
            var radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 2f;
            var fov = 45f * MathF.PI / 180f;
            var dist = radius / MathF.Sin(fov / 2f) * 1.25f;
            var proj = Perspective(fov, (float)W / H, radius * 0.01f, dist + radius * 4f);
            var mvpLoc = Uniform(prog, "uMvp");
            glUniform4f(Uniform(prog, "uColor"), model.BaseColor[0], model.BaseColor[1], model.BaseColor[2], model.BaseColor[3]);
            glUniform1i(Uniform(prog, "uTex"), 0);
            glUniform1i(Uniform(prog, "uHasTex"), hasTex);

            var pixels = new byte[W * H * 4];
            int DrawAt(float angle)
            {
                var view = LookAt(cx + MathF.Sin(angle) * dist, cy + dist * 0.35f, cz + MathF.Cos(angle) * dist, cx, cy, cz);
                var mvp = Gltf.Multiply(proj, view);
                fixed (float* m = mvp) glUniformMatrix4fv(mvpLoc, 1, 0, m);
                glClearColor(0.055f, 0.067f, 0.086f, 1f);
                glClear(COLOR_BUFFER_BIT | DEPTH_BUFFER_BIT);
                glDrawElements(TRIANGLES, model.Indices.Length, UNSIGNED_INT, (void*)0);
                var err = glGetError();
                if (err != 0) { Console.WriteLine($"glprobe: FAIL glGetError 0x{err:X}"); return -1; }
                fixed (byte* p = pixels) glReadPixels(0, 0, W, H, RGBA, UNSIGNED_BYTE, p);
                return 0;
            }

            if (DrawAt(0.6f) != 0) return 1;

            var background = 0; var drawn = 0;
            var levels = new bool[256]; var reds = new bool[256];
            long sr = 0, sg = 0, sb = 0;
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

            Console.WriteLine($"glprobe: model pixels = {drawn:n0} ({100.0 * drawn / (W * H):F1}% of frame)");
            Console.WriteLine($"glprobe: distinct luminance levels = {shades}, distinct red levels = {tones}");
            if (drawn > 0)
                // The same mean the web leg reports, so the two can be compared directly rather than
                // each merely "looking right" on its own.
                Console.WriteLine($"glprobe: mean rgb over model pixels = {(double)sr / drawn:F1},{(double)sg / drawn:F1},{(double)sb / drawn:F1}");

            var first = new byte[pixels.Length];
            Array.Copy(pixels, first, pixels.Length);
            if (DrawAt(2.2f) != 0) return 1;
            var moved = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                if (Math.Abs(pixels[i] - first[i]) > 8 || Math.Abs(pixels[i + 1] - first[i + 1]) > 8) moved++;
            Console.WriteLine($"glprobe: pixels changed when the camera orbited = {moved:n0}");

            var ok = drawn > 5000 && shades > 20 && moved > 5000 && (hasTex == 0 || tones > 30);
            Console.WriteLine(ok
                ? "glprobe: PASS teapot.glb rendered, lit and textured on the desktop GL path"
                : "glprobe: FAIL the model did not render the way a lit, textured, orbitable mesh should");
            exit = ok ? 0 : 1;
        }
        finally { window?.Dispose(); }
        return exit;
    }
}
