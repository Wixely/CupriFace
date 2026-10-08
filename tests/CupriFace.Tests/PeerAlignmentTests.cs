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
        // 200px rather than a number close to the natural width: the point is that the floor
        // equalises them, and a floor only just above one machine's measurement is not a floor on
        // another's. (Linux measures these buttons ~10px wider than Windows does.)
        Assert.Empty(Check(Connections(buttonCss: ".act{min-width:200px}")));
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
    public void A_difference_too_small_to_see_is_not_worth_anyone_s_time()
    {
        // 3px, just under the floor — and built from PADDING rather than from text, so the fixture
        // does not depend on which fonts this machine happens to have.
        //
        // It did at first, and CI caught it: with two labels differing by one letter, the Linux
        // runner measured "Connect" at 109px and "Connecl" at 107px and duly reported a 2px jut,
        // while Windows and macOS measured less than a pixel between them and stayed silent. That
        // is what raised the floor from 2px to 4px — a real measurement, not a preference.
        var html = "<body><div class='rows'>"
            + Row("A", "x", "Connect") + Row("B", "y", "Connect") + Row("C", "z", "Connect")
            + "</div></body>";
        Assert.Empty(Check(html, Css + " .row:nth-child(2) .act{padding-left:21px}"));
    }

    // ---- the same idea along the other axis: a ROW whose heights disagree ----------------------

    [Fact]
    public void A_card_that_is_taller_than_its_row_is_reported()
    {
        // The mirror of the reported defect. Three cards side by side, one with a description that
        // wrapped to an extra line, so its bottom edge drops below the others. Same principle:
        // peers must agree on their CROSS-axis size, and here that is the height.
        var html = "<body><div class='deck'>"
            + "<div class='card' role='button'>Trakt sync</div>"
            + "<div class='card' role='button'>TMDB sync</div>"
            + "<div class='card' role='button'>Real-Debrid cache checks and source resolution for every link</div>"
            + "</div></body>";
        var finding = Assert.Single(Check(html, "body{margin:0} .deck{display:flex;align-items:flex-start;gap:12px}"
                                          + " .card{width:180px;background:#223;padding:12px}"));
        Assert.Contains("tall", finding.Message);
        Assert.Contains("top edges line up", finding.Message);
        Assert.Contains("align-items: stretch", finding.Fix);
    }

    [Fact]
    public void A_row_the_flex_default_already_evens_out_is_silent()
    {
        // align-items:stretch is the default and gives every item the tallest one's height, so
        // there is nothing ragged to report. This is the common case, and it must cost nothing.
        var html = "<body><div class='deck'>"
            + "<div class='card' role='button'>Trakt sync</div>"
            + "<div class='card' role='button'>TMDB sync</div>"
            + "<div class='card' role='button'>Real-Debrid cache checks and source resolution for every link</div>"
            + "</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .deck{display:flex;gap:12px} .card{width:180px;background:#223;padding:12px}"));
    }

    [Fact]
    public void A_hand_rolled_bar_chart_is_silent()
    {
        // The false positive that would have made the row half unusable: bars in a chart are the
        // same class, side by side, and differ in height BY DEFINITION.
        //
        // Quiet for two independent reasons, and a mutation run corrected which one does the work:
        // the bars carry an explicit `height`, so the author-said-so rule excludes them before the
        // controls-only rule is ever consulted. That is the stronger guard for a chart, since a bar
        // is always explicitly sized. The role rule is the backstop, pinned by the test below.
        var html = "<body><div class='chart'>"
            + "<div class='bar'></div><div class='bar'></div><div class='bar'></div><div class='bar'></div>"
            + "</div></body>";
        var css = "body{margin:0} .chart{display:flex;align-items:flex-end;gap:6px;height:120px}"
            + " .bar{width:20px;background:#6c6}"
            + " .bar:nth-child(1){height:40px} .bar:nth-child(2){height:40px}"
            + " .bar:nth-child(3){height:90px} .bar:nth-child(4){height:40px}";
        Assert.Empty(Check(html, css));
    }

    [Fact]
    public void Ragged_text_blocks_in_a_row_are_not_a_defect()
    {
        // The row-axis twin of the status labels: three columns of prose, one of which wraps to an
        // extra line. Auto heights, so the explicit-size rule cannot help — the controls-only rule
        // is the only thing standing here, which is what makes this test worth having.
        var html = "<body><div class='cols'>"
            + "<div class='col'>Watchlist and history</div>"
            + "<div class='col'>Search and artwork</div>"
            + "<div class='col'>Account-authorized cache checks and source resolution</div>"
            + "</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .cols{display:flex;align-items:flex-start;gap:12px} .col{width:160px}"));
    }

    [Fact]
    public void Controls_of_differing_widths_in_a_row_stay_silent()
    {
        // Restated because it is the asymmetry that makes the whole check work: along the axis the
        // peers are stacked on, size is just content being different lengths. Only the cross axis
        // carries a line.
        var html = "<body><div class='bar'>"
            + "<cupri-button class='act'>Save</cupri-button>"
            + "<cupri-button class='act'>Cancel</cupri-button>"
            + "<cupri-button class='act'>Save and close</cupri-button>"
            + "</div></body>";
        Assert.Empty(Check(html, "body{margin:0} .bar{display:flex;gap:8px;align-items:center}"));
    }

    [Fact]
    public void A_control_s_own_insides_are_not_peers()
    {
        // Only the outermost control counts: a caller cannot restyle what <cupri-button> expands
        // into, so a finding in there would be this repository's bug and not theirs.
        Assert.DoesNotContain(Check(Connections()), f => f.Message.Contains("cupri-button-label"));
    }
}
