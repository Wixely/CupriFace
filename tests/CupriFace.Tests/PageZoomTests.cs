using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Whole-document zoom — the accessibility kind, not a magnifier.
///
/// The distinction is the whole design: a magnifier keeps layout fixed and pans around a bigger
/// surface, which forces a low-vision reader to scroll sideways through every line. This REFLOWS —
/// the document lays out at viewport/Zoom and paints scaled up — so a zoomed page stays one column,
/// exactly as a browser's Ctrl+= behaves, and `@media` queries see the narrower width.
///
/// Everything below is a claim a host depends on: what you click is what you see, and what a screen
/// reader is told matches where things are drawn.
/// </summary>
public class PageZoomTests
{
    private const string Html =
        "<body><div class='page'><div class='box'>Target</div><div class='tall'>t</div></div></body>";
    private const string Css =
        "body{margin:0} .page{width:100%;height:100%;overflow:scroll} " +
        ".box{width:100px;height:40px} .tall{height:900px}";

    private static RenderNode Find(CupriDocument doc, string cls)
    {
        static RenderNode? F(RenderNode n, string c) =>
            n.Element?.ClassList.Contains(c) == true ? n
            : n.Children.Select(ch => F(ch, c)).FirstOrDefault(r => r is not null);
        return F(doc.Root, cls)!;
    }

    [Fact]
    public void Zoom_defaults_to_one_and_clamps()
    {
        using var doc = CupriDocument.Load(Html, Css);
        Assert.Equal(1f, doc.Zoom);

        doc.Zoom = 99f;
        Assert.Equal(CupriDocument.MaxZoom, doc.Zoom);
        doc.Zoom = 0.01f;
        Assert.Equal(CupriDocument.MinZoom, doc.Zoom);
    }

    [Fact]
    public void Zooming_in_reflows_to_a_narrower_viewport()
    {
        // The heart of "reflow, not magnify": at 2x the page is laid out in HALF the width, so text
        // wraps into the column instead of running off the side.
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(800, 600);
        Assert.Equal(800f, Find(doc, "page").Width, 1);

        doc.Zoom = 2f;
        doc.BuildFrame(800, 600);
        Assert.Equal(400f, Find(doc, "page").Width, 1);
    }

    [Fact]
    public void Media_queries_see_the_zoomed_width()
    {
        // A phone layout at desktop size, purely because the reader zoomed in — which is precisely
        // the behaviour that makes zoom usable rather than a mess of overlapping boxes.
        using var doc = CupriDocument.Load(
            "<body><div class='box'>t</div></body>",
            "body{margin:0} .box{width:50px;height:10px} @media (max-width: 500px){ .box{width:33px} }");
        doc.BuildFrame(800, 600);
        Assert.Equal(50f, Find(doc, "box").Width, 1);   // 800px wide: the wide rule

        doc.Zoom = 2f;                                   // now laying out at 400px
        doc.BuildFrame(800, 600);
        Assert.Equal(33f, Find(doc, "box").Width, 1);
    }

    [Fact]
    public void A_click_lands_where_the_element_is_drawn()
    {
        // The bug that would make zoom worthless: the host reports a point in WINDOW pixels, and a
        // zoomed document must divide it before addressing a node. The box is 100x40 at the origin,
        // so at 2x it occupies 0..200 x 0..80 on screen.
        using var doc = CupriDocument.Load(Html, Css);
        var hits = 0;
        doc.OnClick(".box", _ => hits++);
        doc.BuildFrame(800, 600);

        doc.Zoom = 2f;
        doc.BuildFrame(800, 600);

        doc.DispatchClick(150, 60);       // inside the drawn box, outside its unzoomed 100x40
        Assert.Equal(1, hits);

        doc.DispatchClick(260, 60);       // beyond the drawn box: must miss
        Assert.Equal(1, hits);
    }

    [Fact]
    public void The_semantics_tree_reports_bounds_in_host_space()
    {
        // A screen reader draws its focus rectangle from these bounds and a magnifier follows them.
        // Reporting document coordinates on a zoomed page would point assistive technology at the
        // wrong part of the screen — the one bug that would make zoom hostile to its own users.
        using var doc = CupriDocument.Load(
            "<body><div class='box' role='button' aria-label='Go'>Go</div></body>",
            "body{margin:0} .box{width:100px;height:40px}");
        doc.BuildFrame(800, 600);

        static Accessibility.AccessibilityNode? Named(Accessibility.AccessibilityNode n, string name) =>
            n.Name == name ? n : n.Children.Select(c => Named(c, name)).FirstOrDefault(r => r is not null);

        var at1 = Named(doc.BuildAccessibilityTree(800, 600), "Go")!;
        doc.Zoom = 2f;
        var at2 = Named(doc.BuildAccessibilityTree(800, 600), "Go")!;

        Assert.Equal(at1.Bounds.W * 2, at2.Bounds.W, 1);
        Assert.Equal(at1.Bounds.H * 2, at2.Bounds.H, 1);
    }

    [Fact]
    public void Scrolling_still_addresses_the_right_scroller_when_zoomed()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 300);
        doc.Zoom = 2f;
        doc.BuildFrame(400, 300);

        Assert.True(doc.DispatchWheel(200, 150, 120), "a wheel over the zoomed page scrolled nothing");
        Assert.True(Find(doc, "page").ScrollY > 50);
    }

    // ---- the gesture -----------------------------------------------------------------------

    private static void Finger(CupriDocument doc, int id, Interaction.PointerPhase phase, float x, float y) =>
        doc.DispatchPointer(id, phase, x, y);

    [Fact]
    public void Two_fingers_spreading_zoom_the_page_in()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(800, 600);

        Finger(doc, 1, Interaction.PointerPhase.Down, 300, 300);
        Finger(doc, 2, Interaction.PointerPhase.Down, 500, 300);   // 200 apart
        Finger(doc, 1, Interaction.PointerPhase.Move, 200, 300);
        Finger(doc, 2, Interaction.PointerPhase.Move, 600, 300);   // 400 apart: 2x

        Assert.Equal(2f, doc.Zoom, 1);
    }

    [Fact]
    public void Pinching_together_zooms_back_out()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(800, 600);
        doc.Zoom = 2f;

        Finger(doc, 1, Interaction.PointerPhase.Down, 200, 300);
        Finger(doc, 2, Interaction.PointerPhase.Down, 600, 300);   // 400 apart
        Finger(doc, 1, Interaction.PointerPhase.Move, 300, 300);
        Finger(doc, 2, Interaction.PointerPhase.Move, 500, 300);   // 200 apart: half

        Assert.Equal(1f, doc.Zoom, 1);
    }

    [Fact]
    public void One_finger_is_not_a_pinch()
    {
        // A single finger must still tap, scroll and fling exactly as before — the recogniser only
        // consumes a pointer once a second one joins it.
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(800, 600);

        Assert.False(doc.DispatchPointer(1, Interaction.PointerPhase.Down, 300, 300),
            "a lone finger was consumed — taps and scrolling would stop working");
        Assert.False(doc.DispatchPointer(1, Interaction.PointerPhase.Move, 300, 260),
            "a lone finger's move was consumed");
        Assert.Equal(1f, doc.Zoom);
        Assert.False(doc.PageZoomActive);
    }

    [Fact]
    public void An_element_that_owns_the_gesture_keeps_both_fingers()
    {
        // The rule that keeps a collage tile (or a map) working: capture wins. Page zoom must never
        // steal a gesture an element opted into.
        using var doc = CupriDocument.Load(
            "<body><div class='tile' data-gesture='photo'>t</div></body>",
            "body{margin:0} .tile{width:400px;height:400px}");
        var seen = 0;
        doc.OnManipulate("data-gesture", _ => { seen++; return true; });
        doc.BuildFrame(800, 600);

        Finger(doc, 1, Interaction.PointerPhase.Down, 100, 100);
        Finger(doc, 2, Interaction.PointerPhase.Down, 300, 100);
        Finger(doc, 1, Interaction.PointerPhase.Move, 50, 100);
        Finger(doc, 2, Interaction.PointerPhase.Move, 350, 100);

        Assert.True(seen >= 3, "the element's own gesture never ran");
        Assert.Equal(1f, doc.Zoom);          // the page did NOT zoom
        Assert.False(doc.PageZoomActive);
    }

    [Fact]
    public void The_gesture_can_be_turned_off()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.PageZoomEnabled = false;
        doc.BuildFrame(800, 600);

        Finger(doc, 1, Interaction.PointerPhase.Down, 300, 300);
        Finger(doc, 2, Interaction.PointerPhase.Down, 500, 300);
        Finger(doc, 1, Interaction.PointerPhase.Move, 200, 300);
        Finger(doc, 2, Interaction.PointerPhase.Move, 600, 300);

        Assert.Equal(1f, doc.Zoom);
    }

    [Fact]
    public void Lifting_a_finger_ends_the_pinch_and_the_next_one_starts_fresh()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(800, 600);

        Finger(doc, 1, Interaction.PointerPhase.Down, 300, 300);
        Finger(doc, 2, Interaction.PointerPhase.Down, 500, 300);
        Finger(doc, 1, Interaction.PointerPhase.Move, 200, 300);
        Finger(doc, 2, Interaction.PointerPhase.Move, 600, 300);
        var afterFirst = doc.Zoom;
        Assert.True(afterFirst > 1.5f);

        Finger(doc, 1, Interaction.PointerPhase.Up, 200, 300);
        Finger(doc, 2, Interaction.PointerPhase.Up, 600, 300);
        Assert.False(doc.PageZoomActive);

        // A second pinch composes onto the first rather than snapping back — the same
        // bank-then-multiply rule the element recogniser needed.
        Finger(doc, 3, Interaction.PointerPhase.Down, 300, 300);
        Finger(doc, 4, Interaction.PointerPhase.Down, 500, 300);
        Assert.Equal(afterFirst, doc.Zoom, 2);
        Finger(doc, 3, Interaction.PointerPhase.Move, 250, 300);
        Finger(doc, 4, Interaction.PointerPhase.Move, 550, 300);
        Assert.True(doc.Zoom > afterFirst, "the second pinch reset instead of continuing");
    }
}
