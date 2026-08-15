using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Caret and delete arithmetic must step by CODE POINT, not UTF-16 unit: an emoji is a surrogate
/// pair, and unit-stepping splits it into mojibake that the model then commits. Android's
/// InputConnection contract (deleteSurroundingTextInCodePoints) has no correct mapping without
/// this, and it was wrong for plain hardware-keyboard emoji too.
/// </summary>
public class CodePointEditingTests
{
    private sealed class Model { public string Text { get; set; } = ""; }

    private static TestDoc Field(Model m)
    {
        var t = new TestDoc("<body><cupri-textfield value=\"{{Text}}\"></cupri-textfield></body>",
            "", m, components: true);
        var f = t.FindRole("textbox");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();
        return t;
    }

    [Fact]
    public void Backspace_removes_a_whole_emoji()
    {
        var m = new Model { Text = "hi😀" };
        using var t = Field(m);
        t.Doc.DispatchKey(null, EditKey.End);

        t.Doc.DispatchKey(null, EditKey.Backspace);

        Assert.Equal("hi", m.Text);                    // both units gone, no lone surrogate
    }

    [Fact]
    public void Delete_removes_a_whole_emoji()
    {
        var m = new Model { Text = "😀hi" };
        using var t = Field(m);
        t.Doc.DispatchKey(null, EditKey.Home);

        t.Doc.DispatchKey(null, EditKey.Delete);

        Assert.Equal("hi", m.Text);
    }

    [Fact]
    public void Arrows_never_land_inside_a_surrogate_pair()
    {
        var m = new Model { Text = "a😀b" };
        using var t = Field(m);
        t.Doc.DispatchKey(null, EditKey.End);          // caret at 4

        t.Doc.DispatchKey(null, EditKey.Left);         // over 'b'  → 3
        t.Doc.DispatchKey(null, EditKey.Left);         // over 😀   → 1, not 2
        Assert.Equal(1, t.Doc.GetTextInputState().SelStart);

        t.Doc.DispatchKey(null, EditKey.Right);        // back over 😀 → 3, not 2
        Assert.Equal(3, t.Doc.GetTextInputState().SelStart);
    }

    [Fact]
    public void Typing_an_emoji_still_coalesces_into_the_undo_run()
    {
        var m = new Model();
        using var t = Field(m);

        t.Doc.DispatchKey("a", EditKey.None);
        t.Doc.DispatchKey("😀", EditKey.None);          // two UTF-16 units, ONE code point
        t.Doc.DispatchKey("b", EditKey.None);
        Assert.Equal("a😀b", m.Text);

        t.Doc.Undo();                                   // one run, one step — emoji didn't split it
        Assert.Equal("", t.Doc.GetTextInputState().Value);
    }

    [Fact]
    public void A_masked_field_peeks_a_typed_emoji()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-password value=\"{{Text}}\"></cupri-password></body>",
            "", m, components: true);
        var f = t.FindRole("textbox");
        t.Doc.DispatchClick(f.X + 10, f.Y + f.Height / 2);
        t.Layout();

        t.Doc.DispatchKey("😀", EditKey.None);

        // The peek keeps the host frame pump alive while it shows — that's the observable contract.
        Assert.True(t.Doc.HasActiveAnimations, "the peek is showing (Length==1 used to reject pairs)");
    }
}
