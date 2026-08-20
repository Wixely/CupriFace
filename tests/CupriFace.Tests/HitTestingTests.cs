using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class HitTestingTests
{
    [Fact]
    public void A_fully_clipped_child_cannot_receive_a_click()
    {
        using var t = new TestDoc(
            "<body><div class='clip'><div class='target'>Hidden</div></div></body>",
            "body{margin:0}.clip{position:relative;width:120px;height:50px;overflow:hidden}" +
            ".target{position:absolute;left:0;top:70px;width:100px;height:30px}",
            width: 200, height: 140);
        var clicks = 0;
        t.Doc.OnClick(".target", _ => clicks++);

        var (x, y, _, _) = HitTesting.ScreenBox(t.FindClass("target"));
        t.Click(x + 10, y + 10);

        Assert.Equal(0, clicks);
    }

    [Fact]
    public void Only_the_visible_part_of_a_partially_clipped_child_is_hittable()
    {
        using var t = new TestDoc(
            "<body><div class='clip'><div class='target'>Partial</div></div></body>",
            "body{margin:0}.clip{position:relative;width:120px;height:50px;overflow:hidden}" +
            ".target{position:absolute;left:0;top:30px;width:100px;height:40px}",
            width: 200, height: 140);
        var clicks = 0;
        t.Doc.OnClick(".target", _ => clicks++);

        var (x, y, _, _) = HitTesting.ScreenBox(t.FindClass("target"));
        t.Click(x + 10, y + 10); // y=40: inside both the child and its ancestor clip
        t.Click(x + 10, y + 30); // y=60: inside the child, but below the visible clip

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void A_child_cannot_be_hit_through_a_rounded_overflow_corner()
    {
        using var t = new TestDoc(
            "<body><div class='clip'><div class='target'>Corner</div></div></body>",
            "body{margin:0}.clip{position:relative;width:100px;height:100px;overflow:hidden;border-radius:40px}" +
            ".target{position:absolute;left:0;top:0;width:30px;height:30px}",
            width: 140, height: 140);
        var clicks = 0;
        t.Doc.OnClick(".target", _ => clicks++);

        t.Click(5, 5);   // inside the child's box, but outside the painted rounded clip
        t.Click(25, 25); // inside both the child and the rounded clip

        Assert.Equal(1, clicks);
    }
}
