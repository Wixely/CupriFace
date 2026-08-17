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

    private const string Html =
        "<body><div class='page'><cupri-textfield value=\"{{Secret}}\"></cupri-textfield>" +
        "<div class='tall'>s</div></div></body>";
    private const string Css = "body{margin:0} .page{width:400px;height:300px;overflow:scroll} .tall{height:900px}";

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
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
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

        Assert.True(Page(t.Doc).ScrollY < 50,
            $"the caret was left out of sight at scroll {Page(t.Doc).ScrollY:F0}px");
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
