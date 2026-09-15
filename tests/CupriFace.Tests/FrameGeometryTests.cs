using CupriFace.Demo;
using CupriFace.Paint;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// No frame may contain a rectangle that is not a number.
///
/// <para>This is not hypothetical. An inline <c>&lt;code&gt;</c> chip lays out as a 0x0 box with
/// padding, which gives it a NEGATIVE content height — so "content taller than the box" was
/// arithmetically true of an element with nothing in it, and the scrollbar code accepted it as a
/// scroll container. Its thumb height then divided by a zero content height and came out infinite,
/// and its position multiplied zero by that infinity and came out NaN.</para>
///
/// <para>Nothing failed. The bar was never seen, because a rectangle at NaN is nowhere. What it did
/// instead was quieter: the Showcase's gradient swatches, further down the same page, painted
/// visibly washed out — a non-finite rectangle in the middle of a display list disturbs what is
/// composited after it. Five percent of the page was wrong for as long as that arithmetic existed,
/// and it was found by differencing screenshots over an unrelated change.</para>
///
/// <para>So the rule is checked directly, on real pages, rather than trusted to the arithmetic that
/// produced it.</para>
/// </summary>
public class FrameGeometryTests(ITestOutputHelper output)
{
    private static IEnumerable<(string What, float[] Values)> Geometry(DisplayList list)
    {
        foreach (var cmd in list.Commands)
            switch (cmd)
            {
                case FillRect r: yield return ("FillRect", [r.X, r.Y, r.W, r.H]); break;
                case GradientRect r: yield return ("GradientRect", [r.X, r.Y, r.W, r.H]); break;
                case BorderRect r: yield return ("BorderRect", [r.X, r.Y, r.W, r.H]); break;
                case PushClip c: yield return ("PushClip", [c.X, c.Y, c.W, c.H]); break;
                case TextRun t: yield return ("TextRun", [t.X, t.Y]); break;
            }
    }

    /// <summary>Every page of the Showcase, because the one that had the bug was not the one anybody
    /// would have thought to check.</summary>
    [Theory]
    [InlineData("styling")]
    [InlineData("controls")]
    [InlineData("components")]
    [InlineData("layout")]
    [InlineData("charts")]
    public void No_command_in_a_frame_has_non_finite_geometry(string section)
    {
        var app = new ShowcaseApp(section);
        using var doc = app.CreateDocument();
        doc.Refresh();
        doc.Settle(940, 720, TimeSpan.FromSeconds(10));
        using (doc.RenderToImage(940, 720)) { }

        var bad = new List<string>();
        var n = 0;
        foreach (var (what, values) in Geometry(doc.BuildFrame(940, 720)))
        {
            n++;
            foreach (var v in values)
                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    bad.Add($"{what}({string.Join(", ", values)})");
                    break;
                }
        }

        output.WriteLine($"{section}: {n} geometric commands, {bad.Count} non-finite");
        Assert.True(bad.Count == 0, $"{section} painted non-finite geometry: {string.Join("; ", bad.Take(5))}");
    }

    /// <summary>The specific shape that caused it: an inline chip, which lays out with no box and
    /// some padding. It must not be treated as something that scrolls.</summary>
    [Fact]
    public void An_inline_chip_with_no_box_is_not_a_scroll_container()
    {
        using var t = new TestDoc(
            "<body><p>text with <code>a chip</code> in it</p></body>",
            "body{margin:0;font-family:sans-serif}code{background:#eee;padding:2px 5px;border-radius:5px}",
            width: 400, height: 200);

        var chip = t.Find(n => n.Element?.LocalName == "code")!;
        output.WriteLine($"chip {chip.Width:0}x{chip.Height:0} contentH={chip.ContentBoxHeight:0.0} "
                         + $"max={chip.MaxScrollY:0.0} bar={CupriFace.Interaction.Scrollbar.Applies(chip)}");

        Assert.False(CupriFace.Interaction.Scrollbar.Applies(chip), "an inline chip gets no scrollbar");
    }
}
