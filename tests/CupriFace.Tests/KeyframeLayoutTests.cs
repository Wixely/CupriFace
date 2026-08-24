using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Width/height animate in <c>@keyframes</c> (issue #56). The declarations were always parsed into
/// the bracketing frames — the interpolation just never read them, so a keyframed bar held its
/// start width for the whole run while the engine reported the animation active. The mechanism is
/// the one <c>transition: height</c> proved: write a definite length, and the layout that follows
/// Animate on every path (host order: Animate → BuildFrame) honours it.
/// </summary>
public class KeyframeLayoutTests
{
    private static float WidthAt(TestDoc t, string cls, double sec) { t.Doc.Animate(sec); t.Layout(); return t.FindClass(cls).Width; }
    private static float HeightAt(TestDoc t, string cls, double sec) { t.Doc.Animate(sec); t.Layout(); return t.FindClass(cls).Height; }
    private static float TopAt(TestDoc t, string cls, double sec) { t.Doc.Animate(sec); t.Layout(); return t.FindClass(cls).Y; }

    [Fact]
    public void Width_interpolates_between_keyframes()
    {
        // The issue's repro: 20px → 200px over 1.2s. The engine's keyframe clock is linear, so
        // t=0.3 is progress 0.25 (width 65) and t=0.6 is progress 0.5 (width 110).
        using var t = new TestDoc(
            "<body><div class='slide'>x</div></body>",
            """
            @keyframes slide { 0% { width:20px } 100% { width:200px } }
            .slide { width:20px; height:60px; animation: slide 1.2s; }
            """,
            null, width: 400, height: 200);

        Assert.Equal(20f, WidthAt(t, "slide", 0.0), 1);
        Assert.Equal(65f, WidthAt(t, "slide", 0.3), 1);
        Assert.Equal(110f, WidthAt(t, "slide", 0.6), 1);
    }

    [Fact]
    public void An_animated_height_reflows_the_content_below_it()
    {
        // The half that makes this a LAYOUT animation and not a paint trick: the marker under the
        // growing bar must move down with it, frame by frame.
        using var t = new TestDoc(
            "<body><div class='bar'></div><div class='after'></div></body>",
            """
            @keyframes grow { 0% { height:10px } 100% { height:110px } }
            .bar { width:40px; height:10px; animation: grow 1s; }
            .after { width:40px; height:20px; }
            """,
            null, width: 400, height: 300);

        Assert.Equal(10f, TopAt(t, "after", 0.0), 1);
        Assert.Equal(60f, TopAt(t, "after", 0.5), 1);     // bar is 60 → marker pushed to 60
        Assert.Equal(60f, HeightAt(t, "bar", 0.5), 1);
    }

    [Fact]
    public void Percentages_interpolate_in_their_own_unit_and_resolve_in_layout()
    {
        // 10% → 90% at progress 0.5 is 50% — of the 400px containing block, so 200px. The lerp
        // stays in % and layout resolves it, where the containing block is actually known.
        using var t = new TestDoc(
            "<body><div class='p'></div></body>",
            "@keyframes span { 0% { width:10% } 100% { width:90% } } .p { height:20px; animation: span 1s; }",
            null, width: 400, height: 200);

        Assert.Equal(200f, WidthAt(t, "p", 0.5), 1);
    }

    [Fact]
    public void A_non_interpolable_pair_flips_at_the_midpoint()
    {
        // px → % is not interpolable (no shared basis at style time); CSS animates such pairs
        // discretely. Before the midpoint the start value holds, after it the end value.
        using var t = new TestDoc(
            "<body><div class='m'></div></body>",
            "@keyframes mix { 0% { width:20px } 100% { width:50% } } .m { height:20px; animation: mix 1s; }",
            null, width: 400, height: 200);

        Assert.Equal(20f, WidthAt(t, "m", 0.25), 1);
        Assert.Equal(200f, WidthAt(t, "m", 0.75), 1);     // 50% of 400
    }

    [Fact]
    public void Opacity_and_transform_keyframes_are_untouched()
    {
        // The pre-existing halves, pinned: the same tick that now moves width must keep tweening
        // the paint-only properties exactly as before.
        using var t = new TestDoc(
            "<body><div class='pulse'></div></body>",
            "@keyframes pulse { 0% { opacity:0.1 } 100% { opacity:0.9 } } .pulse { width:40px; height:40px; animation: pulse 1s; }",
            null, width: 200, height: 200);

        t.Doc.Animate(0.5); t.Layout();
        Assert.Equal(0.5f, t.FindClass("pulse").Style.Opacity, 2);
    }

    [Fact]
    public void A_width_transition_tweens_when_its_target_changes()
    {
        // The issue reported `transition: width` dead too — it never was: transitions START on a
        // target-value change (hover, class, model), which a clock-only harness never triggers.
        // Pinned here the same way the height-transition tests drive theirs.
        using var t = new TestDoc(
            "<body><div class='box'>x</div></body>",
            """
            .box { width:100px; height:30px; transition: width 0.3s linear; }
            .box:hover { width:300px; }
            """,
            null, width: 400, height: 200);

        Assert.Equal(100f, t.FindClass("box").Width, 1);
        t.HoverClass("box");
        Assert.True(t.Doc.HasActiveTransitions);

        Assert.Equal(100f, WidthAt(t, "box", 0.0), 1);
        Assert.InRange(WidthAt(t, "box", 0.15), 170f, 230f);  // linear halfway
        Assert.Equal(300f, WidthAt(t, "box", 0.4), 1);
        Assert.False(t.Doc.HasActiveTransitions);
    }
}
