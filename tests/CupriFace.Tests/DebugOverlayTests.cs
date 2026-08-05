using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

public class DebugOverlayTests
{
    private const string Html = "<body><div class='a'></div><div class='b'></div></body>";
    private const string Css = "body{margin:0} .a{width:40px;height:20px;background:#eee} .b{width:30px;height:30px}";

    private static int OutlineCount(DisplayList list)
    {
        var n = 0;
        foreach (var cmd in list.Commands)
            if (cmd is BorderRect b && b.Top == 1 && b.Color.Alpha is 0x66 or 0x99) n++; // 1px debug outlines
        return n;
    }

    [Fact]
    public void Off_by_default_adds_no_outlines()
    {
        using var doc = CupriDocument.Load(Html, Css);
        Assert.Equal(0, OutlineCount(doc.BuildFrame(100, 100)));
    }

    [Fact]
    public void On_outlines_each_element_box()
    {
        using var doc = CupriDocument.Load(Html, Css) ;
        doc.DebugOverlay = true;
        var outlines = OutlineCount(doc.BuildFrame(100, 100));
        Assert.True(outlines >= 3, $"expected an outline for body + 2 divs, got {outlines}"); // body + .a + .b
    }

    [Fact]
    public void Scroll_containers_get_a_distinct_colour()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='s'><div class='tall'></div></div></body>",
            "body{margin:0} .s{width:80px;height:40px;overflow:scroll} .tall{width:20px;height:200px}");
        doc.DebugOverlay = true;
        var list = doc.BuildFrame(100, 100);
        var hasScrollColour = false;
        foreach (var cmd in list.Commands)
            if (cmd is BorderRect b && b.Color.Alpha == 0x99) hasScrollColour = true;
        Assert.True(hasScrollColour, "the overflow:scroll container should get the scroll outline colour");
    }
}
