using CupriFace.Dom;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// An element can carry a LIST of animations, as CSS allows (#284).
///
/// <para>There used to be room for exactly one. The shorthand was tokenised on spaces alone, so a
/// comma-separated list collapsed into a single hybrid built from parts of each entry:
/// <c>slide 2s linear both, fade 1s linear 1s both</c> ran SLIDE with FADE's delay, and the
/// even-looking case ran the first animation at its very first frame, which looks exactly like
/// nothing happening. The longhand list failed differently and just as quietly —
/// <c>animation-name: slide,fade</c> was stored whole and matched no <c>@keyframes</c>. Neither
/// said a word.</para>
///
/// <para>It is the constraint a whole GSAP translation layer was built around: with one animation
/// per element, a stagger, two overlapping tweens and per-tween easing are all inexpressible.</para>
/// </summary>
public class AnimationListTests(ITestOutputHelper output)
{
    private const string Keyframes =
        "@keyframes slide{from{transform:translateX(0)}to{transform:translateX(200px)}}"
        + "@keyframes fade{from{opacity:1}to{opacity:0.25}}"
        + "@keyframes grow{from{width:40px}to{width:140px}}";

    private const string Base = "body{margin:0;background:#fff} .k{width:40px;height:40px;background:#000;";

    private RenderNode At(TestDoc t, double time, string cls = "k")
    {
        t.Doc.Animate(time);
        t.Layout();
        return TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains(cls) == true)!;
    }

    private TestDoc Doc(string decl, string html = "<body><div class='k'></div></body>") =>
        new(html, Base + decl + "} " + Keyframes, width: 400, height: 100);

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>The headline: two animations on one element both run. Each used to cancel the other
    /// out into a hybrid that looked like nothing at all.</summary>
    [Theory]
    [InlineData("animation: slide 2s linear both, fade 2s linear both")]
    [InlineData("animation-name:slide,fade;animation-duration:2s,2s;animation-fill-mode:both,both")]
    [InlineData("animation: slide 2s linear both, fade 1s linear 1s both")]
    public void Two_animations_both_run(string decl)
    {
        using var t = Doc(decl);
        var k = At(t, 2.0);
        output.WriteLine($"{decl}\n   -> translateX {k.Style.TranslateX:0}, opacity {k.Style.Opacity}");
        Assert.Equal(200f, k.Style.TranslateX, 0.5);
        Assert.Equal(0.25f, k.Style.Opacity, 0.01);
    }

    /// <summary>Each still runs correctly on its own, and leaves the other property alone.</summary>
    [Fact]
    public void One_animation_still_behaves()
    {
        using (var t = Doc("animation: slide 2s linear both"))
        {
            var k = At(t, 2.0);
            Assert.Equal(200f, k.Style.TranslateX, 0.5);
            Assert.Equal(1f, k.Style.Opacity, 0.01);
        }
        using (var t = Doc("animation: fade 2s linear both"))
        {
            var k = At(t, 2.0);
            Assert.Equal(0f, k.Style.TranslateX, 0.5);
            Assert.Equal(0.25f, k.Style.Opacity, 0.01);
        }
    }

    /// <summary>The hybrid row the issue called out: the element used to sit at x=100, which is
    /// SLIDE evaluated with FADE's delay. Each entry now keeps its own timing.</summary>
    [Fact]
    public void Each_entry_keeps_its_own_duration_and_delay()
    {
        using var t = Doc("animation: slide 2s linear both, fade 1s linear 1s both");
        var half = At(t, 1.0);
        output.WriteLine($"t=1: translateX {half.Style.TranslateX:0}, opacity {half.Style.Opacity}");
        Assert.Equal(100f, half.Style.TranslateX, 0.5);     // slide, half way through its 2s
        Assert.Equal(1f, half.Style.Opacity, 0.01);         // fade has not started: its delay is 1s
        var end = At(t, 2.0);
        Assert.Equal(200f, end.Style.TranslateX, 0.5);
        Assert.Equal(0.25f, end.Style.Opacity, 0.01);       // fade ran its 1s and filled forwards
    }

    // ---- what the list unlocks -----------------------------------------------------------------

    /// <summary>A stagger: the same keyframes on three elements at three delays. 14 of these across
    /// 10 corpus blocks were refused outright, so pieces that should arrive apart arrived together.
    /// (A stagger needs only per-element delay, which this makes expressible.)</summary>
    [Fact]
    public void A_stagger_puts_three_elements_at_three_places()
    {
        using var t = new TestDoc(
            "<body><div class='a'></div><div class='b'></div><div class='c'></div></body>",
            "body{margin:0} div{width:20px;height:20px;background:#000}"
            + " .a{animation:slide 2s linear 0s both} .b{animation:slide 2s linear 0.5s both}"
            + " .c{animation:slide 2s linear 1s both} " + Keyframes, width: 400, height: 100);
        t.Doc.Animate(1.0); t.Layout();
        float X(string cls) => TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains(cls) == true)!.Style.TranslateX;
        output.WriteLine($"a={X("a"):0} b={X("b"):0} c={X("c"):0}");
        Assert.Equal(100f, X("a"), 0.5);
        Assert.Equal(50f, X("b"), 0.5);
        Assert.Equal(0f, X("c"), 0.5);
    }

    /// <summary>Per-entry easing: two entries, one linear and one eased, must differ at the same
    /// instant. Every eased tween used to be sampled into stops and run linear.</summary>
    [Fact]
    public void Each_entry_carries_its_own_timing_function()
    {
        using var linear = Doc("animation: slide 2s linear both");
        using var eased = Doc("animation: slide 2s ease-in both");
        var a = At(linear, 0.5).Style.TranslateX;
        var b = At(eased, 0.5).Style.TranslateX;
        output.WriteLine($"at t=0.5: linear {a:0.0}, ease-in {b:0.0}");
        Assert.True(b < a - 5f, "ease-in should be behind linear a quarter of the way through");
    }

    [Fact]
    public void A_timing_function_list_pairs_with_the_entries()
    {
        using var t = Doc("animation-name:slide,fade;animation-duration:2s,2s;"
                        + "animation-timing-function:ease-in,linear;animation-fill-mode:both,both");
        var specs = TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains("k") == true)!.Style.Animations!;
        Assert.Equal(2, specs.Count);
        Assert.NotEqual(specs[0].Timing, specs[1].Timing);
        Assert.Equal(Easing.Linear, specs[1].Timing);
    }

    /// <summary>A third animation driving LAYOUT runs beside the two paint ones, so the frame's
    /// layout follows too.</summary>
    [Fact]
    public void A_layout_animation_composes_with_paint_ones()
    {
        using var t = Doc("animation: slide 2s linear both, fade 2s linear both, grow 2s linear both");
        var k = At(t, 2.0);
        output.WriteLine($"translateX {k.Style.TranslateX:0}, opacity {k.Style.Opacity}, width {k.Width:0}");
        Assert.Equal(200f, k.Style.TranslateX, 0.5);
        Assert.Equal(0.25f, k.Style.Opacity, 0.01);
        Assert.Equal(140f, k.Width, 0.5);
    }

    // ---- the parsed shape ----------------------------------------------------------------------

    [Fact]
    public void The_shorthand_parses_one_entry_per_comma()
    {
        using var t = Doc("animation: slide 2s ease-in 0.5s 2 both, fade 1s linear 1s infinite forwards");
        var specs = TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains("k") == true)!.Style.Animations!;
        Assert.Equal(2, specs.Count);

        Assert.Equal("slide", specs[0].Name);
        Assert.Equal(2f, specs[0].Duration);
        Assert.Equal(0.5f, specs[0].Delay);
        Assert.Equal(2f, specs[0].Iterations);
        Assert.True(specs[0].FillForwards && specs[0].FillBackwards);

        Assert.Equal("fade", specs[1].Name);
        Assert.Equal(1f, specs[1].Duration);
        Assert.Equal(1f, specs[1].Delay);
        Assert.Equal(float.PositiveInfinity, specs[1].Iterations);
        Assert.True(specs[1].FillForwards);
        Assert.False(specs[1].FillBackwards);
    }

    /// <summary>A longhand list shorter than the names repeats across them, as CSS pairs them.</summary>
    [Fact]
    public void A_shorter_longhand_list_repeats_across_the_entries()
    {
        using var t = Doc("animation-name:slide,fade;animation-duration:2s;animation-fill-mode:both");
        var specs = TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains("k") == true)!.Style.Animations!;
        Assert.Equal(2, specs.Count);
        Assert.All(specs, s => Assert.Equal(2f, s.Duration));
        Assert.All(specs, s => Assert.True(s.FillForwards && s.FillBackwards));
    }

    /// <summary>A later shorthand replaces the whole list, as a shorthand does.</summary>
    [Fact]
    public void A_later_shorthand_replaces_the_list()
    {
        using var t = Doc("animation: slide 2s linear both, fade 2s linear both; animation: fade 2s linear both");
        var k = At(t, 2.0);
        Assert.Single(k.Style.Animations!);
        Assert.Equal(0f, k.Style.TranslateX, 0.5);
        Assert.Equal(0.25f, k.Style.Opacity, 0.01);
    }

    /// <summary>An entry naming keyframes that do not exist is skipped, and the others still run.
    /// Nothing throws and nothing is hybridised out of the survivors.</summary>
    [Fact]
    public void An_unknown_name_in_the_list_is_skipped()
    {
        using var t = Doc("animation: slide 2s linear both, nope 2s linear both, fade 2s linear both");
        var k = At(t, 2.0);
        Assert.Equal(200f, k.Style.TranslateX, 0.5);
        Assert.Equal(0.25f, k.Style.Opacity, 0.01);
    }

    // ---- it reaches the frame ------------------------------------------------------------------

    /// <summary>The composed result is what actually paints: moved AND faded.</summary>
    [Fact]
    public void Both_animations_reach_the_painted_frame()
    {
        using var t = Doc("animation: slide 2s linear both, fade 2s linear both");
        t.Doc.Animate(2.0);
        using var bmp = t.Render();
        int x0 = int.MaxValue, darkest = 255;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            { var c = bmp.GetPixel(x, y); if (c.Red < 250) { x0 = Math.Min(x0, x); darkest = Math.Min(darkest, c.Red); } }
        output.WriteLine($"painted from x={x0}, darkest {darkest}");
        Assert.Equal(200, x0, 2.0);                 // slid
        Assert.InRange(darkest, 150, 230);          // and faded, not solid black
    }

    /// <summary>The document still reports itself as animating while a list is running, which is
    /// what makes a host draw the next frame.</summary>
    [Fact]
    public void The_document_knows_a_list_is_running()
    {
        using var t = Doc("animation: slide 2s linear both, fade 2s linear both");
        t.Doc.Animate(0.5);
        Assert.True(t.Doc.HasActiveAnimations);
    }
}
