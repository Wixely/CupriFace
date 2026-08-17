using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "When clicking on an input (login form) the keyboard hides the input box, maybe we should scroll
/// to keep it in view!"
///
/// The caret-follow already existed, but it fires when the CARET moves — the right trigger for
/// typing and the wrong one for the VIEW moving. Tapping a field sets the caret while the window is
/// still full height; the keyboard takes its half a moment later, by which time the caret has not
/// moved and nothing looked again, so the field sat behind the keyboard.
///
/// What the host needs is exactly what these assert: the view changed, the caret did not, and the
/// caret comes back into sight anyway. The Android side calls this whenever the usable area shrinks
/// (the IME inset is applied as padding, so the surface itself gets shorter).
/// </summary>
public class KeyboardVisibilityTests
{
    // Bound, not a literal: focus is keyed by BINDING PATH, so an unbound field can never be
    // focused and every assertion below would be vacuous.
    private sealed class Model { public string Secret { get; set; } = "secret"; }

    // The lead spacer gives the reveal headroom: a field at the very top of the content cannot
    // show a top margin (the clamp correctly stops at scroll 0), and the margin assertion below
    // would be impossible rather than meaningful.
    private const string Html =
        "<body><div class='page'><div class='lead'>l</div>" +
        "<cupri-textfield value=\"{{Secret}}\"></cupri-textfield>" +
        "<div class='tall'>s</div></div></body>";
    private const string Css = "body{margin:0} .page{width:400px;height:300px;overflow:scroll} " +
                               ".lead{height:60px} .tall{height:900px}";

    private static RenderNode Page(CupriDocument doc)
    {
        static RenderNode? F(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("page") == true) return n;
            foreach (var c in n.Children) if (F(c) is { } f) return f;
            return null;
        }
        return F(doc.Root)!;
    }

    private static TestDoc Focused()
    {
        var t = new TestDoc(Html, Css, new Model(), width: 400, height: 300, components: true);
        var f = t.FindRole("textbox");
        // Absolute coordinates: with the lead spacer above it, the field's parent-relative X/Y no
        // longer coincide with screen position.
        var (cx, cy, _, ch) = Interaction.HitTesting.ScreenBox(f);
        t.Doc.DispatchClick(cx + 10, cy + ch / 2);
        t.Layout();
        Assert.True(t.Doc.GetTextInputState().Focused, "the tap did not focus the field");
        return t;
    }

    [Fact]
    public void The_caret_is_brought_back_when_the_view_moves_without_it()
    {
        using var t = Focused();

        // The view moves and the caret does not — the same shape as a keyboard arriving and taking
        // the space the field was sitting in.
        t.Doc.DispatchWheel(200, 150, 400);
        t.Layout();
        Assert.True(Page(t.Doc).ScrollY > 100,
            "the field never left the visible band, so this proves nothing");

        t.Doc.EnsureCaretVisible();
        t.Layout();

        // The restore must have travelled back near the field (from 400+ to ~52: the field's
        // content position minus its breathing margin). The exact endpoint is asserted
        // geometrically below - this only proves the restore acted at all.
        Assert.True(Page(t.Doc).ScrollY < 100,
            $"the caret was left out of sight at scroll {Page(t.Doc).ScrollY:F0}px");

        // And not parked flush against the band's last pixel: the WHOLE field must sit inside the
        // visible band with breathing room, or on a phone its bottom chrome hugs the tab bar and
        // reads as "the footer is still covering the input" - the device report, round 2.
        var f = t.FindRole("textbox");
        // Screen coordinates (scroll-adjusted, absolute) - a parent-relative Y here would compare
        // against the wrong origin and pass vacuously.
        var (_, fy, _, fh) = Interaction.HitTesting.ScreenBox(f);
        // This scenario reveals upward (the page was scrolled DOWN past the field), so the margin
        // that matters is the TOP edge: before the box-aware fix, the caret ROW landed flush at
        // the band top, which put the field's border and padding ABOVE it - chrome cut off, the
        // same flush-parking bug in the other direction.
        Assert.True(fy >= 4, $"the field's box starts at {fy:F0}px - its top chrome is cut off by the band edge");
        Assert.True(fy + fh <= 300 - 4,
            $"the field's box ends at {fy + fh:F0}px in a 300px window - flush at the edge");
    }

    [Fact]
    public void Nothing_moves_when_no_field_is_focused()
    {
        // The host calls this on every shrink, focused or not; it must be inert otherwise rather
        // than yanking the page around while the user is only reading.
        using var t = new TestDoc(Html, Css, new Model(), width: 400, height: 300, components: true);
        t.Doc.DispatchWheel(200, 150, 400);
        t.Layout();
        var before = Page(t.Doc).ScrollY;

        t.Doc.EnsureCaretVisible();
        t.Layout();

        Assert.Equal(before, Page(t.Doc).ScrollY, 1);
    }
}
