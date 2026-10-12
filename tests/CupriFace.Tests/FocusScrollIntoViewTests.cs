using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Keyboard and controller focus has to bring what it focuses INTO VIEW. A pointer cannot select
/// what it cannot see, so a click never needs this; a D-pad can walk straight past the bottom of a
/// scroll box and leave the user pressing Down at a selection they have no way of seeing.
///
/// <para>And it scrolls with a MARGIN rather than to the edge: parking the focused control flush
/// against the bottom of the scrollport tells the user nothing about what comes next, which is half
/// of what they are navigating by.</para>
/// </summary>
public class FocusScrollIntoViewTests(ITestOutputHelper output)
{
    // 20 rows in a 200px box: the focused row leaves the port long before the list ends.
    private static string Html(string extra = "") =>
        // A COLUMN, explicitly: cupri-button is inline-flex, so without this the 20 buttons flow
        // four to a row and fill the box exactly — nothing overflows and there is nothing to scroll.
        // flex-shrink:0 for the reason #300 records: a column shrinks its items, and squeezed rows
        // would again fit. (CLAUDE.md, "A row is display:flex. There is no inline box.")
        "<body><div class='box' style='height:200px;overflow:scroll;display:flex;" +
        "flex-direction:column;align-items:stretch'>" +
        string.Concat(Enumerable.Range(0, 20).Select(i =>
            $"<cupri-button class='b{i}' style='height:40px;flex-shrink:0' {extra}>Row {i}</cupri-button>")) +
        "</div></body>";

    private static TestDoc Doc(int height = 400) =>
        new(Html(), "", components: true, width: 400, height: height);

    /// <summary>The scrollport's content box, on screen — what "visible" is measured against.</summary>
    private static (float Top, float Bottom) Port(TestDoc t)
    {
        var box = t.FindClass("box");
        var (_, y, _, _) = HitTesting.ScreenBox(box);
        return (y + box.ContentTopInset, y + box.ContentTopInset + box.ContentBoxHeight);
    }

    [Fact]
    public void Walking_down_past_the_fold_scrolls_the_focused_control_into_view()
    {
        using var t = Doc();
        t.Layout();

        // Down eight times: four rows fit, so by now the focus is well past the fold.
        for (var i = 0; i < 8; i++) Assert.True(t.Doc.MoveFocus(NavigationDirection.Down), $"move {i}");
        t.Layout();

        var box = t.FindClass("box");
        output.WriteLine($"scrollY={box.ScrollY} maxScrollY={box.MaxScrollY}");
        Assert.True(box.ScrollY > 0, "the scroller should have followed the focus");

        var (top, bottom) = Port(t);
        var (_, fy, _, fh) = HitTesting.ScreenBox(t.FindClass("b7"));
        output.WriteLine($"focused row: {fy}..{fy + fh}   port: {top}..{bottom}");
        Assert.True(fy >= top && fy + fh <= bottom, "the focused row must be inside the scrollport");
    }

    [Fact]
    public void It_keeps_a_margin_so_the_next_row_is_visible_too()
    {
        // The reason for the margin: a row parked flush against the bottom edge says nothing about
        // what is below it, and "what is below it" is what the user is about to press Down for.
        using var t = Doc();
        t.Layout();
        for (var i = 0; i < 8; i++) t.Doc.MoveFocus(NavigationDirection.Down);
        t.Layout();

        var (top, bottom) = Port(t);
        var (_, fy, _, fh) = HitTesting.ScreenBox(t.FindClass("b7"));
        var margin = t.Doc.FocusScrollMargin;

        Assert.True(bottom - (fy + fh) >= margin - 0.5f,
                    $"only {bottom - (fy + fh)}px below the focused row; expected at least {margin}");
    }

    [Fact]
    public void Walking_back_up_scrolls_the_other_way_with_the_same_margin()
    {
        using var t = Doc();
        t.Layout();
        for (var i = 0; i < 12; i++) t.Doc.MoveFocus(NavigationDirection.Down);
        t.Layout();
        Assert.True(t.FindClass("box").ScrollY > 0);

        // Back to row 6, NOT row 0: near the start of a list the margin is limited by the content
        // itself — row 1 has a single row above it and the scroller is already at zero, so there is
        // no 48px to show and demanding it would be demanding the impossible.
        for (var i = 0; i < 6; i++) t.Doc.MoveFocus(NavigationDirection.Up);
        t.Layout();

        var (top, bottom) = Port(t);
        var (_, fy, _, fh) = HitTesting.ScreenBox(t.FindClass("b6"));
        Assert.True(fy >= top, "the focused row must not be above the port");
        Assert.True(fy - top >= t.Doc.FocusScrollMargin - 0.5f,
                    $"only {fy - top}px above the focused row; the margin should show the row before it");
    }

    [Fact]
    public void Tab_scrolls_too_not_only_the_arrows()
    {
        // Tab is a different code path from directional navigation and the same promise applies.
        using var t = Doc();
        t.Layout();
        for (var i = 0; i < 8; i++) t.Key(EditKey.Tab);
        t.Layout();

        Assert.True(t.FindClass("box").ScrollY > 0, "Tab should scroll the focused control into view");
    }

    [Fact]
    public void An_application_can_set_the_margin()
    {
        using var t = Doc();
        t.Doc.FocusScrollMargin = 90;
        t.Layout();
        for (var i = 0; i < 8; i++) t.Doc.MoveFocus(NavigationDirection.Down);
        t.Layout();

        var (_, bottom) = Port(t);
        var (_, fy, _, fh) = HitTesting.ScreenBox(t.FindClass("b7"));
        Assert.True(bottom - (fy + fh) >= 89.5f, $"expected a 90px margin, got {bottom - (fy + fh)}");
    }

    [Fact]
    public void Zero_scrolls_only_as_far_as_it_must()
    {
        // Off means "no margin", not "no scrolling": the control still has to be visible.
        using var t = Doc();
        t.Doc.FocusScrollMargin = 0;
        t.Layout();
        for (var i = 0; i < 8; i++) t.Doc.MoveFocus(NavigationDirection.Down);
        t.Layout();

        var (top, bottom) = Port(t);
        var (_, fy, _, fh) = HitTesting.ScreenBox(t.FindClass("b7"));
        Assert.True(fy >= top - 0.5f && fy + fh <= bottom + 0.5f, "it must still be inside the port");
        Assert.True(bottom - (fy + fh) < 1f, "with no margin it should sit at the edge it scrolled to");
    }

    [Fact]
    public void A_margin_larger_than_the_box_does_not_strand_the_control()
    {
        // A margin bigger than the scrollport cannot be honoured on both sides at once. The control
        // being VISIBLE wins over the margin — the alternative is scrolling it off-screen to satisfy
        // an impossible request, which is worse than the problem.
        using var t = Doc();
        t.Doc.FocusScrollMargin = 5000;
        t.Layout();
        for (var i = 0; i < 8; i++) t.Doc.MoveFocus(NavigationDirection.Down);
        t.Layout();

        var (top, bottom) = Port(t);
        var (_, fy, _, fh) = HitTesting.ScreenBox(t.FindClass("b7"));
        output.WriteLine($"focused row: {fy}..{fy + fh}   port: {top}..{bottom}");
        Assert.True(fy + fh > top && fy < bottom, "the focused row must still be on screen");
    }

    [Fact]
    public void A_control_already_comfortably_visible_does_not_move_the_scroller()
    {
        // No scrolling on every keypress: a jumpy list is its own bug.
        using var t = Doc();
        t.Layout();
        Assert.True(t.Doc.MoveFocus(NavigationDirection.Down));   // row 0, at the very top
        t.Layout();
        Assert.Equal(0f, t.FindClass("box").ScrollY, 1);
    }

    [Fact]
    public void The_app_level_margin_reaches_the_document()
    {
        var app = new RoomyApp();
        using var doc = app.CreateDocument();
        Assert.Equal(120f, doc.FocusScrollMargin);
    }

    private sealed class RoomyApp : CupriApp
    {
        public override string Html => FocusScrollIntoViewTests.Html();
        public override float FocusScrollMargin => 120;
    }
}
