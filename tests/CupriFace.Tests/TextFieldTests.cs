using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class TextFieldTests
{
    private sealed class Model { public string Name { get; set; } = ""; }

    private const string Html = "<body><div style='padding:20px'><cupri-textfield value=\"{{Name}}\"></cupri-textfield></div></body>";

    [Fact]
    public void Long_value_stays_one_line_and_scrolls_horizontally()
    {
        var model = new Model { Name = new string('x', 200) + " END" };
        using var t = new TestDoc(Html, "", model, width: 420, height: 220, components: true);
        RenderNode Field() => t.FindRole("textbox");
        RenderNode Text(RenderNode f) => TestDoc.Find(f, n => n.IsText && n.Lines is { Count: > 0 })!;

        var field = Field();
        Assert.Single(Text(field).Lines!);                   // single visual line (no wrap ballooning)
        Assert.True(field.Height < 60, $"H={field.Height}");

        t.ClickNode(field);
        t.Key(EditKey.End);
        Assert.True(Field().ScrollX > 10, $"ScrollX={Field().ScrollX}"); // caret at end scrolls into view

        t.Key(EditKey.Home);
        Assert.True(Field().ScrollX < 1, $"ScrollX={Field().ScrollX}");  // back to start

        t.Key(EditKey.SelectAll);
        Assert.Equal(model.Name, t.Doc.CopySelection());

        t.Type("a\nb\nc");                                    // pasted newlines flatten to spaces (one line)
        Assert.Single(Text(Field()).Lines!);
    }

    [Fact]
    public void Selection_is_wrap_aware_on_a_multiline_value()
    {
        // A textarea wraps; double-click on the 2nd visual row selects a word from THAT row.
        var words = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa";
        var model = new Notes { Text = words };
        const string html = "<body><div style='padding:20px'><cupri-textarea value=\"{{Text}}\" style='width:300px'></cupri-textarea></div></body>";
        using var t = new TestDoc(html, "", model, width: 400, height: 320, components: true);

        var area = t.FindRole("textbox");
        var anchor = TestDoc.Find(area, n => n.Element?.HasAttribute("data-caret-anchor") == true)!;
        // The textarea nests one line-div per logical line; its wrapped text node has >1 visual lines.
        var textNode = TestDoc.Find(anchor, n => n.IsText && n.Lines is { Count: > 1 });
        Assert.NotNull(textNode);

        var lines = textNode!.Lines!;
        var tb = HitTesting.AbsoluteBox(textNode);
        (float x, float y) InRow(int row, float frac) => (tb.X + lines[row].X + lines[row].Width * frac, tb.Y + lines[row].Y + lines[row].Height / 2f);

        var (r0x, r0y) = InRow(0, 0.05f);
        t.Click(r0x, r0y);                                    // focus
        var (r1x, r1y) = InRow(1, 0.02f);
        t.Click(r1x, r1y, 2);                                 // double-click a word on row 1
        var row1First = lines[1].Text.Trim().Split(' ')[0];
        var sel = t.Doc.CopySelection();
        Assert.False(string.IsNullOrEmpty(sel));
        Assert.Contains(sel!, lines[1].Text);                 // the selected word belongs to row 1
        Assert.NotEqual("alpha", sel);                        // not row 0's first word (the old bug)
    }

    private sealed class Notes { public string Text { get; set; } = ""; }
}
