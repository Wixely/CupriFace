using System.Collections.Generic;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>A `transition: height` animates the laid-out height — a real layout animation: the element
/// resizes each frame and everything below it reflows. Covers auto (measured natural height) and
/// explicit-px targets, expand and collapse.</summary>
public class HeightTransitionTests
{
    // A panel that's 40px tall collapsed and expands to fit a 160px child (height:auto) on hover, with a
    // marker below it so we can watch the reflow. Move to a far corner to un-hover (collapse).
    private const string Css = """
        body { background:#ffffff; }
        .panel { width:240px; height:40px; overflow:hidden; background:#eeeeee;
                 transition: height 0.3s linear; }
        .panel:hover { height:auto; }
        .tall { height:160px; background:#4682B4; }
        .after { height:20px; background:#B87333; }
        """;
    private const string Html = "<body><div class='panel'><div class='tall'>c</div></div><div class='after'>a</div></body>";

    // Height after applying the transition at `sec`, then laying out (host order: Animate → BuildFrame).
    private static float HeightAt(TestDoc t, string cls, double sec) { t.Doc.Animate(sec); t.Layout(); return t.FindClass(cls).Height; }
    private static float TopAt(TestDoc t, string cls, double sec) { t.Doc.Animate(sec); t.Layout(); return t.FindClass(cls).Y; }

    [Fact]
    public void Height_animates_from_collapsed_to_the_measured_auto_height()
    {
        using var t = new TestDoc(Html, Css, null, width: 400, height: 400);
        Assert.Equal(40f, t.FindClass("panel").Height, 0.5);   // collapsed

        t.HoverClass("panel");
        Assert.True(t.Doc.HasActiveTransitions);               // hover flipped height:40 → auto

        Assert.Equal(40f, HeightAt(t, "panel", 0.0), 1.0);     // t=0 holds the start
        Assert.InRange(HeightAt(t, "panel", 0.15), 70f, 130f); // linear halfway between 40 and 160
        Assert.Equal(160f, HeightAt(t, "panel", 0.4), 1.0);    // settles at the natural (auto) height
        Assert.False(t.Doc.HasActiveTransitions);              // done
    }

    [Fact]
    public void Expanding_reflows_the_content_below_it()
    {
        using var t = new TestDoc(Html, Css, null, width: 400, height: 400);
        t.HoverClass("panel");

        var closed = TopAt(t, "after", 0.0);                   // marker sits just under the 40px panel
        var open = TopAt(t, "after", 0.4);                     // …and is pushed down as the panel grows
        Assert.Equal(40f, closed, 1.0);
        Assert.Equal(160f, open, 1.0);
        Assert.True(open > closed + 100f, $"the marker below reflowed down ({closed} → {open})");
    }

    [Fact]
    public void Un_hover_collapses_back_down()
    {
        using var t = new TestDoc(Html, Css, null, width: 400, height: 400);
        t.HoverClass("panel");
        HeightAt(t, "panel", 0.0); HeightAt(t, "panel", 0.4);  // settle open (160)

        t.Move(390, 390);                                      // far corner → un-hover
        Assert.True(t.Doc.HasActiveTransitions);
        HeightAt(t, "panel", 0.5);                             // first frame stamps the collapse start
        Assert.InRange(HeightAt(t, "panel", 0.65), 70f, 130f); // 0.15 into the collapse, back through the middle
        Assert.Equal(40f, HeightAt(t, "panel", 0.9), 1.0);     // fully collapsed again
    }

    private sealed class TwoPanels { public bool A { get; set; } = true; public bool B { get; set; } }

    private static List<RenderNode> ByClass(RenderNode n, string c, List<RenderNode>? a = null)
    { a ??= new(); if (n.Element?.ClassList.Contains(c) == true) a.Add(n); foreach (var k in n.Children) ByClass(k, c, a); return a; }

    [Fact]
    public void An_open_panel_is_not_disturbed_by_toggling_a_sibling_with_mouse_movement()
    {
        // Regression: A stays open while B is toggled. A hover-update fires right after the click's rebuild
        // but before layout, so its capture saw an unlaid (0-height) tree — A's displayed height was read
        // as 0 while its natural height stayed correct, and A spuriously animated open from nothing.
        var m = new TwoPanels(); // A open, B closed
        const string html = "<body><cupri-accordion>" +
            "<cupri-accordion-item label='A' open='{{A}}'>alpha beta gamma delta epsilon zeta eta theta iota</cupri-accordion-item>" +
            "<cupri-accordion-item label='B' open='{{B}}'>one two three four five six seven eight nine ten eleven</cupri-accordion-item>" +
            "</cupri-accordion></body>";
        using var t = new TestDoc(html, "", m, width: 400, height: 500, components: true);

        float PanelA() => ByClass(t.Root, "cupri-acc-panel")[0].Height;
        var stable = PanelA();
        Assert.True(stable > 10f, "A starts open");

        var b = HitTesting.AbsoluteBox(ByClass(t.Root, "cupri-acc-header")[1]); // B's header (A above is fixed)
        float bx = b.X + b.W / 2, by = b.Y + b.H / 2;
        t.Doc.DispatchPointerMove(bx, by); t.Layout();

        for (var i = 0; i < 8; i++)
        {
            t.Doc.DispatchClick(bx, by, 1);        // toggle B → rebuild (fresh tree, not yet laid out)
            t.Doc.DispatchPointerMove(bx, by);     // hover-update lands on that unlaid tree
            t.Doc.Animate(i * 0.05); t.Layout();
            Assert.InRange(PanelA(), stable - 1f, stable + 1f); // A must not move
        }
    }

    [Fact]
    public void Explicit_px_height_targets_animate_too()
    {
        const string css = """
            body { background:#ffffff; }
            .d { width:120px; height:50px; overflow:hidden; background:#eeeeee; transition: height 0.4s linear; }
            .d:hover { height:150px; }
            """;
        using var t = new TestDoc("<body><div class='d'>x</div></body>", css, null, width: 300, height: 300);
        t.HoverClass("d");
        Assert.Equal(50f, HeightAt(t, "d", 0.0), 1.0);
        Assert.Equal(100f, HeightAt(t, "d", 0.2), 3.0);        // linear halfway 50 → 150
        Assert.Equal(150f, HeightAt(t, "d", 0.4), 1.0);
    }
}
