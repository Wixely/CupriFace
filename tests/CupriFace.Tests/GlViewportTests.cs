using CupriFace.Gl;
using CupriFace.Gl.Internal;
using CupriFace.Paint;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The <c>CupriFace.Gl</c> seam, from the side a machine with no GL context can reach — which is
/// more of it than one might expect, and deliberately so.
///
/// <para>What cannot be tested here is anything that needs a driver: the shared-GPU handoff, the
/// readback flip, the WebGL2 canvas. Those are verified by the sample and its gates, on real
/// hardware, because the whole lesson of this work is that a driver is the one thing a test suite
/// cannot stand in for. Every rendering defect in the 3D work so far passed CI and 800 unit tests
/// and looked correct on one desktop driver.</para>
///
/// <para>What CAN be tested is the part that decides whether a driver is ever reached: registration,
/// the on-screen gate, element-following sizing, the degradation path, and the promise that a
/// failure names all of its causes at once. Those are also where the bugs that reached a human
/// actually were.</para>
/// </summary>
public class GlViewportTests
{
    /// <summary>Content that records what it was asked and does no GL — enough to prove the viewport
    /// never reaches for a driver it has not got.</summary>
    private sealed class NoopContent : IGlContent
    {
        public int InitialiseCalls, RenderCalls, ShutdownCalls;
        public bool Initialise(GlContext gl) { InitialiseCalls++; return true; }
        public void Render(GlContext gl, in GlFrame frame) => RenderCalls++;
        public void Shutdown(GlContext gl) => ShutdownCalls++;
    }

    private const string Html = "<body><div class='stage' data-cupri-surface='scene'></div></body>";
    private const string Css = ".stage { width: 100px; height: 60px; }";

    [Fact]
    public void Attaching_registers_the_surface_and_disposing_takes_it_away()
    {
        using var t = new TestDoc(Html, Css);
        var viewport = GlViewport.Attach(t.Doc, "scene", new NoopContent());

        Assert.Same(viewport, t.Doc.Surfaces.Get("scene"));
        Assert.Equal(GlViewportState.Waiting, viewport.State);
        Assert.Equal(GlLane.None, viewport.Lane);

        viewport.Dispose();
        Assert.Null(t.Doc.Surfaces.Get("scene"));
    }

    [Fact]
    public void Attach_refuses_the_arguments_that_could_only_be_mistakes()
    {
        using var t = new TestDoc(Html, Css);
        var content = new NoopContent();

        Assert.Throws<ArgumentNullException>(() => GlViewport.Attach(null!, "scene", content));
        Assert.Throws<ArgumentNullException>(() => GlViewport.Attach(t.Doc, "scene", null!));
        Assert.Throws<ArgumentException>(() => GlViewport.Attach(t.Doc, "", content));
    }

    [Fact]
    public void Nothing_touches_gl_before_the_element_is_on_screen()
    {
        // The element in this document does not exist at all, which is the same shape as a tabbed app
        // parked on another section — and must not be treated as a failure.
        using var t = new TestDoc("<body><p>no viewport here</p></body>", "");
        var content = new NoopContent();
        using var viewport = GlViewport.Attach(t.Doc, "scene", content);
        t.Doc.Refresh();

        for (var i = 0; i < 50; i++) Assert.False(viewport.Ticking);

        Assert.Equal(0, content.InitialiseCalls);
        Assert.Equal(GlViewportState.Waiting, viewport.State);
        Assert.Null(viewport.Diagnostic);
    }

    [Fact]
    public void A_viewport_inside_a_hidden_section_does_not_keep_the_host_awake()
    {
        // The bug this pins down cost a browser gate: a surface that reports Ticking for ever folds
        // into the document's "something is animating" signal, so a render-on-demand host NEVER
        // idles. It looks like nothing — the paint count stays flat, because there is no damage —
        // and it surfaced as tabbing no longer reaching text fields.
        //
        // The subtlety is WHICH signal is right. "Did the painter ask about me" is wrong and looks
        // right: the display list is rebuilt every tick to compute damage, so the painter consults
        // surfaces inside display:none too. LaidOut is the discriminator.
        using var t = new TestDoc(
            "<body><div style='display:none'><div data-cupri-surface='scene' " +
            "style='width:100px;height:60px'></div></div></body>", "");
        using var viewport = GlViewport.Attach(t.Doc, "scene", new NoopContent());
        t.Doc.Refresh();
        t.Doc.RenderToPixels(200, 120);

        Assert.False(viewport.Ticking);
        Assert.False(t.Doc.Surfaces.AnyTicking);
    }

    [Fact]
    public void A_host_that_shares_no_context_degrades_and_says_why()
    {
        // No GPU hook (this document is never painted by a GL host) and no offscreen factory: the
        // viewport must give up, exactly once, with a reason a human can act on — and the element
        // goes on showing its poster. This is the ordinary outcome on a software window, not an
        // error path.
        using var t = new TestDoc(Html, Css);
        var content = new NoopContent();
        using var viewport = GlViewport.Attach(t.Doc, "scene", content);
        t.Doc.Refresh();
        t.Doc.RenderToPixels(200, 120);

        Assert.False(t.Doc.Surfaces.HasGpuFrameHook);
        for (var i = 0; i < 40; i++) _ = viewport.Ticking;

        Assert.Equal(GlViewportState.Unavailable, viewport.State);
        Assert.Contains("OffscreenContext", viewport.Diagnostic);
        Assert.Equal(0, content.InitialiseCalls);
        Assert.False(viewport.Ticking);
        Assert.False(t.Doc.Surfaces.AnyTicking);
    }

    [Fact]
    public void The_render_size_follows_the_element_and_the_host_scale()
    {
        // Item 2 of the scoping document. The sample rendered a fixed 512x512 into whatever box the
        // CSS gave it, so a 3x phone upscaled a third-resolution image into the panel — soft, with
        // nothing in the markup to explain it.
        using var t = new TestDoc(Html, Css);
        using var viewport = GlViewport.Attach(t.Doc, "scene", new NoopContent());
        t.Doc.Refresh();
        t.Doc.RenderToPixels(200, 120);

        t.Doc.Surfaces.DeviceScale = 1f;
        Assert.Equal((100, 60), viewport.ElementSize());

        t.Doc.Surfaces.DeviceScale = 2f;
        Assert.Equal((200, 120), viewport.ElementSize());

        // A fractional scale must round UP: a target one pixel short of the box is upscaled, and the
        // seam between the viewport and the CSS beside it is exactly where that shows.
        t.Doc.Surfaces.DeviceScale = 1.5f;
        Assert.Equal((150, 90), viewport.ElementSize());
    }

    [Fact]
    public void A_fixed_size_overrides_the_element_and_the_natural_size_never_moves()
    {
        using var t = new TestDoc(Html, Css);
        using var viewport = GlViewport.Attach(t.Doc, "scene", new NoopContent(),
            new GlViewportOptions { Size = (320, 240) });
        t.Doc.Refresh();
        t.Doc.RenderToPixels(200, 120);
        t.Doc.Surfaces.DeviceScale = 3f;

        Assert.Equal((320, 240), viewport.ElementSize());
        // NaturalSize feeds intrinsic sizing. Reporting the live render size there would put a box's
        // own layout back into its own input, which is a loop rather than a refinement.
        Assert.Equal((320, 240), viewport.NaturalSize);
    }

    [Fact]
    public void The_size_is_clamped_so_a_huge_display_cannot_ask_for_a_target_no_driver_will_make()
    {
        using var t = new TestDoc(Html, Css);
        using var viewport = GlViewport.Attach(t.Doc, "scene", new NoopContent(),
            new GlViewportOptions { MinPixels = 32, MaxPixels = 128 });
        t.Doc.Refresh();
        t.Doc.RenderToPixels(200, 120);

        t.Doc.Surfaces.DeviceScale = 10f;
        Assert.Equal((128, 128), viewport.ElementSize());

        t.Doc.Surfaces.DeviceScale = 0.05f;
        Assert.Equal((32, 32), viewport.ElementSize());
    }

    [Fact]
    public void Off_the_browser_there_is_no_underlay_to_ask_for()
    {
        using var t = new TestDoc(Html, Css);
        using var viewport = GlViewport.Attach(t.Doc, "scene", new NoopContent());

        // The painted lanes composite through the engine. Reporting HostComposited here would punch a
        // transparent hole with nothing behind it.
        Assert.False(viewport.HostComposited);
        Assert.Null(viewport.UnderlayElement);
    }

    // ---- the pieces below the viewport ----------------------------------------------------------

    [Theory]
    [InlineData(GlDialect.Gl330Core, "#version 330 core\n")]
    [InlineData(GlDialect.GlEs300, "#version 300 es\n")]
    public void The_shader_header_is_the_one_thing_an_app_cannot_be_shielded_from(GlDialect dialect, string expected)
    {
        var gl = new GlContext(dialect, GlLane.SharedGpu, _ => 0, null!);
        Assert.Equal(expected, gl.ShaderHeader);
    }

    [Fact]
    public void Resolving_entry_points_names_every_absentee_rather_than_the_first()
    {
        // On a partial or unusual driver this is the difference between one diagnosis and ten runs —
        // which is why it is a named method rather than something a caller loops by hand.
        var gl = new GlContext(GlDialect.Gl330Core, GlLane.SharedGpu,
            name => name.Contains("Missing") ? 0 : 42, null!);

        var addresses = gl.GetProcAddresses(
            ["glOne", "glMissingA", "glTwo", "glMissingB"], out var missing);

        Assert.Equal([42, 0, 42, 0], addresses);
        Assert.Equal(["glMissingA", "glMissingB"], missing);
    }

    [Fact]
    public void An_entry_point_found_only_under_its_extension_spelling_still_counts()
    {
        // Some drivers publish only the ARB name of a function that later became core. A loader that
        // did not try both would report a working driver as broken.
        var gl = new GlContext(GlDialect.Gl330Core, GlLane.SharedGpu,
            name => name == "glThingARB" ? 7 : 0, null!);

        Assert.Equal(7, gl.GetProcAddress("glThing"));
    }

    [Fact]
    public void The_packages_own_table_refuses_to_load_half_resolved_and_lists_what_was_absent()
    {
        // Half a table is worse than none: it resolves far enough to look healthy and then calls
        // into address zero somewhere else entirely.
        var fn = GlFunctions.Load(
            name => name.StartsWith("glGen") ? 0 : 1, out var missing);

        Assert.Null(fn);
        Assert.Contains("glGenFramebuffers", missing);
        Assert.Contains("glGenTextures", missing);
        Assert.Contains("glGenRenderbuffers", missing);
        Assert.DoesNotContain("glClear", missing);
    }

    [Fact]
    public void The_device_scale_refuses_values_that_would_produce_no_pixels()
    {
        // Hosts set this every frame from their own state; a zero or a NaN arriving during a resize
        // must not turn into a zero-sized framebuffer several layers away.
        var registry = new SurfaceRegistry();
        Assert.Equal(1f, registry.DeviceScale);

        registry.DeviceScale = 2.5f;
        Assert.Equal(2.5f, registry.DeviceScale);

        registry.DeviceScale = 0f;
        Assert.Equal(1f, registry.DeviceScale);

        registry.DeviceScale = -3f;
        Assert.Equal(1f, registry.DeviceScale);

        registry.DeviceScale = float.NaN;
        Assert.Equal(1f, registry.DeviceScale);
    }
}
