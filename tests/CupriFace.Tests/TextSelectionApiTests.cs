using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The engine half of the IME contract: an input method moving the caret and re-marking committed
/// text. A soft keyboard does not press arrow keys — it names offsets, and until the engine could
/// accept them, swiping the spacebar (FUTO, Gboard) moved nothing and tapping a finished word to
/// correct it did nothing.
/// </summary>
public class TextSelectionApiTests
{
    private sealed class Holder { public string V { get; set; } = "hello world"; }

    /// <summary>A focused single-line field, built the way every other input test builds one —
    /// components expanded, then clicked to take focus.</summary>
    private static (TestDoc T, CupriDocument Doc) Field()
    {
        var t = new TestDoc("<body><cupri-textfield value=\"{{V}}\"></cupri-textfield></body>",
                            "", new Holder(), components: true);
        var f = t.FindRole("textbox");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();
        return (t, t.Doc);
    }

    [Fact]
    public void An_ime_can_place_the_caret_by_offset()
    {
        var (t, doc) = Field();
        using var _t = t;

        Assert.True(doc.SetTextSelection(6, 6));
        var s = doc.GetTextInputState();
        Assert.Equal(6, s.SelStart);
        Assert.Equal(6, s.SelEnd);

        // …and typing lands there, which is the only proof that matters.
        doc.DispatchKey("brave ", EditKey.None);
        Assert.Equal("hello brave world", doc.GetTextInputState().Value);
    }

    [Fact]
    public void An_ime_can_select_a_range_and_replace_it()
    {
        var (t, doc) = Field();
        using var _t = t;

        Assert.True(doc.SetTextSelection(6, 11));      // "world"
        var s = doc.GetTextInputState();
        Assert.Equal(6, s.SelStart);
        Assert.Equal(11, s.SelEnd);

        doc.DispatchKey("there", EditKey.None);
        Assert.Equal("hello there", doc.GetTextInputState().Value);
    }

    [Fact]
    public void Offsets_outside_the_text_clamp_instead_of_failing()
    {
        // An IME's model of the text can lag ours by a frame; refusing would strand the keyboard.
        var (t, doc) = Field();
        using var _t = t;

        doc.SetTextSelection(-5, 999);
        var s = doc.GetTextInputState();
        Assert.Equal(0, s.SelStart);
        Assert.Equal("hello world".Length, s.SelEnd);
    }

    [Fact]
    public void A_committed_word_can_be_re_marked_as_the_composition()
    {
        // Tap an existing word on a phone keyboard: it becomes preedit again, and the next
        // composition update replaces exactly that range.
        var (t, doc) = Field();
        using var _t = t;

        Assert.True(doc.SetComposingRegion(6, 11));    // "world"
        Assert.True(doc.HasComposition);
        Assert.True(doc.GetTextInputState().Composing);

        doc.SetComposition("worlds");
        Assert.Equal("hello worlds", doc.GetTextInputState().Value);

        doc.CommitComposition();
        Assert.False(doc.HasComposition);
        Assert.Equal("hello worlds", doc.GetTextInputState().Value);
    }

    [Fact]
    public void Correcting_a_word_is_one_undo_step()
    {
        var (t, doc) = Field();
        using var _t = t;

        doc.SetComposingRegion(6, 11);
        doc.SetComposition("worlds");
        doc.CommitComposition();
        Assert.Equal("hello worlds", doc.GetTextInputState().Value);

        Assert.True(doc.Undo());
        Assert.Equal("hello world", doc.GetTextInputState().Value);
    }

    [Fact]
    public void An_empty_region_is_refused_rather_than_starting_an_empty_composition()
    {
        var (t, doc) = Field();
        using var _t = t;
        Assert.False(doc.SetComposingRegion(4, 4));
        Assert.False(doc.HasComposition);
    }
}
