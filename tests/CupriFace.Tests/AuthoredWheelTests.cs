using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public sealed class AuthoredWheelTests
{
    [Fact]
    public void Opted_in_element_consumes_wheel_before_its_scroller()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='stage' data-wheel='zoom'><div class='tall'>x</div></div></body>",
            "body{margin:0}.stage{width:200px;height:100px;overflow:scroll}.tall{height:500px}");
        CupriWheelEvent? seen = null;
        doc.OnWheel("data-wheel", e => { seen = e; return true; });
        doc.BuildFrame(300, 200);

        Assert.True(doc.DispatchWheel(50, 50, 120));
        Assert.NotNull(seen);
        Assert.Equal("zoom", seen!.Value);
        Assert.Equal(120, seen.DeltaY);
        var stage = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("stage") == true)!;
        Assert.Equal(0, stage.ScrollY);
    }

    [Fact]
    public void Declined_wheel_falls_through_to_scrolling()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='stage' data-wheel='zoom'><div class='tall'>x</div></div></body>",
            "body{margin:0}.stage{width:200px;height:100px;overflow:scroll}.tall{height:500px}");
        doc.OnWheel("data-wheel", _ => false);
        doc.BuildFrame(300, 200);

        Assert.True(doc.DispatchWheel(50, 50, 120));
        var stage = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("stage") == true)!;
        Assert.Equal(120, stage.ScrollY);
    }
}
