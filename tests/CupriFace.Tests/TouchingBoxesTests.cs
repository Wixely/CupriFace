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

    // ---- #296: the box is not painted where it was laid out ------------------------------------
    // Two false positives found by running this check over 172 independent documents, which is 10x
    // the corpus it shipped validated against. Both documents are correct, and correct BECAUSE of
    // something that happens after layout — which is the whole class of defect a check that reasons
    // about layout boxes is exposed to.

    [Fact]
    public void A_stack_of_faded_out_cues_is_silent()
    {
        // vfx-magnetic: four subtitle cues share one bar, stacked flush, every one `opacity: 0` at
        // rest and faded in one at a time by a timeline. No viewer ever sees two of them, let alone
        // their corners colliding — flush stacking is exactly right for cues exclusive in time.
        const string html = "<body><div class='bar'>"
            + "<div class='subtitle'>One</div><div class='subtitle'>Two</div><div class='subtitle'>Three</div>"
            + "</div></body>";
        const string css = "body{margin:0} .subtitle{background:rgba(0,0,0,0.7);padding:12px 32px;"
            + "border-radius:8px;opacity:0}";
        Assert.Empty(Check(html, css, 900, 400));
    }

    [Fact]
    public void A_faded_ancestor_hides_its_children_too()
    {
        // Opacity reaches the screen through the ancestors: a faded parent hides a fully opaque
        // child, so the exclusion has to walk up rather than read one element.
        const string html = "<body><div class='group'>"
            + "<div class='chip'>One</div><div class='chip'>Two</div></div></body>";
        const string css = "body{margin:0} .group{opacity:0} .chip{background:#223;border-radius:8px;padding:12px}";
        Assert.Empty(Check(html, css, 900, 400));
    }

    [Fact]
    public void A_face_turned_edge_on_is_silent()
    {
        // ui-3d-reveal: a 3D card's depth face, placed at exactly the card's width — which is what
        // makes it flush — and then turned 90 degrees about its own left edge. Flush is not
        // incidental here, it is the construction: a depth face that did NOT meet the front face
        // would be a visible crack in the solid.
        const string html = "<body><div class='stage'><div class='ui-card'></div>"
            + "<div class='depth-right'></div></div></body>";
        const string css = "body{margin:0} .stage{position:relative}"
            + ".ui-card{width:300px;height:200px;background:#223;border-radius:12px}"
            + ".depth-right{position:absolute;top:12px;left:300px;width:10px;height:176px;"
            + "background:#114;border-radius:4px;transform-origin:0% 50%;transform:rotateY(90deg)}";
        Assert.Empty(Check(html, css, 900, 400));
    }

    [Fact]
    public void A_transform_on_a_shared_ancestor_still_reports()
    {
        // The exclusion is about a box painted away from its own layout box. A transform on a
        // shared ancestor moves both peers together and leaves the seam between them exactly as it
        // was — so it must NOT silence the check, or one `transform` on a page would disable it.
        const string html = "<body><div class='stage'><div class='a'>One</div><div class='b'>Two</div></div></body>";
        const string css = "body{margin:0} .stage{transform:translateY(10px)}"
            + ".a{background:#223;border-radius:12px;padding:12px} .b{background:#334;border-radius:12px;padding:12px}";
        Assert.Single(Check(html, css, 400, 300));
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
