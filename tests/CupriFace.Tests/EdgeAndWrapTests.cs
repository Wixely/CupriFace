using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// CF0077, a rounded box flush against the edge of what holds it (or the screen), and CF0078,
/// wrapped flex lines with nothing between them. Each pair of tests is a case that must report and
/// its innocent twin that must not — the twin being the design the mistake looks like.
/// </summary>
public class EdgeAndWrapTests(ITestOutputHelper output)
{
    private static DoctorReport Check(string html, string css, int w = 400, int h = 300)
        => CupriDoctor.Check(html, "body{margin:0;font-family:sans-serif} " + css, width: w, height: h);

    // The container fixtures sit in a page WITH a gutter, so the only edge in question is the
    // card's. Without one, the rounded card is itself flush against the screen — which is CF0077
    // too, and the first thing this suite caught in its own fixtures.
    private static string InPage(string inner) => $"<body><div class='page'>{inner}</div></body>";
    private const string Page = ".page{padding:20px} ";

    // ---- CF0077: against the container ----------------------------------------------------------

    [Fact]
    public void A_rounded_button_jammed_against_the_side_of_a_card_is_reported()
    {
        var report = Check(
            InPage("<div class='card'><div class='btn'>Save</div></div>"),
            Page + ".card{background:#fff;border-radius:12px;padding:16px 0;display:flex;justify-content:flex-end}"
            + " .btn{background:#B87333;border-radius:8px;padding:8px 16px}");
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0077");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("<div class='btn'>", f.Message);
        Assert.Contains("right-hand edge", f.Message);
        Assert.Contains("<div class='card'>", f.Message);
        Assert.Contains("padding", f.Fix);
    }

    [Fact]
    public void The_same_button_with_padding_between_it_and_the_edge_is_not()
    {
        var report = Check(
            InPage("<div class='card'><div class='btn'>Save</div></div>"),
            Page + ".card{background:#fff;border-radius:12px;padding:16px;display:flex;justify-content:flex-end}"
            + " .btn{background:#B87333;border-radius:8px;padding:8px 16px}");
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0077");
    }

    /// <summary>The construction this must not mistake for the mistake: a header given the card's own
    /// radius, filling its top corners so the two curves coincide.</summary>
    [Fact]
    public void A_header_that_fills_the_cards_corners_with_the_cards_radius_is_not()
    {
        var report = Check(
            "<body><div class='page'><div class='card'><div class='head'>Title</div><div class='body'>Body</div></div></div></body>",
            ".page{padding:20px} .card{background:#fff;border-radius:12px}"
            + " .head{background:#eef1f5;border-radius:12px 12px 0 0;padding:10px} .body{padding:10px}");
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0077");
    }

    /// <summary>…but the same header with a SMALLER radius than the card's is the collision: its
    /// curve pulls away from the card's inside the corner.</summary>
    [Fact]
    public void A_header_whose_radius_does_not_match_the_cards_is_reported()
    {
        var report = Check(
            "<body><div class='page'><div class='card'><div class='head'>Title</div></div></div></body>",
            ".page{padding:20px} .card{background:#fff;border-radius:16px}"
            + " .head{background:#eef1f5;border-radius:6px;padding:10px}");
        output.WriteLine(report.ToString());
        Assert.Contains(report.Findings, x => x.Code == "CF0077" && x.Message.Contains("<div class='head'>"));
    }

    [Fact]
    public void A_square_box_flush_against_its_container_is_not()
    {
        var report = Check(
            InPage("<div class='card'><div class='strip'>x</div></div>"),
            Page + ".card{background:#fff;border-radius:12px} .strip{background:#eef1f5}");
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0077");
    }

    /// <summary>Flush with a transparent wrapper is not touching anything anyone can see; the edge
    /// that counts is the nearest container that paints.</summary>
    [Fact]
    public void Flush_against_a_transparent_wrapper_inside_a_padded_card_is_not()
    {
        var report = Check(
            InPage("<div class='card'><div class='wrap'><div class='btn'>Go</div></div></div>"),
            Page + ".card{background:#fff;border-radius:12px;padding:16px} .wrap{display:flex}"
            + " .btn{background:#B87333;border-radius:8px;padding:8px 16px}");
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0077");
    }

    // ---- CF0077: against the screen ------------------------------------------------------------

    [Fact]
    public void A_rounded_card_with_no_page_gutter_on_a_phone_is_reported()
    {
        var report = Check(
            "<body><div class='card'>Card</div></body>",
            ".card{background:#334;color:#fff;border-radius:12px;padding:16px;margin-top:20px}", 412, 600);
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0077");
        Assert.Contains("left-hand and right-hand edges", f.Message);       // its top has a margin
        Assert.Contains("412px viewport", f.Message);
        Assert.Contains("gutter", f.Fix);
    }

    /// <summary>The design it must not catch: a bottom sheet, rounded only on top, running edge to
    /// edge on purpose. On its left and right sides only one corner is rounded.</summary>
    [Fact]
    public void A_bottom_sheet_rounded_only_on_top_is_not()
    {
        var report = Check(
            "<body><div class='page'>Page</div><div class='sheet'>Sheet</div></body>",
            ".page{padding:20px} .sheet{position:fixed;left:0;right:0;bottom:0;height:200px;background:#fff;"
            + "border-radius:16px 16px 0 0}", 412, 600);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0077");
    }

    /// <summary>Below the viewport is where a page keeps the rest of itself, so an in-flow box at
    /// the bottom edge is not touching anything. A fixed one is.</summary>
    [Fact]
    public void Only_a_fixed_box_can_touch_the_bottom_of_the_screen()
    {
        var inflow = Check(
            "<body><div class='spacer'></div><div class='card'>c</div></body>",
            ".spacer{height:260px} .card{margin:0 20px;height:40px;background:#334;border-radius:12px}", 400, 300);
        Assert.DoesNotContain(inflow.Findings, x => x.Code == "CF0077");

        var fixedBox = Check(
            "<body><div class='bar'>bar</div></body>",
            ".bar{position:fixed;left:20px;right:20px;bottom:0;height:40px;background:#334;border-radius:12px}", 400, 300);
        output.WriteLine(fixedBox.ToString());
        Assert.Contains(fixedBox.Findings, x => x.Code == "CF0077" && x.Message.Contains("bottom edge"));
    }

    [Fact]
    public void The_viewport_sweep_says_the_gutter_is_missing_only_on_the_phone()
    {
        // A centred card capped at 600px: a desktop has room either side, a phone does not.
        var report = CupriDoctor.Check(
            "<body><div class='card'>Card</div></body>",
            "body{margin:0} .card{max-width:600px;margin:20px auto 0;background:#334;color:#fff;border-radius:12px;padding:16px}",
            CupriDoctor.DesktopAndPhone);
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0077");
        Assert.Equal("412x915", f.Viewports);
    }

    [Fact]
    public void A_faded_or_transformed_box_is_not()
    {
        var css = Page + ".card{background:#fff;border-radius:12px;padding:16px 0;display:flex;justify-content:flex-end}"
                + " .btn{background:#B87333;border-radius:8px;padding:8px 16px}";
        var html = InPage("<div class='card'><div class='btn'>Save</div></div>");
        Assert.DoesNotContain(Check(html, css + " .btn{opacity:0}").Findings, x => x.Code == "CF0077");
        Assert.DoesNotContain(Check(html, css + " .btn{transform:translateX(-8px)}").Findings, x => x.Code == "CF0077");
    }

    // ---- CF0078: wrapped lines -----------------------------------------------------------------

    private const string Chips =
        "<body><div class='chips'><div class='chip'>Alpha</div><div class='chip'>Beta</div><div class='chip'>Gamma</div>"
        + "<div class='chip'>Delta</div><div class='chip'>Epsilon</div><div class='chip'>Zeta</div></div></body>";
    private const string ChipCss = ".chips{display:flex;flex-wrap:wrap;padding:12px} .chip{background:#eef1f5;padding:6px 12px}";

    [Fact]
    public void A_wrapping_row_with_a_column_gap_but_no_row_gap_is_a_warning()
    {
        var report = Check(Chips, ChipCss + " .chips{column-gap:8px}", 260, 300);
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0078");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("<div class='chips'>", f.Message);
        Assert.Contains("column-gap: 8px but no row-gap", f.Message);
        Assert.Contains("row-gap: 8px", f.Fix);
    }

    [Fact]
    public void A_wrapping_row_with_no_gap_at_all_is_info()
    {
        var report = Check(Chips, ChipCss, 260, 300);
        var f = Assert.Single(report.Findings, x => x.Code == "CF0078");
        Assert.Equal(Severity.Info, f.Severity);
    }

    [Theory]
    [InlineData(" .chips{gap:8px}")]                 // the shorthand sets both
    [InlineData(" .chips{column-gap:8px;row-gap:8px}")]
    [InlineData(" .chip{margin-bottom:6px}")]          // spaced by the items instead
    public void Lines_with_room_between_them_are_not(string css)
    {
        var report = Check(Chips, ChipCss + css, 260, 300);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0078");
    }

    /// <summary>The whole point of the check: the row only touches once it wraps, so the desktop
    /// is clean and the phone is not — which the viewport sweep reports as such.</summary>
    [Fact]
    public void A_row_that_only_wraps_on_the_phone_is_reported_only_there()
    {
        var report = CupriDoctor.Check(Chips, "body{margin:0;font-family:sans-serif} " + ChipCss + " .chips{column-gap:8px}",
                                       [(1024, 768), (260, 600)]);
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0078");
        Assert.Equal("260x600", f.Viewports);
    }

    [Fact]
    public void Transparent_items_touching_across_lines_are_not()
    {
        var report = Check(Chips, ".chips{display:flex;flex-wrap:wrap;column-gap:8px} .chip{padding:6px 12px}", 260, 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0078");
    }

    [Fact]
    public void A_row_that_does_not_wrap_is_not()
    {
        var report = Check(Chips, ChipCss + " .chips{column-gap:8px}", 1000, 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0078");
    }

    [Fact]
    public void One_finding_per_row_however_many_seams()
    {
        var many = "<body><div class='chips'>" + string.Concat(Enumerable.Range(0, 24).Select(i => $"<div class='chip'>Item {i}</div>")) + "</div></body>";
        var report = Check(many, ChipCss + " .chips{column-gap:8px}", 260, 900);
        Assert.Single(report.Findings, x => x.Code == "CF0078");
    }
}
