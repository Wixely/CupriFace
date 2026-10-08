using CupriFace.Interaction;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "z-index is ignored entirely: stacking follows document order whatever is declared." (#290)
///
/// The property was read for exactly one thing — ordering the top layer, so a dialog sits above a
/// dropdown — and ignored everywhere else. Two overlapping positioned siblings always stacked in
/// document order, and even <c>z-index: -1</c> did not put an element behind. 71 of 165 corpus
/// compositions declare it, 258 declarations in all: it is how a scrim goes over a photo and a
/// caption above a gradient, and document order is frequently the opposite of what the author
/// wanted, which is why they reached for the property at all.
///
/// Sampled as the issue measured it: a blue <c>.under</c> declared FIRST, an orange <c>.p</c>
/// declared SECOND, read inside the overlap. Orange means <c>.p</c> is on top, blue means
/// <c>.under</c> is.
/// </summary>
public class ZIndexTests
{
    private static readonly SKColor Orange = new(0xFF, 0x88, 0x00);
    private static readonly SKColor Blue = new(0, 0, 0xFF);

    private const string Html = "<body><div class='under'></div><div class='p'></div></body>";

    // Both boxes are absolute and overlap between x=40..80: .under covers 0..80, .p covers 40..120.
    private static string Css(string pZ, string underZ = "z-index:2") =>
        "body{margin:0;background:#fff}"
        + ".under{position:absolute;left:0;top:0;width:80px;height:60px;background:#0000ff;" + underZ + "}"
        + ".p{position:absolute;left:40px;top:0;width:80px;height:60px;background:#ff8800;" + pZ + "}";

    private static SKColor InTheOverlap(string pZ, string underZ = "z-index:2")
    {
        using var t = new TestDoc(Html, Css(pZ, underZ), width: 160, height: 60);
        using var bmp = t.Render(SKColors.White);
        return bmp.GetPixel(60, 30);
    }

    [Fact]
    public void Document_order_still_decides_when_nothing_declares_a_layer()
    {
        // The issue's first row, and the behaviour that must not change: no z-index anywhere, so the
        // later element wins. Everything below is the exception, not the rule.
        Assert.Equal(Orange, InTheOverlap(pZ: "", underZ: ""));
    }

    [Theory]
    // The issue's table. Every row measured #FF8800 — orange, .p on top — including the last two,
    // where CSS says the blue .under should be in front. "who" is the class that must win.
    [InlineData("", "under")]              // .p declares nothing; .under's 2 beats an implicit 0
    [InlineData("z-index:9", "p")]         // .p declares the higher layer
    [InlineData("z-index:1", "under")]     // .under's 2 beats it
    [InlineData("z-index:-1", "under")]    // behind, as -1 asks
    public void The_declared_layer_decides(string pZ, string who)
    {
        Assert.Equal(who == "p" ? Orange : Blue, InTheOverlap(pZ));
    }

    [Fact]
    public void A_negative_layer_goes_behind_a_static_sibling_too()
    {
        // The case a decorative glow is written for: a positioned layer declared AFTER the content
        // it is meant to sit behind.
        using var t = new TestDoc(
            "<body><div class='content'></div><div class='glow'></div></body>",
            "body{margin:0;background:#fff}"
            + ".content{width:80px;height:60px;background:#0000ff}"
            + ".glow{position:absolute;left:0;top:0;width:80px;height:60px;background:#ff8800;z-index:-1}",
            width: 160, height: 60);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(Blue, bmp.GetPixel(40, 30));
    }

    [Fact]
    public void Equal_layers_keep_document_order()
    {
        // Stability: two siblings on the same layer must not swap, or a page would flicker between
        // frames. Same declared value on both, so the later one wins as it always did.
        Assert.Equal(Orange, InTheOverlap(pZ: "z-index:2", underZ: "z-index:2"));
    }

    [Fact]
    public void A_z_index_on_a_static_block_is_still_ignored()
    {
        // CSS applies z-index to positioned elements and flex/grid items only. A static block in
        // normal flow that declares one must keep painting where it did — the alternative is
        // quietly reordering pages that render correctly today.
        using var t = new TestDoc(
            "<body><div class='a'></div><div class='b'></div></body>",
            "body{margin:0;background:#fff}"
            + ".a{width:80px;height:60px;background:#0000ff;z-index:9;margin-bottom:-60px}"
            + ".b{width:80px;height:60px;background:#ff8800}",
            width: 160, height: 60);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(Orange, bmp.GetPixel(40, 30));
    }

    [Fact]
    public void A_flex_item_takes_its_layer_without_being_positioned()
    {
        // The one place CSS honours z-index on an unpositioned box. Negative margin overlaps them.
        using var t = new TestDoc(
            "<body><div class='row'><div class='a'></div><div class='b'></div></div></body>",
            "body{margin:0;background:#fff} .row{display:flex}"
            + ".a{width:80px;height:60px;background:#0000ff;z-index:2}"
            + ".b{width:80px;height:60px;background:#ff8800;margin-left:-40px}",
            width: 160, height: 60);
        using var bmp = t.Render(SKColors.White);
        Assert.Equal(Blue, bmp.GetPixel(60, 30));
    }

    [Fact]
    public void A_click_lands_on_what_is_actually_in_front()
    {
        // The half that is not visible in a screenshot: hit-testing walks the painter's order, so a
        // scrim that paints above the photo also receives the click. Without this the pointer falls
        // through whatever is visibly in front of it.
        using var t = new TestDoc(Html, Css(pZ: "z-index:1"), width: 160, height: 60);
        var hit = HitTesting.HitTest(t.Root, 60, 30);
        Assert.Equal("under", hit?.Element?.ClassList.ToString());
    }

    [Fact]
    public void And_still_lands_on_the_later_sibling_when_no_layer_is_declared()
    {
        using var t = new TestDoc(Html, Css(pZ: "", underZ: ""), width: 160, height: 60);
        var hit = HitTesting.HitTest(t.Root, 60, 30);
        Assert.Equal("p", hit?.Element?.ClassList.ToString());
    }
}
