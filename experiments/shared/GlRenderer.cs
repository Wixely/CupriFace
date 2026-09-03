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
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, float, void> Uniform3f;
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
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
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
