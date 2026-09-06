using System.Runtime.InteropServices;

namespace CupriFace.Gl.Internal;

/// <summary>
/// The entry points the SEAM itself calls: a framebuffer to draw into, a texture to hand back, and
/// the state reset. Twenty-odd functions, none of them about rendering anything.
///
/// <para><b>Instanced, and internal.</b> One of these belongs to one <see cref="GlContext"/> —
/// because <c>wglGetProcAddress</c>'s answers are only valid for the context that was current when
/// it was asked, so a static table is a latent bug the moment a process has two. It stays internal
/// because an app's GL needs are its own: it gets <see cref="GlContext.GetProcAddress"/> and builds
/// whatever table suits it, rather than inheriting this one's arbitrary membership as API.</para>
///
/// <para>Every function here is core in BOTH OpenGL 3.3 and OpenGL ES 3.0, so a driver that provides
/// either provides all of them. Anything missing is therefore a real diagnosis — a broken loader or
/// a context that is not what it claimed — and is reported by name rather than crashed on.</para>
/// </summary>
internal sealed unsafe class GlFunctions
{
    // ---- constants ------------------------------------------------------------------------------
    // Spelled out rather than pulled from a binding library, because pulling in a binding library is
    // exactly the dependency this package exists to avoid imposing.

    internal const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    internal const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02;
    internal const uint DEPTH_TEST = 0x0B71, LESS = 0x0201;
    internal const uint BLEND = 0x0BE2, SCISSOR_TEST = 0x0C11, STENCIL_TEST = 0x0B90;
    internal const uint CULL_FACE = 0x0B44, DITHER = 0x0BD0;
    internal const uint TEXTURE0 = 0x84C0, TEXTURE_2D = 0x0DE1;
    internal const uint MAX_COMBINED_TEXTURE_IMAGE_UNITS = 0x8B4D;
    internal const uint UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05;
    internal const uint RGBA = 0x1908, RGBA8 = 0x8058, UNSIGNED_BYTE = 0x1401;
    internal const uint TEX_MIN_FILTER = 0x2801, TEX_MAG_FILTER = 0x2800;
    internal const uint TEX_WRAP_S = 0x2802, TEX_WRAP_T = 0x2803;
    internal const int LINEAR = 0x2601, CLAMP_TO_EDGE = 0x812F;
    internal const uint FRAMEBUFFER = 0x8D40, RENDERBUFFER = 0x8D41;
    internal const uint COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00;
    internal const uint DEPTH_COMPONENT24 = 0x81A6, FRAMEBUFFER_COMPLETE = 0x8CD5;

    // ---- the table ------------------------------------------------------------------------------

    internal delegate* unmanaged<uint, byte*> GetString;
    internal delegate* unmanaged<uint> GetError;
    internal delegate* unmanaged<uint, int*, void> GetIntegerv;
    internal delegate* unmanaged<int, int, int, int, void> Viewport;
    internal delegate* unmanaged<float, float, float, float, void> ClearColor;
    internal delegate* unmanaged<uint, void> Clear;
    internal delegate* unmanaged<uint, void> Enable;
    internal delegate* unmanaged<uint, void> Disable;
    internal delegate* unmanaged<uint, void> DepthFunc;
    internal delegate* unmanaged<byte, void> DepthMask;
    internal delegate* unmanaged<byte, byte, byte, byte, void> ColorMask;
    internal delegate* unmanaged<uint, void> ActiveTexture;
    internal delegate* unmanaged<uint, uint, void> BindSampler;
    internal delegate* unmanaged<uint, void> UseProgram;
    internal delegate* unmanaged<uint, int, void> PixelStorei;
    internal delegate* unmanaged<int, uint*, void> GenTextures;
    internal delegate* unmanaged<int, uint*, void> DeleteTextures;
    internal delegate* unmanaged<uint, uint, void> BindTexture;
    internal delegate* unmanaged<uint, uint, int, void> TexParameteri;
    internal delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    internal delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    internal delegate* unmanaged<int, uint*, void> GenFramebuffers;
    internal delegate* unmanaged<int, uint*, void> DeleteFramebuffers;
    internal delegate* unmanaged<uint, uint, void> BindFramebuffer;
    internal delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    internal delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    internal delegate* unmanaged<int, uint*, void> GenRenderbuffers;
    internal delegate* unmanaged<int, uint*, void> DeleteRenderbuffers;
    internal delegate* unmanaged<uint, uint, void> BindRenderbuffer;
    internal delegate* unmanaged<uint, uint, int, int, void> RenderbufferStorage;
    internal delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;

    /// <summary>How many texture units this driver has, so the sampler-object reset covers all of
    /// them rather than a guess. Clamped: the loop is per frame, and a driver reporting an
    /// implausible number should not turn a reset into a stall.</summary>
    internal int TextureUnits = 8;

    /// <summary>Fill the table. Names that do not resolve are collected rather than thrown on, so a
    /// failure can name all of them at once.</summary>
    internal static GlFunctions? Load(Func<string, nint> proc, out IReadOnlyList<string> missing)
    {
        var fn = new GlFunctions();
        List<string>? absent = null;

        nint P(string n)
        {
            var p = proc(n);
            if (p == 0) p = proc(n + "ARB");
            if (p == 0) (absent ??= []).Add(n);
            return p;
        }

        fn.GetString = (delegate* unmanaged<uint, byte*>)P("glGetString");
        fn.GetError = (delegate* unmanaged<uint>)P("glGetError");
        fn.GetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        fn.Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        fn.ClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        fn.Clear = (delegate* unmanaged<uint, void>)P("glClear");
        fn.Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        fn.Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        fn.DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        fn.DepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        fn.ColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        fn.ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        fn.BindSampler = (delegate* unmanaged<uint, uint, void>)P("glBindSampler");
        fn.UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        fn.PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        fn.GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        fn.DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        fn.BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        fn.TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        fn.TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        fn.ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        fn.GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        fn.DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        fn.BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        fn.FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        fn.CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        fn.GenRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
        fn.DeleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteRenderbuffers");
        fn.BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        fn.RenderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)P("glRenderbufferStorage");
        fn.FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");

        missing = (IReadOnlyList<string>?)absent ?? [];
        if (missing.Count > 0) return null;

        var units = 8;
        fn.GetIntegerv(MAX_COMBINED_TEXTURE_IMAGE_UNITS, &units);
        fn.TextureUnits = units is > 0 and <= 64 ? units : 8;
        return fn;
    }

    internal string Str(uint name) =>
        GetString is null ? "" : Marshal.PtrToStringUTF8((nint)GetString(name)) ?? "";

    /// <summary>
    /// Put the driver into the documented state <see cref="IGlContent"/> is promised. Called before
    /// every frame, on every lane.
    ///
    /// <para><b>This is item 3 of the scoping document, and it exists because being written down was
    /// not enough.</b> The sample documented the same reset and still shipped a bug that took a full
    /// session to find, invisible on one desktop driver and glaring on a phone. The cause is the
    /// second line below.</para>
    ///
    /// <para><b>The sampler objects are the one nobody guesses.</b> Skia binds a sampler object to
    /// the texture units it uses, and a bound sampler object OVERRIDES EVERY TEXTURE PARAMETER on
    /// that unit — filtering and, fatally, wrap mode. An app that sets <c>GL_REPEAT</c> on its
    /// texture and draws with a tiling UV gets clamping instead, so half the model samples one edge
    /// texel and the texture looks like it was projected rather than mapped. Nothing errors; the
    /// texture parameters read back exactly as they were set. The only tell is that it renders
    /// correctly for anyone whose Skia happens not to have touched that unit.</para>
    ///
    /// <para>The reverse direction is handled and needs nothing here: raw GL leaves Skia's own state
    /// tracking wrong, and <c>SurfaceRegistry.RenderGpuFrames</c> calls <c>GRContext.ResetContext</c>
    /// centrally once every producer has run.</para>
    /// </summary>
    internal void ResetState()
    {
        // Sampler objects first: everything below is pointless while one is overriding it.
        for (var unit = 0u; unit < (uint)TextureUnits; unit++) BindSampler(unit, 0);

        Disable(BLEND);
        Disable(SCISSOR_TEST);
        Disable(STENCIL_TEST);
        Disable(CULL_FACE);
        Disable(DITHER);

        Enable(DEPTH_TEST);
        DepthFunc(LESS);
        DepthMask(1);
        ColorMask(1, 1, 1, 1);

        ActiveTexture(TEXTURE0);
        // Skia sets these to 1 for its own uploads. A row-aligned upload then reads the wrong bytes
        // per row and the texture shears — the classic diagonal-smear artefact.
        PixelStorei(UNPACK_ALIGNMENT, 4);
        PixelStorei(PACK_ALIGNMENT, 4);

        // Leaving someone else's program bound turns "the app forgot to call glUseProgram" into
        // "the app drew with Skia's shader", which is far harder to recognise than drawing nothing.
        UseProgram(0);
    }
}
