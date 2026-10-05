using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A percentage <c>translate()</c> is a fraction of the element's OWN border box (#258).
///
/// <para><c>translate(-50%, -50%)</c> is the commonest centring idiom there is — 31 of 165 blocks in
/// one surveyed corpus author it, and another 30 animate a percentage translate through a keyframe.
/// It parsed as 0px and did nothing: the element painted exactly where it would with no transform
/// at all, in a static declaration and inside <c>@keyframes</c> alike, and no diagnostic named it.</para>
/// </summary>
public class PercentTranslateTests(ITestOutputHelper output)
{
    // The issue's document, scaled down: a 200x100 card whose top-left is pinned at the viewport's
    // centre, then pulled back by half its own size.
    private const string Html = "<body><div id='root'><div class='card'></div></div></body>";
    private const string Card = "body{margin:0;background:#fff} .card{position:absolute;top:150px;left:200px;width:200px;height:100px;background:#15202b;";

    /// <summary>The painted box of the one dark thing on a white page, as (x0, y0, x1, y1).</summary>
    private static (int X0, int Y0, int X1, int Y1) Painted(SKBitmap bmp)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red < 0x80)
                { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        return (x0, y0, x1, y1);
    }

    private (int X0, int Y0, int X1, int Y1) Box(string transform, string extraCss = "")
    {
        using var t = new TestDoc(Html, Card + "transform:" + transform + "}" + extraCss, width: 400, height: 300);
        using var bmp = t.Render();
        var b = Painted(bmp);
        output.WriteLine($"{transform,-32} -> {b.X0}..{b.X1}, {b.Y0}..{b.Y1}");
        return b;
    }

    /// <summary>The issue's table: the percentage form must land where the hand-written px form does,
    /// and NOT where <c>none</c> does.</summary>
    [Fact]
    public void A_percentage_translate_moves_by_a_fraction_of_the_elements_own_box()
    {
        var none = Box("none");
        var px = Box("translate(-100px, -50px)");
        var pct = Box("translate(-50%, -50%)");

        Assert.Equal((200, 150, 399, 249), none);
        Assert.Equal((100, 100, 299, 199), px);
        Assert.Equal(px, pct);
    }

    [Theory]
    [InlineData("translateX(-50%)", 100, 150)]
    [InlineData("translateY(-50%)", 200, 100)]
    [InlineData("translate(25%)", 250, 150)]           // one argument: x only
    [InlineData("translateX(50%) translateY(-50px)", 300, 100)]
    public void Each_translate_form_resolves_its_percentage(string transform, int x0, int y0)
    {
        var b = Box(transform);
        Assert.Equal((x0, y0), (b.X0, b.Y0));
    }

    /// <summary>Inside a keyframe the same stop used to apply its px term and drop its percentage
    /// term — the "slides in from below and is centred" card drew at the top-right of the frame.</summary>
    [Fact]
    public void A_percentage_translate_in_a_keyframe_is_honoured_and_interpolates()
    {
        var css = Card + "animation:rise 1s linear forwards}"
                + "@keyframes rise{0%{transform:translateX(-50%) translateY(100px)}100%{transform:translateX(-50%) translateY(0px)}}";
        using var t = new TestDoc(Html, css, width: 400, height: 300);

        t.Doc.Animate(0.0); t.Layout();
        using (var bmp = t.Render())
        {
            var b = Painted(bmp);
            output.WriteLine($"t=0.0 -> {b.X0}..{b.X1}, {b.Y0}..{b.Y1}");
            Assert.Equal((100, 250), (b.X0, b.Y0));   // x pulled back by half the width; y +100
        }
        t.Doc.Animate(0.5); t.Layout();
        using (var bmp = t.Render())
        {
            var b = Painted(bmp);
            output.WriteLine($"t=0.5 -> {b.X0}..{b.X1}, {b.Y0}..{b.Y1}");
            Assert.Equal((100, 200), (b.X0, b.Y0));
        }
        t.Doc.Animate(1.0); t.Layout();
        using (var bmp = t.Render())
        {
            var b = Painted(bmp);
            output.WriteLine($"t=1.0 -> {b.X0}..{b.X1}, {b.Y0}..{b.Y1}");
            Assert.Equal((100, 150), (b.X0, b.Y0));
        }
    }

    /// <summary>The pointer follows the same resolution the painter used: the centred card is
    /// clickable where it is drawn, and not where its untransformed box was.</summary>
    [Fact]
    public void A_percentage_translated_element_is_hit_where_it_paints()
    {
        using var t = new TestDoc(Html, Card + "transform:translate(-50%, -50%)}", width: 400, height: 300);
        var card = t.FindClass("card");
        Assert.Same(card, Interaction.HitTesting.HitTest(t.Root, 150, 150));   // inside the painted box
        Assert.NotSame(card, Interaction.HitTesting.HitTest(t.Root, 350, 230)); // inside the layout box only
    }
}
