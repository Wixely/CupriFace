namespace CupriFace.Gl.Internal;

/// <summary>
/// The offscreen framebuffer a painted-lane viewport draws into: a colour texture the engine can be
/// handed directly, plus a depth buffer.
///
/// <para>Items 2 and 4 of the scoping document live here. <b>Item 2:</b> the size follows the
/// element's device box rather than being fixed at 512×512, so a 3× phone is not upscaling a
/// third-resolution image into a panel. <b>Item 4:</b> a resize deletes and rebuilds rather than
/// leaving a stale framebuffer, and <see cref="Delete"/> exists at all — the sample never released
/// anything, which is invisible in a demo that runs once and a leak in an app whose user opens and
/// closes a panel.</para>
/// </summary>
internal sealed unsafe class GlTarget
{
    internal uint Fbo, Texture, Depth;
    internal int Width, Height;

    /// <summary>True once the framebuffer exists and is complete.</summary>
    internal bool Ready => Fbo != 0;

    /// <summary>
    /// Make the target exactly <paramref name="w"/>×<paramref name="h"/>, rebuilding it if the size
    /// changed. Cheap and returns true when nothing needed doing, which is the common case.
    /// </summary>
    /// <param name="error">Why the framebuffer could not be made, when this returns false.</param>
    internal bool EnsureSize(GlFunctions fn, int w, int h, out string? error)
    {
        error = null;
        if (Ready && w == Width && h == Height) return true;

        // Rebuild rather than glTexImage2D over the existing texture. Reallocating a texture that a
        // previously handed-out SKImage still wraps would change what that image shows mid-flight;
        // a fresh object leaves the old one intact until its frame is retired.
        Delete(fn);

        uint fbo, tex, depth;
        fn.GenFramebuffers(1, &fbo);
        fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, fbo);

        fn.GenTextures(1, &tex);
        fn.BindTexture(GlFunctions.TEXTURE_2D, tex);
        fn.TexImage2D(GlFunctions.TEXTURE_2D, 0, (int)GlFunctions.RGBA8, w, h, 0,
                      GlFunctions.RGBA, GlFunctions.UNSIGNED_BYTE, null);
        // LINEAR, and CLAMP_TO_EDGE rather than the default REPEAT: this texture is sampled by SKIA
        // when it composites the frame, and a repeat wrap on a non-power-of-two target is both
        // meaningless here and, on some GLES drivers, incomplete.
        fn.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEX_MIN_FILTER, GlFunctions.LINEAR);
        fn.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEX_MAG_FILTER, GlFunctions.LINEAR);
        fn.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEX_WRAP_S, GlFunctions.CLAMP_TO_EDGE);
        fn.TexParameteri(GlFunctions.TEXTURE_2D, GlFunctions.TEX_WRAP_T, GlFunctions.CLAMP_TO_EDGE);
        fn.FramebufferTexture2D(GlFunctions.FRAMEBUFFER, GlFunctions.COLOR_ATTACHMENT0,
                                GlFunctions.TEXTURE_2D, tex, 0);

        fn.GenRenderbuffers(1, &depth);
        fn.BindRenderbuffer(GlFunctions.RENDERBUFFER, depth);
        fn.RenderbufferStorage(GlFunctions.RENDERBUFFER, GlFunctions.DEPTH_COMPONENT24, w, h);
        fn.FramebufferRenderbuffer(GlFunctions.FRAMEBUFFER, GlFunctions.DEPTH_ATTACHMENT,
                                   GlFunctions.RENDERBUFFER, depth);

        var status = fn.CheckFramebufferStatus(GlFunctions.FRAMEBUFFER);
        if (status != GlFunctions.FRAMEBUFFER_COMPLETE)
        {
            error = $"framebuffer incomplete (0x{status:X}) at {w}x{h}";
            fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
            fn.DeleteFramebuffers(1, &fbo);
            fn.DeleteTextures(1, &tex);
            fn.DeleteRenderbuffers(1, &depth);
            return false;
        }

        Fbo = fbo; Texture = tex; Depth = depth; Width = w; Height = h;
        return true;
    }

    /// <summary>Release everything. Must be called with the owning context current — the only moment
    /// deleting GL objects is legal.</summary>
    internal void Delete(GlFunctions fn)
    {
        if (Fbo == 0) return;
        uint fbo = Fbo, tex = Texture, depth = Depth;
        fn.BindFramebuffer(GlFunctions.FRAMEBUFFER, 0);
        fn.DeleteFramebuffers(1, &fbo);
        fn.DeleteTextures(1, &tex);
        fn.DeleteRenderbuffers(1, &depth);
        Fbo = Texture = Depth = 0;
        Width = Height = 0;
    }
}
