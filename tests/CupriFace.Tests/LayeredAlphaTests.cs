using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The invariants the Windows layered-alpha path (#139/#140) is allowed to ship on.
///
/// None of this can be exercised by running it: it needs a Windows compositor, a real HWND and, for
/// the GPU variant, a working GL driver — the exact three things CI does not have. So it is checked
/// the way the cursor tables are (see <see cref="CursorTableTests"/>): against the source. That is
/// weaker than executing the code, and it is chosen over the alternative, which is a release-blocking
/// rule enforced by nothing but somebody remembering it.
///
/// Each test below corresponds to a specific way this path has already been observed to fail, or a
/// specific promise made in the release notes. A test that merely restates the code is worse than no
/// test; if one of these stops corresponding to a real failure mode, delete it.
/// </summary>
public class LayeredAlphaTests(ITestOutputHelper output)
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Shell(string file) =>
        File.ReadAllText(Path.Combine(Root(), "src", "CupriFace.Shell", file));

    /// <summary>The body of a member, by brace matching from its signature — so "is this throw inside
    /// a try" is answered about the member and not about whatever follows it in the file.</summary>
    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no member matching '{signature}'");
        var open = source.IndexOf('{', at);
        Assert.True(open >= 0, $"'{signature}' has no body");
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"unbalanced braces after '{signature}'");
    }

    /// <summary>
    /// Blocker 1. Construction may throw — failing to make the window layered is permanent, and the
    /// caller must not show a window it cannot present to. Presenting ONE frame may not:
    /// <c>UpdateLayeredWindow</c> and <c>GetWindowRect</c> both fail transiently during ordinary
    /// desktop upheaval (a session lock, an RDP transition, a monitor change), and this runs on every
    /// frame. Throwing there turns a compositor hiccup into a dead application.
    /// </summary>
    [Theory]
    [InlineData("public unsafe bool Present")]
    [InlineData("public (int Width, int Height)? WindowSize")]
    public void Presentation_survives_a_compositor_that_refuses_a_frame(string member)
    {
        var body = Body(Shell("WindowsAlphaPresenter.cs"), member);

        Assert.Contains("catch (Exception", body, StringComparison.Ordinal);
        Assert.Contains("ReportOnce", body, StringComparison.Ordinal);

        // Every throw must be reachable only from inside the try, i.e. between try and catch.
        var tryAt = body.IndexOf("try", StringComparison.Ordinal);
        var catchAt = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        foreach (Match t in Regex.Matches(body, @"\bthrow\b"))
        {
            if (t.Index > tryAt && t.Index < catchAt) continue;
            // A wrong pixel format is a caller bug, not a compositor one, and stays fatal.
            var line = body[body.LastIndexOf('\n', t.Index)..body.IndexOf('\n', t.Index)];
            Assert.Contains("ArgumentException", line);
        }
        output.WriteLine($"{member}: guarded");
    }

    /// <summary>…and it must SAY so. A dropped frame that is neither counted nor mentioned is
    /// indistinguishable from a working one, which is how #139 looked like "transparency works" on
    /// every machine except the reporter's.</summary>
    [Fact]
    public void A_dropped_frame_is_counted_and_reported_once()
    {
        var src = Shell("WindowsAlphaPresenter.cs");
        Assert.Contains("public int DroppedFrames", src, StringComparison.Ordinal);
        Assert.Contains("DroppedFrames++", src, StringComparison.Ordinal);

        // Once, not per frame: a compositor refusing frames refuses many, and a per-frame message
        // buries the one line that mattered.
        Assert.Contains("if (reported) return;", Body(src, "private static void ReportOnce"),
            StringComparison.Ordinal);

        // And the window must retry rather than sit on stale pixels — the damage path clears the
        // dirty flag before presenting, so a refused frame has to set it back.
        Assert.Contains("if (!_alphaPresenter.Present(_bitmap)) _presentDirty = true;",
            Shell("SdlSoftwareWindow.cs"), StringComparison.Ordinal);
    }

    /// <summary>The Viewer publishes with <c>-p:Aot=true</c>, and #131 was the UIA bridge going
    /// silently dead under exactly that. Reflection-based interop is how it happened.</summary>
    [Fact]
    public void The_windows_interop_is_aot_safe()
    {
        var src = Shell("WindowsAlphaPresenter.cs");
        // The attribute form, not the word — the file explains in prose why DllImport is avoided.
        Assert.DoesNotContain("[DllImport", src, StringComparison.Ordinal);
        Assert.Contains("[SupportedOSPlatform(\"windows\")]", src, StringComparison.Ordinal);

        var imports = Regex.Matches(src, @"\[LibraryImport").Count;
        var declarations = Regex.Matches(src, @"static partial ").Count;
        output.WriteLine($"{imports} LibraryImport attributes, {declarations} partial declarations");
        Assert.True(imports >= 10, $"only {imports} native imports found — did the file move?");
        Assert.Equal(imports, declarations);
    }

    /// <summary>
    /// Blocker 2. The resize watch is the ONLY thing that runs while a window is being dragged or
    /// resized — the OS modal loop starves the frame tick until the mouse comes up. Returning early
    /// from it for layered windows meant no frames at all during a drag, <c>ResizeFrames</c> stuck at
    /// 0 (the repo's own instrument for "is this streaming?"), and <c>PollDeviceScale</c> — the whole
    /// mechanism that makes a #137 DPI change land mid-drag — never running.
    /// </summary>
    [Fact]
    public void The_resize_watch_still_runs_for_a_layered_window()
    {
        var body = Body(Shell("SdlSoftwareWindow.cs"), "private int ResizeWatch");

        Assert.Contains("PollDeviceScale", body, StringComparison.Ordinal);
        Assert.Contains("RenderFrame", body, StringComparison.Ordinal);
        Assert.Contains("ResizeFrames++", body, StringComparison.Ordinal);

        // Both branches must count, because the one that matters here is MOVE, not resize. A
        // transparent layered window is frameless by definition — no OS title bar, no OS resize
        // border — so SizeChanged never fires on it and ResizeFrames reads 0 however healthy the
        // path is. That is not hypothetical: it is why the first instrument for this fix measured
        // nothing on the very window type it was built for.
        Assert.Equal(2, Regex.Matches(body, @"ModalFrames\+\+").Count);

        // What it MAY skip is feeding the event's own coordinates back as a size: UpdateLayeredWindow
        // sizes the HWND itself, so those numbers are the stale ones. RenderFrame asks the presenter
        // for the settled outer rect instead. Skipping the surface call is fine; skipping the frame
        // is what this test exists to stop.
        var beforeFirstFrame = body[..body.IndexOf("RenderFrame", StringComparison.Ordinal)];
        Assert.DoesNotContain("return 0;", beforeFirstFrame);
        output.WriteLine("resize watch streams frames for layered windows");
    }

    /// <summary>An opt-in that is quietly dropped is indistinguishable from one that is broken.
    /// ThreadedRender rasterises on a background thread into the CPU bitmap; the layered GPU path
    /// draws on the GL context and reads back on the UI thread. They cannot both own the frame, so
    /// one of them loses — and the app must be told which.</summary>
    [Fact]
    public void An_ignored_threaded_render_says_so()
    {
        var src = Shell("DesktopHost.cs");
        Assert.Contains("app.ThreadedRender && window.UseLayeredGpu", src, StringComparison.Ordinal);
        Assert.Contains("ThreadedRender is ignored under layered GPU presentation", src,
            StringComparison.Ordinal);

        // …and actually ignored, not half-applied.
        Assert.Contains("app.ThreadedRender && !window.UseLayeredGpu ? new CupriFace.Threading.ThreadedPresenter()",
            src, StringComparison.Ordinal);
    }

    /// <summary>Blocker 3's other half: the public entry point is callable from any host without a
    /// platform check at the call site. On a Mac, a Linux box, or a non-transparent app it must fall
    /// back to normal rendering and say so — never throw, and never open a window it cannot present
    /// to.</summary>
    [Fact]
    public void The_layered_gpu_entry_point_falls_back_instead_of_throwing()
    {
        var body = Body(Shell("DesktopHost.cs"), "public static void RunWithLayeredGpu");

        var guard = body[..body.IndexOf("RunCore", StringComparison.Ordinal)];
        Assert.Contains("!OperatingSystem.IsWindows()", guard, StringComparison.Ordinal);
        Assert.Contains("!app.Transparent", guard, StringComparison.Ordinal);
        Assert.Contains("!app.Frameless", guard, StringComparison.Ordinal);
        Assert.Contains("Run(app, configure);", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("throw", body);
        output.WriteLine("RunWithLayeredGpu degrades to DesktopHost.Run");
    }

    /// <summary>The readback is the cost this mode trades for working alpha, and "some overhead" is
    /// not a number anyone can decide on. Measure it in the shipped build rather than describing it
    /// in a caveat.</summary>
    [Fact]
    public void The_readback_cost_is_measured_not_described()
    {
        var renderer = Shell("LayeredGpuRenderer.cs");
        Assert.Contains("public double AverageReadbackMs", renderer, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetTimestamp", Body(renderer, "public void ReadBack"),
            StringComparison.Ordinal);

        // …and surfaced, or it is a field nobody reads.
        var report = Body(Shell("SdlSoftwareWindow.cs"), "private void ReportAlphaState");
        Assert.Contains("AverageReadbackMs", report, StringComparison.Ordinal);
        Assert.Contains("DroppedFrames", report, StringComparison.Ordinal);
        Assert.Contains("ResizeFrames", report, StringComparison.Ordinal);
    }
}
