using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A pinned tooltip on a phone rendered as a sliver with its text spilling out (device
/// screenshot). The cause was nowhere near where it looked: <c>width:max-content</c> is not a
/// length, and ParseLen's fallthrough handed it to the px parser, whose failure fallback is 0 —
/// so the bubble got a DEFINITE width of 0px, a 20px padding-only box, and text wrapped against
/// zero. The engine's max-width handling was correct the whole time.
/// </summary>
public class TooltipSizingTests
{
    private const string LongText = "This tooltip has a deliberately long explanation that would " +
        "run far past any phone screen if nothing constrained the bubble it sits in";

    private static (RenderNode Bubble, TestDoc T) Pinned(string text)
    {
        var t = new TestDoc(
            $"<body><cupri-tooltip text='{text}' open='true'><span>anchor</span></cupri-tooltip></body>",
            "body{margin:0}", null, width: 393, height: 771, components: true);
        var bubble = t.Find(n => n.Element?.ClassList.Contains("cupri-tt-bubble") == true);
        Assert.NotNull(bubble);
        return (bubble!, t);
    }

    [Fact]
    public void A_pinned_tooltip_is_sized_by_its_text()
    {
        var (bubble, t) = Pinned("Always shown (open)");
        using var _ = t;

        // The regression shape was a 20px padding-only box: definite 0px content width.
        Assert.True(bubble.Width > 60,
            $"bubble is {bubble.Width:F0}px wide — width:max-content has collapsed to a definite 0 again");
        Assert.True(bubble.Width < 260,
            $"bubble is {bubble.Width:F0}px wide for a short text — no longer shrink-wrapped");
    }

    [Fact]
    public void A_long_tooltip_wraps_at_max_width_and_stays_on_a_phone_screen()
    {
        var (bubble, t) = Pinned(LongText);
        using var _ = t;

        Assert.True(bubble.Width <= 260 + 24,
            $"bubble is {bubble.Width:F0}px — max-width:260px is not being applied");
        // Fixed-position nodes store viewport-absolute coordinates.
        Assert.True(bubble.X >= 0 && bubble.X + bubble.Width <= 393 + 1,
            $"bubble spans {bubble.X:F0}..{bubble.X + bubble.Width:F0} on a 393px viewport");
    }

    [Fact]
    public void The_text_stays_inside_the_bubble()
    {
        // The visible half of the device report: a narrow box with lines drawn wider than it.
        var (bubble, t) = Pinned(LongText);
        using var _ = t;

        static IEnumerable<RenderNode> All(RenderNode n)
        {
            yield return n;
            foreach (var c in n.Children)
                foreach (var d in All(c)) yield return d;
        }
        foreach (var line in All(bubble).Where(n => n.IsText && n.Width > 0))
            Assert.True(line.Width <= bubble.Width + 0.5f,
                $"a text line is {line.Width:F0}px wide inside a {bubble.Width:F0}px bubble");
    }
}
