using Xunit;

namespace CupriFace.Tests;

/// <summary>A `transition: width` animates the laid-out width — a real layout animation: the element
/// resizes and its neighbours reflow. Mirrors the height transition, used for collapsible sidebars/rails.</summary>
public class WidthTransitionTests
{
    // A 64px rail in a flex row that expands to 200px on hover; the main pane (flex:1) fills the rest.
    private const string Css = """
        body { background:#ffffff; }
        .app { display:flex; width:400px; height:100px; }
        .side { width:64px; height:100px; overflow:hidden; background:#eeeeee; transition: width 0.3s linear; }
        .side:hover { width:200px; }
        .main { flex:1; height:100px; background:#dddddd; }
        """;
    private const string Html = "<body><div class='app'><div class='side'>s</div><div class='main'>m</div></div></body>";

    private static float WidthAt(TestDoc t, string cls, double sec) { t.Doc.Animate(sec); t.Layout(); return t.FindClass(cls).Width; }

    [Fact]
    public void Width_animates_from_collapsed_to_expanded()
    {
        using var t = new TestDoc(Html, Css, null, width: 500, height: 200);
        Assert.Equal(64f, t.FindClass("side").Width, 0.5);     // collapsed rail

        t.HoverClass("side");
        Assert.True(t.Doc.HasActiveTransitions);                // hover flipped width 64 → 200

        Assert.Equal(64f, WidthAt(t, "side", 0.0), 1.0);        // t=0 holds the start
        Assert.InRange(WidthAt(t, "side", 0.15), 100f, 165f);   // linear halfway between 64 and 200
        Assert.Equal(200f, WidthAt(t, "side", 0.4), 1.0);       // settled expanded
        Assert.False(t.Doc.HasActiveTransitions);
    }

    [Fact]
    public void Expanding_the_rail_reflows_the_main_pane()
    {
        using var t = new TestDoc(Html, Css, null, width: 500, height: 200);
        t.HoverClass("side");

        var mainClosed = WidthAt(t, "main", 0.0);               // 400 - 64 = 336
        var mainOpen = WidthAt(t, "main", 0.4);                 // 400 - 200 = 200
        Assert.Equal(336f, mainClosed, 1.5);
        Assert.Equal(200f, mainOpen, 1.5);
        Assert.True(mainClosed - mainOpen > 100f, $"the main pane gave up width as the rail grew ({mainClosed} → {mainOpen})");
    }

    [Fact]
    public void Un_hover_collapses_the_rail_back()
    {
        using var t = new TestDoc(Html, Css, null, width: 500, height: 200);
        t.HoverClass("side");
        WidthAt(t, "side", 0.0); WidthAt(t, "side", 0.4);       // settle expanded (200)

        t.Move(470, 190);                                       // far corner → un-hover
        Assert.True(t.Doc.HasActiveTransitions);
        WidthAt(t, "side", 0.4);                                // first frame stamps the collapse start
        Assert.InRange(WidthAt(t, "side", 0.55), 100f, 165f);   // 0.15 into the collapse
        Assert.Equal(64f, WidthAt(t, "side", 0.8), 1.0);        // fully collapsed again
    }
}
