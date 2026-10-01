using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>display: inline-flex</c> — a row that shrinks to its content.
///
/// <para>The engine could express "a row with its contents centred" (<c>flex</c>, which fills the
/// width) and "a box that shrinks to its content" (<c>inline-block</c>, whose contents align on the
/// text baseline), but not both at once — and both at once is a button, a badge, a chip, a tag, a
/// toolbar item and a status pill. <c>inline-flex</c> parsed and mapped to plain <c>flex</c>, so
/// asking for it got a full-width row.</para>
///
/// <para>CSS's <c>display</c> is two decisions in one name: an OUTER role (block or inline) and an
/// INNER one (flow, flex, grid). <c>inline-flex</c> changes only the outer, so this is a flag beside
/// the existing flex rather than a new DisplayType — 34 places compare against
/// <c>DisplayType.Flex</c>, and a new enum value would have had to be right in every one of them,
/// where missing one leaves a flex container that silently stops behaving like one.</para>
/// </summary>
public class InlineFlexTests(ITestOutputHelper output)
{
    private const string Css = """
        .chip { display:inline-flex; align-items:center; gap:8px; padding:10px 18px; background:#345; }
        .dot  { width:24px; height:24px; background:#f90; }
        .lbl  { font-size:15px; color:#fff; }
        """;

    private static TestDoc Two() => new(
        "<body><div class='chip'><div class='dot'></div><div class='lbl'>Shortcuts</div></div>" +
        "<div class='chip'><div class='lbl'>Demo</div></div></body>",
        Css, width: 700, height: 200);

    /// <summary>It shrinks to its contents rather than filling the row — the half <c>flex</c> could
    /// not do.</summary>
    [Fact]
    public void An_inline_flex_box_shrinks_to_its_content()
    {
        using var t = Two();
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        output.WriteLine(t.Doc.DumpTree(maxDepth: 3));

        var chip = t.FindClass("chip");
        Assert.True(chip.Width < 300, $"shrink-wrapped, not the full 700 — got {chip.Width}");
    }

    /// <summary>Two of them share a line instead of stacking. This is the property that makes a row
    /// of pills a row, and the one a block-level flex container silently takes away.</summary>
    [Fact]
    public void Two_inline_flex_boxes_share_a_line()
    {
        using var t = Two();
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }

        var first = t.FindClass("chip");
        var second = t.Find(n => n.Element?.ClassList.Contains("chip") == true && n != first)!;
        Assert.True(second.X >= first.X + first.Width - 1,
            $"the second starts after the first ends — first ends {first.X + first.Width}, second at {second.X}");
    }

    /// <summary>…and the contents are centred on each other, which is the half <c>inline-block</c>
    /// could not do. A 24px dot and an 18px label are different heights, so equal centres — not equal
    /// tops — is the claim.</summary>
    [Fact]
    public void Its_contents_are_centred_on_each_other()
    {
        using var t = Two();
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }

        var dot = t.FindClass("dot");
        var label = t.FindClass("lbl");
        Assert.Equal(dot.Y + dot.Height / 2f, label.Y + label.Height / 2f, 1.0);
    }

    /// <summary><c>gap</c> still applies — it is the same flex layout inside, and the point of the
    /// change is that only the OUTER role differs.</summary>
    [Fact]
    public void The_gap_between_its_children_is_honoured()
    {
        using var t = Two();
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }

        var dot = t.FindClass("dot");
        var label = t.FindClass("lbl");
        Assert.Equal(8f, label.X - (dot.X + dot.Width), 0.5);
    }

    /// <summary>Plain <c>flex</c> is untouched: still block-level, still full width. The whole design
    /// of this change is that the inner role is left alone, so the regression to guard against is the
    /// flag leaking onto containers that never asked for it.</summary>
    [Fact]
    public void A_plain_flex_container_still_fills_the_row()
    {
        using var t = new TestDoc(
            "<body><div class='row'><div class='lbl'>a</div></div></body>",
            ".row { display:flex; } .lbl { font-size:15px; }", width: 700, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        Assert.Equal(700f, t.FindClass("row").Width, 0.5);
    }

    /// <summary>A later <c>display:flex</c> genuinely undoes an earlier <c>inline-flex</c>. The outer
    /// role is ASSIGNED on every display declaration rather than or-ed in, or a box would stay inline
    /// for the rest of the cascade once anything had made it so.</summary>
    [Fact]
    public void A_later_display_declaration_resets_the_outer_role()
    {
        using var t = new TestDoc(
            "<body><div class='row override'><div class='lbl'>a</div></div></body>",
            ".row { display:inline-flex; } .override { display:flex; } .lbl { font-size:15px; }",
            width: 700, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        Assert.Equal(700f, t.FindClass("row").Width, 0.5);
    }

    /// <summary><c>inline-grid</c> is still BLOCK-level, and this pins that rather than leaving it
    /// to be discovered. It rides the same distinction in CSS, but the shrink-to-fit path needs an
    /// intrinsic width and <c>MaxContentWidth</c> has no track sizing for a grid — measured, an
    /// inline-grid came back the full width of its parent, so making it inline-level would have
    /// accepted the value and changed nothing. When grid learns intrinsic sizing this test is the
    /// one to flip.</summary>
    [Fact]
    public void Inline_grid_is_still_block_level_for_now()
    {
        using var t = new TestDoc(
            "<body><div class='g'><div class='lbl'>a</div></div></body>",
            ".g { display:inline-grid; padding:6px; background:#345; } .lbl { font-size:15px; }",
            width: 700, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        Assert.Equal(700f, t.FindClass("g").Width, 0.5);
    }

    /// <summary>
    /// ONE OF THEM SHRINKS TOO. A lone inline-level child used to skip the inline path entirely and
    /// lay out as a block, so a single chip came out the full width of its parent while two of them
    /// shrank correctly — a difference with no reason an author could see, and most of "my badge
    /// fills the row". Eighteen of the shipped controls were affected: a cupri-badge alone measured
    /// 600 wide and now measures 52.5, with its height unchanged.
    ///
    /// <para>Narrowed to ATOMIC boxes — inline-block and inline-flex. A lone TEXT child still takes
    /// the block path: routing it through the line changes white-space handling, line-box heights
    /// and measurement across the engine, which 24 tests say in chorus.</para>
    /// </summary>
    [Fact]
    public void A_lone_inline_flex_box_shrinks_as_well()
    {
        using var t = new TestDoc(
            "<body><div class='chip'><div class='lbl'>Demo</div></div></body>", Css,
            width: 700, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        output.WriteLine(t.Doc.DumpTree(maxDepth: 3));
        Assert.True(t.FindClass("chip").Width < 300,
            $"one on its own shrinks like two do — got {t.FindClass("chip").Width}");
    }

    /// <summary>A lone inline-BLOCK shrinks for the same reason, which is the half of this that was
    /// wrong before inline-flex existed at all.</summary>
    [Fact]
    public void A_lone_inline_block_shrinks_as_well()
    {
        using var t = new TestDoc(
            "<body><div class='pill'>Demo</div></body>",
            ".pill { display:inline-block; padding:8px 14px; background:#345; font-size:15px; }",
            width: 700, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        Assert.True(t.FindClass("pill").Width < 300, "a lone inline-block shrinks to its content");
    }

    /// <summary>A lone TEXT child is untouched — it keeps the block path, and this pins the boundary
    /// of the change rather than leaving it to be rediscovered by whoever widens it next.</summary>
    [Fact]
    public void A_lone_text_child_is_unaffected()
    {
        using var t = new TestDoc("<body><div class='t'>hello</div></body>",
                                  ".t { font-size:15px; }", width: 700, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(700, 200)) { }
        Assert.Equal(700f, t.FindClass("t").Width, 0.5);
    }
}
