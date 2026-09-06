using System.Runtime.InteropServices;

namespace CupriFace.Gl.Internal;

/// <summary>
/// Resolving GL entry points on the HOST'S OWN CONTEXT — the loader half of the seam, and the part
/// that is different on every platform for reasons none of them share.
///
/// <para>The context here belongs to the host: a desktop GL window, or Android's
/// <c>SKGLSurfaceView</c>. It is already current when this runs, and there is no windowing object to
/// ask, so the entry points have to come from the platform's own loader.</para>
/// </summary>
internal static class HostGl
{
    // wglGetProcAddress is itself a GL call and must be imported rather than dlsym'd — its answers
    // are only valid for the context current when it was asked, which is precisely why the table it
    // fills is per-GlContext rather than static.
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi, EntryPoint = "wglGetProcAddress")]
    private static extern nint WglGetProcAddress(string name);

    private static nint _opengl32, _gles;

    /// <summary>
    /// A proc-address function for the host's context, or null when this platform has none that this
    /// package knows how to use — in which case the viewport reports
    /// <see cref="GlViewportState.Unavailable"/> and says so rather than guessing.
    /// </summary>
    internal static Func<string, nint>? ForHostContext(out string? why)
    {
        why = null;

        if (OperatingSystem.IsWindows())
        {
            // Both, and in this order. opengl32.dll exports OpenGL 1.1 and nothing newer, so
            // everything modern comes from wglGetProcAddress — but wglGetProcAddress in turn refuses
            // to answer for the 1.1 core. A single lookup half-works, which is the worst outcome:
            // it resolves most of a table and fails on glGetString.
            return name =>
            {
                var p = WglGetProcAddress(name);
                // Not null but 1, 2, 3 or -1: wglGetProcAddress's documented sentinels for a name it
                // does not know. Treating them as addresses calls into nothing.
                if (p is 0 or 1 or 2 or 3 or -1)
                {
                    if (_opengl32 == 0 && !NativeLibrary.TryLoad("opengl32.dll", out _opengl32)) return 0;
                    return NativeLibrary.TryGetExport(_opengl32, name, out var q) ? q : 0;
                }
                return p;
            };
        }

        if (OperatingSystem.IsAndroid())
        {
            // libGLESv3.so directly, and deliberately NOT eglGetProcAddress: some drivers' EGL
            // returns a non-null stub for ANY name, which makes a missing entry point look present
            // and then crash on the call. Exported symbols cannot lie in the same way.
            if (_gles == 0 && !NativeLibrary.TryLoad("libGLESv3.so", out _gles))
            {
                why = "libGLESv3.so did not load";
                return null;
            }
            return name => NativeLibrary.TryGetExport(_gles, name, out var p) ? p : 0;
        }

        if (OperatingSystem.IsLinux())
        {
            if (_opengl32 == 0
                && !NativeLibrary.TryLoad("libGL.so.1", out _opengl32)
                && !NativeLibrary.TryLoad("libGL.so", out _opengl32))
            {
                why = "libGL.so.1 did not load";
                return null;
            }
            var lib = _opengl32;
            // glXGetProcAddress answers for extensions; the exported symbols cover the core. Same
            // shape as Windows, different spelling.
            var glx = NativeLibrary.TryGetExport(lib, "glXGetProcAddressARB", out var a) ? a
                    : NativeLibrary.TryGetExport(lib, "glXGetProcAddress", out var b) ? b : 0;
            return name =>
            {
                if (NativeLibrary.TryGetExport(lib, name, out var p)) return p;
                return glx == 0 ? 0 : CallGlx(glx, name);
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            const string framework = "/System/Library/Frameworks/OpenGL.framework/OpenGL";
            if (_opengl32 == 0 && !NativeLibrary.TryLoad(framework, out _opengl32))
            {
                why = "the OpenGL framework did not load";
                return null;
            }
            var lib = _opengl32;
            return name => NativeLibrary.TryGetExport(lib, name, out var p) ? p : 0;
        }

        why = $"no GL loader for {RuntimeInformation.RuntimeIdentifier}";
        return null;
    }

    private static unsafe nint CallGlx(nint glXGetProcAddress, string name)
    {
        var fn = (delegate* unmanaged<byte*, nint>)glXGetProcAddress;
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return fn(p);
    }
}
