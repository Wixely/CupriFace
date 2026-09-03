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
    private static async Task GoToAsync(IPage page, string section, string expect)
    {
        var y = ShowcaseNav[section];
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await page.Mouse.ClickAsync(67, y);
            try
            {
                await page.WaitForSelectorAsync(expect, new() { Timeout = 5_000 });
                return;
            }
            catch (TimeoutException) { /* the app may not have been listening yet */ }
        }
        throw new TimeoutException(
            $"'{expect}' never appeared after 12 clicks on the {section} row at y={y}. Either the app " +
            "never became interactive, or the sidebar rows have moved and ShowcaseNav needs remeasuring " +
            "at the fixture's 1100x780 viewport.");
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
        await GoToAsync(page, "Images", "video");

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
        await GoToAsync(page, "3D", "canvas#cupri-underlay-showcase3d");
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
