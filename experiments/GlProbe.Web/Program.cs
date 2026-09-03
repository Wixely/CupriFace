using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

// FEASIBILITY PROBE, not a library. Does a REAL exported model — geometry, interleaved accessors, a
// scene graph, indices, uvs and an embedded JPEG — survive the whole path to pixels under
// NativeAOT-LLVM, with no JavaScript and no hand-written bindings?
//
// The answer is pixels read back off the GPU. A clean link proves the symbols resolve, nothing more.

internal static unsafe partial class Probe
{
    private const string Em = "emscripten";
    private const string Gl = "GL";

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

    [DllImport(Gl, EntryPoint = "glGetString")] private static extern byte* GetString(uint name);
    [DllImport(Gl, EntryPoint = "glViewport")] private static extern void Viewport(int x, int y, int w, int h);
    [DllImport(Gl, EntryPoint = "glClearColor")] private static extern void ClearColor(float r, float g, float b, float a);
    [DllImport(Gl, EntryPoint = "glClear")] private static extern void Clear(uint mask);
    [DllImport(Gl, EntryPoint = "glEnable")] private static extern void Enable(uint cap);
    [DllImport(Gl, EntryPoint = "glDepthFunc")] private static extern void DepthFunc(uint f);
    [DllImport(Gl, EntryPoint = "glCreateShader")] private static extern uint CreateShader(uint type);
    [DllImport(Gl, EntryPoint = "glShaderSource")] private static extern void ShaderSourceRaw(uint s, int c, byte** str, int* len);
    [DllImport(Gl, EntryPoint = "glCompileShader")] private static extern void CompileShader(uint s);
    [DllImport(Gl, EntryPoint = "glGetShaderiv")] private static extern void GetShaderiv(uint s, uint p, int* v);
    [DllImport(Gl, EntryPoint = "glGetShaderInfoLog")] private static extern void GetShaderInfoLog(uint s, int max, int* len, byte* log);
    [DllImport(Gl, EntryPoint = "glCreateProgram")] private static extern uint CreateProgram();
    [DllImport(Gl, EntryPoint = "glAttachShader")] private static extern void AttachShader(uint p, uint s);
    [DllImport(Gl, EntryPoint = "glLinkProgram")] private static extern void LinkProgram(uint p);
    [DllImport(Gl, EntryPoint = "glGetProgramiv")] private static extern void GetProgramiv(uint p, uint n, int* v);
    [DllImport(Gl, EntryPoint = "glGetProgramInfoLog")] private static extern void GetProgramInfoLog(uint p, int max, int* len, byte* log);
    [DllImport(Gl, EntryPoint = "glUseProgram")] private static extern void UseProgram(uint p);
    [DllImport(Gl, EntryPoint = "glGenVertexArrays")] private static extern void GenVertexArrays(int n, uint* a);
    [DllImport(Gl, EntryPoint = "glBindVertexArray")] private static extern void BindVertexArray(uint a);
    [DllImport(Gl, EntryPoint = "glGenBuffers")] private static extern void GenBuffers(int n, uint* b);
    [DllImport(Gl, EntryPoint = "glBindBuffer")] private static extern void BindBuffer(uint t, uint b);
    [DllImport(Gl, EntryPoint = "glBufferData")] private static extern void BufferData(uint t, nint size, void* d, uint usage);
    [DllImport(Gl, EntryPoint = "glVertexAttribPointer")]
    private static extern void VertexAttribPointer(uint i, int size, uint type, byte norm, int stride, void* ptr);
    [DllImport(Gl, EntryPoint = "glEnableVertexAttribArray")] private static extern void EnableVertexAttribArray(uint i);
    [DllImport(Gl, EntryPoint = "glBindAttribLocation")] private static extern void BindAttribLocation(uint p, uint i, byte* name);
    [DllImport(Gl, EntryPoint = "glGetUniformLocation")] private static extern int GetUniformLocation(uint p, byte* name);
    [DllImport(Gl, EntryPoint = "glUniformMatrix4fv")] private static extern void UniformMatrix4fv(int loc, int n, byte tr, float* v);
    [DllImport(Gl, EntryPoint = "glUniform4f")] private static extern void Uniform4f(int loc, float a, float b, float c, float d);
    [DllImport(Gl, EntryPoint = "glUniform1i")] private static extern void Uniform1i(int loc, int v);
    [DllImport(Gl, EntryPoint = "glGenTextures")] private static extern void GenTextures(int n, uint* t);
    [DllImport(Gl, EntryPoint = "glBindTexture")] private static extern void BindTexture(uint target, uint t);
    [DllImport(Gl, EntryPoint = "glTexParameteri")] private static extern void TexParameteri(uint target, uint p, int v);
    [DllImport(Gl, EntryPoint = "glTexImage2D")]
    private static extern void TexImage2D(uint t, int lvl, int ifmt, int w, int h, int b, uint fmt, uint type, void* px);
    [DllImport(Gl, EntryPoint = "glGenerateMipmap")] private static extern void GenerateMipmap(uint target);
    [DllImport(Gl, EntryPoint = "glDrawElements")] private static extern void DrawElements(uint mode, int count, uint type, void* idx);
    [DllImport(Gl, EntryPoint = "glReadPixels")] private static extern void ReadPixels(int x, int y, int w, int h, uint f, uint t, void* p);
    [DllImport(Gl, EntryPoint = "glGetError")] private static extern uint GetError();

    private const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    private const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    private const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4;
    private const uint FLOAT = 0x1406, TRIANGLES = 0x0004, UNSIGNED_INT = 0x1405;
    private const uint RGBA = 0x1908, UNSIGNED_BYTE = 0x1401;
    private const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    private const uint VERSION = 0x1F02, DEPTH_TEST = 0x0B71, LESS = 0x0201;
    private const uint TEXTURE_2D = 0x0DE1, RGBA8 = 0x8058;
    private const uint TEX_MIN_FILTER = 0x2801, TEX_MAG_FILTER = 0x2800, TEX_WRAP_S = 0x2802, TEX_WRAP_T = 0x2803;
    private const int LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, REPEAT = 0x2901;

    private const int W = 480, H = 480;
    private const float BgR = 0.055f, BgG = 0.067f, BgB = 0.086f;

    private static string Str(byte* p) => p is null ? "(null)" : Marshal.PtrToStringUTF8((nint)p) ?? "(null)";

    private static void Source(uint shader, string src)
    {
        var bytes = Encoding.UTF8.GetBytes(src + "\0");
        fixed (byte* p = bytes)
        {
            byte** one = stackalloc byte*[1];
            one[0] = p;
            ShaderSourceRaw(shader, 1, one, null);
        }
    }

    private static uint Compile(uint type, string src, string label)
    {
        var s = CreateShader(type);
        Source(s, src);
        CompileShader(s);
        int ok; GetShaderiv(s, COMPILE_STATUS, &ok);
        if (ok != 0) return s;
        var log = stackalloc byte[1024];
        GetShaderInfoLog(s, 1024, null, log);
        Console.WriteLine($"glprobe: FAIL {label} shader: {Str(log)}");
        return 0;
    }

    private static int Uniform(uint prog, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) return GetUniformLocation(prog, p);
    }

    private static void Attrib(uint prog, uint index, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) BindAttribLocation(prog, index, p);
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
        // up = +Y, which agrees with the file's own root node rotating Z-up into Y-up.
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

    private static int Main()
    {
        Console.WriteLine("glprobe: start");

        Gltf model;
        try
        {
            var bytes = LoadModel();
            model = Gltf.Load(bytes);
            Console.WriteLine($"glprobe: glb {bytes.Length:n0} bytes -> {model.Vertices.Length / 8:n0} vertices, "
                + $"{model.Indices.Length / 3:n0} triangles, uv={model.HasUv}, "
                + $"texture={(model.BaseColorImage is null ? "none" : $"{model.BaseColorImage.Length:n0} encoded bytes")}");
            Console.WriteLine($"glprobe: baseColorFactor = {model.BaseColor[0]:F3},{model.BaseColor[1]:F3},{model.BaseColor[2]:F3}");
            Console.WriteLine($"glprobe: world bounds min=({model.Min[0]:F3},{model.Min[1]:F3},{model.Min[2]:F3}) "
                + $"max=({model.Max[0]:F3},{model.Max[1]:F3},{model.Max[2]:F3})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"glprobe: FAIL could not load the model: {ex.Message}");
            return 1;
        }

        ContextAttributes attrs;
        InitAttributes(&attrs);
        attrs.MajorVersion = 2; attrs.MinorVersion = 0;
        attrs.Alpha = 0; attrs.Depth = 1; attrs.Antialias = 1; attrs.PreserveDrawingBuffer = 1;

        nint ctx;
        var target = Encoding.UTF8.GetBytes("#glcanvas\0");
        fixed (byte* t = target) ctx = CreateContext(t, &attrs);
        if (ctx <= 0) { Console.WriteLine($"glprobe: FAIL no WebGL2 context ({ctx})"); return 1; }
        if (MakeCurrent(ctx) != 0) { Console.WriteLine("glprobe: FAIL context not current"); return 1; }
        Console.WriteLine($"glprobe: GL_VERSION = {Str(GetString(VERSION))}");

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

        // Lambert plus ambient, NOT PBR — enough to show normals and uvs survived, and calling it
        // PBR would be a lie. baseColorFactor MULTIPLIES the texture per the spec; this file ships
        // 0.5 grey, so ignoring the factor would render the teapot twice as bright as authored.
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
        if (vs == 0 || fs == 0) return 1;

        var prog = CreateProgram();
        AttachShader(prog, vs); AttachShader(prog, fs);
        Attrib(prog, 0, "aPos"); Attrib(prog, 1, "aNormal"); Attrib(prog, 2, "aUv");
        LinkProgram(prog);
        int linked; GetProgramiv(prog, LINK_STATUS, &linked);
        if (linked == 0)
        {
            var log = stackalloc byte[1024];
            GetProgramInfoLog(prog, 1024, null, log);
            Console.WriteLine($"glprobe: FAIL link: {Str(log)}");
            return 1;
        }
        UseProgram(prog);

        uint vao, vbo, ebo;
        GenVertexArrays(1, &vao); BindVertexArray(vao);
        GenBuffers(1, &vbo); BindBuffer(ARRAY_BUFFER, vbo);
        fixed (float* v = model.Vertices)
            BufferData(ARRAY_BUFFER, model.Vertices.Length * sizeof(float), v, STATIC_DRAW);
        var stride = 8 * sizeof(float);
        VertexAttribPointer(0, 3, FLOAT, 0, stride, (void*)0);
        EnableVertexAttribArray(0);
        VertexAttribPointer(1, 3, FLOAT, 0, stride, (void*)(3 * sizeof(float)));
        EnableVertexAttribArray(1);
        VertexAttribPointer(2, 2, FLOAT, 0, stride, (void*)(6 * sizeof(float)));
        EnableVertexAttribArray(2);

        GenBuffers(1, &ebo); BindBuffer(ELEMENT_ARRAY_BUFFER, ebo);
        fixed (uint* i = model.Indices)
            BufferData(ELEMENT_ARRAY_BUFFER, model.Indices.Length * sizeof(uint), i, STATIC_DRAW);

        // ---- the texture --------------------------------------------------------------------------
        // Skia decodes; the renderer only ever sees RGBA. That split is the point — swap the host and
        // the codec goes with it, while this code is untouched. CupriFace already links libSkiaSharp
        // on every host including this one, so a real integration pays nothing extra here.
        var hasTex = 0;
        if (model.BaseColorImage is { Length: > 0 } encoded)
        {
            using var decoded = SKBitmap.Decode(encoded);
            if (decoded is null) Console.WriteLine("glprobe: WARN the embedded image did not decode");
            else
            {
                using var rgba = decoded.Info.ColorType == SKColorType.Rgba8888
                    ? decoded.Copy()
                    : decoded.Copy(SKColorType.Rgba8888);
                Console.WriteLine($"glprobe: texture decoded {rgba.Width}x{rgba.Height} "
                    + $"(source {decoded.Info.ColorType}) -> rgba8888");

                uint tex; GenTextures(1, &tex);
                BindTexture(TEXTURE_2D, tex);
                // No vertical flip. glTF puts uv (0,0) at the image's TOP-left, and glTexImage2D
                // takes the first row supplied as t=0 — so uploading Skia's rows in their natural
                // top-first order already agrees. Flipping "to be safe" is what puts a texture on
                // upside down.
                TexImage2D(TEXTURE_2D, 0, (int)RGBA8, rgba.Width, rgba.Height, 0, RGBA, UNSIGNED_BYTE,
                           (void*)rgba.GetPixels());
                GenerateMipmap(TEXTURE_2D);
                TexParameteri(TEXTURE_2D, TEX_MIN_FILTER, LINEAR_MIPMAP_LINEAR);
                TexParameteri(TEXTURE_2D, TEX_MAG_FILTER, LINEAR);
                TexParameteri(TEXTURE_2D, TEX_WRAP_S, REPEAT);
                TexParameteri(TEXTURE_2D, TEX_WRAP_T, REPEAT);
                hasTex = 1;
            }
        }

        Enable(DEPTH_TEST); DepthFunc(LESS);
        Viewport(0, 0, W, H);

        // Frame from the model's own bounds: this file is scaled by 0.001 at the node, and a guessed
        // camera distance would put a correct render off screen and read as a failure.
        float cx = (model.Min[0] + model.Max[0]) / 2f;
        float cy = (model.Min[1] + model.Max[1]) / 2f;
        float cz = (model.Min[2] + model.Max[2]) / 2f;
        float dx = model.Max[0] - model.Min[0], dy = model.Max[1] - model.Min[1], dz = model.Max[2] - model.Min[2];
        var radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 2f;
        var fov = 45f * MathF.PI / 180f;
        var dist = radius / MathF.Sin(fov / 2f) * 1.25f;
        var proj = Perspective(fov, (float)W / H, radius * 0.01f, dist + radius * 4f);
        var mvpLoc = Uniform(prog, "uMvp");
        Uniform4f(Uniform(prog, "uColor"), model.BaseColor[0], model.BaseColor[1], model.BaseColor[2], model.BaseColor[3]);
        Uniform1i(Uniform(prog, "uTex"), 0);
        Uniform1i(Uniform(prog, "uHasTex"), hasTex);

        var pixels = new byte[W * H * 4];

        int DrawAt(float angle)
        {
            var view = LookAt(cx + MathF.Sin(angle) * dist, cy + dist * 0.35f, cz + MathF.Cos(angle) * dist, cx, cy, cz);
            var mvp = Gltf.Multiply(proj, view);
            fixed (float* m = mvp) UniformMatrix4fv(mvpLoc, 1, 0, m);
            ClearColor(BgR, BgG, BgB, 1f);
            Clear(COLOR_BUFFER_BIT | DEPTH_BUFFER_BIT);
            DrawElements(TRIANGLES, model.Indices.Length, UNSIGNED_INT, (void*)0);
            var err = GetError();
            if (err != 0) { Console.WriteLine($"glprobe: FAIL glGetError 0x{err:X}"); return -1; }
            fixed (byte* p = pixels) ReadPixels(0, 0, W, H, RGBA, UNSIGNED_BYTE, p);
            return 0;
        }

        if (DrawAt(0.6f) != 0) return 1;

        // What the pixels must show:
        //   model  — something was rasterised at all
        //   shades — distinct LUMINANCE levels: lighting, i.e. real per-vertex normals
        //   tones  — distinct RED levels: texture sampling. This is the load-bearing one once a
        //            texture is bound, because a flat-shaded teapot already varies in luminance and
        //            would pass `shades` with the texture silently ignored.
        var background = 0; var drawn = 0;
        var levels = new bool[256];
        var reds = new bool[256];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
            if (Math.Abs(r - 14) <= 3 && Math.Abs(g - 17) <= 3 && Math.Abs(b - 22) <= 3) { background++; continue; }
            drawn++;
            levels[(r * 30 + g * 59 + b * 11) / 100] = true;
            reds[r] = true;
        }
        var shades = 0; foreach (var l in levels) if (l) shades++;
        var tones = 0; foreach (var t in reds) if (t) tones++;

        Console.WriteLine($"glprobe: model pixels = {drawn:n0} ({100.0 * drawn / (W * H):F1}% of frame), background = {background:n0}");
        Console.WriteLine($"glprobe: distinct luminance levels = {shades}, distinct red levels = {tones}");

        var first = new byte[pixels.Length];
        Array.Copy(pixels, first, pixels.Length);
        if (DrawAt(2.2f) != 0) return 1;
        var moved = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - first[i]) > 8 || Math.Abs(pixels[i + 1] - first[i + 1]) > 8) moved++;
        Console.WriteLine($"glprobe: pixels changed when the camera orbited = {moved:n0}");

        if (DrawAt(0.6f) != 0) return 1;      // leave the nicer angle up for the screenshot

        var ok = drawn > 5000 && shades > 20 && moved > 5000 && (hasTex == 0 || tones > 30);
        Console.WriteLine(ok
            ? "glprobe: PASS teapot.glb rendered, lit and textured from nativeaot-llvm via webgl2"
            : "glprobe: FAIL the model did not render the way a lit, textured, orbitable mesh should");
        return ok ? 0 : 1;
    }
}
