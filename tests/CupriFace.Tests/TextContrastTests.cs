using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>CF0090</c>: text too close in colour to what is behind it to read (WCAG AA).
///
/// <para>The quietest defect there is. The element lays out, the colours are valid CSS, nothing
/// throws, and the only symptom is that nobody can read the words. The case this was written from
/// is a button: white bold text on a lime accent, which measures 1.3:1 where text that size needs
/// 4.5:1.</para>
///
/// <para>Half of these tests are about staying SILENT. A contrast warning on a button that is
/// actually fine is how a check like this gets switched off wholesale, so anything it cannot
/// compute exactly — a gradient, an image, a partially transparent ancestor — it declines to
/// judge rather than guessing.</para>
/// </summary>
public class TextContrastTests(ITestOutputHelper output)
{
    private List<Finding> Check(string html, string css)
    {
        var report = CupriDoctor.Check(html, css, width: 400, height: 200);
        var hits = report.Findings.Where(f => f.Code == "CF0090").ToList();
        output.WriteLine(hits.Count == 0 ? "(quiet)" : string.Join("\n", hits));
        return hits;
    }

    private List<Finding> Button(string decl) =>
        Check("<body><div class='btn'>Find playable media</div></body>",
            "body{margin:0;background:#16233a} .btn{padding:14px 22px;" + decl + "}");

    // ---- the reported case ---------------------------------------------------------------------

    [Fact]
    public void White_bold_text_on_a_lime_accent_is_reported()
    {
        var f = Assert.Single(Button("background:#c8f135;color:#fff;font-size:17px;font-weight:700"));
        output.WriteLine(f.ToString());
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("1.3:1", f.Message);
        Assert.Contains("4.5:1", f.Message);
        Assert.Contains("#ffffff", f.Message);
        Assert.Contains("#c8f135", f.Message);
        // The fix names a colour that would actually work, not just "improve the contrast".
        Assert.Contains("#000000", f.Fix);
    }

    /// <summary>The same button with a readable foreground says nothing.</summary>
    [Theory]
    [InlineData("color:#16233a")]      // the page's own navy
    [InlineData("color:#000")]
    [InlineData("color:#333")]
    public void A_readable_foreground_is_not_reported(string color) =>
        Assert.Empty(Button("background:#c8f135;font-size:17px;font-weight:700;" + color));

    // ---- the thresholds -------------------------------------------------------------------------

    /// <summary>White on this grey is 3.4:1 — under AA for ordinary text, over it for large. The
    /// same two colours are therefore reported at 16px and accepted at 30px.</summary>
    [Fact]
    public void Large_text_is_held_to_the_looser_bar()
    {
        Assert.Single(Check("<body><div class='t'>Readable?</div></body>",
            "body{margin:0;background:#fff} .t{background:#8c8c8c;color:#fff;font-size:16px}"));
        Assert.Empty(Check("<body><div class='t'>Readable?</div></body>",
            "body{margin:0;background:#fff} .t{background:#8c8c8c;color:#fff;font-size:30px}"));
    }

    /// <summary>Bold counts as large from 18.66px, so a point either side of that changes the
    /// answer for the same colours.</summary>
    [Fact]
    public void Bold_reaches_the_looser_bar_sooner()
    {
        Assert.Single(Check("<body><div class='t'>Readable?</div></body>",
            "body{margin:0;background:#fff} .t{background:#8c8c8c;color:#fff;font-size:18px;font-weight:700}"));
        Assert.Empty(Check("<body><div class='t'>Readable?</div></body>",
            "body{margin:0;background:#fff} .t{background:#8c8c8c;color:#fff;font-size:19px;font-weight:700}"));
    }

    [Fact]
    public void Ordinary_body_copy_is_not_reported() =>
        Assert.Empty(Check("<body><p>The quick brown fox jumps over the lazy dog</p></body>",
            "body{margin:0;background:#fff;color:#15202b}"));

    /// <summary>The background is inherited through transparent ancestors, so text in a plain
    /// wrapper is still measured against the panel that actually paints.</summary>
    [Fact]
    public void A_transparent_wrapper_is_seen_through()
    {
        var f = Assert.Single(Check(
            "<body><div class='card'><div class='row'><span>Nearly invisible</span></div></div></body>",
            "body{margin:0;background:#fff} .card{background:#2b2b2b} .row{padding:8px} span{color:#3a3a3a;font-size:15px}"));
        Assert.Contains("#2b2b2b", f.Message);
    }

    /// <summary>A semi-transparent TEXT colour is measured as what the eye sees, composited onto
    /// its background rather than taken at face value.</summary>
    [Fact]
    public void Translucent_text_is_measured_composited()
    {
        // #000 at 10% over white is effectively #e6e6e6 — unreadable, though the declared colour is black.
        var f = Assert.Single(Check("<body><div class='t'>Faint</div></body>",
            "body{margin:0;background:#fff} .t{color:#0000001a;font-size:16px}"));
        output.WriteLine(f.ToString());
        Assert.DoesNotContain("#000000 on", f.Message);
    }

    // ---- where it stays silent -------------------------------------------------------------------

    /// <summary>Anything it cannot compute exactly, it declines to judge. Each of these has text
    /// that WOULD fail against a guessed background, and must still be quiet.</summary>
    [Theory]
    [InlineData("background-image:linear-gradient(90deg,#000,#fff);color:#888", "a gradient behind it")]
    [InlineData("background:#00000080;color:#777", "a semi-transparent panel")]
    [InlineData("opacity:0.5;background:#eee;color:#f4f4f4", "a faded ancestor")]
    public void What_cannot_be_computed_is_not_reported(string decl, string why)
    {
        output.WriteLine(why);
        Assert.Empty(Check("<body><div class='t'>Unknowable</div></body>",
            "body{margin:0;background:#fff} .t{font-size:16px;" + decl + "}"));
    }

    [Fact]
    public void Text_with_no_opaque_background_anywhere_is_not_reported() =>
        Assert.Empty(Check("<body><div class='t'>No background at all</div></body>", ".t{color:#eee;font-size:16px}"));

    [Theory]
    [InlineData("<div class='t' aria-hidden='true'>decoration</div>", "aria-hidden")]
    [InlineData("<div class='t' style='color:transparent'>invisible</div>", "transparent text")]
    [InlineData("<div class='t' hidden>hidden data</div>", "the hidden attribute")]
    [InlineData("<div class='t'>   </div>", "whitespace only")]
    public void Text_that_is_not_meant_to_be_read_is_not_reported(string markup, string why)
    {
        output.WriteLine(why);
        Assert.Empty(Check("<body>" + markup + "</body>",
            "body{margin:0;background:#fff} .t{color:#fafafa;font-size:16px}"));
    }

    /// <summary>One line per distinct colour pair, however many elements share it — a repeated row
    /// should not produce a repeated finding.</summary>
    [Fact]
    public void The_same_pair_is_reported_once()
    {
        var hits = Check(
            "<body><div class='t'>Same</div><div class='t'>Same</div><div class='t'>Same</div></body>",
            "body{margin:0;background:#fff} .t{background:#c8f135;color:#fff;font-size:16px}");
        Assert.Single(hits);
    }

    /// <summary>
    /// A control's own insides are not reported: a caller cannot restyle what
    /// <c>&lt;cupri-button&gt;</c> expands into, so a finding there is noise they cannot act on.
    /// The same sweep CF0050 already does for the component library's CSS.
    /// </summary>
    [Fact]
    public void A_components_own_internals_are_not_reported() =>
        Assert.Empty(Check("<body><div class='card'><cupri-button>Save</cupri-button></div></body>",
            ".card{display:flex;padding:12px;background:#fff}"));

    /// <summary>
    /// …but the number is on file, because it is this repository's own bug rather than a caller's:
    /// the stock primary button is white on the copper accent, which is under the AA bar for text
    /// that size. Pinned so that changing the accent is a deliberate act with this test to update.
    /// </summary>
    [Fact]
    public void The_stock_accent_button_is_below_AA_and_that_is_recorded()
    {
        var copper = SKColor.Parse("#b87333");
        var ratio = Contrast.Ratio(SKColors.White, copper);
        output.WriteLine($"white on the copper accent: {ratio:0.00}:1 (AA normal needs {Contrast.AaNormal})");
        Assert.InRange(ratio, 3.7, 3.9);
        Assert.False(Contrast.MeetsAa(SKColors.White, copper, 15f, 400));
        // It does clear the looser bar, so the same button with large text would be fine.
        Assert.True(Contrast.MeetsAa(SKColors.White, copper, 24f, 400));
    }

    // ---- the arithmetic itself -------------------------------------------------------------------

    /// <summary>The published WCAG anchors, so the helper is pinned to the standard rather than to
    /// this engine's output.</summary>
    [Fact]
    public void The_ratio_matches_the_published_values()
    {
        Assert.Equal(21.0, Contrast.Ratio(SKColors.Black, SKColors.White), 2);
        Assert.Equal(1.0, Contrast.Ratio(SKColors.White, SKColors.White), 3);
        Assert.Equal(Contrast.Ratio(SKColors.Black, SKColors.White),
                     Contrast.Ratio(SKColors.White, SKColors.Black), 6);          // order does not matter
        Assert.Equal(4.54, Contrast.Ratio(SKColors.White, SKColor.Parse("#767676")), 2);
        Assert.Equal(1.31, Contrast.Ratio(SKColors.White, SKColor.Parse("#c8f135")), 2);
    }

    [Theory]
    [InlineData(16f, 400, 4.5)]
    [InlineData(23.9f, 400, 4.5)]
    [InlineData(24f, 400, 3.0)]
    [InlineData(18f, 700, 4.5)]
    [InlineData(19f, 700, 3.0)]
    [InlineData(19f, 400, 4.5)]
    public void The_large_text_rule_is_size_and_weight(float size, int weight, double need) =>
        Assert.Equal(need, Contrast.RequiredFor(size, weight), 3);

    [Fact]
    public void Compositing_a_translucent_colour_lands_between_the_two()
    {
        // 128/255 is a hair under half, so the midpoint lands on 127 rather than 128.
        Assert.Equal(new SKColor(0x7f, 0x7f, 0x7f), Contrast.Over(new SKColor(0, 0, 0, 128), SKColors.White));
        Assert.Equal(SKColors.Black, Contrast.Over(SKColors.Black, SKColors.White));
        Assert.Equal(SKColors.White, Contrast.Over(new SKColor(0, 0, 0, 0), SKColors.White));
    }
}
