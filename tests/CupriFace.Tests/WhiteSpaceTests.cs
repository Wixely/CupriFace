using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>white-space: pre | pre-wrap | pre-line</c> and the no-break space (issue #69). Newlines in
/// bound text used to collapse unconditionally and all three preserved modes laid out identically
/// to none, so a multi-line value — an agent reply with paragraphs and code — rendered as one
/// run-on line. And   was treated as collapsible whitespace (.NET's IsWhiteSpace says yes,
/// CSS says no), so the classic keep-this-line-tall workaround measured zero.
/// </summary>
public class WhiteSpaceTests
{
    private sealed class Model { public string Text { get; set; } = ""; }

    private static RenderNode TextNode(CupriDocument doc)
    {
        RenderNode? hit = null;
        void W(RenderNode n) { if (hit is null && n.IsText) hit = n; foreach (var c in n.Children) W(c); }
        W(doc.Root);
        return hit!;
    }

    private static RenderNode Bind(string css, string value, out CupriDocument doc)
    {
        doc = CupriDocument.Load(
            "<body><div class='box'><span class='t'>{{Text}}</span></div></body>",
            "body { margin:0 } .box { width:400px } " + css);
        doc.Bind(new Model { Text = value });
        doc.BuildFrame(500, 400);
        return TextNode(doc);
    }

    [Theory]
    // The issue's exact table: three bound lines in a 400px box. One line without a rule (the
    // CSS-correct default, deliberately unchanged), three lines under every preserved mode.
    [InlineData("", 1)]
    [InlineData(".t { white-space: pre-wrap }", 3)]
    [InlineData(".t { white-space: pre-line }", 3)]
    [InlineData(".t { white-space: pre }", 3)]
    public void Bound_newlines_follow_the_white_space_property(string css, int expectedLines)
    {
        var text = Bind(css, "line one\nline two\nline three", out var doc);
        using (doc)
        {
            Assert.Equal(expectedLines, text.Lines!.Count);
            Assert.Equal(expectedLines * text.Lines[0].Height, text.TextH, 1);
        }
    }

    [Fact]
    public void A_blank_line_keeps_its_height()
    {
        var text = Bind(".t { white-space: pre-wrap }", "a\n\nb", out var doc);
        using (doc)
        {
            Assert.Equal(3, text.Lines!.Count);
            Assert.Equal("", text.Lines[1].Text);            // the empty middle line is real…
            Assert.Equal(text.Lines[0].Height, text.Lines[1].Height); // …and full height
        }
    }

    [Fact]
    public void Pre_wrap_still_wraps_a_long_segment()
    {
        var long1 = string.Join(' ', Enumerable.Repeat("word", 40));
        var text = Bind(".t { white-space: pre-wrap }", long1 + "\nshort", out var doc);
        using (doc)
        {
            Assert.True(text.Lines!.Count > 3, "the long first segment should wrap onto several lines");
            Assert.Equal("short", text.Lines[^1].Text);
            // Spaces at a wrap point HANG off the line end (CSS: they may poke past the edge —
            // they are content in pre-wrap, not separators), so each line's measured width may
            // exceed the box by up to one space. The CONTENT must fit.
            Assert.All(text.Lines, l => Assert.True(
                l.Width <= 400.5f || l.Text.EndsWith(' '),
                $"a line overflowed by more than its hanging space: {l.Width:F0}px '{l.Text}'"));
        }
    }

    [Fact]
    public void Pre_never_wraps_and_keeps_spaces_verbatim()
    {
        var text = Bind(".t { white-space: pre }", "alpha   beta\n    indented", out var doc);
        using (doc)
        {
            Assert.Equal(2, text.Lines!.Count);
            Assert.Equal("alpha   beta", text.Lines[0].Text);   // triple space intact
            Assert.Equal("    indented", text.Lines[1].Text);   // leading indentation intact
        }

        // And a line longer than the box overflows rather than wrapping.
        var wide = Bind(".t { white-space: pre }", string.Join(' ', Enumerable.Repeat("word", 40)), out var doc2);
        using (doc2)
        {
            Assert.Single(wide.Lines!);
            Assert.True(wide.Lines![0].Width > 400f, "pre must overflow, not wrap");
        }
    }

    [Fact]
    public void Pre_wrap_preserves_indentation_across_the_break()
    {
        // Two spaces of indentation at a segment start survive: the wrapped code-block case.
        var text = Bind(".t { white-space: pre-wrap }", "  indented", out var doc);
        using (doc)
        {
            Assert.Single(text.Lines!);
            Assert.Equal("  indented", text.Lines[0].Text);
        }
    }

    [Fact]
    public void Pre_line_collapses_spaces_but_keeps_newlines()
    {
        var text = Bind(".t { white-space: pre-line }", "x   y\nz", out var doc);
        using (doc)
        {
            Assert.Equal(2, text.Lines!.Count);
            Assert.Equal("x y", text.Lines[0].Text);
            Assert.Equal("z", text.Lines[1].Text);
        }
    }

    [Fact]
    public void A_no_break_space_occupies_space()
    {
        // An element whose only content is &nbsp; used to lay out at height 0 — the text node was
        // swallowed as a whitespace separator. CSS does not count   as collapsible.
        using var doc = CupriDocument.Load(
            "<body><div class='n'>&nbsp;</div><div class='after'>x</div></body>",
            "body { margin:0 } .n { width:100px }");
        doc.BuildFrame(300, 200);

        var text = TextNode(doc);
        Assert.Single(text.Lines!);
        Assert.True(text.Lines![0].Width > 0, "the nbsp must measure wider than nothing");
        Assert.True(text.TextH > 10, "the nbsp line must keep line height");
    }

    [Fact]
    public void No_break_spaces_between_words_are_not_collapsed()
    {
        using var one = CupriDocument.Load("<body><div class='t'>a&nbsp;&nbsp;&nbsp;b</div></body>", "body{margin:0}");
        one.BuildFrame(300, 100);
        using var two = CupriDocument.Load("<body><div class='t'>a b</div></body>", "body{margin:0}");
        two.BuildFrame(300, 100);

        Assert.True(TextNode(one).Lines![0].Width > TextNode(two).Lines![0].Width + 1,
            "three nbsp must be wider than one collapsed space");
    }

    [Fact]
    public void The_emergency_break_works_inside_a_preserved_segment()
    {
        // #59's overflow-wrap inside #69's pre-wrap: an unbreakable token in a multi-line value.
        var address = new string('x', 80);
        var text = Bind(".t { white-space: pre-wrap; overflow-wrap: break-word }", "before\n" + address, out var doc);
        using (doc)
        {
            Assert.True(text.Lines!.Count > 2, "the long token should split across lines");
            Assert.All(text.Lines, l => Assert.True(l.Width <= 400.5f, "no line may overflow the box"));
            Assert.Equal("before" + address, string.Concat(text.Lines.Select(l => l.Text)));
        }
    }

    [Fact]
    public void Markup_indentation_still_collapses_in_normal_flow()
    {
        // The other half of the contract: source formatting around elements keeps collapsing —
        // the change is scoped to the preserved modes and the nbsp, not to ordinary documents.
        using var doc = CupriDocument.Load(
            "<body>\n  <div class='a'>\n    hello   world\n  </div>\n</body>", "body{margin:0}");
        doc.BuildFrame(300, 100);

        var text = TextNode(doc);
        Assert.Single(text.Lines!);
        Assert.Equal("hello world", text.Lines![0].Text);
    }
}
