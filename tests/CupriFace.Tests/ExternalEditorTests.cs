using CupriFace.Components;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>CupriDocument.SetEditText</c>: the seam an EXTERNAL editor reports through.
///
/// <para>The web host puts a real, transparent <c>&lt;input&gt;</c> over the painted field, so the
/// browser's own editor, IME, clipboard and password manager work on the thing they were built for.
/// While it holds focus the browser owns the text — and hands it back here. Everything the
/// keystroke path guarantees has to hold on this path too, or the web quietly becomes a different
/// editor from every other host.</para>
/// </summary>
public class ExternalEditorTests(ITestOutputHelper output)
{
    private sealed class Holder { public string V { get; set; } = ""; public int N { get; set; } = 50; }

    /// <summary>A focused field, built the way the other input tests build one.</summary>
    private static (TestDoc T, Holder M) Field(string html = "<body><cupri-textfield value=\"{{V}}\"></cupri-textfield></body>")
    {
        var m = new Holder();
        var t = new TestDoc(html, "", m, components: true);
        var f = t.FindRole("textbox") ?? t.FindRole("spinbutton");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();
        return (t, m);
    }

    [Fact]
    public void Text_and_selection_arrive_together_and_the_engine_agrees_about_both()
    {
        var (t, m) = Field();
        using var _ = t;

        Assert.True(t.Doc.SetEditText("hello", 5, 5));
        var s = t.Doc.GetTextInputState();
        Assert.Equal("hello", s.Value);
        Assert.Equal((5, 5), (s.SelStart, s.SelEnd));
        Assert.Equal("hello", m.V);             // …and the model tracks a valid buffer live

        // The caret is the browser's to place: a selection arrives with the text it belongs to.
        Assert.True(t.Doc.SetEditText("hello", 1, 4));
        s = t.Doc.GetTextInputState();
        Assert.Equal((1, 4), (s.SelStart, s.SelEnd));
        // …and the engine's own machinery reads it, so Copy takes what the browser had selected.
        Assert.Equal("ell", t.Doc.CopySelection());
    }

    [Fact]
    public void A_value_that_is_invalid_mid_edit_is_kept_rather_than_clamped()
    {
        // The rule this seam exists to respect: never block or correct the user mid-edit. A number
        // field bound to a minimum of 10 must hold "1" while it is being typed — committing every
        // keystroke through the binding would clamp it to 10 under the cursor and make "15"
        // untypeable. The buffer stays permissive and the MODEL keeps its last good value.
        var (t, m) = Field("<body><cupri-number value=\"{{N}}\" min=\"10\" max=\"99\"></cupri-number></body>");
        using var _ = t;
        output.WriteLine(t.Doc.DumpTree(3));

        Assert.True(t.Doc.SetEditText("1", 1, 1));
        Assert.Equal("1", t.Doc.GetTextInputState().Value);   // what the field shows
        Assert.Equal(50, m.N);                                // what the model still holds

        Assert.True(t.Doc.SetEditText("15", 2, 2));
        Assert.Equal(15, m.N);                                // valid again → live-committed
    }

    [Fact]
    public void A_single_line_field_takes_no_newlines_and_line_endings_are_normalised()
    {
        var (t, _) = Field();
        using var _t = t;

        // The same normalisation the keystroke path applies: a paste from a Windows clipboard
        // arrives as "\r\n", and a single-line field collapses hard breaks to spaces like <input>.
        Assert.True(t.Doc.SetEditText("a\r\nb", 4, 4));
        Assert.Equal("a b", t.Doc.GetTextInputState().Value);
    }

    [Fact]
    public void A_run_of_typing_undoes_as_one_step_the_way_keystrokes_do()
    {
        // Each character arrives as its own call from the browser, so without grouping Ctrl+Z
        // walked back one letter at a time on the web and one word at a time everywhere else.
        var (t, _) = Field();
        using var _t = t;

        foreach (var (text, caret) in new[] { ("h", 1), ("hi", 2), ("hi ", 3), ("hi t", 4), ("hi th", 5) })
            t.Doc.SetEditText(text, caret, caret);

        t.Doc.Undo();
        var after = t.Doc.GetTextInputState().Value;
        output.WriteLine($"undo → '{after}'");
        Assert.Equal("hi ", after);            // back to the start of the word, not one letter
    }

    [Fact]
    public void A_composition_in_flight_owns_the_buffer()
    {
        // The preedit lives INSIDE this same buffer (the engine paints it underlined), so while an
        // IME is composing the composition seam owns it. The page ignores the browser's
        // intermediate values for the same reason; this is the engine's half of that contract.
        var (t, _) = Field();
        using var _t = t;
        t.Doc.SetEditText("ni", 2, 2);

        Assert.True(t.Doc.SetComposition("hao"));
        Assert.True(t.Doc.HasComposition);
        Assert.False(t.Doc.SetEditText("something else", 3, 3), "a value push must not disturb a preedit");
        Assert.Equal("nihao", t.Doc.GetTextInputState().Value);

        t.Doc.CommitComposition();
        Assert.True(t.Doc.SetEditText("nihao!", 6, 6));   // …and it applies again once committed
    }

    [Fact]
    public void Nothing_focused_and_nothing_changed_are_both_refusals()
    {
        using var t = new TestDoc("<body><cupri-textfield value=\"{{V}}\"></cupri-textfield></body>",
                                  "", new Holder(), components: true);
        Assert.False(t.Doc.SetEditText("x", 1, 1));       // nothing focused: there is no buffer to set

        var f = t.FindRole("textbox");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();
        Assert.True(t.Doc.SetEditText("x", 1, 1));
        Assert.False(t.Doc.SetEditText("x", 1, 1));       // identical text AND caret → no repaint asked for
    }

    [Fact]
    public void An_out_of_range_selection_clamps_rather_than_throwing()
    {
        // An external editor's model of the text can lag the engine's by a frame, exactly as an
        // IME's can — the existing selection seam clamps for that reason, and so does this one.
        var (t, _) = Field();
        using var _t = t;

        Assert.True(t.Doc.SetEditText("ab", 99, 99));
        var s = t.Doc.GetTextInputState();
        Assert.Equal((2, 2), (s.SelStart, s.SelEnd));
    }

    /// <summary>The other door: a value arriving for a field NOBODY is editing — a password manager
    /// filling a form, which focuses nothing first. That goes through the binding, because there is
    /// no edit in progress to be permissive about.</summary>
    [Fact]
    public void A_fill_for_an_unfocused_field_goes_through_the_binding_by_path()
    {
        var m = new Holder();
        using var t = new TestDoc("<body><cupri-textfield value=\"{{V}}\"></cupri-textfield></body>",
                                  "", m, components: true);
        var node = FindNode(t.Doc.BuildAccessibilityTree(400, 300), "textbox");
        Assert.NotNull(node);

        Assert.True(t.Doc.AccessibilitySetText(node!.Path, "ada@example.com"));
        Assert.Equal("ada@example.com", m.V);
        Assert.Equal(default, t.Doc.GetTextInputState());   // and it focused nothing on the way

        static Accessibility.AccessibilityNode? FindNode(Accessibility.AccessibilityNode n, string role)
        {
            if (n.Role == role) return n;
            foreach (var c in n.Children) if (FindNode(c, role) is { } f) return f;
            return null;
        }
    }
}
