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
    public void Without_a_reveal_binding_there_is_no_eye_toggle()
    {
        var m = new PwModel { Pw = "x" };
        using var t = new TestDoc(
            "<body><cupri-password value=\"{{Pw}}\"></cupri-password></body>",
            "", m, components: true);
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-pw-eye") == true));
    }
}
