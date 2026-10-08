using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A fade to <c>transparent</c> keeps its hue instead of travelling through transparent black (#283).
///
/// <para>CSS interpolates gradient stops in PREMULTIPLIED alpha, which is what makes the
/// <c>transparent</c> keyword usable: it means "this colour at zero alpha". Interpolated in
/// straight RGBA it is <c>rgba(0,0,0,0)</c>, so a warm glow fading out was dragged towards black
/// and came out darker and desaturated. 124 gradients across 45 of 165 corpus compositions fade to
/// transparent; it is how every glow, vignette, scrim and soft edge is built, and it reads as "a
/// bit flat" rather than as a defect.</para>
///
/// <para>The check the issue asks for: the keyword and the same colour written at alpha 0 must
/// produce identical pixels.</para>
/// </summary>
public class TransparentGradientStopTests(ITestOutputHelper output)
{
    private SKBitmap Render(string gradient)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            "body{margin:0;background:#fff} .p{width:400px;height:200px;background-image:" + gradient + "}",
            width: 400, height: 200);
        return t.Render();
    }

    private (int R, int G, int B) Mean(string gradient)
    {
        using var bmp = Render(gradient);
        long r = 0, g = 0, b = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); r += c.Red; g += c.Green; b += c.Blue; }
        var n = bmp.Width * (long)bmp.Height;
        var mean = ((int)(r / n), (int)(g / n), (int)(b / n));
        output.WriteLine($"{gradient,-72} -> {mean}");
        return mean;
    }

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>The two declarations are the same gradient in CSS, and must be the same pixels.
    /// They differed by 34, 33 and 16 on the mean, the keyword always darker.</summary>
    [Theory]
    [InlineData("linear-gradient(90deg, #f0d3a6 0, {0} 100%)", "rgba(240,211,166,0)")]
    [InlineData("radial-gradient(circle, #e8a86a 0, {0} 100%)", "rgba(232,168,106,0)")]
    [InlineData("linear-gradient(90deg, #2e6a8c 0, {0} 100%)", "rgba(46,106,140,0)")]
    [InlineData("linear-gradient(90deg, #ffffff 0, {0} 100%)", "rgba(255,255,255,0)")]
    public void The_keyword_matches_the_same_colour_at_alpha_zero(string shape, string explicitStop)
    {
        var keyword = Mean(string.Format(shape, "transparent"));
        var spelled = Mean(string.Format(shape, explicitStop));
        Assert.Equal(spelled, keyword);
    }

    /// <summary>Pixel for pixel, not just on the mean.</summary>
    [Fact]
    public void They_are_identical_pixel_for_pixel()
    {
        using var keyword = Render("linear-gradient(90deg, #f0d3a6 0, transparent 100%)");
        using var spelled = Render("linear-gradient(90deg, #f0d3a6 0, rgba(240,211,166,0) 100%)");
        var d = Diagnostics.ImageDiff.Compare(keyword, spelled);
        output.WriteLine(d.ToString());
        Assert.True(d.IsIdentical, "the two spellings must paint the same gradient");
    }

    /// <summary>The fade must not darken: a warm colour fading out over white stays warm all the
    /// way, rather than dipping grey in the middle.</summary>
    [Fact]
    public void A_warm_fade_stays_warm_across_its_whole_length()
    {
        using var bmp = Render("linear-gradient(90deg, #f0d3a6 0, transparent 100%)");
        for (var x = 0; x < 400; x += 50)
        {
            var c = bmp.GetPixel(x, 100);
            output.WriteLine($"x={x,3} {c}");
            // Warm means red >= green >= blue, and over a white page nothing gets darker than the
            // colour it started from.
            Assert.True(c.Red >= c.Green && c.Green >= c.Blue, $"at x={x} the fade went cold: {c}");
            Assert.True(c.Red >= 0xF0 - 1, $"at x={x} the fade darkened: {c}");
        }
    }

    // ---- where the hue comes from --------------------------------------------------------------

    /// <summary>A transparent stop between two colours takes the one BEFORE it, so each half of the
    /// fade keeps the hue it is leaving.</summary>
    [Fact]
    public void A_transparent_stop_in_the_middle_takes_the_colour_before_it()
    {
        using var bmp = Render("linear-gradient(90deg, #ff0000 0, transparent 50%, #0000ff 100%)");
        var left = bmp.GetPixel(100, 100);
        var right = bmp.GetPixel(300, 100);
        output.WriteLine($"left {left}, right {right}");
        Assert.True(left.Red > left.Blue, "the left half should still be fading from red");
        Assert.True(right.Blue > right.Red, "the right half should be fading into blue");
    }

    /// <summary>A gradient that starts transparent takes the hue it is fading INTO, since there is
    /// nothing before it.</summary>
    [Fact]
    public void A_leading_transparent_stop_takes_the_colour_after_it()
    {
        using var bmp = Render("linear-gradient(90deg, transparent 0, #f0d3a6 100%)");
        var c = bmp.GetPixel(100, 100);
        output.WriteLine($"quarter way in: {c}");
        Assert.True(c.Red >= c.Green && c.Green >= c.Blue, $"the fade in went cold: {c}");
    }

    /// <summary>A gradient with no transparent stop at all is untouched.</summary>
    [Fact]
    public void An_opaque_gradient_is_unchanged()
    {
        using var bmp = Render("linear-gradient(90deg, #ff0000 0, #0000ff 100%)");
        Assert.Equal(new SKColor(0xff, 0, 0), bmp.GetPixel(0, 100));
        Assert.True(bmp.GetPixel(399, 100).Blue > 240);
    }

    /// <summary>Every stop transparent: nothing to borrow a hue from, and nothing paints. It must
    /// not throw looking for a donor.</summary>
    [Fact]
    public void An_entirely_transparent_gradient_paints_nothing()
    {
        using var bmp = Render("linear-gradient(90deg, transparent 0, transparent 100%)");
        Assert.Equal(SKColors.White, bmp.GetPixel(200, 100));
    }
}
