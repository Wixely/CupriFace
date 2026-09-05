using System.Runtime.InteropServices;
using System.Text;

namespace CupriFace.Gl.Internal;

/// <summary>
/// The browser's half: making a real WebGL2 context on the underlay canvas the host created, and
/// resolving GL entry points against it.
///
/// <para>Every import here is called only when <see cref="OperatingSystem.IsBrowser"/>, so the
/// symbols never have to exist anywhere else. On wasm they must be named at BUILD time — there is no
/// lazy P/Invoke resolution there — which is what <c>buildTransitive/CupriFace.Gl.props</c> is for.</para>
/// </summary>
internal static unsafe class Emscripten
{
    private const string Lib = "emscripten";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ContextAttributes
    {
        public int Alpha, Depth, Stencil, Antialias, PremultipliedAlpha, PreserveDrawingBuffer;
        public int PowerPreference, FailIfMajorPerformanceCaveat;
        public int MajorVersion, MinorVersion;
        public int EnableExtensionsByDefault, ExplicitSwapControl;
        public int ProxyContextToMainThread, RenderViaOffscreenBackBuffer;
    }

    [DllImport(Lib, EntryPoint = "emscripten_webgl_init_context_attributes")]
    private static extern void InitAttributes(ContextAttributes* attrs);

    [DllImport(Lib, EntryPoint = "emscripten_webgl_create_context")]
    private static extern nint CreateContextRaw(byte* target, ContextAttributes* attrs);

    [DllImport(Lib, EntryPoint = "emscripten_webgl_make_context_current")]
    private static extern int MakeCurrentRaw(nint context);

    [DllImport(Lib, EntryPoint = "emscripten_webgl_destroy_context")]
    private static extern int DestroyContextRaw(nint context);

    [DllImport(Lib, EntryPoint = "emscripten_GetProcAddress")]
    private static extern nint GetProcAddressRaw(byte* name);

    [DllImport(Lib, EntryPoint = "emscripten_get_canvas_element_size")]
    private static extern int GetCanvasSizeRaw(byte* target, int* w, int* h);

    private static byte[] Cstr(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>The canvas's BACKING STORE size, which the host sets from the element's device rect.
    /// Zero until the host has created and positioned the underlay — a normal state, not an error:
    /// the host creates it after a painted frame, and in a tabbed app the section may not have been
    /// opened yet.</summary>
    internal static (int W, int H) CanvasSize(string target)
    {
        int w = 0, h = 0;
        var t = Cstr(target);
        fixed (byte* p = t) GetCanvasSizeRaw(p, &w, &h);
        return (w, h);
    }

    /// <summary>Ask for a WebGL2 context on the underlay canvas. Zero or negative means none.</summary>
    internal static nint CreateContext(string target, bool alpha, bool antialias)
    {
        ContextAttributes attrs;
        InitAttributes(&attrs);
        attrs.MajorVersion = 2;
        attrs.MinorVersion = 0;
        attrs.Alpha = alpha ? 1 : 0;
        attrs.Depth = 1;
        attrs.Antialias = antialias ? 1 : 0;
        var t = Cstr(target);
        fixed (byte* p = t) return CreateContextRaw(p, &attrs);
    }

    internal static bool MakeCurrent(nint ctx) => MakeCurrentRaw(ctx) == 0;

    internal static void DestroyContext(nint ctx) { if (ctx > 0) DestroyContextRaw(ctx); }

    internal static Func<string, nint> ProcAddress => name =>
    {
        var b = Cstr(name);
        fixed (byte* p = b) return GetProcAddressRaw(p);
    };
}
