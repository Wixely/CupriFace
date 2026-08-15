using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>@media (max-width/min-width) re-resolves on viewport change — the mechanism behind the
/// showcase sidebar auto-collapsing to its icon rail on a narrow window.</summary>
public class MediaQueryTests
{
    private static RenderNode Find(RenderNode n, string cls)
    {
        if (n.Element?.ClassList.Contains(cls) == true) return n;
        foreach (var c in n.Children) { var f = Find(c, cls); if (f is not null) return f; }
        return null!;
    }

    [Fact]
    public void An_element_collapses_below_a_max_width_breakpoint_and_restores_above_it()
    {
        const string css = "body{margin:0} .bar{width:190px;height:50px} @media (max-width:600px){ .bar{width:64px} }";
        using var doc = CupriDocument.Load("<body><div class='bar'>x</div></body>", css);

        doc.BuildFrame(900, 400);
        Assert.Equal(190f, Find(doc.Root, "bar").Width, 1);   // wide window → full width

        doc.BuildFrame(500, 400);                             // cross the breakpoint → styles re-resolve
        Assert.Equal(64f, Find(doc.Root, "bar").Width, 1);    // narrow → collapsed

        doc.BuildFrame(900, 400);                             // …and back when it widens again
        Assert.Equal(190f, Find(doc.Root, "bar").Width, 1);
    }

    [Fact]
    public void A_height_qualified_query_tells_phone_landscape_from_a_desktop_window()
    {
        // Same wide viewport, different heights: only the tall (desktop-shaped) one may match.
        const string css = "body{margin:0} .page{width:800px;height:50px}"
                         + " @media (min-width:700px) and (min-height:600px){ .page{width:560px} }";
        using var doc = CupriDocument.Load("<body><div class='page'>x</div></body>", css);

        doc.BuildFrame(850, 312);                             // phone landscape: wide but short
        Assert.Equal(800f, Find(doc.Root, "page").Width, 1);  // → the cap must NOT fire

        doc.BuildFrame(850, 700);                             // desktop window: wide AND tall
        Assert.Equal(560f, Find(doc.Root, "page").Width, 1);  // → the cap fires

        doc.BuildFrame(850, 312);                             // rotate back → re-resolves on height alone
        Assert.Equal(800f, Find(doc.Root, "page").Width, 1);
    }
}
