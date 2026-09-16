using CupriFace.Diagnostics;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>line-height</c> in the units CSS allows (#181).
///
/// <para>A length was divided by a hardcoded 16 to fake a ratio — "refined once font-size is known",
/// and nothing ever refined it. So the line box came out font-size/16 times too tall: three times
/// over at 48px, exactly right at 16px, which is why it looked like a fixed factor to anyone
/// measuring at one size. The glyph sits at the bottom of that box, so text landed BELOW its own
/// container and everything after it was pushed down the page.</para>
///
/// <para>It was silent and the symptom appeared far from the cause — "the last few elements of my
/// layout have vanished off the frame", with nothing pointing at the declaration that did it. It
/// cost a day downstream and produced three plausible wrong conclusions on the way, including that
/// <c>overflow:hidden</c> does not clip a transformed child. It does.</para>
///
/// <para><c>em</c> and <c>%</c> were not recognised at all and fell back to the default with no
/// diagnostic, so a deliberate line-height did nothing.</para>
/// </summary>
public class LineHeightTests(ITestOutputHelper output)
{
    /// <summary>The height of the text node's own box — the number the issue tabulated.</summary>
    private static float TextHeight(string declaration, int fontSize = 48)
    {
        using var t = new TestDoc(
            $"<body><div class='b'>Ag</div></body>",
            "body{margin:0;font-family:sans-serif}"
            + $".b{{width:150px;height:120px;font-size:{fontSize}px;{declaration}}}",
            width: 400, height: 500);
        var text = t.Find(n => n.IsText)!;
        return text.Height;
    }

    /// <summary>The reported table, verbatim. 48px font throughout.</summary>
    [Theory]
    [InlineData("", 57.6f)]                       // 48 x 1.2, the initial ratio
    [InlineData("line-height:120px", 120f)]       // was 360
    [InlineData("line-height:60px", 60f)]         // was 180
    [InlineData("line-height:48px", 48f)]         // was 144
    [InlineData("line-height:24px", 24f)]         // was 72
    [InlineData("line-height:1.5", 72f)]          // 48 x 1.5, always correct
    [InlineData("line-height:2em", 96f)]          // was ignored -> 57.6
    [InlineData("line-height:150%", 72f)]         // was ignored -> 57.6
    [InlineData("line-height:normal", 57.6f)]
    public void The_line_box_is_the_height_that_was_asked_for(string declaration, float expected)
    {
        var got = TextHeight(declaration);
        output.WriteLine($"{declaration,-22} expected {expected:0.0}  got {got:0.0}");
        Assert.Equal(expected, got, 0.5);
    }

    /// <summary>
    /// The heart of it: a LENGTH does not scale with the font size. The old code hid behind a single
    /// test size, because 16px is the one font size at which dividing by 16 is correct.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(64)]
    public void A_px_line_height_is_the_same_box_at_any_font_size(int fontSize)
    {
        var got = TextHeight("line-height:40px", fontSize);
        output.WriteLine($"font-size {fontSize}px -> line box {got:0.0}");
        Assert.Equal(40f, got, 0.5);
    }

    /// <summary>…and a RATIO still scales with it, which is the whole difference between the two.</summary>
    [Theory]
    [InlineData(16, 32f)]
    [InlineData(48, 96f)]
    public void A_ratio_still_scales_with_the_font_size(int fontSize, float expected)
        => Assert.Equal(expected, TextHeight("line-height:2", fontSize), 0.5);

    /// <summary>The consequence that made it expensive: with a line-height equal to its container's
    /// height, the text belongs INSIDE the container. It used to sit entirely below it, which reads
    /// as an empty box and a stray line of text somewhere further down.</summary>
    [Fact]
    public void Text_stays_inside_a_box_whose_height_equals_its_line_height()
    {
        using var t = new TestDoc(
            "<body><div class='b'>Ag</div></body>",
            "body{margin:0;font-family:sans-serif}.b{width:150px;height:120px;font-size:48px;line-height:120px}",
            width: 400, height: 500);

        var box = t.FindClass("b");
        var text = t.Find(n => n.IsText)!;
        var boxBottom = box.Y + box.Height;
        var textBottom = text.Y + text.Height;

        output.WriteLine($"box {box.Y:0}..{boxBottom:0}   text {text.Y:0}..{textBottom:0}");
        Assert.True(textBottom <= boxBottom + 0.5f,
            $"the text should end inside its container: text ends at {textBottom:0}, box at {boxBottom:0}");
    }

    /// <summary>An odometer — a column of digits sliding inside a fixed-height window — is the shape
    /// this made impossible, so it is the shape that proves it works. Each cell is exactly the
    /// window's height, so cell N sits at exactly N windows down.</summary>
    [Fact]
    public void A_digit_column_lines_up_with_its_window()
    {
        using var t = new TestDoc(
            "<body><div class='win'><div class='col'>"
            + string.Concat(Enumerable.Range(0, 10).Select(d => $"<div class='cell'>{d}</div>"))
            + "</div></div></body>",
            "body{margin:0;font-family:sans-serif}"
            + ".win{width:40px;height:50px;overflow:hidden}"
            + ".col{display:block}"
            + ".cell{height:50px;line-height:50px;font-size:32px;text-align:center}",
            width: 200, height: 200);

        var cells = new List<RenderNode>();
        void Walk(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("cell") == true) cells.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Root);

        Assert.Equal(10, cells.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            output.WriteLine($"cell {i}: y={cells[i].Y:0.0} h={cells[i].Height:0.0}");
            Assert.Equal(50f, cells[i].Height, 0.5);
            Assert.Equal(i * 50f, cells[i].Y, 0.5);
        }
    }

    /// <summary>A unit the parser does not understand is REPORTED rather than quietly replaced with
    /// a number. Silence is what made the original bug cost a day.</summary>
    [Fact]
    public void An_unsupported_unit_is_reported_rather_than_guessed()
    {
        var report = CupriDoctor.Check("<div class='b'>x</div>", ".b{ line-height: 2rem; }");
        output.WriteLine(report.ToString());
        Assert.Contains(report.Findings, f => f.Code == "CF0050" && f.Message.Contains("line-height"));
    }

    /// <summary>A length inherits as a length: a child with a different font size keeps the box its
    /// parent set, which is what CSS does with a length.</summary>
    [Fact]
    public void A_px_line_height_inherits_as_a_length()
    {
        using var t = new TestDoc(
            "<body><div class='outer'><span class='inner'>x</span></div></body>",
            "body{margin:0;font-family:sans-serif}"
            + ".outer{font-size:16px;line-height:40px}.inner{font-size:32px}",
            width: 300, height: 200);

        var text = t.Find(n => n.IsText)!;
        output.WriteLine($"inherited line box {text.Height:0.0}");
        Assert.Equal(40f, text.Height, 0.5);
    }
}
