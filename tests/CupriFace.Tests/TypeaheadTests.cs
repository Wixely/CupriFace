using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The three gaps that stopped an app building a typeahead (#111). Each had a workaround; together
/// they set the ceiling, because an @-mention list needs all three at once — arrow keys to move the
/// highlight, a way to insert the completion, and a way to put the cursor back afterwards.
///
/// <para>The issue also reported that a headless harness could not focus a field by clicking, which
/// is why these tests use <c>doc.Focus(selector)</c> for the cases that are not specifically about
/// clicking. That the API makes this testable at all is part of what it is for.</para>
/// </summary>
public class TypeaheadTests
{
    private sealed class Model { public string Composer { get; set; } = ""; }

    // A composer plus a suggestion list, which is the shape the issue describes.
    private const string Html =
        "<body><div style='padding:20px'>" +
        "<cupri-textarea id='composer' value=\"{{Composer}}\"></cupri-textarea>" +
        "</div></body>";

    // ---- 1. a bare arrow reaches the app while a field is focused ------------------------------

    /// <summary>The exact measurement from the issue: Down fired with nothing focused and did not
    /// fire with the composer focused, so an open list could not be driven by the arrow keys.</summary>
    [Fact]
    public void A_bare_arrow_reaches_a_shortcut_while_a_field_is_focused()
    {
        var m = new Model();
        var fired = 0;
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.None, "Down", () => fired++);

        Assert.True(t.Doc.DispatchKey("", EditKey.Down));     // nothing focused
        Assert.Equal(1, fired);
        t.Layout();

        Assert.True(t.Doc.Focus("#composer"));
        Assert.True(t.Doc.DispatchKey("", EditKey.Down));     // focused — used to be swallowed
        Assert.Equal(2, fired);
        Assert.Equal("", m.Composer);                         // and it was not typed into the field
    }

    /// <summary>The rule this exception is carved out of, and the reason it is only two keys: a bare
    /// letter binding must never eat typing.</summary>
    [Fact]
    public void A_bare_letter_still_does_not_fire_while_a_field_is_focused()
    {
        var m = new Model();
        var fired = 0;
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.None, "a", () => fired++);

        Assert.True(t.Doc.Focus("#composer"));
        t.Doc.DispatchKey("a", EditKey.None);
        Assert.Equal(0, fired);
        Assert.Equal("a", m.Composer);   // typed, not swallowed
    }

    /// <summary>Every other editing key is spoken for by the focused field, so none of them may be
    /// handed to an app binding: Left/Right/Home/End move the caret, Enter submits, Backspace edits.
    /// Left is the representative — if this starts firing, the caret has been stolen.</summary>
    [Fact]
    public void A_bare_caret_key_still_does_not_fire_while_a_field_is_focused()
    {
        var m = new Model { Composer = "abc" };
        var fired = 0;
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.None, "Left", () => fired++);

        Assert.True(t.Doc.Focus("#composer"));
        t.Doc.DispatchKey("", EditKey.Left);
        Assert.Equal(0, fired);
    }

    // ---- 2. the completion can be inserted -----------------------------------------------------

    /// <summary>
    /// The corruption from the issue, reproduced and then fixed: completing "@da" to "@dagger" and
    /// typing "look at this" produced "@dalook at thisgger" — the assignment discarded, the sentence
    /// interleaved into the fragment at the stale caret.
    /// </summary>
    [Fact]
    public void Setting_a_focused_fields_value_survives_and_the_caret_follows()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);

        Assert.True(t.Doc.Focus("#composer"));
        foreach (var ch in "@da") t.Doc.DispatchKey(ch.ToString(), EditKey.None);
        Assert.Equal("@da", m.Composer);

        Assert.True(t.Doc.SetFieldValue("#composer", "@dagger "));
        foreach (var ch in "look at this") t.Doc.DispatchKey(ch.ToString(), EditKey.None);

        Assert.Equal("@dagger look at this", m.Composer);
    }

    /// <summary>…and the plain assignment it replaces still loses, which is why the method exists.
    /// If this ever starts passing, the buffer no longer outranks the model and SetFieldValue's
    /// reason for being should be re-examined rather than the test deleted.</summary>
    [Fact]
    public void A_plain_assignment_to_a_focused_field_is_still_discarded()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);

        Assert.True(t.Doc.Focus("#composer"));
        foreach (var ch in "@da") t.Doc.DispatchKey(ch.ToString(), EditKey.None);

        m.Composer = "@dagger ";      // the assignment the issue tried first
        t.Doc.Refresh();
        t.Doc.DispatchKey("X", EditKey.None);

        Assert.NotEqual("@dagger X", m.Composer);
    }

    // ---- 3. focus can be moved from code -------------------------------------------------------

    /// <summary>The "right-click → Edit loads the message into the composer" flow: the text is placed
    /// and focus put back, so typing continues in the field instead of going nowhere.</summary>
    [Fact]
    public void Focus_moves_to_a_field_so_typing_lands_in_it()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);

        t.Doc.DispatchKey("x", EditKey.None);        // nothing focused: goes nowhere
        Assert.Equal("", m.Composer);

        Assert.True(t.Doc.SetFieldValue("#composer", "the original message"));
        Assert.True(t.Doc.Focus("#composer"));
        foreach (var ch in " edited") t.Doc.DispatchKey(ch.ToString(), EditKey.None);

        Assert.Equal("the original message edited", m.Composer);
    }

    [Fact]
    public void Focus_and_SetFieldValue_report_failure_rather_than_pretending()
    {
        using var t = new TestDoc(Html, "", new Model(), width: 400, height: 200, components: true);
        Assert.False(t.Doc.Focus("#nothing-here"));
        Assert.False(t.Doc.SetFieldValue("#nothing-here", "x"));
    }

    [Fact]
    public void Blur_commits_and_releases_the_field()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        var fired = 0;
        t.Doc.OnShortcut(KeyMods.None, "Down", () => fired++);

        Assert.True(t.Doc.Focus("#composer"));
        foreach (var ch in "hi") t.Doc.DispatchKey(ch.ToString(), EditKey.None);
        Assert.True(t.Doc.Blur());
        Assert.Equal("hi", m.Composer);      // the buffer was committed, not dropped

        t.Layout();
        t.Doc.DispatchKey("", EditKey.Down); // and a bare key behaves as "nothing focused" again
        Assert.Equal(1, fired);
    }
}
