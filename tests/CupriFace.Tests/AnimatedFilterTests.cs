using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "filter paints statically but never animates: a @keyframes over blur or brightness runs and
/// changes nothing, silently." (#291)
///
/// <c>filter: blur(12px)</c> in a rule worked; the same value reached through a keyframe did
/// nothing, at every instant. Not an interpolation fault — the property was simply never read off
/// the active keyframe, so the animation was inert from its first frame to its last while the
/// timing, the easing and the stops all worked. 171 tweens across 18 of 165 corpus compositions
/// animate a filter, and in the transitions family the blur that ramps up as one slide leaves and
/// down as the next arrives IS the transition.
///
/// Sampled the way the issue measured it: a black box on white, read 4px OUTSIDE its left edge,
/// where white means a sharp edge and grey means the blur has spread.
/// </summary>
public class AnimatedFilterTests
{
    private const string Html = "<body><div class='b'></div></body>";

    // The box sits at 40,20 and is 80x60, so x=36 is 4px outside its left edge.
    private const string Base =
        "body{margin:0;background:#fff} .b{margin:20px 40px;width:80px;height:60px;background:#000;";

    private static SKColor OutsideEdge(string decl, string keyframes, double at)
    {
        using var t = new TestDoc(Html, Base + decl + "} " + keyframes, width: 200, height: 140);
        t.Doc.Animate(at);
        t.Layout();
        using var bmp = t.Render(SKColors.White);
        return bmp.GetPixel(36, 50);
    }

    private static SKColor Inside(string decl, string keyframes, double at)
    {
        using var t = new TestDoc(Html, Base + decl + "} " + keyframes, width: 200, height: 140);
        t.Doc.Animate(at);
        t.Layout();
        using var bmp = t.Render(SKColors.White);
        return bmp.GetPixel(80, 50);
    }

    [Fact]
    public void A_keyframed_blur_spreads_by_the_end()
    {
        // The issue's table, bottom row: at t=2 (the end of a 2s run) the edge was still #FFFFFF.
        const string Kf = "@keyframes soften{from{filter:blur(0px)}to{filter:blur(12px)}}";
        var start = OutsideEdge("animation:soften 2s linear forwards", Kf, 0.0);
        var end = OutsideEdge("animation:soften 2s linear forwards", Kf, 2.0);

        Assert.True(start.Red > 250, $"at t=0 the edge should still be sharp; got {start}");
        Assert.True(end.Red < 235, $"at the end of the run the blur has not spread at all; got {end}");
    }

    [Fact]
    public void And_half_way_it_is_half_way()
    {
        // The point of interpolating rather than flipping: the midpoint is between the two ends.
        const string Kf = "@keyframes soften{from{filter:blur(0px)}to{filter:blur(12px)}}";
        var mid = OutsideEdge("animation:soften 2s linear forwards", Kf, 1.0);
        var end = OutsideEdge("animation:soften 2s linear forwards", Kf, 2.0);
        Assert.True(mid.Red < 250, $"nothing had spread by the midpoint; got {mid}");
        Assert.True(mid.Red > end.Red, $"the midpoint ({mid.Red}) should be sharper than the end ({end.Red})");
    }

    [Fact]
    public void A_keyframed_brightness_darkens()
    {
        // The issue's other measurement: brightness(1) -> brightness(0.2) behaved the same way.
        const string Kf = "@keyframes dim{from{filter:brightness(1)}to{filter:brightness(0.2)}}";
        using var t = new TestDoc("<body><div class='w'></div></body>",
            "body{margin:0;background:#000} .w{margin:20px 40px;width:80px;height:60px;background:#fff;"
            + "animation:dim 2s linear forwards} " + Kf, width: 200, height: 140);

        t.Doc.Animate(0.0); t.Layout();
        using var first = t.Render(SKColors.Black);
        var bright = first.GetPixel(80, 50);

        t.Doc.Animate(2.0); t.Layout();
        using var last = t.Render(SKColors.Black);
        var dimmed = last.GetPixel(80, 50);

        Assert.True(bright.Red > 240, $"the run should start at full brightness; got {bright}");
        Assert.True(dimmed.Red < 120, $"brightness(0.2) never applied; got {dimmed}");
    }

    [Fact]
    public void A_stop_that_omits_the_filter_holds_the_elements_own()
    {
        // The same contract opacity and clip-path keep: an end the author did not write is the
        // element's resting value, not "no filter".
        const string Kf = "@keyframes sharpen{from{filter:blur(12px)}to{opacity:1}}";
        var start = OutsideEdge("filter:blur(6px); animation:sharpen 2s linear forwards", Kf, 0.0);
        var end = OutsideEdge("filter:blur(6px); animation:sharpen 2s linear forwards", Kf, 2.0);
        Assert.True(start.Red < 235, $"the written stop should blur at t=0; got {start}");
        Assert.True(end.Red < 245, $"the unwritten end should hold the element's own blur(6px); got {end}");
    }

    [Fact]
    public void Chains_of_different_shapes_flip_at_the_midpoint()
    {
        // Not interpolable, so it flips rather than inventing a blend — what clip-path already does
        // between unlike shapes. blur -> grayscale: the halo is there for the first half and gone
        // for the second.
        const string Kf = "@keyframes swap{from{filter:blur(10px)}to{filter:grayscale(1)}}";
        var early = OutsideEdge("animation:swap 2s linear forwards", Kf, 0.4);
        var late = OutsideEdge("animation:swap 2s linear forwards", Kf, 1.6);
        Assert.True(early.Red < 235, $"the first half should still be the blur; got {early}");
        Assert.True(late.Red > 250, $"the second half should have flipped to grayscale; got {late}");
    }

    [Fact]
    public void A_finished_animation_that_does_not_fill_gives_the_filter_back()
    {
        // Restore's half of the contract: seeking past a no-fill run must give the element's own
        // filter, not the last frame it happened to paint.
        const string Kf = "@keyframes soften{from{filter:blur(0px)}to{filter:blur(12px)}}";
        var after = Inside("animation:soften 1s linear", Kf, 5.0);
        Assert.Equal(new SKColor(0, 0, 0), after);
        var edge = OutsideEdge("animation:soften 1s linear", Kf, 5.0);
        Assert.True(edge.Red > 250, $"a finished no-fill run left its blur behind; got {edge}");
    }
}
