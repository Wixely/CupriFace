using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.WebTouchGate;

/// <summary>
/// The web UNDERLAY seam, in a real browser.
///
/// <para>An underlay is a DOM element living beneath the engine's canvas, shown through a hole the
/// painter punches. A <c>&lt;video&gt;</c> the browser decodes is one; a <c>&lt;canvas&gt;</c> an app
/// renders WebGL into is another. The hard part is not the hole — it is keeping the element glued to
/// a box the ENGINE laid out, when the element itself knows nothing of engine scrolling, engine
/// <c>overflow</c> clipping or engine transforms. All three have to be recreated in CSS every frame.</para>
///
/// <para>This exists because that code was generalised out of the video path, and the unit tests
/// cannot see a browser. They caught one regression already (gating the walk on
/// <c>HostComposited</c> left every video pinned at its first laid-out box, because a
/// <c>&lt;video&gt;</c> reports it only once it can show pixels). What they cannot catch is anything
/// that lives in the JS or in the browser's own layout — which is exactly where the
/// canvas-backing-store bug lived: the element was CSS-sized correctly while the app rendered into
/// the 300x150 default, stretched, with every managed test passing.</para>
/// </summary>
[Collection("web")]
public class UnderlayTests(WebHostFixture host, ITestOutputHelper output)
{
    /// <summary>
    /// Sidebar row centres at <see cref="WebHostFixture.DesktopAsync"/>'s fixed 1100x780 viewport.
    ///
    /// <para>Clicking a coordinate is not the first choice, it is the only one. The engine paints to
    /// a canvas, so there is no element for Playwright to click; the ARIA mirror (<c>#cupri-a11y</c>)
    /// stays empty until a screen reader asks for it; and the command palette cannot be reached
    /// either, because Chromium keeps Ctrl+K for itself and the app never sees the key. So: a fixed
    /// viewport, measured rows, and every caller asserts on what actually opened — a nav that moves
    /// fails the wait with a named selector rather than quietly testing the wrong page.</para>
    /// </summary>
    private static readonly Dictionary<string, int> ShowcaseNav = new()
    {
        ["Inputs"] = 140, ["Components"] = 186, ["Charts"] = 232,
        ["Images"] = 278, ["3D"] = 324, ["Overlays"] = 370,
    };

    /// <summary>
    /// Click a sidebar row until <paramref name="expect"/> exists, then return.
    ///
    /// <para>Retried rather than clicked once, because there is no readiness signal both hosts
    /// publish. The fixture waits for a sized canvas and <c>__cupri.isCoarse</c>, but neither means
    /// the app has BUILT a document yet — and the two hosts do not agree on what else is available
    /// (<c>__cupri</c> carries paints/underlays/canvas on NativeAOT-LLVM, and only <c>I</c> and
    /// <c>isCoarse</c> on Mono). A single click on the interpreted host lands before anything is
    /// listening and is silently dropped, which then reports as "no video underlay" 30 seconds later
    /// and blames the seam. Clicking a nav row again is harmless — it re-selects the same section.</para>
    /// </summary>
    private static async Task GoToAsync(IPage page, string section, string expect, ITestOutputHelper? log = null)
    {
        var y = ShowcaseNav[section];

        // Sweep a little either side of the measured row, and repeat the sweep.
        //
        // The offsets are for PORTABILITY: these positions were measured on Windows Chromium, and CI
        // runs the same gate on ubuntu-latest, where different font metrics can move a 46px row by a
        // few pixels and would otherwise fail the whole job on typography. The repeats are for
        // READINESS: neither host publishes a signal meaning "the document is built", and a click
        // that lands before anything is listening is silently dropped — on the interpreted host that
        // then reports as "no video underlay" 30 seconds later, blaming the seam.
        //
        // Landing on the wrong row cannot fool this: every caller passes the selector that only its
        // own section has, so a miss simply tries again.
        int[] offsets = [0, -8, 8, -16, 16, -24, 24];
        for (var round = 0; round < 3; round++)
        {
            foreach (var dy in offsets)
            {
                await page.Mouse.ClickAsync(67, y + dy);
                try
                {
                    await page.WaitForSelectorAsync(expect, new() { Timeout = 2_500 });
                    if (dy != 0) log?.WriteLine(
                        $"note: the {section} row answered at y={y + dy}, not the measured {y} — " +
                        "ShowcaseNav is drifting and should be remeasured.");
                    return;
                }
                catch (TimeoutException) { /* wrong row, or the app was not listening yet */ }
            }
        }
        throw new TimeoutException(
            $"'{expect}' never appeared after clicking around y={y} for the {section} row. Either the " +
            "app never became interactive, or the sidebar has changed enough that ShowcaseNav needs " +
            "remeasuring at the fixture's 1100x780 viewport.");
    }

    /// <summary>Every underlay's geometry as the BROWSER sees it, which is the only opinion that
    /// counts — the managed side can be perfectly correct and still be undone by the JS.</summary>
    private static async Task<IReadOnlyList<Rect>> UnderlaysAsync(IPage page) =>
        await page.EvaluateAsync<Rect[]>(
            "() => Array.from(document.querySelectorAll('video, canvas[id^=\"cupri-underlay\"]'))" +
            "  .map(e => { const r = e.getBoundingClientRect(); return {" +
            "    id: e.id || e.tagName, top: Math.round(r.top), left: Math.round(r.left)," +
            "    w: Math.round(r.width), h: Math.round(r.height)," +
            "    bufW: e.width|0, bufH: e.height|0," +
            "    clip: e.style.clipPath || '', display: e.style.display || '' }; })");

    /// <summary>Settable properties with a parameterless constructor, because Playwright deserialises
    /// evaluate results by Activator.CreateInstance — a positional record fails with "no parameterless
    /// constructor" from inside the transport, which reads like a Playwright bug and is a C# one.</summary>
    private sealed class Rect
    {
        public string Id { get; set; } = "";
        public int Top { get; set; }
        public int Left { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int BufW { get; set; }
        public int BufH { get; set; }
        public string Clip { get; set; } = "";
        public string Display { get; set; } = "";
    }

    /// <summary>
    /// The regression guard for the refactor: a video is an underlay the HOST does not create, and
    /// its positioning must keep working after the syncer stopped being video-specific. The failure
    /// this protects against is not a crash — it is a player frozen at whatever box it first laid
    /// out, which looks fine in a screenshot taken before anything scrolls.
    /// </summary>
    [Fact]
    public async Task A_video_underlay_is_positioned_where_the_engine_laid_it_out()
    {
        var page = await host.DesktopAsync();
        // Wait for the ELEMENT, not for playback: a poster-only player is still an underlay that
        // has to be in the right place.
        await GoToAsync(page, "Images", "video", output);

        var video = (await UnderlaysAsync(page)).FirstOrDefault(u => u.Id == "VIDEO");
        Assert.True(video is not null, $"[{host.Host}] no <video> underlay after opening Images");

        output.WriteLine($"[{host.Host}] video underlay: {video!.Left},{video.Top} {video.W}x{video.H} " +
                         $"clip='{video.Clip}' display='{video.Display}'");

        // Positioned at all — left/top are written by the syncer, so an unsynced element sits at the
        // page origin with no size.
        Assert.True(video.W > 0 && video.H > 0,
            $"[{host.Host}] the video underlay has no size ({video.W}x{video.H}) — the syncer never " +
            "sent it a rect, which is what gating the walk on HostComposited did.");
    }

    /// <summary>
    /// The seam's own test, and the one only a browser can run: an app asks for a canvas underlay,
    /// and the host must create it, size its DRAWING BUFFER (not just its CSS box), and keep both
    /// the position and the clip correct as the engine scrolls underneath it.
    /// </summary>
    [Underlay3dFact]
    public async Task A_canvas_underlay_is_created_sized_and_clipped_by_the_engines_scrolling()
    {
        var page = await host.DesktopAsync();
        await GoToAsync(page, "3D", "canvas#cupri-underlay-showcase3d", output);
        var before = (await UnderlaysAsync(page)).Single(u => u.Id == "cupri-underlay-showcase3d");
        output.WriteLine($"[{host.Host}] canvas underlay: {before.Left},{before.Top} {before.W}x{before.H} " +
                         $"buffer {before.BufW}x{before.BufH} clip='{before.Clip}'");

        Assert.True(before.W > 0 && before.H > 0,
            $"[{host.Host}] the canvas underlay was never given a box ({before.W}x{before.H}).");

        // THE BACKING STORE. A <canvas> defaults to a 300x150 drawing buffer regardless of its CSS
        // size, and a <video> has no such thing — so the video path never needed this and the seam
        // inherited the gap. The symptom is not a missing image but a stretched one, which no
        // managed test can see.
        Assert.True(Math.Abs(before.BufW - before.W) <= 2 && Math.Abs(before.BufH - before.H) <= 2,
            $"[{host.Host}] the canvas drawing buffer is {before.BufW}x{before.BufH} but its box is " +
            $"{before.W}x{before.H}. 300x150 means the host set the CSS size and not the buffer, and " +
            "the app is rendering into the default and being stretched to fit.");

        // …and now the part a hand-rolled "set left and top" cannot do. Scrolling the Showcase's own
        // pane must move the element AND re-clip it against that pane, because the engine's clip is
        // invisible to a DOM element sitting underneath the canvas.
        //
        // SHORTEN THE WINDOW FIRST. At the navigation viewport the whole 3D section fits, so the
        // pane has nothing to scroll and a wheel event changes nothing — which the first version of
        // this test reported as "the underlay did not react", blaming the seam for a page that was
        // simply not scrollable. Resizing after the click is safe: the section is already open.
        await page.SetViewportSizeAsync(1100, 460);
        await page.WaitForTimeoutAsync(600);
        var atRest = (await UnderlaysAsync(page)).Single(u => u.Id == "cupri-underlay-showcase3d");

        await page.Mouse.MoveAsync(700, 300);
        await page.Mouse.WheelAsync(0, 220);
        await page.WaitForTimeoutAsync(700);

        var after = (await UnderlaysAsync(page)).Single(u => u.Id == "cupri-underlay-showcase3d");
        output.WriteLine($"[{host.Host}] at rest after resize: {atRest.Left},{atRest.Top} clip='{atRest.Clip}'");
        output.WriteLine($"[{host.Host}] after scrolling:    {after.Left},{after.Top} clip='{after.Clip}'");

        Assert.True(after.Top != atRest.Top || after.Clip != atRest.Clip,
            $"[{host.Host}] the underlay did not react to the engine scrolling: it stayed at " +
            $"top={after.Top} with clip='{after.Clip}'. The element is a sibling of the engine's " +
            "canvas, so if the syncer stops re-sending its rect it simply floats over the page.");

        // Whatever the pane did, the element must still be a real box — a clip that swallowed it
        // entirely, or a rect collapsed to nothing, would also satisfy "it changed".
        Assert.True(after.W > 0 && after.H > 0 && after.Display != "none",
            $"[{host.Host}] the underlay vanished while scrolling ({after.W}x{after.H}, " +
            $"display='{after.Display}') — visible content should not be hidden.");
    }

    /// <summary>
    /// The ENGINE half of the web seam, which nothing else in this repository checks.
    ///
    /// <para>Its sibling above proves CupriFace created a canvas, sized its drawing buffer and kept
    /// it glued to a scrolling box — every one of which passes with nothing whatsoever drawn into
    /// it. From the DOM, a blank canvas over the right hole is indistinguishable from a working one,
    /// and until this test the browser gates could not tell the two apart.</para>
    ///
    /// <para>So this asserts on the line the engine prints about itself. <c>Khalkos3dContent</c>
    /// logs <c>ready</c> only once <c>GlRenderer.Create</c> has handed back a renderer, which means
    /// a WebGL2 context was acquired and the <c>#version 300 es</c> shader compiled and linked;
    /// anything short of that prints "the engine would not start" instead and this fails on the
    /// missing line. It also asserts the two libraries AGREE: both decide their dialect by parsing
    /// <c>GL_VERSION</c> rather than by guessing from the platform, and a disagreement is precisely
    /// the bug that would otherwise surface as a shader that will not compile on someone's phone.
    /// WebGL2 is OpenGL ES 3.0, so this is the same dialect the Android device gate runs, reached
    /// through a completely different driver.</para>
    ///
    /// <para>It does NOT prove sustained drawing the way the Android gate's <c>frames=60</c> does.
    /// A WebGL canvas without <c>preserveDrawingBuffer</c> reads back blank from outside its own
    /// frame, so there is nothing here for a browser test to sample. Context, compile, link and
    /// first frame is what a browser can honestly witness from this side.</para>
    /// </summary>
    [Underlay3dFact]
    public async Task The_engine_starts_and_agrees_on_the_dialect_in_a_browser()
    {
        var page = await host.DesktopAsync();

        // Subscribed before the SECTION opens rather than before navigation. The viewport acquires
        // its context the first time it is laid out, which is when the 3D section opens, so this is
        // early enough — and DesktopAsync owns the initial Goto, so it is also the earliest point
        // reachable without giving the fixture a console hook that only one test would use.
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) console.Add($"{m.Type}: {m.Text}"); };
        page.PageError += (_, e) => { lock (console) console.Add($"pageerror: {e}"); };

        await GoToAsync(page, "3D", "canvas#cupri-underlay-showcase3d", output);

        // Polled, because the element exists as soon as the painter punches the hole and the context
        // is acquired a frame or two after that. Fifteen seconds is for the interpreted host, where
        // everything between the click and the first GL call runs an order of magnitude slower.
        string? ready = null;
        for (var i = 0; i < 60 && ready is null; i++)
        {
            lock (console) ready = console.FirstOrDefault(l => l.Contains("3d: ready"));
            if (ready is null) await page.WaitForTimeoutAsync(250);
        }

        string[] seen;
        lock (console) seen = [.. console];
        foreach (var line in seen.Where(l => l.Contains("3d:") || l.StartsWith("error") || l.StartsWith("pageerror")))
            output.WriteLine($"[{host.Host}] {line}");

        Assert.True(ready is not null,
            $"[{host.Host}] the engine never reported itself ready in the browser. Every other test " +
            "in this file would still pass with an empty canvas, so this line is the only thing that " +
            "separates a working viewport from a hole with nothing behind it. Last console lines: " +
            string.Join(" | ", seen.TakeLast(15)));

        // GlEs300 on both halves. The engine's answer and the toolkit's answer are computed
        // independently from the same GL_VERSION string, so this is two readings, not one echoed.
        Assert.True(ready!.Contains("dialect=GlEs300") && ready.Contains("cupriface=GlEs300"),
            $"[{host.Host}] WebGL2 is OpenGL ES 3.0, so both libraries should have chosen GlEs300. " +
            $"They reported: {ready}");
    }
}

/// <summary>Opt-in, because only a build with a 3D surface wired at its composition root has a canvas
/// underlay to look at. WebWasm deliberately does not wire one — it shows the poster — so this would
/// otherwise fail on half the matrix for the correct reason.</summary>
internal sealed class Underlay3dFactAttribute : FactAttribute
{
    public const string OptIn = "CUPRI_WEB_3D";

    public Underlay3dFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
            Skip = $"set {OptIn}=1 and point CUPRI_WEB_WWWROOT at a build whose composition root " +
                   "wires a canvas surface (samples/WebLlvm)";
    }
}
