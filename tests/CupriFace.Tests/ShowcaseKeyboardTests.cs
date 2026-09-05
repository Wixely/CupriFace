using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The Showcase's Keyboard page, driven from the keyboard.
///
/// A page that documents keyboard behaviour is worth very little once it drifts from the handlers
/// behind it, and this is the one page where nothing on screen would show the drift. So the demo is
/// exercised rather than rendered: Tab really reaches the controls, the composer really submits on
/// Enter, Shift+Enter really inserts instead, and an open palette really takes the first Escape.
///
/// Focus is asserted through behaviour, not internals — keyboard focus is an index into a focusable
/// list with no public accessor, and only the field being edited carries <c>data-focus</c>. Asking
/// "did typing land here" is the better question anyway.
/// </summary>
public class ShowcaseKeyboardTests(ITestOutputHelper output)
{
    private const int W = 940, H = 720;

    private static (CupriDocument Doc, ShowcaseModel Model) OnKeyboardPage()
    {
        var app = new ShowcaseApp();
        var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Section = "keyboard";
        doc.Refresh();
        using (doc.RenderToImage(W, H)) { }
        return (doc, model);
    }

    // TestDoc.Find is a pure walk over a node, so it works on any document — not only the ones
    // TestDoc builds. Nine other test files declare their own identical copy; this one does not.
    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> p) => TestDoc.Find(n, p);

    private static void Frame(CupriDocument doc) { using (doc.RenderToImage(W, H)) { } }

    private static bool IsComposer(RenderNode n) => n.Element?.HasAttribute("data-submit-on-enter") == true;

    /// <summary>Tab until the wanted element holds the caret. The sidebar is focusable too — every
    /// <c>.nav</c> row has a click handler, which is what makes it reachable — so the section's own
    /// controls are a dozen stops in. That is the real Tab order, not an obstacle to route around.</summary>
    private static RenderNode? TabTo(CupriDocument doc, Func<RenderNode, bool> want, int max = 60)
    {
        for (var i = 0; i < max; i++)
        {
            doc.DispatchKey(null, EditKey.Tab);
            Frame(doc);
            var focused = Find(doc.Root, n => n.Element?.HasAttribute("data-focus") == true);
            if (focused is not null && want(focused)) return focused;
        }
        return null;
    }

    [Fact]
    public void The_page_exists_and_is_reachable_from_the_sidebar()
    {
        var (doc, _) = OnKeyboardPage();
        using var _d = doc;

        Assert.NotNull(Find(doc.Root, n => n.Element?.GetAttribute("data-section") == "keyboard"));
        // submit-on-enter must have expanded into the engine's own attribute, or the page's central
        // claim is decoration.
        Assert.NotNull(Find(doc.Root, IsComposer));
    }

    /// <summary>The headline claim: the page is usable with no pointer. Tab reaches the composer —
    /// past the sidebar and the row of controls above it — and typing lands there.</summary>
    [Fact]
    public void Tab_reaches_the_composer_and_typing_lands_in_it()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;

        var composer = TabTo(doc, IsComposer);
        Assert.NotNull(composer);

        doc.DispatchKey("typed with no pointer", EditKey.None);
        Frame(doc);
        doc.DispatchKey(null, EditKey.Enter);                  // submit commits the buffer, then sends

        Assert.Equal(1, model.SentCount);
        Assert.Contains("typed with no pointer", model.KeyLog);
        output.WriteLine(model.KeyLog);
    }

    /// <summary>The composer's contract, both halves: Shift+Enter inserts, a plain Enter submits and
    /// names the field it came from.</summary>
    [Fact]
    public void Shift_enter_writes_a_newline_and_plain_enter_submits()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        Assert.NotNull(TabTo(doc, IsComposer));

        doc.DispatchKey("one", EditKey.None);
        doc.DispatchKey(null, EditKey.Enter, KeyMods.Shift);   // a newline, NOT a send
        doc.DispatchKey("two", EditKey.None);
        Frame(doc);
        Assert.Equal(0, model.SentCount);

        doc.DispatchKey(null, EditKey.Enter);
        Frame(doc);

        Assert.Equal(1, model.SentCount);
        Assert.Contains("main", model.KeyLog);                 // e.Value named which field submitted
        Assert.Contains("⏎", model.KeyLog);                    // the Shift+Enter newline survived
        output.WriteLine(model.KeyLog);
    }

    /// <summary>Ctrl+Enter fires anywhere, which is what makes it a send key — the binding that could
    /// not exist at all before named keys became bindable.</summary>
    [Fact]
    public void Ctrl_enter_sends_the_composer()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        model.Composer = "hello there";
        doc.Refresh();
        Frame(doc);

        Assert.True(doc.DispatchKey(null, EditKey.Enter, KeyMods.Ctrl));

        Assert.Equal(1, model.SentCount);
        Assert.Equal("", model.Composer);
        Assert.Contains("Ctrl+Enter", model.KeyLog);
        output.WriteLine(model.KeyLog);
    }

    /// <summary>The precedence the page claims in prose, which a screenshot could never show: an open
    /// overlay takes the first Escape, so an app's binding cannot strand it.</summary>
    [Fact]
    public void An_open_palette_takes_the_first_escape_and_the_page_takes_the_second()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        model.Composer = "draft text";
        model.PaletteOpen = true;
        doc.Refresh();
        Frame(doc);

        doc.DispatchKey(null, EditKey.Escape);                 // closes the palette…
        Frame(doc);
        Assert.False(model.PaletteOpen);
        Assert.Equal("draft text", model.Composer);            // …and leaves the composer alone

        doc.DispatchKey(null, EditKey.Escape);                 // now the page's own binding runs
        Assert.Equal("", model.Composer);
        Assert.Contains("Escape", model.KeyLog);
    }

    /// <summary>The handlers are scoped to this page. A global Ctrl+Enter that sent from the Charts
    /// section would be a demo leaking a shortcut into the rest of the app.</summary>
    [Fact]
    public void The_page_shortcuts_do_nothing_on_another_section()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        model.Composer = "should not send";
        model.Section = "charts";
        doc.Refresh();
        Frame(doc);

        doc.DispatchKey(null, EditKey.Enter, KeyMods.Ctrl);
        doc.DispatchKey(null, EditKey.Escape);

        Assert.Equal(0, model.SentCount);
        Assert.Equal("should not send", model.Composer);
    }

    /// <summary>Ctrl+Enter sends what is on screen, not what was last committed.
    ///
    /// Worth pinning because it is not obvious from the code: a Ctrl chord is handled in the shortcut
    /// block, which runs BEFORE the field's own key handling and does not commit the edit buffer the
    /// way a submit does. If the buffer were the only place the typing lived, a composer's own send
    /// key would send the previous message — the most embarrassing failure this page could have.
    /// It does not, because a bound field writes through as you type and validates on blur.</summary>
    [Fact]
    public void Ctrl_enter_sends_what_was_typed_not_what_was_last_committed()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        Assert.NotNull(TabTo(doc, IsComposer));

        doc.DispatchKey("typed but never blurred", EditKey.None);
        Frame(doc);

        doc.DispatchKey(null, EditKey.Enter, KeyMods.Ctrl);

        Assert.Equal(1, model.SentCount);
        Assert.Contains("typed but never blurred", model.KeyLog);
        output.WriteLine($"after Ctrl+Enter: sent={model.SentCount} log={model.KeyLog}");
    }

    // ---- the @-mention typeahead (#111) --------------------------------------

    /// <summary>
    /// The whole point of #111, driven end to end through the real Showcase: type a partial mention,
    /// move the highlight with a BARE arrow while the composer still holds focus, commit with Enter,
    /// and carry on typing where the caret was left. Any one of those three failing puts the feature
    /// back where it was — buildable only with Ctrl-chords nobody guesses and a keystroke-replay
    /// workaround.
    /// </summary>
    [Fact]
    public void An_at_mention_completes_with_bare_arrows_and_leaves_the_caret_in_the_composer()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        Assert.NotNull(TabTo(doc, IsComposer));

        // "da" matches two names, so the highlight has somewhere to move.
        foreach (var ch in "hi @da") doc.DispatchKey(ch.ToString(), EditKey.None);
        Frame(doc);
        Assert.Equal("block", model.MentionOpen);          // the list opened on its own
        Assert.Equal("hi @da", model.Composer);

        // A bare Down, with the composer focused. This is the key that used to be swallowed.
        Assert.True(doc.DispatchKey(null, EditKey.Down));
        Frame(doc);
        Assert.Equal(1, model.MentionHi);                  // the highlight moved…
        Assert.Equal("hi @da", model.Composer);            // …and nothing was typed

        doc.DispatchKey(null, EditKey.Enter);              // commit the highlighted name
        Frame(doc);
        Assert.Equal("hi @dahlia ", model.Composer);

        // The caret is still in the composer: keep typing and it appends rather than going nowhere
        // or landing at a stale offset inside the old text.
        foreach (var ch in "hello") doc.DispatchKey(ch.ToString(), EditKey.None);
        Assert.Equal("hi @dahlia hello", model.Composer);
        output.WriteLine($"composer after completing + typing: \"{model.Composer}\"");
    }

    /// <summary>Enter still SENDS when no list is open — the typeahead must not have stolen the
    /// composer's own submit key.</summary>
    [Fact]
    public void Enter_still_sends_when_no_mention_list_is_open()
    {
        var (doc, model) = OnKeyboardPage();
        using var _d = doc;
        Assert.NotNull(TabTo(doc, IsComposer));

        foreach (var ch in "no mention here") doc.DispatchKey(ch.ToString(), EditKey.None);
        Frame(doc);
        Assert.Equal("none", model.MentionOpen);

        doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal(1, model.SentCount);
    }
}
