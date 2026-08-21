using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Hover has to be CHEAP and QUIET, because a mouse moving across a window produces a flood of
/// positions and every one of them asks the engine a question. A host repaints whenever a dispatch
/// returns true, so "did the hover state actually change?" is not a detail — it is the difference
/// between a still cursor over a still page and a window that repaints continuously.
///
/// These came from a real report: an agent building on CupriFace saw "lots of flickering or
/// blinking" when moving the mouse over elements. Each test below is one way that symptom can be
/// produced, asserted at the engine's own boundary.
/// </summary>
public class HoverStabilityTests
{
    private const string Html =
        "<body><div class='card'><span class='label'>Hover me</span></div><div class='gap'></div></body>";

    // Deliberately ordinary: a hover style that repaints but does not move anything.
    private const string Css =
        "body{margin:0;width:400px;height:300px} " +
        ".card{width:200px;height:100px;background:#eee} " +
        ".card[data-hover]{background:#ddd} " +
        ".label{display:block;width:100px;height:20px} " +
        ".gap{width:400px;height:150px}";

    [Fact]
    public void Moving_WITHIN_one_element_reports_a_change_once_not_per_pixel()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 300);

        Assert.True(doc.DispatchPointerMove(50, 50), "the first move onto the card is a real change");

        // Twenty more moves inside the same element. The hovered element never changes, so the
        // engine must report no change — a host that repaints per move would burn a frame each.
        var changed = 0;
        for (var i = 0; i < 20; i++)
            if (doc.DispatchPointerMove(50 + i, 50)) changed++;

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Moving_over_a_region_with_NO_element_stays_quiet_too()
    {
        // The case with no hovered element at all. Nothing is highlighted, nothing can change —
        // so every one of these moves must be silent. If the engine reports a change here, a host
        // repaints continuously while the pointer crosses empty space, which is exactly what
        // "flickering while moving the mouse" looks like from the outside.
        using var doc = CupriDocument.Load(
            "<body><div class='card'>x</div></body>",
            "body{margin:0;width:400px;height:300px;background:#fff} .card{width:50px;height:50px}");
        doc.BuildFrame(400, 300);

        doc.DispatchPointerMove(300, 250);   // settle somewhere with no card under it

        var changed = 0;
        for (var i = 0; i < 20; i++)
            if (doc.DispatchPointerMove(300 + i, 250)) changed++;

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Crossing_between_two_elements_reports_exactly_one_change_per_crossing()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 300);

        doc.DispatchPointerMove(50, 10);     // on the label (0,0)-(100,20) inside the card
        var onCard = doc.DispatchPointerMove(150, 80);   // still the card, past the label
        var onGap = doc.DispatchPointerMove(200, 200);   // the gap below

        Assert.True(onCard, "leaving the label for the card is a change");
        Assert.True(onGap, "leaving the card for the gap is a change");
        Assert.False(doc.DispatchPointerMove(210, 210), "staying in the gap is not");
    }

    [Fact]
    public void A_hover_style_that_RESIZES_its_element_does_not_oscillate_under_a_still_pointer()
    {
        // The classic CSS flicker: hovering grows the element, which moves its own edge past the
        // cursor, which un-hovers it, which shrinks it back… A still pointer must reach a stable
        // answer. If this test ever fails, the engine is feeding its own layout back into hit
        // testing, and the fix belongs here rather than in every app's stylesheet.
        using var doc = CupriDocument.Load(
            "<body><div class='grow'>x</div></body>",
            "body{margin:0;width:400px;height:300px} " +
            ".grow{width:100px;height:40px;background:#eee} " +
            ".grow[data-hover]{height:80px}");
        doc.BuildFrame(400, 300);

        // Sit just inside the element's UNHOVERED bottom edge; hovering grows it downward, so the
        // point stays inside either way. Repeated identical moves must settle.
        doc.DispatchPointerMove(50, 35);
        doc.BuildFrame(400, 300);

        var changed = 0;
        for (var i = 0; i < 10; i++)
        {
            if (doc.DispatchPointerMove(50, 35)) changed++;
            doc.BuildFrame(400, 300);
        }

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Hover_survives_a_model_driven_rebuild_under_a_still_pointer()
    {
        // A dashboard that refreshes replaces every DOM element. If hover blinked off on each
        // refresh and came back on the next move, a still cursor over a refreshing panel would
        // strobe. The engine re-hit-tests the last position after a rebuild; this pins that.
        using var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(400, 300);
        doc.DispatchPointerMove(50, 50);

        static bool Hovered(CupriDocument d)
        {
            static bool F(RenderNode n) =>
                n.Element?.HasAttribute("data-hover") == true || n.Children.Any(F);
            return F(d.Root);
        }
        Assert.True(Hovered(doc), "the pointer is over the card");

        doc.Refresh();
        doc.BuildFrame(400, 300);
        Assert.True(Hovered(doc), "a rebuild under a still pointer must not drop hover");
    }

    [Fact]
    public void A_refresh_under_a_still_pointer_does_not_restart_the_hover_transition()
    {
        // The strobe shape, and the reason this suite exists. A dashboard on a refresh timer
        // rebuilds its whole DOM; if each rebuild re-detected the hover transition as "newly
        // changed", a still cursor over a refreshing card would replay the fade forever — which
        // reads to a user as blinking, with no mouse movement at all to explain it.
        using var doc = CupriDocument.Load(
            "<body><div class='card'>x</div></body>",
            "body{margin:0;width:400px;height:300px} " +
            ".card{width:200px;height:100px;background:#eee;transition:background 300ms} " +
            ".card[data-hover]{background:#333}");
        doc.BuildFrame(400, 300);

        doc.DispatchPointerMove(50, 50);          // hover begins: a transition SHOULD start here
        doc.BuildFrame(400, 300);
        Assert.True(doc.HasActiveTransitions, "hovering a transitioned property starts a transition");

        // Let it finish, so anything still running afterwards was started fresh. Animate takes an
        // ABSOLUTE clock: the first call establishes the baseline, later ones advance it.
        for (var t = 0; t <= 12; t++)
        {
            doc.Animate(t * 0.1);
            doc.BuildFrame(400, 300);
        }
        Assert.False(doc.HasActiveTransitions, "300ms of transition is over after 1.2s of clock");

        for (var i = 0; i < 5; i++)
        {
            doc.Refresh();
            doc.BuildFrame(400, 300);
            Assert.False(doc.HasActiveTransitions,
                $"refresh {i + 1} restarted the hover transition under a motionless pointer");
        }
    }
}
