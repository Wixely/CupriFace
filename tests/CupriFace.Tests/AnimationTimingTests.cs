using Xunit;

namespace CupriFace.Tests;

/// <summary>The animation clock is absolute document time (<see cref="CupriDocument.Animate"/>), so
/// <c>animation-delay</c>, <c>animation-iteration-count</c> and <c>animation-fill-mode</c> are pure
/// functions of t — any time, in any order, gives that time's frame. A finished animation stops
/// reporting itself active, so a host (or a frame renderer) knows when nothing more will change.</summary>
public class AnimationTimingTests
{
    // Base opacity 1; the keyframes run 0.2 → 0.6, so base, first frame and last frame are all distinct.
    private const string Keyframes = "@keyframes dim { from { opacity: 0.2 } to { opacity: 0.6 } } .a { width: 10px; height: 10px; }";

    private static float OpacityAt(TestDoc td, double t)
    {
        td.Doc.Animate(t);
        return td.FindClass("a").Style.Opacity;
    }

    [Fact]
    public void Delay_waits_then_runs_then_reverts()
    {
        using var td = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: dim 1s 0.5s; }");
        Assert.Equal(1f, OpacityAt(td, 0.25), 2);     // in the delay: base, no backwards fill
        Assert.True(td.Doc.HasActiveAnimations);       // but pending: a later frame differs
        Assert.Equal(0.4f, OpacityAt(td, 1.0), 2);    // half-way through
        Assert.Equal(1f, OpacityAt(td, 1.5), 2);      // one iteration done: back to base
        Assert.False(td.Doc.Animate(2.0));            // nothing animates any more
        Assert.False(td.Doc.HasActiveAnimations);
    }

    [Fact]
    public void Fill_modes_hold_the_first_and_last_frames()
    {
        using var back = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: dim 1s 0.5s backwards; }");
        Assert.Equal(0.2f, OpacityAt(back, 0.25), 2);  // first frame during the delay
        Assert.Equal(1f, OpacityAt(back, 2.0), 2);     // no forwards fill: reverts

        using var fwd = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: dim 1s forwards; }");
        Assert.Equal(0.6f, OpacityAt(fwd, 2.0), 2);    // holds the last frame
        Assert.False(fwd.Doc.HasActiveAnimations);     // and is finished
    }

    [Fact]
    public void Iteration_count_ends_the_run_and_infinite_never_does()
    {
        using var twice = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: dim 1s 2; }");
        Assert.Equal(0.4f, OpacityAt(twice, 1.5), 2);  // second iteration, half-way
        Assert.True(twice.Doc.HasActiveAnimations);
        Assert.Equal(1f, OpacityAt(twice, 2.5), 2);    // both done
        Assert.False(twice.Doc.HasActiveAnimations);

        using var loop = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: dim 1s infinite; }");
        Assert.Equal(0.3f, OpacityAt(loop, 100.25), 2);
        Assert.True(loop.Doc.HasActiveAnimations);
    }

    [Fact]
    public void Longhands_compose_and_seeking_is_stateless()
    {
        const string css = Keyframes + " .a { animation-name: dim; animation-duration: 1s; animation-delay: 1s; animation-iteration-count: 3; animation-fill-mode: both; }";
        using var td = new TestDoc("<div class='a'></div>", css);
        Assert.Equal(0.2f, OpacityAt(td, 0), 2);       // backwards fill through the delay
        Assert.Equal(0.4f, OpacityAt(td, 3.5), 2);     // third iteration
        Assert.Equal(0.6f, OpacityAt(td, 4.0), 2);     // forwards fill after
        Assert.Equal(0.3f, OpacityAt(td, 1.25), 2);    // back in time: that time's frame, not the last one's
        Assert.Equal(0.6f, OpacityAt(td, 10), 2);

        using var plain = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: dim 1s; }");
        Assert.Equal(0.6f, OpacityAt(plain, 0.99), 1); // last frame of the only iteration
        Assert.Equal(1f, OpacityAt(plain, 5), 2);      // finished, no fill: base
        Assert.Equal(0.3f, OpacityAt(plain, 0.25), 2); // seeking back gives that frame, not the base that was just restored
    }

    [Fact]
    public void Shorthand_reads_duration_then_delay_and_a_bare_count()
    {
        using var td = new TestDoc("<div class='a'></div>", Keyframes + " .a { animation: 2s ease-in-out 0.5s 2 dim both; }");
        var s = td.FindClass("a").Style;
        Assert.Equal("dim", s.AnimationName);
        Assert.Equal(2f, s.AnimationDuration);
        Assert.Equal(0.5f, s.AnimationDelay);
        Assert.Equal(2f, s.AnimationIterations);
        Assert.True(s.AnimationFillForwards && s.AnimationFillBackwards);
    }
}
