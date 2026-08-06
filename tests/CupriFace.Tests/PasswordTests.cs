using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>&lt;cupri-password&gt; masks its display while the bound model keeps the plaintext.</summary>
public class PasswordTests
{
    private sealed class PwModel { public string Pw { get; set; } = ""; }
    private sealed class RevealModel { public string Pw { get; set; } = ""; public bool Show { get; set; } }

    // Rendered text of the focused/unfocused field (bullets or plaintext or placeholder).
    private static string Shown(TestDoc t)
    {
        var field = t.FindRole("textbox");
        var txt = TestDoc.Find(field, n => n.IsText && n.Lines is { Count: > 0 });
        return txt?.Text ?? "";
    }

    private static bool Masked(TestDoc t) => t.FindRole("textbox").Element!.HasAttribute("data-mask");

    [Fact]
    public void Value_is_painted_as_bullets_but_model_keeps_plaintext()
    {
        var m = new PwModel { Pw = "secret" };
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-password value=\"{{Pw}}\"></cupri-password></div></body>",
            "", m, components: true, width: 340, height: 140);

        Assert.Equal(new string('•', 6), Shown(t)); // painted mask
        Assert.Equal("secret", m.Pw);               // plaintext untouched
        Assert.True(Masked(t));
    }

    [Fact]
    public void Typing_edits_plaintext_while_the_display_stays_masked()
    {
        var m = new PwModel();
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-password value=\"{{Pw}}\" placeholder=\"Password\"></cupri-password></div></body>",
            "", m, components: true, width: 340, height: 140);

        t.ClickNode(t.FindRole("textbox"));
        t.Type("hunter2");
        Assert.Equal(new string('•', 7), Shown(t)); // masked mid-edit

        // Caret operates on the plaintext (not the bullets): delete the last char, then commit on blur.
        t.Key(EditKey.End);
        t.Key(EditKey.Backspace);
        t.Key(EditKey.Escape);                       // blur → commit
        Assert.Equal("hunter", m.Pw);
    }

    [Fact]
    public void Reveal_toggle_flips_between_bullets_and_plaintext()
    {
        var m = new RevealModel { Pw = "hi", Show = false };
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-password value=\"{{Pw}}\" reveal=\"{{Show}}\"></cupri-password></div></body>",
            "", m, components: true, width: 360, height: 140);

        Assert.Equal("••", Shown(t));
        Assert.True(Masked(t));

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-pw-eye") == true);
        Assert.True(m.Show);
        Assert.Equal("hi", Shown(t));   // now revealed as plaintext
        Assert.False(Masked(t));        // and no longer masked while editing

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-pw-eye") == true);
        Assert.False(m.Show);
        Assert.Equal("••", Shown(t));   // masked again
    }

    [Fact]
    public void Masked_field_blocks_copy_until_revealed()
    {
        var m = new RevealModel { Pw = "secret", Show = false };
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-password value=\"{{Pw}}\" reveal=\"{{Show}}\"></cupri-password></div></body>",
            "", m, components: true, width: 360, height: 140);

        t.ClickNode(t.FindRole("textbox"));
        t.Key(EditKey.SelectAll);
        Assert.Null(t.Doc.CopySelection());      // masked → nothing copyable
        Assert.Null(t.Doc.CutSelection());       // and cut is blocked too (no delete)

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-pw-eye") == true); // reveal (blurs field)
        Assert.True(m.Show);
        t.ClickNode(t.FindRole("textbox"));
        t.Key(EditKey.SelectAll);
        Assert.Equal("secret", t.Doc.CopySelection()); // revealed → plaintext copyable
    }

    [Fact]
    public void Masked_field_peeks_the_last_typed_char_then_remasks_on_the_clock()
    {
        var m = new PwModel();
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-password value=\"{{Pw}}\"></cupri-password></div></body>",
            "", m, components: true, width: 340, height: 140);

        t.ClickNode(t.FindRole("textbox"));
        t.Type("a");
        Assert.Equal("a", Shown(t));                 // freshly typed char is visible
        t.Type("b");
        Assert.Equal("•b", Shown(t));                // previous char masked, newest peeks
        Assert.True(t.Doc.HasActiveAnimations);      // asks the host to keep ticking

        t.Doc.Animate(0.0); t.Layout();              // first frame stamps the peek clock
        Assert.Equal("•b", Shown(t));                // still within the peek window
        t.Doc.Animate(0.5); t.Layout();
        Assert.Equal("•b", Shown(t));

        t.Doc.Animate(2.0); t.Layout();              // past the window → fully re-masked
        Assert.Equal("••", Shown(t));
        Assert.False(t.Doc.HasActiveAnimations);     // and it stops asking for frames
    }

    [Fact]
    public void Bulk_insert_and_backspace_do_not_peek()
    {
        var m = new PwModel();
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-password value=\"{{Pw}}\"></cupri-password></div></body>",
            "", m, components: true, width: 340, height: 140);

        t.ClickNode(t.FindRole("textbox"));
        t.Type("paste");                             // multi-char insert (e.g. paste) → no peek
        Assert.Equal("•••••", Shown(t));
        t.Type("x");                                 // single char → peeks
        Assert.Equal("•••••x", Shown(t));
        t.Key(EditKey.Backspace);                    // delete hides the peek at once
        Assert.Equal("•••••", Shown(t));
    }

    [Fact]
    public void Without_a_reveal_binding_there_is_no_eye_toggle()
    {
        var m = new PwModel { Pw = "x" };
        using var t = new TestDoc(
            "<body><cupri-password value=\"{{Pw}}\"></cupri-password></body>",
            "", m, components: true);
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-pw-eye") == true));
    }
}
