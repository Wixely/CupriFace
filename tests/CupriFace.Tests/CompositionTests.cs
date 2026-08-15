using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// IME composition (preedit): the marked range lives inside the edit buffer, renders underlined,
/// and — the load-bearing rule — NEVER reaches the model until the IME commits. These are the
/// engine-side guarantees both the Android InputConnection and the web composition events build on.
/// </summary>
public class CompositionTests
{
    private sealed class Model { public string Text { get; set; } = ""; public int Count { get; set; } = 5; }

    private static TestDoc Field(Model m) =>
        new("<body><cupri-textfield value=\"{{Text}}\"></cupri-textfield></body>", "", m, components: true);

    private static void Focus(TestDoc t)
    {
        var f = t.FindRole("textbox");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();
    }

    [Fact]
    public void Preedit_updates_in_place_and_never_touches_the_model()
    {
        var m = new Model();
        using var t = Field(m);
        Focus(t);

        Assert.True(t.Doc.SetComposition("n"));
        Assert.True(t.Doc.SetComposition("ni"));
        Assert.True(t.Doc.SetComposition("nihao"));

        Assert.True(t.Doc.HasComposition);
        Assert.Equal("nihao", t.Doc.GetTextInputState().Value);   // the buffer shows the preedit…
        Assert.Equal("", m.Text);                                  // …and the model has seen NOTHING

        Assert.True(t.Doc.CommitComposition("你好"));              // the IME's real output
        Assert.False(t.Doc.HasComposition);
        Assert.Equal("你好", m.Text);                              // committed through the normal path
    }

    [Fact]
    public void Clearing_a_composition_restores_the_precomposition_text()
    {
        var m = new Model { Text = "ab" };
        using var t = Field(m);
        Focus(t);
        t.Doc.DispatchKey(null, EditKey.End);

        t.Doc.SetComposition("xyz");
        Assert.Equal("abxyz", t.Doc.GetTextInputState().Value);

        Assert.True(t.Doc.ClearComposition());                     // the IME cancelled
        Assert.Equal("ab", t.Doc.GetTextInputState().Value);
        Assert.Equal("ab", m.Text);
    }

    [Fact]
    public void A_whole_composition_is_one_undo_step()
    {
        var m = new Model { Text = "start " };
        using var t = Field(m);
        Focus(t);
        t.Doc.DispatchKey(null, EditKey.End);

        t.Doc.SetComposition("s");
        t.Doc.SetComposition("su");
        t.Doc.SetComposition("sushi");
        t.Doc.CommitComposition("寿司");
        Assert.Equal("start 寿司", m.Text);

        Assert.True(t.Doc.Undo());                                 // ONE step undoes the whole thing
        Assert.Equal("start ", t.Doc.GetTextInputState().Value);
    }

    [Fact]
    public void Escape_abandons_the_preedit_and_is_swallowed()
    {
        var m = new Model { Text = "keep" };
        using var t = Field(m);
        Focus(t);

        t.Doc.SetComposition("zzz");
        Assert.True(t.Doc.DispatchKey(null, EditKey.Escape));      // consumed by the composition…

        Assert.False(t.Doc.HasComposition);
        Assert.Equal("keep", t.Doc.GetTextInputState().Value);
        Assert.True(t.Doc.GetTextInputState().Focused);            // …NOT by blur/overlay handling
    }

    [Fact]
    public void Enter_commits_the_composition_then_acts_normally()
    {
        var m = new Model();
        using var t = Field(m);
        Focus(t);

        t.Doc.SetComposition("done");
        t.Doc.DispatchKey(null, EditKey.Enter);                    // single-line Enter = commit + blur

        Assert.Equal("done", m.Text);                              // the preedit was committed first
        Assert.False(t.Doc.GetTextInputState().Focused);           // then Enter blurred, as always
    }

    [Fact]
    public void Typing_mid_composition_commits_then_inserts()
    {
        var m = new Model();
        using var t = Field(m);
        Focus(t);

        t.Doc.SetComposition("ab");
        t.Doc.DispatchKey("!", EditKey.None);

        Assert.False(t.Doc.HasComposition);
        Assert.Equal("ab!", t.Doc.GetTextInputState().Value);
    }

    [Fact]
    public void Blur_commits_the_composition()
    {
        var m = new Model();
        using var t = Field(m);
        Focus(t);

        t.Doc.SetComposition("bye");
        t.Doc.DispatchClick(390, 290);                             // click empty space → blur

        Assert.False(t.Doc.HasComposition);
        Assert.Equal("bye", m.Text);
    }

    [Fact]
    public void Composition_respects_the_permissive_buffer_validation()
    {
        // A numeric field mid-composition holds the text; an invalid commit stays in the buffer
        // (red border), the model keeps its last good value — the NumberInput contract, via IME.
        var m = new Model();
        using var t = new TestDoc(
            "<body><cupri-number value=\"{{Count}}\" min=\"0\" max=\"10\"></cupri-number></body>",
            "", m, components: true);
        var f = t.FindRole("spinbutton");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();
        t.Doc.DispatchKey(null, EditKey.SelectAll);

        t.Doc.SetComposition("99");
        Assert.Equal(5, m.Count);                                  // preedit: model untouched
        t.Doc.CommitComposition();
        Assert.Equal(5, m.Count);                                  // 99 > max: invalid, not committed

        var state = t.Doc.GetTextInputState();
        Assert.Equal("99", state.Value);                           // buffer keeps what the IME wrote
    }

    [Fact]
    public void The_text_input_state_reports_focus_kind_and_caret()
    {
        var m = new Model { Text = "hi" };
        using var t = Field(m);

        Assert.False(t.Doc.GetTextInputState().Focused);           // nothing focused yet

        Focus(t);
        var state = t.Doc.GetTextInputState();
        Assert.True(state.Focused);
        Assert.Equal("textbox", state.Role);
        Assert.False(state.Numeric);
        Assert.False(state.Masked);
        Assert.NotNull(state.CaretRect);                           // laid out → a real rectangle
        Assert.True(state.CaretRect!.Value.H > 0);
    }

    [Fact]
    public void Focus_changes_raise_the_event_for_the_keyboard()
    {
        var m = new Model();
        using var t = Field(m);
        var events = new List<bool>();
        t.Doc.TextInputStateChanged += s => events.Add(s.Focused);

        Focus(t);                                                  // → focused
        t.Doc.DispatchClick(390, 290);                             // empty space → blurred

        Assert.Equal(new[] { true, false }, events);
    }
}
