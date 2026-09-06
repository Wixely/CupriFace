namespace CupriFace.Gl;

/// <summary>
/// A live GL context, handed to <see cref="IGlContent"/> whenever its code runs. Everything an app
/// needs in order to talk to the driver, and nothing it does not.
///
/// <para><b>There is no entry-point table here, and that is the design.</b> The sample this package
/// grew from carried 54 GL functions in a static mutable class, which is wrong in the specific way
/// item 1 of the scoping document describes: static means one context per process, and a library is
/// in no position to promise an app that it will only ever have one. Publishing such a table would
/// also freeze the sample's own choice of functions into this package's API for ever — 54 arbitrary
/// entry points, chosen for one teapot.</para>
///
/// <para>So the package resolves only what IT needs (framebuffers, the target texture, the state
/// reset), keeps those private and per-context, and gives an app <see cref="GetProcAddress"/> to
/// build whatever table it wants. That table is then instanced by construction, because the app made
/// it and holds it. It is also the shape every real GL loader already expects, so an existing one
/// can be pointed at this and work.</para>
/// </summary>
public sealed class GlContext
{
    private readonly Func<string, nint> _proc;

    internal GlContext(GlDialect dialect, GlLane lane, Func<string, nint> proc, Internal.GlFunctions fn)
    {
        Dialect = dialect;
        Lane = lane;
        _proc = proc;
        Fn = fn;
    }

    /// <summary>Which GLSL dialect this context compiles. Desktop differs; a phone and a browser do
    /// not, because WebGL2 is OpenGL ES 3.0.</summary>
    public GlDialect Dialect { get; }

    /// <summary>How this viewport's pixels reach the screen. Published for diagnostics; drawing code
    /// should not need to branch on it.</summary>
    public GlLane Lane { get; }

    /// <summary>
    /// The line to put at the top of every shader, newline included.
    ///
    /// <para>Prepend it rather than writing a version directive by hand. The failure this prevents is
    /// unusually indirect: a <c>#version 300 es</c> shader on a desktop, or a <c>#version 330 core</c>
    /// one in a browser, produces a compile error naming a line the app did not write, on a build
    /// that linked cleanly.</para>
    /// </summary>
    public string ShaderHeader => Dialect == GlDialect.GlEs300 ? "#version 300 es\n" : "#version 330 core\n";

    /// <summary>What the driver calls itself — <c>GL_RENDERER</c>. Names the actual hardware ("NVIDIA
    /// GeForce…", "Adreno…", "SwiftShader" for a software emulator), which is the single most useful
    /// thing to log when a report says it looks wrong on someone else's machine.</summary>
    public string Renderer { get; internal set; } = "";

    /// <summary>The GL version string — <c>GL_VERSION</c>.</summary>
    public string Version { get; internal set; } = "";

    /// <summary>The driver vendor — <c>GL_VENDOR</c>.</summary>
    public string Vendor { get; internal set; } = "";

    /// <summary>
    /// Resolve a GL entry point, or 0 if this driver does not have it.
    ///
    /// <para>Valid only while this context is current, which — for code called from
    /// <see cref="IGlContent"/> — is always. Cache the results; do not call this per frame.</para>
    ///
    /// <para>Names are tried verbatim and then with an <c>ARB</c> suffix, because some drivers
    /// publish only the extension spelling of a function that later became core.</para>
    /// </summary>
    public nint GetProcAddress(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var p = _proc(name);
        if (p == 0) p = _proc(name + "ARB");
        return p;
    }

    /// <summary>
    /// Resolve several entry points at once and report the ones that are missing, rather than dying
    /// on the first.
    ///
    /// <para>Worth preferring over a loop of <see cref="GetProcAddress"/> for exactly one reason: on
    /// a partial or unusual driver, the difference between naming all the absentees and naming the
    /// first is the difference between one diagnosis and ten runs.</para>
    /// </summary>
    /// <param name="names">Entry points to resolve.</param>
    /// <param name="missing">Those that did not resolve, in the order asked.</param>
    /// <returns>Addresses in the same order as <paramref name="names"/>; 0 for each missing one.</returns>
    public nint[] GetProcAddresses(IReadOnlyList<string> names, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(names);
        var result = new nint[names.Count];
        List<string>? absent = null;
        for (var i = 0; i < names.Count; i++)
        {
            result[i] = GetProcAddress(names[i]);
            if (result[i] == 0) (absent ??= []).Add(names[i]);
        }
        missing = (IReadOnlyList<string>?)absent ?? [];
        return result;
    }

    /// <summary>The package's own entry points — the handful the seam itself calls. Private on
    /// purpose: see the class remarks.</summary>
    internal Internal.GlFunctions Fn { get; }
}
