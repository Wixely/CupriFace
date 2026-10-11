using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Issue #304, the half that needs a window — so, like <see cref="LayeredAlphaTests"/> and
/// <see cref="CursorTableTests"/>, it is checked against the source rather than by running it.
///
/// <para><c>SkiaWindow.IsFullscreen</c> became load-bearing in three places at once when the host
/// started reporting observed state to the document: the early-return guard in
/// <c>SetFullscreen</c>, the Escape-exits-fullscreen handler, and the
/// <c>NotifyHostFullscreen(window.IsFullscreen)</c> that tells the document whether to keep the
/// video expanded. One wrong answer therefore breaks all three.</para>
///
/// <para>It had one. Deriving it from Silk's <c>WindowState</c> is wrong for a MAXIMIZED window,
/// because that getter ranks the states and checks Maximized before Fullscreen:</para>
/// <code>
/// if (GetWindowAttrib(Iconified)) return Minimized;
/// if (GetWindowAttrib(Maximized)) return Maximized;     // ← wins
/// if (GetWindowMonitor() != null)  return Fullscreen;
/// </code>
/// <para>…and GLFW keeps the maximized attribute set while a maximized window is fullscreen.
/// Measured directly against GLFW 2.22 on Windows, maximizing and then calling
/// <c>SetWindowMonitor</c> exactly as <c>GlfwFullscreenOnCurrentMonitor</c> does:</para>
/// <code>
/// after MaximizeWindow          maximized=True  hasMonitor=False  => WindowState=Maximized
/// fullscreen FROM MAXIMIZED     maximized=True  hasMonitor=True   => WindowState=Maximized
/// </code>
/// <para>So a window maximized before it went fullscreen reported "not fullscreen" while
/// fullscreen: exit returned early and left the window fullscreen (the symptom #304 reports),
/// Escape did nothing, and the document was told fullscreen had FAILED and dropped the video back
/// into the layout around a still-fullscreen window — the very desync the fix exists to prevent.
/// Having the monitor is GLFW's own definition of fullscreen and does not rank anything.</para>
/// </summary>
public class HostFullscreenStateTests(ITestOutputHelper output)
{
    private static string Shell(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", "CupriFace.Shell", file));
    }

    [Fact]
    public void Fullscreen_is_read_from_the_windows_monitor_not_from_a_ranked_window_state()
    {
        var src = Shell("SkiaWindow.cs");
        var at = src.IndexOf("public bool IsFullscreen", StringComparison.Ordinal);
        Assert.True(at >= 0, "SkiaWindow.IsFullscreen is gone — three call sites depend on it");
        var decl = src[at..src.IndexOf(';', at)];

        Assert.Contains("GlfwFullscreen", decl, StringComparison.Ordinal);
        Assert.Contains("GetWindowMonitor", src, StringComparison.Ordinal);

        // Silk's ranked answer may remain only as the FALLBACK for a non-GLFW windowing layer,
        // which is what every non-GLFW host already relied on. It must not be the whole answer.
        Assert.Contains("??", decl, StringComparison.Ordinal);
        output.WriteLine(decl.Trim());
    }

    [Fact]
    public void The_glfw_answer_distinguishes_no_glfw_from_not_fullscreen()
    {
        // bool? and not bool: collapsing "GLFW is not underneath" into "not fullscreen" would make
        // the SDL and any future windowing path permanently report windowed, which is the same
        // three failures with a different trigger.
        var src = Shell("SkiaWindow.cs");
        Assert.Contains("private unsafe bool? GlfwFullscreen", src, StringComparison.Ordinal);

        var at = src.IndexOf("private unsafe bool? GlfwFullscreen", StringComparison.Ordinal);
        var body = src[at..src.IndexOf("public void SetFullscreen", StringComparison.Ordinal)];
        Assert.Contains("return null", body, StringComparison.Ordinal);   // no handle → fall back
        Assert.Contains("catch", body, StringComparison.Ordinal);         // …and never throw
    }

    [Fact]
    public void The_sdl_path_tracks_its_own_confirmed_state()
    {
        // The SDL window is immune to the above because it records what SDL CONFIRMED rather than
        // deriving it. That is the property worth keeping, not the specific line.
        var src = Shell("SdlSoftwareWindow.cs");
        Assert.Contains("public bool IsFullscreen { get; private set; }", src, StringComparison.Ordinal);

        var at = src.IndexOf("public void SetFullscreen", StringComparison.Ordinal);
        var body = src[at..src.IndexOf("public void SetTopMost", StringComparison.Ordinal)];
        // Assigned inside the success branch of the SDL call, not before it.
        var call = body.IndexOf("SetWindowFullscreen", StringComparison.Ordinal);
        var assign = body.IndexOf("IsFullscreen = on", StringComparison.Ordinal);
        Assert.True(call >= 0 && assign > call,
                    "SDL fullscreen state must be recorded only after SDL accepts the change");
    }
}
