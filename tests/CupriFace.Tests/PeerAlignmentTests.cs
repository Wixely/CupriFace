using CupriFace.Diagnostics;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// CF0073: repeated controls in a column that are not the same size.
///
/// Reported from a real Connections page — three rows, each ending in a button, reading "Connect",
/// "Configure", "Connect". The middle one is 12px wider because its label is longer, so its left
/// edge juts out of a column that is otherwise flush. Every box is exactly the size it asked to be;
/// nothing overflows, nothing clips, every binding resolves, and every automatic check said the
/// document was fine. The set is wrong, not any element in it.
///
/// The hard half of this check is silence. In that same screenshot the status labels beside the
/// buttons are 88px, 250px and 103px wide — ragged by 162px and completely correct — so "sizes
/// disagree" cannot be the rule. Two things narrow it: a peer must be a CONTROL (the role a screen
/// reader would read), and the peers must sit in SIBLING ROWS, which is what makes a column people
/// expect to line up. A stack of pills, chat bubbles or sidebar links shares one parent, is
/// shrink-wrapped by nature, and must stay silent.
/// </summary>
public class PeerAlignmentTests
{
    private const string Css = """
        body{margin:0;background:#0b0f14}
        .row{display:flex;align-items:center;gap:16px;padding:18px;background:#121820}
        .meta{flex:1}
        .status{color:#c8ff2e}
        """;

    /// <summary>The reported page: three rows, the middle button's label longer than the others.</summary>
    private static string Connections(string buttonCss = "", string middle = "Configure") =>
        "<body><div class='rows'>"
        + Row("Trakt", "Demo mode", "Connect")
        + Row("TMDB", "Local TMDB development snapshot", middle)
        // "Demo mode" twice on purpose: it gives the STATUS column a majority width too, so the
        // silence below is the controls-only rule doing its job rather than an accident of three
        // different lengths. (A mutation run caught that exact vacuity.)
        + Row("Real-Debrid", "Demo mode", "Connect")
        + "</div></body>"
        + (buttonCss.Length > 0 ? $"<style>{buttonCss}</style>" : "");

    private static string Row(string name, string status, string action) =>
        $"<div class='row'><div class='meta'>{name}</div><div class='status'>{status}</div>"
        + $"<cupri-button class='act'>{action}</cupri-button></div>";

    private static IReadOnlyList<Finding> Check(string html, string css = Css) =>
        [.. CupriDoctor.Check(html, css, width: 1480, height: 500).Findings.Where(f => f.Code == "CF0073")];

    // ---- the reported defect -------------------------------------------------------------------

    [Fact]
    public void The_odd_button_out_is_reported()
    {
        var finding = Assert.Single(Check(Connections()));
        Assert.Contains("Configure", finding.Message);     // names the label that did it…
        Assert.Contains("Connect", finding.Message);       // …and what the others say
        Assert.Contains("min-width", finding.Fix);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void It_says_which_edge_is_ragged()
    {
        // The buttons are pushed right by the flexed .meta, so their right edges align and the
        // LEFT edges are the ragged ones — which is what the eye caught in the screenshot.
        Assert.Contains("right edges line up", Assert.Single(Check(Connections())).Message);
    }

    [Fact]
    public void Pinning_the_width_silences_it()
    {
        // The whole point: the suggested fix must actually work.
        Assert.Empty(Check(Connections(buttonCss: ".act{min-width:120px}")));
    }

    [Fact]
    public void Equal_labels_say_nothing()
    {
        Assert.Empty(Check(Connections(middle: "Connect")));
    }

    [Fact]
    public void A_left_aligned_column_reports_the_other_edge()
    {
        // Same defect, mirrored: without the flexed spacer the buttons sit at the left, so their
        // left edges align and the right edges wander.
        var html = "<body><div class='rows'>"
            + "<div class='row'><cupri-button class='act'>Connect</cupri-button></div>"
            + "<div class='row'><cupri-button class='act'>Configure</cupri-button></div>"
            + "<div class='row'><cupri-button class='act'>Connect</cupri-button></div>"
            + "</div></body>";
        Assert.Contains("left edges line up", Assert.Single(Check(html)).Message);
    }

    // ---- the innocent twins, which must stay silent ---------------------------------------------

    [Fact]
    public void Ragged_text_in_the_same_rows_is_not_a_defect()
    {
        // The status labels from the very same screenshot, two of them matching and one 162px
        // longer — the exact shape that fires for the buttons beside them, and completely correct
        // here. If this ever reports, the check is worse than useless: every real finding would be
        // buried under one of these per text column.
        Assert.DoesNotContain(Check(Connections()), f => f.Message.Contains("status"));
    }

    [Fact]
    public void A_shrink_wrapped_stack_sharing_one_parent_is_silent()
    {
        // The innocent twin, and the reason the sibling-rows rule exists. Shrink-wrapped chips in
        // a column are ragged by nature and nobody has ever filed a bug about one.
        //
        // Two labels are deliberately IDENTICAL. Without that there are three different widths,
        // no majority, and the check declines for an unrelated reason — the test would pass while
        // proving nothing, which is exactly what it did before a mutation run caught it.
        var html = "<body><div class='tags'>"
            + "<div class='tag' role='button'>Open</div>"
            + "<div class='tag' role='button'>Open</div>"
            + "<div class='tag' role='button'>Open for review</div>"
            + "</div></body>";
        Assert.Empty(Check(html, Css + " .tags{display:flex;flex-direction:column;align-items:flex-start;gap:8px}"
                                + " .tag{background:#223;padding:6px 12px}"));
    }

    [Fact]
    public void But_the_same_chips_in_sibling_rows_are_reported()
    {
        // The control case for the test above: identical markup, identical widths, moved into one
        // row each. That single structural difference is the whole rule — so this pair is what
        // says the check is drawing the line where it claims to.
        var html = "<body><div class='rows'>"
            + "<div class='row'><div class='tag' role='button'>Open</div></div>"
            + "<div class='row'><div class='tag' role='button'>Open</div></div>"
            + "<div class='row'><div class='tag' role='button'>Open for review</div></div>"
            + "</div></body>";
        Assert.Single(Check(html, Css + " .tag{background:#223;padding:6px 12px}"));
    }

    [Fact]
    public void A_sidebar_of_links_is_silent()
    {
        // The case that would have made this check unusable in any app with a nav: every sidebar
        // has links of differing lengths. Quiet for two independent reasons here (one shared
        // parent, and no two labels the same width), which is the belt-and-braces this deserves.
        var html = "<body><div class='nav'>"
            + "<a href='#a'>Home</a><a href='#b'>Connections</a><a href='#c'>Settings</a>"
            + "</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .nav{display:flex;flex-direction:column;align-items:flex-start}"));
    }

    [Fact]
    public void Buttons_side_by_side_in_a_row_are_silent()
    {
        // A toolbar's buttons are as wide as their labels and nobody expects otherwise. Only a
        // COLUMN carries the expectation this check is about.
        var html = "<body><div class='bar'>"
            + "<cupri-button class='act'>Save</cupri-button>"
            + "<cupri-button class='act'>Cancel</cupri-button>"
            + "<cupri-button class='act'>Save and close</cupri-button>"
            + "</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .bar{display:flex;gap:8px}"));
    }

    [Fact]
    public void Two_buttons_are_not_enough_to_name_an_odd_one()
    {
        // A deliberate under-report: with two peers there is no majority, so no honest "the others
        // are 98px" and no width to suggest pinning to.
        var html = "<body><div class='rows'>"
            + "<div class='row'><cupri-button class='act'>Connect</cupri-button></div>"
            + "<div class='row'><cupri-button class='act'>Configure</cupri-button></div>"
            + "</div></body>";
        Assert.Empty(Check(html));
    }

    [Fact]
    public void An_explicit_width_is_taken_at_its_word()
    {
        // The author said what they wanted and got it. (Width, not min-width: a min-width that is
        // too small to hold the group together is still the bug.)
        Assert.Empty(Check(Connections(buttonCss: ".row:nth-child(2) .act{width:200px}")));
    }

    [Fact]
    public void Three_different_widths_say_nothing()
    {
        // No majority, so there is no "the others" to disagree with. Ragged, under-reported, and
        // documented as such.
        var html = "<body><div class='rows'>"
            + Row("A", "x", "Go") + Row("B", "y", "Configure") + Row("C", "z", "Disconnect now")
            + "</div></body>";
        Assert.Empty(Check(html));
    }

    [Fact]
    public void A_sub_pixel_difference_is_not_worth_anyone_s_time()
    {
        // Text measurement is not exact; a difference nobody can see must not be a finding.
        var html = "<body><div class='rows'>"
            + Row("A", "x", "Connect") + Row("B", "y", "Connecl") + Row("C", "z", "Connect")
            + "</div></body>";
        Assert.Empty(Check(html));
    }

    [Fact]
    public void A_control_s_own_insides_are_not_peers()
    {
        // Only the outermost control counts: a caller cannot restyle what <cupri-button> expands
        // into, so a finding in there would be this repository's bug and not theirs.
        Assert.DoesNotContain(Check(Connections()), f => f.Message.Contains("cupri-button-label"));
    }
}
