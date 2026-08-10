using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Wheel scroll chains to the ancestor when the scroller under the pointer is at its edge —
/// browser behaviour. Without it, a wheel over an inner scroller (the showcase table) went dead at its
/// bottom instead of continuing to scroll the page.</summary>
public class ScrollChainingTests
{
    private const string Html =
        "<body><div class='outer' style='height:200px; overflow:scroll'>" +
        "  <div style='height:80px'>above</div>" +
        "  <div class='inner' style='height:100px; overflow:scroll'>" +
        "    <div style='height:90px'>a</div><div style='height:90px'>b</div>" +
        "  </div>" +
        "  <div style='height:400px'>below</div>" +
        "</div></body>";

    [Fact]
    public void At_the_inner_edge_the_wheel_scrolls_the_outer()
    {
        using var t = new TestDoc(Html, "", width: 300, height: 220);
        var inner = t.FindClass("inner");
        var (x, y) = TestDoc.Center(inner);

        // First wheels scroll the INNER scroller only.
        t.Doc.DispatchWheel(x, y, 500f); t.Layout();
        Assert.True(t.FindClass("inner").ScrollY >= t.FindClass("inner").MaxScrollY - 0.5f, "inner should hit bottom");
        Assert.Equal(0, t.FindClass("outer").ScrollY, 0.5);

        // At the inner's bottom, the next wheel chains to the OUTER.
        Assert.True(t.Doc.DispatchWheel(x, y, 120f));
        t.Layout();
        Assert.True(t.FindClass("outer").ScrollY > 50f, $"outer should have scrolled (got {t.FindClass("outer").ScrollY})");

    }

    [Fact]
    public void At_the_inner_top_a_wheel_up_scrolls_the_outer()
    {
        // Fresh document: outer pre-scrolled down, inner at its own top. (AbsoluteBox is unscrolled,
        // so the on-screen position subtracts the outer's scroll explicitly.)
        using var t = new TestDoc(Html, "", width: 300, height: 220);
        t.FindClass("outer").ScrollY = 120;
        t.Layout();

        var ib = CupriFace.Interaction.HitTesting.AbsoluteBox(t.FindClass("inner"));
        float sx = ib.X + ib.W / 2, sy = ib.Y + 10 - 120;

        Assert.True(t.Doc.DispatchWheel(sx, sy, -60f), "the wheel should chain to the outer");
        t.Layout();
        Assert.True(t.FindClass("outer").ScrollY < 120f, "outer should scroll up");
        Assert.Equal(0, t.FindClass("inner").ScrollY, 0.5);  // the inner stayed at its top
    }
}
