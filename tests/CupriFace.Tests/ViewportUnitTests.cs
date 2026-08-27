using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Viewport-relative lengths (<c>vh</c>/<c>vw</c>/<c>vmin</c>/<c>vmax</c>, and the
/// <c>dvh</c>/<c>svh</c>/<c>lvh</c> family). These were not parsed at all: the value fell through
/// to the px parser, whose fallback is 0, so <c>height:100vh</c> became a DEFINITE <c>0px</c>.
///
/// That is not a cosmetic miss. A full-screen <c>height:100vh</c> container with
/// <c>overflow:hidden</c> collapsed to zero height and clipped its whole subtree away, so an app
/// with a complete, populated display list painted a uniformly black screen — reported as an
/// Android surface bug (#71) because it first showed up after an activity recreate.
/// </summary>
public class ViewportUnitTests
{
    private const int W = 400, H = 800;
    private const string Reset = "body{margin:0;padding:0}";

    private static float HeightOf(string style, int w = W, int h = H)
    {
        using var t = new TestDoc($"<body><div id=\"x\" style=\"{style}\">x</div></body>", Reset, width: w, height: h);
        return TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "x")!.Height;
    }

    private static float WidthOf(string style, int w = W, int h = H)
    {
        using var t = new TestDoc($"<body><div id=\"x\" style=\"{style}\">x</div></body>", Reset, width: w, height: h);
        return TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "x")!.Width;
    }

    [Theory]
    [InlineData("height:100vh", 800f)]
    [InlineData("height:50vh", 400f)]
    [InlineData("height:12.5vh", 100f)]
    [InlineData("height:100dvh", 800f)]   // no browser chrome here, so d/s/l are all the viewport
    [InlineData("height:100svh", 800f)]
    [InlineData("height:100lvh", 800f)]
    [InlineData("height:100vmin", 400f)]  // min(400,800)
    [InlineData("height:100vmax", 800f)]  // max(400,800)
    public void Vertical_viewport_units_resolve(string style, float expected) =>
        Assert.Equal(expected, HeightOf(style), 3);

    [Theory]
    [InlineData("width:100vw", 400f)]
    [InlineData("width:25vw", 100f)]
    [InlineData("width:100dvw", 400f)]
    [InlineData("width:50vmin", 200f)]
    [InlineData("width:50vmax", 400f)]
    public void Horizontal_viewport_units_resolve(string style, float expected) =>
        Assert.Equal(expected, WidthOf(style), 3);

    [Fact]
    public void Viewport_units_work_inside_calc()
    {
        // calc is parsed downstream of the substitution, so its terms get viewport units too.
        Assert.Equal(736f, HeightOf("height:calc(100vh - 64px)"), 3);
        Assert.Equal(464f, HeightOf("height:calc(50vh + 64px)"), 3);
    }

    [Fact]
    public void Viewport_units_work_through_a_custom_property()
    {
        // Substitution runs after var() resolution, so a token holding a viewport unit works.
        using var t = new TestDoc(
            "<body style=\"--full:100vh\"><div id=\"x\" style=\"height:var(--full)\">x</div></body>",
            Reset, width: W, height: H);
        Assert.Equal(800f, TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "x")!.Height, 3);
    }

    [Fact]
    public void Viewport_units_track_the_viewport_size()
    {
        // The resolver is rebuilt with the live viewport each frame, so a resize re-resolves.
        Assert.Equal(600f, HeightOf("height:100vh", w: 300, h: 600), 3);
        Assert.Equal(1000f, HeightOf("height:100vh", w: 300, h: 1000), 3);
    }

    [Fact]
    public void Other_units_are_unaffected()
    {
        Assert.Equal(64f, HeightOf("height:64px"), 3);
        Assert.Equal(32f, HeightOf("height:2rem"), 3);
        Assert.Equal(400f, HeightOf("height:50%"), 3);
    }

    [Fact]
    public void A_v_in_an_ordinary_value_is_not_rewritten()
    {
        // The substitution is anchored to a number, so identifiers and functions are untouched.
        using var t = new TestDoc(
            "<body><div id=\"x\" style=\"font-family:Avenir; color:rgb(1,2,3); height:7px\">x</div></body>",
            Reset, width: W, height: H);
        var n = TestDoc.Find(t.Root, e => e.Element?.GetAttribute("id") == "x")!;
        Assert.Equal(7f, n.Height, 3);
        Assert.Equal("Avenir", n.Style.FontFamily);
    }

    [Fact]
    public void An_unreadable_unit_is_auto_never_a_definite_zero()
    {
        // The general defence behind #71: a length the parser cannot read must not become a
        // definite 0px, because a definite zero collapses the box and clips its subtree.
        // `auto` on a block means "fill the inline axis", so the width is the viewport, not 0.
        Assert.Equal(400f, WidthOf("width:20qq"), 3);
        Assert.True(HeightOf("height:20qq") > 0f, "an unreadable height must not collapse the box");
    }

    /// <summary>The #71 shape end to end: a full-screen flex column with <c>overflow:hidden</c>.
    /// With <c>100vh</c> collapsed to zero this clipped everything and rendered black; the children
    /// also shrank to nothing because a zero-height flex column shrinks its items, leaving only the
    /// one pinned by <c>min-height</c> — exactly the "stage collapsed, inspector overlapping"
    /// report.</summary>
    [Fact]
    public void Full_screen_flex_column_lays_out_and_does_not_clip_itself_away()
    {
        const string html =
            "<body><div id=\"editor\" style=\"display:flex; height:100vh; flex-direction:column; overflow:hidden\">" +
            "<div id=\"header\" style=\"height:64px\">Header</div>" +
            "<div id=\"stage\" style=\"flex:1; min-height:80px\">Stage</div>" +
            "<div id=\"inspector\" style=\"height:238px\">Inspector</div>" +
            "</div></body>";

        using var t = new TestDoc(html, Reset, width: W, height: H);
        float Box(string id, out float y)
        {
            var n = TestDoc.Find(t.Root, e => e.Element?.GetAttribute("id") == id)!;
            y = n.Y;
            return n.Height;
        }

        var editor = Box("editor", out _);
        var header = Box("header", out var headerY);
        var stage = Box("stage", out var stageY);
        var inspector = Box("inspector", out var inspectorY);

        Assert.Equal(H, editor, 3);                    // the container fills the viewport
        Assert.Equal(64f, header, 3);
        Assert.Equal(238f, inspector, 3);
        Assert.Equal(H - 64f - 238f, stage, 3);        // flex:1 takes the remainder, well above min-height

        // Stacked, not overlapping — the malformed layout in the report was these colliding.
        Assert.Equal(0f, headerY, 3);
        Assert.Equal(64f, stageY, 3);
        Assert.Equal(H - 238f, inspectorY, 3);

        // And the clip the container imposes is its real box, so the subtree survives it.
        Assert.True(stageY + stage <= editor, "content must sit inside the overflow clip");
    }

    /// <summary>The same container revealed by a binding change, which is how the reporting app
    /// reached it (home page → editor page). The transition must land on the same layout as
    /// building it visible from the start.</summary>
    [Fact]
    public void Revealing_a_full_screen_container_matches_building_it_visible()
    {
        const string html =
            "<body><div id=\"editor\" style=\"display:{{Display}}; height:100vh; flex-direction:column; overflow:hidden\">" +
            "<div id=\"header\" style=\"height:64px\">Header</div>" +
            "<div id=\"stage\" style=\"flex:1; min-height:80px\">Stage</div>" +
            "</div></body>";

        var shown = new Model { Display = "flex" };
        using var reference = new TestDoc(html, Reset, shown, width: W, height: H);
        var refStage = TestDoc.Find(reference.Root, n => n.Element?.GetAttribute("id") == "stage")!.Height;

        var m = new Model { Display = "none" };
        using var t = new TestDoc(html, Reset, m, width: W, height: H);
        m.Display = "flex";
        t.Doc.Refresh();
        t.Layout();

        var editor = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "editor")!;
        var stage = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "stage")!;
        Assert.Equal(H, editor.Height, 3);
        Assert.Equal(refStage, stage.Height, 3);
        Assert.Equal(H - 64f, stage.Height, 3);
    }

    private sealed class Model { public string Display { get; set; } = "none"; }
}
