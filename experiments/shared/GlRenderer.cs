using System.Runtime.InteropServices;
using System.Text;

namespace CupriFace.Experiments;

/// <summary>
/// The desktop/GLX half of the probe, extracted so more than one host can drive it: the standalone
/// desktop probe renders to a window, the CupriFace integration renders to an offscreen framebuffer
/// and hands the pixels to the engine. Sharing it is part of the point — if the renderer had to be
/// rewritten per host, the portability claim would be hollow.
///
/// <para>The entry points are function pointers rather than DllImports because that is what desktop
/// GL requires: <c>opengl32</c> exports OpenGL 1.1 and nothing newer, so everything modern comes from
/// <c>wglGetProcAddress</c>, whose result is only valid for the context that was current when it was
/// asked. The web leg gets the identical call shape from static Emscripten symbols, and Android from
/// a plain DllImport into libGLESv3.so.</para>
/// </summary>
public static unsafe class Gl
{
    public static delegate* unmanaged<uint, byte*> GetString;
    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<float, float, float, float, void> ClearColor;
    public static delegate* unmanaged<uint, void> ClearBits;
    public static delegate* unmanaged<uint, void> Enable;
    public static delegate* unmanaged<uint, void> DepthFunc;
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, uint, int*, void> GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    public static delegate* unmanaged<uint, void> UseProgram;
    public static delegate* unmanaged<int, uint*, void> GenVertexArrays;
    public static delegate* unmanaged<uint, void> BindVertexArray;
    public static delegate* unmanaged<int, uint*, void> GenBuffers;
    public static delegate* unmanaged<uint, uint, void> BindBuffer;
    public static delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, uint*, void> GenTextures;
    public static delegate* unmanaged<uint, uint, void> BindTexture;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, void> GenerateMipmap;
    public static delegate* unmanaged<uint, int, uint, void*, void> DrawElements;
    public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    public static delegate* unmanaged<uint> GetError;
    // Offscreen rendering, needed only by the CupriFace integration: the engine wants pixels, not a
    // window, so the 3D goes to a framebuffer object rather than to anyone's back buffer.
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<int, uint*, void> GenRenderbuffers;
    public static delegate* unmanaged<uint, uint, void> BindRenderbuffer;
    public static delegate* unmanaged<uint, uint, int, int, void> RenderbufferStorage;
    public static delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;

    public const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    public const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4;
    public const uint FLOAT = 0x1406, TRIANGLES = 0x0004, UNSIGNED_INT = 0x1405;
    public const uint RGBA = 0x1908, UNSIGNED_BYTE = 0x1401;
    public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const uint VERSION = 0x1F02, RENDERER = 0x1F01, DEPTH_TEST = 0x0B71, LESS = 0x0201;
    public const uint TEXTURE_2D = 0x0DE1, RGBA8 = 0x8058;
    public const uint TEX_MIN_FILTER = 0x2801, TEX_MAG_FILTER = 0x2800, TEX_WRAP_S = 0x2802, TEX_WRAP_T = 0x2803;
    public const int LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, REPEAT = 0x2901;
    public const uint FRAMEBUFFER = 0x8D40, RENDERBUFFER = 0x8D41, COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint DEPTH_ATTACHMENT = 0x8D00, DEPTH_COMPONENT24 = 0x81A6, FRAMEBUFFER_COMPLETE = 0x8CD5;

    public static readonly List<string> Missing = [];

    /// <summary>Fill the table from a platform proc-address function. Names that do not resolve are
    /// collected rather than throwing, so a failure can name ALL of them at once instead of dying on
    /// the first — which on a partial driver is the difference between one diagnosis and ten runs.</summary>
    public static void Load(Func<string, nint> proc)
    {
        Missing.Clear();
        nint P(string n)
        {
            var p = proc(n);
            if (p == 0) p = proc(n + "ARB");        // some drivers only publish the extension spelling
            if (p == 0) Missing.Add(n);
            return p;
        }

        GetString = (delegate* unmanaged<uint, byte*>)P("glGetString");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        ClearBits = (delegate* unmanaged<uint, void>)P("glClear");
        Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        GenerateMipmap = (delegate* unmanaged<uint, void>)P("glGenerateMipmap");
        DrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)P("glDrawElements");
        ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        GetError = (delegate* unmanaged<uint>)P("glGetError");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        GenRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
        BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        RenderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)P("glRenderbufferStorage");
        FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
    }

    public static string Str(byte* p) => p is null ? "(null)" : Marshal.PtrToStringUTF8((nint)p) ?? "(null)";
}

/// <summary>
/// The teapot, as a thing that can be uploaded once and drawn many times. Everything host-specific
/// (where the context came from, where the pixels go) is the caller's problem.
/// </summary>
public sealed unsafe class TeapotRenderer
{
    private readonly Gltf _model;
    private uint _prog;
    private int _mvpLoc;
    private int _hasTex;
    // Remembered so Draw can re-bind it. Binding once at init is NOT enough and the way it fails is
    // silent: the CupriFace host creates its offscreen framebuffer's colour attachment AFTER this
    // runs, which rebinds TEXTURE_2D to the very texture being rendered into. Sampling your own
    // render target is undefined and reads black — a fully-shaped, correctly-lit, entirely black
    // teapot, which looked enough like "a teapot" that the first pixel assertion passed.
    private uint _tex;

    public TeapotRenderer(Gltf model) => _model = model;

    public Gltf Model => _model;

    private static void Source(uint shader, string src)
    {
        var bytes = Encoding.UTF8.GetBytes(src + "\0");
        fixed (byte* p = bytes)
        {
            byte** one = stackalloc byte*[1];
            one[0] = p;
            Gl.ShaderSource(shader, 1, one, null);
        }
    }

    private static uint Compile(uint type, string src, string label, Action<string> log)
    {
        var s = Gl.CreateShader(type);
        Source(s, src);
        Gl.CompileShader(s);
        int ok; Gl.GetShaderiv(s, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) return s;
        var buf = stackalloc byte[1024];
        Gl.GetShaderInfoLog(s, 1024, null, buf);
        log($"FAIL {label} shader: {Gl.Str(buf)}");
        return 0;
    }

    private static int Uniform(uint prog, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }

    private static void Attrib(uint prog, uint index, string name)
    {
        var b = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = b) Gl.BindAttribLocation(prog, index, p);
    }

    /// <summary>Compile, upload and configure. <paramref name="decodeRgba"/> keeps the codec out of
    /// the renderer: the caller turns encoded bytes into RGBA however its platform already can.</summary>
    public bool Initialise(Func<byte[], (byte[] Pixels, int W, int H)?> decodeRgba, Action<string> log)
    {
        var vs = Compile(Gl.VERTEX_SHADER, """
            #version 330 core
            in vec3 aPos;
            in vec3 aNormal;
            in vec2 aUv;
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec2 vUv;
            void main() { vNormal = aNormal; vUv = aUv; gl_Position = uMvp * vec4(aPos, 1.0); }
            """, "vertex", log);
        var fs = Compile(Gl.FRAGMENT_SHADER, """
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
            """, "fragment", log);
        if (vs == 0 || fs == 0) return false;

        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs);
        Attrib(_prog, 0, "aPos"); Attrib(_prog, 1, "aNormal"); Attrib(_prog, 2, "aUv");
        Gl.LinkProgram(_prog);
        int linked; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &linked);
        if (linked == 0)
        {
            var buf = stackalloc byte[1024];
            Gl.GetProgramInfoLog(_prog, 1024, null, buf);
            log($"FAIL link: {Gl.Str(buf)}");
            return false;
        }
        Gl.UseProgram(_prog);

        uint vao, vbo, ebo;
        Gl.GenVertexArrays(1, &vao); Gl.BindVertexArray(vao);
        Gl.GenBuffers(1, &vbo); Gl.BindBuffer(Gl.ARRAY_BUFFER, vbo);
        fixed (float* v = _model.Vertices)
            Gl.BufferData(Gl.ARRAY_BUFFER, _model.Vertices.Length * sizeof(float), v, Gl.STATIC_DRAW);
        var stride = 8 * sizeof(float);
        Gl.VertexAttribPointer(0, 3, Gl.FLOAT, 0, stride, (void*)0); Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 3, Gl.FLOAT, 0, stride, (void*)(3 * sizeof(float))); Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(2, 2, Gl.FLOAT, 0, stride, (void*)(6 * sizeof(float))); Gl.EnableVertexAttribArray(2);
        Gl.GenBuffers(1, &ebo); Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, ebo);
        fixed (uint* i = _model.Indices)
            Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, _model.Indices.Length * sizeof(uint), i, Gl.STATIC_DRAW);

        if (_model.BaseColorImage is { Length: > 0 } encoded && decodeRgba(encoded) is { } img)
        {
            log($"texture decoded {img.W}x{img.H} -> rgba8888");
            uint tex; Gl.GenTextures(1, &tex);
            _tex = tex;
            Gl.BindTexture(Gl.TEXTURE_2D, _tex);
            fixed (byte* p = img.Pixels)
                Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, img.W, img.H, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
            Gl.GenerateMipmap(Gl.TEXTURE_2D);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_MAG_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_WRAP_S, Gl.REPEAT);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEX_WRAP_T, Gl.REPEAT);
            _hasTex = 1;
        }

        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LESS);
        _mvpLoc = Uniform(_prog, "uMvp");
        Gl.Uniform4f(Uniform(_prog, "uColor"), _model.BaseColor[0], _model.BaseColor[1], _model.BaseColor[2], _model.BaseColor[3]);
        Gl.Uniform1i(Uniform(_prog, "uTex"), 0);
        Gl.Uniform1i(Uniform(_prog, "uHasTex"), _hasTex);
        return true;
    }

    public bool HasTexture => _hasTex == 1;

    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fovY / 2f);
        var m = new float[16];
        m[0] = f / aspect; m[5] = f;
        m[10] = (far + near) / (near - far); m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    public static float[] LookAt(float ex, float ey, float ez, float cx, float cy, float cz)
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

    /// <summary>Frame the model from its own bounds. Baked in rather than left to callers because a
    /// guessed camera distance renders correct geometry off screen, which reads as a failure.</summary>
    public float[] Mvp(float angle, float aspect)
    {
        var m = _model;
        float cx = (m.Min[0] + m.Max[0]) / 2f, cy = (m.Min[1] + m.Max[1]) / 2f, cz = (m.Min[2] + m.Max[2]) / 2f;
        float dx = m.Max[0] - m.Min[0], dy = m.Max[1] - m.Min[1], dz = m.Max[2] - m.Min[2];
        var radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 2f;
        var fov = 45f * MathF.PI / 180f;
        var dist = radius / MathF.Sin(fov / 2f) * 1.25f;
        var proj = Perspective(fov, aspect, radius * 0.01f, dist + radius * 4f);
        var view = LookAt(cx + MathF.Sin(angle) * dist, cy + dist * 0.35f, cz + MathF.Cos(angle) * dist, cx, cy, cz);
        return Gltf.Multiply(proj, view);
    }

    public void Draw(float angle, int w, int h, float bgR, float bgG, float bgB, float bgA)
    {
        Gl.UseProgram(_prog);
        // Re-bind every frame rather than trusting init-time state: whoever owns the context may
        // have bound something else to TEXTURE_2D since (the offscreen host does exactly that).
        if (_hasTex == 1) Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        var mvp = Mvp(angle, (float)w / h);
        fixed (float* p = mvp) Gl.UniformMatrix4fv(_mvpLoc, 1, 0, p);
        Gl.Viewport(0, 0, w, h);
        Gl.ClearColor(bgR, bgG, bgB, bgA);
        Gl.ClearBits(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        Gl.DrawElements(Gl.TRIANGLES, _model.Indices.Length, Gl.UNSIGNED_INT, (void*)0);
    }
}
