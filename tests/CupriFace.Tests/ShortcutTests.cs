using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.OnShortcut</c>: a Ctrl/Cmd chord fires its handler anywhere (even mid-edit) and consumes the
/// key; a plain-key shortcut fires only when no field is focused, so it never eats normal typing.
///
/// Named keys (#88) bind by name — <c>"Enter"</c>, <c>"Escape"</c>, <c>"Tab"</c>, the arrows — because
/// they reach the document as an <see cref="EditKey"/> with no text, and the lookup used to be gated on
/// that text being a single character. Every such registration was therefore dead, silently and
/// identically to a working one: <c>OnShortcut</c> returns the document for chaining, so the call reads
/// fine and the handler simply never runs. Escape is placed deliberately — below the engine's own
/// dismissals, above the plain blur.
/// </summary>
public class ShortcutTests
{
    private sealed class Model { public string Name { get; set; } = ""; }
    private const string Html = "<body><div style='padding:20px'><cupri-textfield value=\"{{Name}}\"></cupri-textfield></div></body>";

    [Fact]
    public void A_ctrl_shortcut_fires_and_consumes_the_key_even_while_editing()
    {
        var m = new Model();
        var fired = 0;
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.Ctrl, "k", () => fired++);

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field

        Assert.True(t.Doc.DispatchKey("k", EditKey.None, KeyMods.Ctrl)); // Ctrl+K
        Assert.Equal(1, fired);
        Assert.Equal("", m.Name);                            // the "k" was NOT typed into the field
    }

    [Fact]
    public void A_plain_shortcut_fires_only_when_no_field_is_focused()
    {
        var fired = 0;
        using var t = new TestDoc(Html, "", new Model(), width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.None, "/", () => fired++);

        Assert.True(t.Doc.DispatchKey("/", EditKey.None));   // nothing focused → fires
        Assert.Equal(1, fired);
        t.Layout();  // a handled shortcut rebuilds the tree — lay out before hit-testing, as hosts do each frame

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field
        t.Doc.DispatchKey("/", EditKey.None);                // now "/" is normal input
        Assert.Equal(1, fired);                              // did not fire again
    }

    // ---- named keys (#88) ----------------------------------------------------

    /// <summary>The case from #88: a composer advertising "Ctrl+Enter to send". Enter arrives as an
    /// EditKey with no text, so the old single-character gate skipped the lookup entirely.</summary>
    [Fact]
    public void A_ctrl_named_key_shortcut_fires_even_while_editing()
    {
        var m = new Model();
        var fired = 0;
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.Ctrl, "Enter", () => fired++);

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field

        Assert.True(t.Doc.DispatchKey(null, EditKey.Enter, KeyMods.Ctrl));
        Assert.Equal(1, fired);
        Assert.Equal("", m.Name);                            // and nothing was typed
    }

    /// <summary>Named keys obey the same plain-key rule as characters: only when nothing is focused,
    /// so a bare Enter binding cannot eat a newline someone is typing into a field.</summary>
    [Fact]
    public void A_plain_named_key_shortcut_respects_the_focus_rule()
    {
        var fired = 0;
        using var t = new TestDoc(Html, "", new Model(), width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.None, "Enter", () => fired++);

        Assert.True(t.Doc.DispatchKey(null, EditKey.Enter)); // nothing focused → fires
        Assert.Equal(1, fired);
        t.Layout();

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field
        t.Doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal(1, fired);                              // did not fire again
    }

    /// <summary>An unbound Ctrl + named key must keep its engine behaviour. The character branch
    /// swallows an unbound Ctrl chord rather than typing it; a named key has no text to type and still
    /// has work to do below, so that early-out must not catch it.</summary>
    [Fact]
    public void An_unbound_ctrl_named_key_still_does_what_it_always_did()
    {
        using var t = new TestDoc("<body><a href=\"about\" class=\"go\">About</a></body>", "",
                                  width: 220, height: 100);
        NavigateEvent? got = null;
        t.Doc.Navigated += e => got = e;
        t.Doc.OnShortcut(KeyMods.Ctrl, "k", () => { });      // some other shortcut exists

        t.Key(EditKey.Tab);                                  // focus the link
        t.Doc.DispatchKey(null, EditKey.Enter, KeyMods.Ctrl); // nothing bound Ctrl+Enter

        Assert.NotNull(got);                                 // …so it still activates the link
        Assert.Equal("about", got!.Value.Href);
    }

    // ---- Escape's place in the dismissal stack -------------------------------

    private sealed class OverlayModel
    {
        public bool Open { get; set; } = true;
        public string Name { get; set; } = "";
    }

    private const string OverlayHtml =
        "<body><div data-bind-open=\"Open\" style='padding:20px'>overlay</div>" +
        "<div style='padding:20px'><cupri-textfield value=\"{{Name}}\"></cupri-textfield></div></body>";

    /// <summary>Escape's built-in dismissals win. An app that binds Escape as "cancel" must not leave an
    /// open overlay stranded because its own handler ran first.</summary>
    [Fact]
    public void An_open_overlay_closes_before_an_escape_shortcut_is_consulted()
    {
        var m = new OverlayModel();
        var fired = 0;
        using var t = new TestDoc(OverlayHtml, "", m, width: 400, height: 240, components: true);
        t.Doc.OnShortcut(KeyMods.None, "Escape", () => fired++);

        Assert.True(t.Doc.DispatchKey(null, EditKey.Escape));
        Assert.False(m.Open);                                // the overlay closed…
        Assert.Equal(0, fired);                              // …and the app shortcut did not run

        t.Layout();
        Assert.True(t.Doc.DispatchKey(null, EditKey.Escape)); // nothing left to dismiss
        Assert.Equal(1, fired);                              // now it is the app's turn
    }

    /// <summary>…but it sits ABOVE the plain blur, because "cancel this edit" is wanted exactly when a
    /// field is still focused — the second dead binding in #88.</summary>
    [Fact]
    public void An_escape_shortcut_fires_while_a_field_is_focused()
    {
        var m = new OverlayModel { Open = false };
        var fired = 0;
        using var t = new TestDoc(OverlayHtml, "", m, width: 400, height: 240, components: true);
        t.Doc.OnShortcut(KeyMods.None, "Escape", () => fired++);

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field

        Assert.True(t.Doc.DispatchKey(null, EditKey.Escape));
        Assert.Equal(1, fired);
    }

    // ---- registration is loud ------------------------------------------------

    /// <summary>#88 shipped two dead bindings because a key that can never match registers happily. A
    /// key with no possible match is a mistake at the call site, so say so there.</summary>
    [Theory]
    [InlineData("F5")]          // no such EditKey — never delivered
    [InlineData("PageDown")]
    [InlineData("")]
    public void Registering_a_key_that_can_never_match_throws(string key)
    {
        using var t = new TestDoc(Html, "", new Model(), width: 400, height: 200, components: true);
        var ex = Assert.Throws<ArgumentException>(() => t.Doc.OnShortcut(KeyMods.Ctrl, key, () => { }));
        Assert.Contains("bindable", ex.Message);
    }

    [Theory]
    [InlineData("k")]
    [InlineData("Enter")]
    [InlineData("escape")]      // case-insensitive, as ShortcutKey already normalises
    [InlineData("Tab")]
    [InlineData("Up")]
    public void Registering_a_key_that_can_match_is_accepted(string key)
    {
        using var t = new TestDoc(Html, "", new Model(), width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.Ctrl, key, () => { });      // does not throw
    }
}
