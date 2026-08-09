using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>An anchored popup (position:fixed + data-cupri-anchor) must track its anchor's on-screen
/// position — so when the anchor's scroll container scrolls, the popup follows it. Regression for chart
/// hover tooltips drifting away from their bar once the page was scrolled.</summary>
public class AnchoredScrollTests
{
    private const string Html =
        "<body><div class='scroller' style='height:250px;overflow:scroll'>" +
          "<div style='height:80px'></div>" +
          "<div id='a' style='height:24px;width:60px'>A</div>" +
          "<div class='tip' style='position:fixed;width:40px;height:20px' data-cupri-anchor='a' data-cupri-placement='bottom'>t</div>" +
          "<div style='height:600px'></div>" +
        "</div></body>";

    [Fact]
    public void An_anchored_popup_follows_its_anchor_when_the_page_scrolls()
    {
        using var t = new TestDoc(Html, "", width: 300, height: 400);
        var y0 = HitTesting.AbsoluteBox(t.FindClass("tip")).Y;   // popup Y with no scroll

        var (sx, sy) = TestDoc.Center(t.FindClass("scroller"));
        t.Doc.DispatchWheel(sx, sy, 40f);                        // scroll the container down 40px
        t.Layout();
        var y1 = HitTesting.AbsoluteBox(t.FindClass("tip")).Y;

        Assert.Equal(y0 - 40, y1, 2.0);                          // the popup tracked the anchor up 40px
    }
}
