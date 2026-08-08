using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>A position:fixed child (a popup/overlay) is out of flow, so opening one inside a flex
/// container must not shove its in-flow siblings — it takes no slot in the flex line. Regression for
/// a context menu opening over a centred region and jumping the region's text aside.</summary>
public class FlexOutOfFlowTests
{
    private const string Css =
        ".card { display:flex; justify-content:center; width:300px; height:60px; }" +
        ".txt { width:40px; }" +
        ".pop { position:fixed; width:120px; height:40px; display:none; }" +
        ".pop.open { display:block; }";

    private static float TextX(string popClass)
    {
        using var t = new TestDoc(
            $"<body><div class='card'><span class='txt'>Hi</span><div class='pop {popClass}'></div></div></body>",
            Css, width: 400, height: 200);
        return HitTesting.AbsoluteBox(t.FindClass("txt")).X;
    }

    [Fact]
    public void Opening_a_fixed_popup_inside_a_flex_row_does_not_move_its_siblings()
    {
        var closed = TextX("");        // popup display:none
        var open = TextX("open");      // popup display:block, position:fixed
        Assert.Equal(closed, open, 0.5); // the span stays centred; the fixed popup takes no flex slot
    }
}
