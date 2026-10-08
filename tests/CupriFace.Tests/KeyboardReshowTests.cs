using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "Dismissing the soft keyboard leaves the focused field unusable: tapping it again does nothing,
/// and the only way back is to focus something else and return." (#288, from a physical device.)
///
/// The dismissal is invisible to the app — Back and the keyboard's own hide button are handled
/// entirely by the platform — so the engine still has the field focused and the host still believes
/// the keyboard is up. And a tap on an already-focused field changes NOTHING about the document, so
/// the focus edge the host shows the keyboard off cannot fire again. The state is self-consistent
/// and wrong, and the symptom is a field you have tapped and cannot type into, which on a phone
/// reads as the app having hung.
///
/// <see cref="CupriDocument.TextInputReactivated"/> is the missing sentence: the user asked for this
/// field again. These pin it where it can be checked without a device — that it fires on the re-tap,
/// that it does NOT fire where the existing focus edge already covers the case, and that a scroll
/// gesture beginning on the field is not mistaken for one.
/// </summary>
public class KeyboardReshowTests
{
    // Bound, not literal: focus is keyed by BINDING PATH, so an unbound field can never be focused
    // and every assertion here would pass vacuously.
    private sealed class Model
    {
        public string Name { get; set; } = "";
        public string Other { get; set; } = "";
    }

    private const string Html =
        "<body><cupri-textfield class='a' value=\"{{Name}}\"></cupri-textfield>" +
        "<cupri-textfield class='b' value=\"{{Other}}\"></cupri-textfield>" +
        "<div class='empty'>nothing here</div></body>";
    private const string Css = "body{margin:0} .a,.b{width:300px} .empty{height:120px}";

    private static TestDoc Doc() => new(Html, Css, new Model(), width: 400, height: 400, components: true);

    private static RenderNode Field(TestDoc t, string cls) =>
        TestDoc.Find(t.Doc.Root, n => n.Element?.ClassList.Contains(cls) == true
                                      && n.Element?.GetAttribute("role") == "textbox")
        ?? t.FindClass(cls);

    /// <summary>Subscribe to both events and record what arrives, in order.</summary>
    private static (List<TextInputState> Edges, List<TextInputState> Reactivations) Watch(TestDoc t)
    {
        var edges = new List<TextInputState>();
        var again = new List<TextInputState>();
        t.Doc.TextInputStateChanged += s => edges.Add(s);
        t.Doc.TextInputReactivated += s => again.Add(s);
        return (edges, again);
    }

    [Fact]
    public void Tapping_the_focused_field_again_asks_for_the_keyboard_again()
    {
        using var t = Doc();
        var (x, y) = TestDoc.Center(Field(t, "a"));

        t.Touch.Tap(x, y);
        Assert.True(t.Doc.GetTextInputState().Focused, "the first tap did not focus the field");

        // From here on the platform has taken the keyboard away without telling anyone, so the
        // engine's view of the world is unchanged — which is precisely the bug: the second tap
        // produces no focus edge, and used to produce nothing at all.
        var (edges, again) = Watch(t);
        t.Touch.Tap(x, y);

        Assert.Empty(edges);                        // nothing changed, so nothing is an edge…
        var state = Assert.Single(again);           // …and that is exactly what must still be said
        Assert.True(state.Focused);
        Assert.Equal("textbox", state.Role);
    }

    [Fact]
    public void A_mouse_click_back_into_the_field_says_it_too()
    {
        // Touch is where the bug lives, but the signal is about what the USER asked for, not about
        // which digitiser asked — a touchscreen on a desktop host arrives as a click here.
        using var t = Doc();
        var (x, y) = TestDoc.Center(Field(t, "a"));
        t.Click(x, y);

        var (edges, again) = Watch(t);
        t.Click(x, y);

        Assert.Empty(edges);
        Assert.Single(again);
    }

    [Fact]
    public void Moving_to_another_field_is_an_edge_and_not_a_reactivation()
    {
        // The existing path already handles this one, and handling it twice would restart the input
        // connection and re-enter the autofill session for a field the user simply moved to.
        using var t = Doc();
        t.Touch.Tap(TestDoc.Center(Field(t, "a")).X, TestDoc.Center(Field(t, "a")).Y);

        var (edges, again) = Watch(t);
        var (bx, by) = TestDoc.Center(Field(t, "b"));
        t.Touch.Tap(bx, by);

        Assert.True(Assert.Single(edges).Focused);
        Assert.Empty(again);
    }

    [Fact]
    public void Tapping_away_blurs_and_asks_for_nothing()
    {
        // A tap outside every field blurs, which is an edge the host hides the keyboard off. If a
        // reactivation also arrived here, the keyboard would come back the instant it was dismissed.
        using var t = Doc();
        var (x, y) = TestDoc.Center(Field(t, "a"));
        t.Touch.Tap(x, y);

        var (edges, again) = Watch(t);
        var (ex, ey) = TestDoc.Center(t.FindClass("empty"));
        t.Touch.Tap(ex, ey);

        Assert.False(Assert.Single(edges).Focused);
        Assert.Empty(again);
        Assert.False(t.Doc.GetTextInputState().Focused);
    }

    [Fact]
    public void A_swipe_that_begins_on_the_field_is_a_scroll_and_not_a_tap()
    {
        // The reason this signal is raised at the ACTIVATION and not at every finger-up: a scroll
        // starts somewhere, and often on the field you were last editing. Re-showing the keyboard
        // because someone scrolled the page would be a worse bug than the one being fixed.
        using var t = Doc();
        var (x, y) = TestDoc.Center(Field(t, "a"));
        t.Touch.Tap(x, y);

        var (edges, again) = Watch(t);
        t.Touch.Swipe(x, y, dy: -180);

        Assert.Empty(again);
        Assert.Empty(edges);
        Assert.True(t.Doc.GetTextInputState().Focused, "the scroll should not have blurred the field");
    }
}
