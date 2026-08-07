using System.Collections.Generic;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Real inline layout: text and inline / inline-block elements flow into wrapping line boxes.</summary>
public class InlineLayoutTests
{
    // The first laid-out fragment (TextLine) whose text equals `text`, anywhere in the tree. All the
    // fragments of one block's inline content share that block's coordinate space, so their X/Y compare.
    private static TextLine Frag(TestDoc t, string text)
    {
        TextLine? found = null;
        void Walk(RenderNode n)
        {
            if (n.Lines is not null) foreach (var l in n.Lines) if (l.Text == text) found ??= l;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Root);
        Assert.NotNull(found);
        return found!;
    }

    private static int LineCount(TestDoc t)
    {
        var ys = new HashSet<int>();
        void Walk(RenderNode n)
        {
            if (n.Lines is not null) foreach (var l in n.Lines) if (l.Text.Length > 0) ys.Add((int)System.MathF.Round(l.Y));
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Root);
        return ys.Count;
    }

    [Fact]
    public void Text_and_inline_elements_flow_on_one_line_in_order()
    {
        using var t = new TestDoc(
            "<body><div style='width:400px'>alpha <b>bravo</b> charlie</div></body>",
            "", null, width: 500, height: 100);

        var a = Frag(t, "alpha");
        var b = Frag(t, "bravo");   // inside <b>, a nested text node
        var c = Frag(t, "charlie");

        Assert.Equal(a.Y, b.Y, 1);                     // one shared line
        Assert.Equal(b.Y, c.Y, 1);
        Assert.True(a.X < b.X && b.X < c.X);           // document order, left to right
        Assert.True(b.X > a.X + a.Width);              // a real space sits between "alpha" and "bravo"
    }

    [Fact]
    public void Inline_content_wraps_across_lines()
    {
        using var t = new TestDoc(
            "<body><div style='width:110px'>one <b>two</b> three four five six seven eight</div></body>",
            "", null, width: 200, height: 200);
        Assert.True(LineCount(t) >= 3, "narrow inline content should wrap to several lines");
    }

    [Fact]
    public void Whitespace_between_runs_is_kept_or_dropped_per_source()
    {
        using var spaced = new TestDoc("<body><div style='width:400px'>a <b>b</b></div></body>", "", null, width: 500, height: 100);
        using var tight = new TestDoc("<body><div style='width:400px'>a<b>b</b></div></body>", "", null, width: 500, height: 100);

        var gapS = Frag(spaced, "b").X - (Frag(spaced, "a").X + Frag(spaced, "a").Width);
        var gapT = Frag(tight, "b").X - (Frag(tight, "a").X + Frag(tight, "a").Width);
        Assert.True(gapS > 2, $"'a <b>b</b>' keeps the space; gap={gapS}");
        Assert.True(gapT < 2, $"'a<b>b</b>' has no space; gap={gapT}");
    }

    [Fact]
    public void Inline_block_shrinks_to_fit_and_sits_on_the_line()
    {
        using var t = new TestDoc(
            "<body><div style='width:400px'>x <span class='chip' style='display:inline-block;padding:2px 8px'>hi</span> y</div></body>",
            "", null, width: 500, height: 100);

        var chip = t.FindClass("chip");
        Assert.InRange(chip.Width, 10f, 90f);          // chip-sized, not the full 400px
        var x = Frag(t, "x");
        Assert.Equal(x.Y, chip.Y, 1);                  // on the same line as the text
        Assert.True(chip.X > x.X);
    }

    [Fact]
    public void A_lone_text_child_still_lays_out_normally()
    {
        // A single inline child keeps the original path — regression guard for text fields / plain text.
        using var t = new TestDoc("<body><div style='width:300px'>just some plain text</div></body>", "", null, width: 400, height: 100);
        Assert.Equal(1, LineCount(t));
        Assert.Equal("just some plain text", Frag(t, "just some plain text").Text);
    }
}
