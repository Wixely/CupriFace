using CupriFace.Interaction;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>visibility: hidden</c> — the box keeps its place and paints nothing.
///
/// Until now the property was parsed by nothing and reported by `CF0050` as ignored, so an element
/// carrying it was fully painted, clickable and a Tab stop. The focus walk had already written down
/// why it would not fix that alone: paint and focus have to agree, or a control that is plainly on
/// screen becomes unreachable by keyboard.
///
/// So the tests here are per subsystem, because the property is only correct if all five give the
/// same answer: paint, hit-testing, focus, the accessibility tree, and `CF0074`.
///
/// The case that shapes the whole implementation is <see cref="A_visible_child_reappears"/>:
/// `visibility` is INHERITED but a descendant may set `visible` and come back. Nothing may skip a
/// subtree on the strength of it — which is exactly what `display: none` is allowed to do, and the
/// reason the two could not share a code path.
/// </summary>
public class VisibilityTests
{
    private const string Html =
        "<body><div class='a'>One</div><div class='b'>Two</div></body>";

    // Two 100x40 boxes stacked; the first is hidden. Solid fills, so "painted" is a pixel question.
    private static string Css(string extra = "") =>
        "body{margin:0;background:#ffffff}"
        + ".a{width:100px;height:40px;background:#ff0000;visibility:hidden}"
        + ".b{width:100px;height:40px;background:#0000ff}" + extra;

    // ---- paint ---------------------------------------------------------------------------------

    [Fact]
    public void It_paints_nothing_but_keeps_its_space()
    {
        using var t = new TestDoc(Html, Css(), width: 200, height: 100);
        using var bmp = t.Render(SKColors.White);

        // Where the hidden box is: the page shows through, not red.
        Assert.Equal(SKColors.White, bmp.GetPixel(50, 20));
        // And the box below has NOT moved up into the space — the whole difference from display:none.
        Assert.Equal(new SKColor(0, 0, 0xFF), bmp.GetPixel(50, 60));
    }

    [Fact]
    public void Display_none_by_contrast_takes_the_space_back()
    {
        // The control case for the test above: same markup, the other property, and the box below
        // moves up. If this ever matches the one above, `visibility` has become `display: none`.
        using var t = new TestDoc(Html, "body{margin:0;background:#ffffff}"
            + ".a{width:100px;height:40px;background:#ff0000;display:none}"
            + ".b{width:100px;height:40px;background:#0000ff}", width: 200, height: 100);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(new SKColor(0, 0, 0xFF), bmp.GetPixel(50, 20));
    }

    [Fact]
    public void Its_text_is_not_painted_either()
    {
        // Text inherits the property, so the text node has to answer the same way its box does.
        using var t = new TestDoc("<body><div class='a'>Hello</div></body>",
            "body{margin:0;background:#ffffff} .a{color:#000000;font-size:30px;visibility:hidden}",
            width: 200, height: 60);
        using var bmp = t.Render(SKColors.White);
        for (var x = 0; x < 200; x++)
            for (var y = 0; y < 60; y++)
                Assert.Equal(SKColors.White, bmp.GetPixel(x, y));
    }

    [Fact]
    public void A_visible_child_reappears()
    {
        // The case that forbids treating this as a subtree skip anywhere in the engine.
        using var t = new TestDoc(
            "<body><div class='wrap'><div class='in'></div></div></body>",
            "body{margin:0;background:#ffffff} .wrap{visibility:hidden;width:100px;height:40px;background:#ff0000}"
            + ".in{visibility:visible;width:50px;height:20px;background:#00ff00}",
            width: 200, height: 100);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(SKColors.White, bmp.GetPixel(80, 30));              // the wrapper: gone
        Assert.Equal(new SKColor(0, 0xFF, 0), bmp.GetPixel(20, 10));     // the child: back
    }

    [Fact]
    public void A_transform_or_clip_on_a_hidden_box_still_applies_to_its_children()
    {
        // The guard is around this node's OWN paint, not around the structure it establishes. A
        // hidden parent that translates its children must still translate them.
        using var t = new TestDoc(
            "<body><div class='wrap'><div class='in'></div></div></body>",
            "body{margin:0;background:#ffffff} .wrap{visibility:hidden;width:100px;height:40px;"
            + "background:#ff0000;transform:translateX(100px)}"
            + ".in{visibility:visible;width:50px;height:20px;background:#00ff00}",
            width: 300, height: 100);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(SKColors.White, bmp.GetPixel(20, 10));              // not at the untransformed spot
        Assert.Equal(new SKColor(0, 0xFF, 0), bmp.GetPixel(120, 10));    // moved with the parent
    }

    // ---- input ---------------------------------------------------------------------------------

    [Fact]
    public void It_cannot_be_clicked()
    {
        using var t = new TestDoc(Html, Css(), width: 200, height: 100);
        var hit = HitTesting.HitTest(t.Root, 50, 20);
        Assert.NotEqual("a", hit?.Element?.ClassList.ToString());
    }

    [Fact]
    public void But_a_visible_child_inside_it_can()
    {
        using var t = new TestDoc(
            "<body><div class='wrap'><div class='in'></div></div></body>",
            "body{margin:0} .wrap{visibility:hidden;width:100px;height:40px}"
            + ".in{visibility:visible;width:50px;height:20px;background:#00ff00}",
            width: 200, height: 100);
        Assert.Equal("in", HitTesting.HitTest(t.Root, 20, 10)?.Element?.ClassList.ToString());
    }

    // ---- accessibility -------------------------------------------------------------------------

    [Fact]
    public void It_is_absent_from_the_accessibility_tree()
    {
        // Invisible to assistive technology for the same reason it is invisible to the eye.
        using var t = new TestDoc(
            "<body><div role='button' class='a'>Hidden</div><div role='button' class='b'>Shown</div></body>",
            ".a{visibility:hidden}", width: 200, height: 100);
        var dump = Accessibility.AccessibilityTree.Dump(t.Doc.BuildAccessibilityTree(200, 100));
        Assert.DoesNotContain("Hidden", dump);
        Assert.Contains("Shown", dump);
    }

    // ---- diagnostics ---------------------------------------------------------------------------

    [Fact]
    public void CF0074_does_not_report_a_hidden_pair()
    {
        // Same reason it skips `opacity: 0`: a box nobody can see is not visually adjacent to
        // anything. Reached through the inherited flag, so asking the node asks its ancestors too.
        const string html = "<body><div class='x'>One</div><div class='y'>Two</div></body>";
        const string css = "body{margin:0} .x,.y{background:#223;border-radius:12px;padding:12px;visibility:hidden}";
        Assert.DoesNotContain(Diagnostics.CupriDoctor.Check(html, css, width: 400, height: 300).Findings,
            f => f.Code == "CF0074");
    }

    [Fact]
    public void The_doctor_no_longer_calls_the_property_unsupported()
    {
        var report = Diagnostics.CupriDoctor.Check("<body><div class='a'>x</div></body>", ".a{visibility:hidden}");
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0050" && f.Message.Contains("visibility"));
    }

    [Fact]
    public void Collapse_is_hidden_outside_a_table()
    {
        using var t = new TestDoc(Html, Css().Replace("visibility:hidden", "visibility:collapse"),
            width: 200, height: 100);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(SKColors.White, bmp.GetPixel(50, 20));
    }
}
