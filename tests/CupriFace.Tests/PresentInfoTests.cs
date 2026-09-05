using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The scaling strategies, which used to be four lines of arithmetic inside one sample. Anyone
/// overriding <c>CupriApp.Present</c> had to re-derive them from a record of three floats — and
/// people reading the engine, including agents asked to "add hybrid zoom", could not find them at
/// all, because they were not there.
///
/// <para>The tests are about the PROPERTIES each strategy claims, not the formulas: a formula test
/// restates the implementation and agrees with it even when the implementation is wrong.</para>
/// </summary>
public class PresentInfoTests
{
    private const float DesignW = 940, DesignH = 720;

    [Fact]
    public void Responsive_lays_out_at_the_window_so_a_bigger_window_shows_more()
    {
        var small = PresentInfo.Responsive(800, 600);
        var large = PresentInfo.Responsive(1600, 1200);

        Assert.Equal(1f, small.Scale);
        Assert.Equal(1f, large.Scale);
        // More window means more logical room — content stays the same size and more of it fits.
        Assert.True(large.LogicalWidth > small.LogicalWidth);
    }

    [Fact]
    public void Fixed_ignores_the_window_entirely()
    {
        var a = PresentInfo.Fixed(DesignW, DesignH);
        Assert.Equal(DesignW, a.LogicalWidth);
        Assert.Equal(DesignH, a.LogicalHeight);
        Assert.Equal(1f, a.Scale);
    }

    [Fact]
    public void Zoom_trades_logical_size_for_scale_so_content_gets_bigger_not_wider()
    {
        var p = PresentInfo.Zoom(1000, 800, 2f);
        Assert.Equal(2f, p.Scale);
        Assert.Equal(500, p.LogicalWidth);      // half the logical room…
        Assert.Equal(400, p.LogicalHeight);
        // …and the window it fills is unchanged: logical * scale is what the host paints into.
        Assert.Equal(1000, p.LogicalWidth * p.Scale, 3);
        Assert.Equal(800, p.LogicalHeight * p.Scale, 3);
    }

    /// <summary>The property that names the strategy: the tighter axis lands exactly at the design
    /// size, so a layout tuned for it is never squeezed below it.</summary>
    [Theory]
    [InlineData(940, 2000)]     // tall and narrow — width is tighter (a phone)
    [InlineData(3000, 720)]     // wide and short — height is tighter (a monitor)
    [InlineData(470, 360)]      // both halved — either axis, exactly at design ratio
    public void Hybrid_puts_the_tighter_axis_at_the_design_size(float winW, float winH)
    {
        var p = PresentInfo.Hybrid(winW, winH, DesignW, DesignH);

        var widthIsTighter = winW / DesignW <= winH / DesignH;
        if (widthIsTighter) Assert.Equal(DesignW, p.LogicalWidth, 2);
        else Assert.Equal(DesignH, p.LogicalHeight, 2);
    }

    /// <summary>…and the roomier axis gets EXTRA logical space to reflow into, rather than being
    /// letterboxed. This is the whole difference from plain Zoom.</summary>
    [Fact]
    public void Hybrid_gives_the_roomier_axis_more_space_than_the_design()
    {
        var tall = PresentInfo.Hybrid(940, 2000, DesignW, DesignH);
        Assert.Equal(DesignW, tall.LogicalWidth, 2);
        Assert.True(tall.LogicalHeight > DesignH,
            $"the long axis should reflow into extra room, got {tall.LogicalHeight} vs design {DesignH}");
    }

    [Fact]
    public void Hybrid_at_exactly_the_design_size_is_unscaled()
    {
        var p = PresentInfo.Hybrid(DesignW, DesignH, DesignW, DesignH);
        Assert.Equal(1f, p.Scale, 3);
        Assert.Equal(DesignW, p.LogicalWidth, 2);
        Assert.Equal(DesignH, p.LogicalHeight, 2);
    }

    // ---- the guards, which matter precisely because a caller does not know the maths ------------

    [Theory]
    [InlineData(0f)]
    [InlineData(-3f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Zoom_refuses_a_factor_that_would_lay_out_at_an_absurd_size(float factor)
    {
        var p = PresentInfo.Zoom(1000, 800, factor);
        Assert.InRange(p.Scale, PresentInfo.MinScale, PresentInfo.MaxScale);
        Assert.True(p.LogicalWidth > 0 && float.IsFinite(p.LogicalWidth));
        Assert.True(p.LogicalHeight > 0 && float.IsFinite(p.LogicalHeight));
    }

    /// <summary>A design size of zero divides to infinity and lays the document out at nothing — a
    /// blank window, which reads as "the engine is broken" rather than "I passed a zero". It falls
    /// back to reflowing instead.</summary>
    [Theory]
    [InlineData(0, 720)]
    [InlineData(940, 0)]
    [InlineData(-940, 720)]
    public void Hybrid_with_no_usable_design_size_reflows_rather_than_blanking(float dw, float dh)
    {
        var p = PresentInfo.Hybrid(1000, 800, dw, dh);
        Assert.Equal(1000, p.LogicalWidth, 2);
        Assert.Equal(800, p.LogicalHeight, 2);
        Assert.Equal(1f, p.Scale);
    }

    /// <summary>An extreme window must still produce something layout-able; the clamp is what stops
    /// a 40x window from asking for a 25-pixel viewport.</summary>
    [Fact]
    public void Hybrid_clamps_rather_than_producing_a_viewport_nothing_can_lay_out_in()
    {
        var huge = PresentInfo.Hybrid(40000, 40000, DesignW, DesignH);
        Assert.InRange(huge.Scale, PresentInfo.MinScale, PresentInfo.MaxScale);
        Assert.True(huge.LogicalWidth >= DesignW / PresentInfo.MaxScale);

        var tiny = PresentInfo.Hybrid(10, 10, DesignW, DesignH);
        Assert.InRange(tiny.Scale, PresentInfo.MinScale, PresentInfo.MaxScale);
    }

    [Fact]
    public void The_default_Present_is_responsive()
    {
        var app = new Bare();
        var p = app.Present(1234, 567);
        Assert.Equal(PresentInfo.Responsive(1234, 567), p);
    }

    private sealed class Bare : CupriApp
    {
        public override string Html => "<body></body>";
        public override string Css => "";
    }
}
