using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Issue #300. A scroll box has to scroll whatever overflows it, on either axis. In a flex ROW the
/// vertical axis is the CROSS axis, and the flow total does not account for a child taller than the
/// line — so the content neither scrolled nor clipped, and painted out of the box with no scrollbar
/// to say so.
/// </summary>
public class CrossAxisScrollTests
{
    private const string Child = "<div style='width:100px;height:500px;flex-shrink:0'>c</div>";

    private static string Row(string direction) =>
        $"<body><div class='box' style='height:200px;overflow:scroll;display:flex;" +
        $"flex-direction:{direction};align-items:flex-start'>{Child}</div></body>";

    [Theory]
    [InlineData("row")]
    [InlineData("column")]
    public void A_scroll_box_scrolls_a_taller_child_whichever_way_it_lays_out(string direction)
    {
        using var t = new TestDoc(Row(direction), "", width: 400, height: 300);
        t.Layout();
        var box = t.FindClass("box");

        Assert.Equal(500f, box.ScrollContentHeight, 1);
        Assert.True(box.IsScrollable, $"flex-direction:{direction} should scroll vertically");
        Assert.Equal(300f, box.MaxScrollY, 1);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("column")]
    public void The_wheel_reaches_cross_axis_overflow(string direction)
    {
        using var t = new TestDoc(Row(direction), "", width: 400, height: 300);
        t.Layout();
        t.Doc.DispatchWheel(50, 50, 120);
        t.Layout();

        Assert.True(t.FindClass("box").ScrollY > 1f, $"flex-direction:{direction} ignored the wheel");
    }

    [Fact]
    public void A_box_whose_content_fits_is_still_not_scrollable()
    {
        // The measurement must not invent overflow where there is none, or every box grows a
        // scrollbar and the damage diff never settles.
        using var t = new TestDoc(
            "<body><div class='box' style='height:200px;overflow:scroll;display:flex'>" +
            "<div style='width:100px;height:50px'>c</div></div></body>", "", width: 400, height: 300);
        t.Layout();
        var box = t.FindClass("box");

        Assert.False(box.IsScrollable);
        Assert.Equal(0f, box.MaxScrollY, 1);
    }

    [Fact]
    public void Flow_content_still_answers_for_its_own_extent()
    {
        // Text owns no child box, so the flow total has to keep winning where it is the larger.
        using var t = new TestDoc(
            "<body><div class='box' style='width:120px;height:40px;overflow:scroll'>" +
            "one two three four five six seven eight nine ten</div></body>", "", width: 400, height: 300);
        t.Layout();
        var box = t.FindClass("box");

        Assert.True(box.ScrollContentHeight > 40f, $"wrapped text should overflow: {box.ScrollContentHeight}");
        Assert.True(box.IsScrollable);
    }
}
