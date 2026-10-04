using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A flex container sized by <c>min-height</c> centres its children in the height it ENDS UP with.
///
/// <para>It did not. <c>min-height</c> was applied after the items were placed, so they were aligned
/// against the content height and the box grew underneath them — <c>align-items:center</c> looked
/// ignored and everything sat at the top, while the border and background were the right size. An
/// explicit <c>height</c> never showed it, because that is known before the items are placed, which
/// is why swapping one for the other appeared to be the fix. Reported from a real integration against
/// a native <c>&lt;button&gt;</c>.</para>
/// </summary>
public class MinHeightFlexTests
{
    private const string Css =
        "body{margin:0} .n{display:flex;align-items:center;justify-content:center;width:200px}";

    // Root IS body here, and a child's own X/Y are relative to its parent's border box — so the gap
    // is measured from absolute boxes, which is what the engine itself hit-tests against.
    private static RenderNode Box(TestDoc t, int index) => t.Root.Children[index];

    private static float ChildTopWithin(TestDoc t, int index)
    {
        var box = HitTesting.AbsoluteBox(Box(t, index));
        var child = HitTesting.AbsoluteBox(Box(t, index).Children[0]);
        return child.Y - box.Y;
    }

    /// <summary>
    /// THE BUG: the two must agree. A box made 60 tall by <c>min-height</c> centres its content
    /// exactly as one made 60 tall by <c>height</c> — asserted against each other rather than against
    /// a number, so the test says "these are the same thing" rather than encoding a font's metrics.
    /// </summary>
    [Fact]
    public void Min_height_centres_like_an_explicit_height()
    {
        using var t = new TestDoc("""
            <body>
              <button class='n' style='min-height:60px'><span>icon</span></button>
              <button class='n' style='height:60px'><span>icon</span></button>
            </body>
            """, Css, width: 400, height: 300);

        var byMin = Box(t, 0);
        var byHeight = Box(t, 1);
        Assert.Equal(byHeight.Height, byMin.Height, 1);          // both 60 — this part always worked
        Assert.Equal(ChildTopWithin(t, 1), ChildTopWithin(t, 0), 1);
        Assert.True(ChildTopWithin(t, 0) > 1f, "centred, not at the top — which is what was wrong");
    }

    /// <summary>Content TALLER than the minimum still grows the box and is not squashed into it. The
    /// fix re-lays-out only when the clamp actually raised the height, so this path is untouched.</summary>
    [Fact]
    public void Content_taller_than_the_minimum_still_grows_the_box()
    {
        using var t = new TestDoc("""
            <body><button class='n' style='min-height:20px'><span style='height:80px'>tall</span></button></body>
            """, Css, width: 400, height: 300);

        Assert.True(Box(t, 0).Height >= 80f, $"the box should grow to its content, got {Box(t, 0).Height}");
    }

    /// <summary>It is the engine's own flex, not something about <c>&lt;button&gt;</c> — a plain div
    /// behaves the same way, which is worth pinning so a fix here is not read as button-specific.</summary>
    [Fact]
    public void A_plain_div_behaves_the_same()
    {
        using var t = new TestDoc(
            "<body><div class='n' style='min-height:60px'><span>icon</span></div></body>",
            Css, width: 400, height: 300);

        Assert.Equal(60f, Box(t, 0).Height, 1);
        Assert.True(ChildTopWithin(t, 0) > 1f, "centred within the minimum height");
    }

    /// <summary>Horizontal centring was never affected and must stay that way — the fix re-runs the
    /// whole flex pass, so the other axis is worth an assertion.</summary>
    [Fact]
    public void The_other_axis_is_unaffected()
    {
        using var t = new TestDoc(
            "<body><div class='n' style='min-height:60px'><span>icon</span></div></body>",
            Css, width: 400, height: 300);

        var box = HitTesting.AbsoluteBox(Box(t, 0));
        var child = HitTesting.AbsoluteBox(Box(t, 0).Children[0]);
        var leftGap = child.X - box.X;
        var rightGap = (box.X + box.W) - (child.X + child.W);
        Assert.Equal(rightGap, leftGap, 1);
    }
}
