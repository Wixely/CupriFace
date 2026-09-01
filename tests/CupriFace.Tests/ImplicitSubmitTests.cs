using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Enter in a SINGLE-LINE field submits without being asked to; a textarea still has to opt in.
///
/// This is what the web platform does — Enter in an <c>&lt;input&gt;</c> submits its form, Enter in a
/// <c>&lt;textarea&gt;</c> writes a newline — and CupriFace previously did neither: a single-line
/// Enter committed and blurred, which is quieter and less useful, and a textarea needed
/// <c>submit-on-enter</c> either way.
///
/// There is no <c>&lt;form&gt;</c> element here, so the scope is the attribute <c>OnSubmit</c> bubbles
/// to. An ancestor carrying <c>data-…</c> is the form boundary, which is what keeps the implicit case
/// from reaching fields nobody scoped: no matching ancestor, no claim, no change in behaviour.
/// </summary>
public class ImplicitSubmitTests
{
    private sealed class Model
    {
        public string Query { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    // A single-line field and a textarea, both inside an ancestor an OnSubmit can bubble to. The
    // textarea is NOT marked submit-on-enter — that is the difference under test.
    private const string Html =
        "<body style='margin:0'><div data-panel=\"search\">" +
        "<cupri-textfield value=\"{{Query}}\" placeholder=\"Search…\"></cupri-textfield>" +
        "<cupri-textarea value=\"{{Notes}}\" placeholder=\"Notes…\"></cupri-textarea>" +
        "</div></body>";

    private static TestDoc Doc(Model m) =>
        new(Html, "body{background:#fff}", m, width: 420, height: 340, components: true);

    private static void FocusFirstTextbox(TestDoc t, int index)
    {
        var boxes = new List<Dom.RenderNode>();
        void Walk(Dom.RenderNode n)
        {
            if (n.Element?.GetAttribute("role") == "textbox") boxes.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Doc.Root);
        var (x, y) = TestDoc.Center(boxes[index]);
        t.Click(x, y);
    }

    /// <summary>The change: a single-line field submits on Enter with no attribute on it at all.</summary>
    [Fact]
    public void A_single_line_field_submits_on_enter_without_opting_in()
    {
        var m = new Model();
        using var t = Doc(m);
        string? submitted = null;
        t.Doc.OnSubmit("data-panel", e => { submitted = e.Value; return true; });

        FocusFirstTextbox(t, 0);
        t.Doc.DispatchKey("boots", EditKey.None);
        Assert.True(t.Doc.DispatchKey(null, EditKey.Enter));

        Assert.Equal("search", submitted);      // the ancestor attribute named the scope
        Assert.Equal("boots", m.Query);         // committed before the handler ran
    }

    /// <summary>The line that must not move: a textarea's Enter is a newline until it opts in. Taking
    /// that by default would eat a keystroke in every textarea an app has.</summary>
    [Fact]
    public void A_textarea_does_not_submit_unless_it_opted_in()
    {
        var m = new Model();
        using var t = Doc(m);
        var fired = 0;
        t.Doc.OnSubmit("data-panel", _ => { fired++; return true; });

        FocusFirstTextbox(t, 1);                // the textarea
        t.Doc.DispatchKey("first", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Doc.DispatchKey("second", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Escape); // blur, committing

        Assert.Equal(0, fired);
        Assert.Equal("first\nsecond", m.Notes);  // Enter wrote a newline, as it always did
    }

    /// <summary>No ancestor to bubble to, so nothing claims it and the old behaviour stands: Enter
    /// commits and blurs. This is what makes the implicit case safe to turn on for everyone.</summary>
    [Fact]
    public void A_single_line_field_outside_any_submit_scope_is_unchanged()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body style='margin:0'><cupri-textfield value=\"{{Query}}\"></cupri-textfield></body>",
            "body{background:#fff}", m, width: 420, height: 200, components: true);
        t.Doc.OnSubmit("data-nothing-here", _ => { Assert.Fail("must not run"); return true; });

        FocusFirstTextbox(t, 0);
        t.Doc.DispatchKey("typed", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Layout();

        Assert.Equal("typed", m.Query);                          // committed, exactly as before
        Assert.False(t.Doc.GetTextInputState().Focused);         // …and blurred, exactly as before
    }

    /// <summary>An app that registered no OnSubmit at all is untouched — the commonest case, and the
    /// one that must not notice this release.</summary>
    [Fact]
    public void With_no_handler_registered_enter_still_commits_and_blurs()
    {
        var m = new Model();
        using var t = Doc(m);

        FocusFirstTextbox(t, 0);
        t.Doc.DispatchKey("plain", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Layout();

        Assert.Equal("plain", m.Query);
        Assert.False(t.Doc.GetTextInputState().Focused);
    }

    /// <summary>Returning false means "not mine", so the key falls through to its ordinary work rather
    /// than being swallowed — the same contract the opt-in case has.</summary>
    [Fact]
    public void A_handler_that_declines_lets_enter_commit_and_blur()
    {
        var m = new Model();
        using var t = Doc(m);
        var seen = 0;
        t.Doc.OnSubmit("data-panel", _ => { seen++; return false; });

        FocusFirstTextbox(t, 0);
        t.Doc.DispatchKey("declined", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Layout();

        Assert.Equal(1, seen);
        Assert.Equal("declined", m.Query);
        Assert.False(t.Doc.GetTextInputState().Focused);
    }

    /// <summary>A textarea that DOES opt in keeps everything it had: Enter submits, Shift+Enter
    /// inserts. The implicit path must not have disturbed the explicit one.</summary>
    [Fact]
    public void An_opted_in_textarea_still_submits_on_enter_and_newlines_on_shift_enter()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body style='margin:0'><div data-panel=\"notes\">" +
            "<cupri-textarea value=\"{{Notes}}\" submit-on-enter></cupri-textarea></div></body>",
            "body{background:#fff}", m, width: 420, height: 260, components: true);
        var fired = 0;
        t.Doc.OnSubmit("data-panel", _ => { fired++; return true; });

        FocusFirstTextbox(t, 0);
        t.Doc.DispatchKey("a", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter, KeyMods.Shift);
        t.Doc.DispatchKey("b", EditKey.None);
        Assert.Equal(0, fired);

        t.Doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal(1, fired);
        Assert.Equal("a\nb", m.Notes);
    }
}
