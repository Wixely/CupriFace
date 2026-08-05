using CupriFace.Dom;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

public class TooltipTests
{
    private const string Html =
        "<body><div style='padding:40px'><cupri-tooltip text='Hi there'><span>trigger</span></cupri-tooltip></div></body>";

    // The bubble node exists but is display:none until shown; this finds it only when visible.
    private static RenderNode? VisibleBubble(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-tt-bubble") == true && n.Style.Display != DisplayType.None);

    [Fact]
    public void Bubble_shows_on_hover_and_hides_on_leave()
    {
        using var t = new TestDoc(Html, "", components: true, width: 300, height: 200);
        Assert.Null(VisibleBubble(t));                       // hidden initially

        var (x, y) = TestDoc.Center(t.FindClass("cupri-tt-anchor"));
        t.Move(x, y);
        var bubble = VisibleBubble(t);
        Assert.NotNull(bubble);                              // revealed on hover
        Assert.True(bubble!.Width > 0 && bubble.Height > 0); // laid out (anchored top-layer)

        t.Move(5, 5);                                        // move off the trigger
        Assert.Null(VisibleBubble(t));                       // hidden again
    }

    [Fact]
    public void Open_true_pins_the_bubble_without_hover()
    {
        const string html =
            "<body><div style='padding:40px'><cupri-tooltip text='Pinned' open='true'><span>x</span></cupri-tooltip></div></body>";
        using var t = new TestDoc(html, "", components: true, width: 300, height: 200);
        Assert.NotNull(VisibleBubble(t));                    // visible with no hover
    }
}
