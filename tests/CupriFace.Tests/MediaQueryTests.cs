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
}
