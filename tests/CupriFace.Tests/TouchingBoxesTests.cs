using CupriFace.Diagnostics;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// CF0074: two rounded, filled boxes sitting flush, with nothing between them.
///
/// Reported from a real page — a rounded card with a rounded "Back to details" button directly
/// beneath it and no margin anywhere, so the two curves collide and the pair reads as one broken
/// shape. A margin nobody set; every box exactly where it asked to be.
///
/// The whole check rests on one observation: FLUSH IS NOT EVIDENCE. Measured, the defect and the
/// most common innocent twin are identical — a card header above a card body is also two filled
/// boxes meeting at exactly 0px, and is correct. What separates them is the corners on the seam:
/// the header's bottom and the body's top are SQUARE, which is the author saying "these two are one
/// surface", while the card and the button are curved on both sides. So only the corners where the
/// two actually touch are examined, and both sides must be round.
/// </summary>
public class TouchingBoxesTests
{
    private static IReadOnlyList<Finding> Check(string html, string css, int w = 760, int h = 320) =>
        [.. CupriDoctor.Check(html, css, width: w, height: h).Findings.Where(f => f.Code == "CF0074")];

    // The reported page.
    private const string Screenshot =
        "<body><div class='card'><div class='kicker'>NATIVE PLAYBACK</div>"
        + "<div class='title'>Animation Of Webb's Orbit</div></div>"
        + "<cupri-button class='back ghost'>Back to details</cupri-button></body>";

    private static string ScreenshotCss(string extra = "") =>
        "body{margin:0;background:#0b0f14}"
        + ".card{background:#121820;border-radius:14px;padding:18px 24px}"
        + ".kicker{color:#c8ff2e;font-size:13px;font-weight:bold}"
        + ".title{color:#fff;font-size:28px;font-weight:bold}"
        + ".back{border-radius:10px}" + extra;

    [Fact]
    public void The_button_touching_the_card_is_reported()
    {
        var finding = Assert.Single(Check(Screenshot, ScreenshotCss()));
        Assert.Contains("flush directly below", finding.Message);
        Assert.Contains("margin-top", finding.Fix);
    }

    [Fact]
    public void It_is_info_rather_than_a_warning()
    {
        // The layout works; it reads badly. That is what Info is for, and this is the first check
        // in the tool to use it — deliberately, so the most subjective finding here is also the
        // easiest to ignore and cannot fail a gate that only counts warnings.
        Assert.Equal(Severity.Info, Assert.Single(Check(Screenshot, ScreenshotCss())).Severity);
    }

    [Fact]
    public void A_margin_silences_it()
    {
        Assert.Empty(Check(Screenshot, ScreenshotCss(".back{margin-top:12px}")));
    }

    [Fact]
    public void A_card_header_above_a_card_body_is_silent()
    {
        // THE test. Identical geometry to the defect — two filled boxes meeting at exactly 0px —
        // and completely correct, because the corners on the seam are square on both sides. If
        // this ever reports, the check has to go: the pattern is in every card ever built.
        const string html = "<body><div class='hdr'>Header</div><div class='bdy'>Body</div></body>";
        const string css = "body{margin:0}"
            + ".hdr{background:#223;border-radius:12px 12px 0 0;padding:12px}"
            + ".bdy{background:#334;border-radius:0 0 12px 12px;padding:12px}";
        Assert.Empty(Check(html, css, 400, 200));
    }

    [Fact]
    public void Square_boxes_flush_against_each_other_are_silent()
    {
        // Two square-edged surfaces meeting is a seam, not a collision — table rows, list items,
        // stacked panels. Nothing to report.
        const string html = "<body><div class='a'>One</div><div class='b'>Two</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .a{background:#223;padding:12px} .b{background:#334;padding:12px}", 400, 200));
    }

    [Fact]
    public void Overlapping_avatars_are_a_design_and_not_a_defect()
    {
        // Round boxes pulled together with a negative margin is a deliberate pattern. Only a gap
        // of NOTHING is reported: zero is the number that means "unset".
        const string html = "<body><div class='row'><div class='av'></div><div class='av2'></div></div></body>";
        const string css = "body{margin:0} .row{display:flex}"
            + ".av,.av2{width:40px;height:40px;border-radius:50%;background:#567}"
            + ".av2{margin-left:-14px}";
        Assert.Empty(Check(html, css, 400, 200));
    }

    [Fact]
    public void A_gap_somebody_chose_is_left_alone()
    {
        // One pixel is a number a person typed. Only zero is evidence of an unset property.
        Assert.Empty(Check(Screenshot, ScreenshotCss(".back{margin-top:1px}")));
    }

    [Fact]
    public void Rounded_but_transparent_boxes_are_silent()
    {
        // A radius nobody can see cannot collide with anything.
        const string html = "<body><div class='a'>One</div><div class='b'>Two</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .a,.b{border-radius:12px;padding:12px}", 400, 200));
    }

    [Fact]
    public void Side_by_side_counts_too()
    {
        // The same defect along the other axis, and it says so.
        const string html = "<body><div class='row'><div class='a'>One</div><div class='b'>Two</div></div></body>";
        const string css = "body{margin:0} .row{display:flex}"
            + ".a{background:#223;border-radius:12px;padding:12px} .b{background:#334;border-radius:12px;padding:12px}";
        Assert.Contains("flush directly beside", Assert.Single(Check(html, css, 400, 200)).Message);
    }

    [Fact]
    public void A_controls_own_insides_are_not_reported()
    {
        // A caller cannot restyle what a component expands into, so a finding in there would be
        // this repository's bug rather than theirs — the same sweep CF0050 and CF0090 apply.
        const string html = "<body><cupri-button><div class='a'>One</div><div class='b'>Two</div></cupri-button></body>";
        const string css = "body{margin:0} .a{background:#223;border-radius:9px} .b{background:#334;border-radius:9px}";
        Assert.Empty(Check(html, css, 400, 200));
    }

    [Fact]
    public void Diagonal_neighbours_are_not_touching()
    {
        // Flush on one axis but not overlapping on the other: they share a corner coordinate and
        // nothing else, so there is no seam to collide along.
        const string html = "<body><div class='a'>One</div><div class='b'>Two</div></body>";
        const string css = "body{margin:0} .a{position:absolute;left:0;top:0;width:60px;height:40px;"
            + "background:#223;border-radius:10px} .b{position:absolute;left:200px;top:40px;width:60px;"
            + "height:40px;background:#334;border-radius:10px}";
        Assert.Empty(Check(html, css, 400, 200));
    }
}
