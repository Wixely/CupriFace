using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The chat-composer idiom: Enter sends, Shift+Enter starts a new line (#90).
///
/// It could not be built before, in an app or around it. A bare <c>Enter</c> <c>OnShortcut</c> is the
/// wrong shape even now that named keys bind (#88) — it would eat newlines in every textarea on the
/// page rather than the one that wants it. <c>FormSubmitted</c> is parameterless and the engine never
/// raises it; only <c>SubmitForm()</c> does, for prompting a password manager, which is a different
/// job. And watching the bound value cannot tell the two apart, because Enter and Shift+Enter both
/// arrive as an inserted "\n" — by the time the model changes the distinction is gone.
///
/// So it is an opt-in on the element, answered by <c>doc.OnSubmit</c> in the vocabulary already used
/// for clicks and context menus.
/// </summary>
public class SubmitOnEnterTests
{
    private sealed class Model { public string Composer { get; set; } = ""; }

    private const string Html =
        "<body style='margin:0'><div data-composer=\"main\">" +
        "<cupri-textarea value=\"{{Composer}}\" submit-on-enter></cupri-textarea>" +
        "</div></body>";

    private const string Plain =
        "<body style='margin:0'><div data-composer=\"main\">" +
        "<cupri-textarea value=\"{{Composer}}\"></cupri-textarea>" +
        "</div></body>";

    private static TestDoc Focused(string html, Model m)
    {
        var t = new TestDoc(html, "", m, width: 420, height: 240, components: true);
        var f = t.FindRole("textbox");
        t.Doc.DispatchClick(f.X + 10, f.Y + 10);
        t.Layout();
        return t;
    }

    [Fact]
    public void Enter_submits_and_does_not_insert_a_newline()
    {
        var m = new Model();
        using var t = Focused(Html, m);
        string? submitted = null;
        t.Doc.OnSubmit("data-composer", e => { submitted = e.Value; return true; });

        t.Doc.DispatchKey("hi", EditKey.None);
        Assert.True(t.Doc.DispatchKey(null, EditKey.Enter));

        Assert.Equal("main", submitted);                  // the attribute names which field submitted
        Assert.Equal("hi", m.Composer);                   // …and it was committed before the handler ran
        Assert.DoesNotContain("\n", m.Composer);
    }

    [Fact]
    public void Shift_enter_still_inserts_a_newline()
    {
        var m = new Model();
        using var t = Focused(Html, m);
        var fired = 0;
        t.Doc.OnSubmit("data-composer", _ => { fired++; return true; });

        t.Doc.DispatchKey("a", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter, KeyMods.Shift);
        t.Doc.DispatchKey("b", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);           // now send it

        Assert.Equal(1, fired);
        Assert.Equal("a\nb", m.Composer);                 // the Shift+Enter survived as a hard newline
    }

    /// <summary>A textarea without the attribute is untouched — that is the whole point of it being
    /// per-field rather than a global shortcut.</summary>
    [Fact]
    public void A_textarea_without_the_attribute_is_unchanged()
    {
        var m = new Model();
        using var t = Focused(Plain, m);
        var fired = 0;
        t.Doc.OnSubmit("data-composer", _ => { fired++; return true; });

        t.Doc.DispatchKey("a", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Doc.DispatchKey("b", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Escape);          // blur, committing the buffer

        Assert.Equal(0, fired);
        Assert.Equal("a\nb", m.Composer);
    }

    /// <summary>Nothing claimed the submit, so Enter must do what it always did rather than vanish.
    /// A field that silently eats a keystroke is the failure mode of #88 and #89 both.</summary>
    [Fact]
    public void An_unclaimed_submit_falls_through_to_a_newline()
    {
        var m = new Model();
        using var t = Focused(Html, m);
        t.Doc.OnSubmit("data-nothing-matches-this", _ => { Assert.Fail("must not run"); return true; });

        t.Doc.DispatchKey("a", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Doc.DispatchKey("b", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Escape);

        Assert.Equal("a\nb", m.Composer);                 // behaves exactly as without the attribute
    }

    /// <summary>Returning false means "not mine", so it keeps bubbling — and if nothing takes it, the
    /// key still falls through. Same contract as OnAction and OnContext.</summary>
    [Fact]
    public void Returning_false_lets_it_bubble_and_then_fall_through()
    {
        var m = new Model();
        using var t = Focused(Html, m);
        var seen = new List<string>();
        t.Doc.OnSubmit("data-composer", e => { seen.Add(e.Value); return false; });

        t.Doc.DispatchKey("a", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Doc.DispatchKey(null, EditKey.Escape);

        Assert.Equal(["main"], seen);                     // the handler did see it…
        Assert.Equal("a\n", m.Composer);                  // …but declined, so Enter did its ordinary work
    }

    /// <summary>The attribute may sit on an ancestor of the field, as it may for a click.</summary>
    [Fact]
    public void The_target_bubbles_to_an_ancestor_carrying_the_attribute()
    {
        var m = new Model();
        using var t = Focused(
            "<body style='margin:0'><div data-room=\"lobby\"><div class='wrap'>" +
            "<cupri-textarea value=\"{{Composer}}\" submit-on-enter></cupri-textarea>" +
            "</div></div></body>", m);
        string? room = null;
        t.Doc.OnSubmit("data-room", e => { room = e.Value; return true; });

        t.Doc.DispatchKey("x", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal("lobby", room);
    }

    /// <summary>A composer goes on composing after it sends, so the field keeps focus — unlike a
    /// single-line field's Enter, which commits and blurs.</summary>
    [Fact]
    public void Focus_is_kept_after_a_submit_so_the_next_message_can_be_typed()
    {
        var m = new Model();
        using var t = Focused(Html, m);
        t.Doc.OnSubmit("data-composer", _ => { m.Composer = ""; return true; });

        t.Doc.DispatchKey("first", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal("", m.Composer);                     // the handler cleared it
        t.Layout();

        Assert.True(t.Doc.GetTextInputState().Focused);   // still focused…
        t.Doc.DispatchKey("second", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal("", m.Composer);                     // …and the next message went through the same path
    }

    /// <summary>On a phone the same gesture is the keyboard's action key, so the two must agree: the
    /// behaviour implies the hint. PerformEditorAction dispatches EditKey.Enter, so the Send key lands
    /// on exactly the branch above.</summary>
    [Fact]
    public void Submit_on_enter_labels_the_on_screen_action_key_send()
    {
        using var t = Focused(Html, new Model());
        Assert.Equal("send", t.Doc.GetTextInputState().EnterKeyHint);
    }

    [Fact]
    public void An_explicit_enterkeyhint_still_wins()
    {
        using var t = Focused(
            "<body style='margin:0'><cupri-textarea value=\"{{Composer}}\" submit-on-enter " +
            "enterkeyhint=\"go\"></cupri-textarea></body>", new Model());
        Assert.Equal("go", t.Doc.GetTextInputState().EnterKeyHint);
    }
}
