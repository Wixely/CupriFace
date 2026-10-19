using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The checks a desktop layout needs the first time it meets a phone: CF0075 (contents that paint
/// over a sibling), the multi-viewport sweep, the device contact sheet, and the absolute-position
/// shrink-to-fit that was painting badges across their cards.
///
/// <para>Every case here was measured first on the real engine, with the doctor silent, before the
/// check that now reports it was written. The silent ones were: a flex card shrunk below the long
/// token in it (its text ran under the next card), and a <c>white-space:nowrap</c> title painting
/// through the buttons beside it. Neither reached the viewport edge, so CF0072 had nothing to say.</para>
/// </summary>
public class ResponsiveDoctorTests(ITestOutputHelper output)
{
    // ---- CF0075: contents that paint over a sibling ---------------------------------------------

    private const string Cards =
        "<body><div class='row'><div class='card'>https://example.com/a/very/long/path/that/cannot/break</div>"
        + "<div class='card'>Second card</div><div class='card'>Short</div></div></body>";
    private const string CardsCss =
        "body{margin:0;font-family:sans-serif} .row{display:flex;gap:12px} .card{flex:1;padding:12px;background:#334}";

    [Fact]
    public void A_long_token_that_runs_under_the_next_card_is_reported()
    {
        // 600px: three cards of ~190px, a URL of ~400px. It runs under the second card and stops
        // well short of the viewport edge, which is why CF0072 stays silent and CF0075 must not.
        var report = CupriDoctor.Check(Cards, CardsCss, width: 600, height: 300);
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0075");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("<div class='card'>", f.Message);
        Assert.Contains("right-hand edge", f.Message);
        Assert.Contains("beside", f.Message);
        Assert.Contains("overflow-wrap", f.Fix);
    }

    [Fact]
    public void The_same_row_with_room_for_the_token_is_quiet()
    {
        var report = CupriDoctor.Check(Cards, CardsCss, width: 1600, height: 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
    }

    /// <summary>The advice the finding gives must silence it: a card that clips paints nothing
    /// outside itself, and a word allowed to break no longer spills.</summary>
    [Theory]
    [InlineData(".card{overflow:hidden}")]
    [InlineData(".card{overflow-wrap:anywhere}")]
    public void The_suggested_fixes_silence_it(string fix)
    {
        var report = CupriDoctor.Check(Cards, CardsCss + " " + fix, width: 600, height: 300);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
    }

    [Fact]
    public void A_nowrap_title_painting_through_the_buttons_beside_it_is_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='bar'><div class='title'>Deployment pipeline for the production environment</div>"
            + "<cupri-button>Run</cupri-button><cupri-button>Configure</cupri-button><div class='spacer'></div></div></body>",
            "body{margin:0;font-family:sans-serif} .bar{display:flex;align-items:center;gap:8px} "
            + ".title{white-space:nowrap;font-size:20px} .spacer{width:400px;height:1px}",
            width: 900, height: 200);
        output.WriteLine(report.ToString());

        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0072");   // nothing reaches the edge
        var f = Assert.Single(report.Findings, x => x.Code == "CF0075");
        Assert.Contains("<div class='title'>", f.Message);
        Assert.Contains("cupri-button", f.Message);
    }

    /// <summary>Overlap is not spill. A stack of avatars pulled together with a negative margin
    /// overlaps by design, and every one of them has its contents inside its own box.</summary>
    [Fact]
    public void Boxes_that_overlap_by_a_negative_margin_are_not_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='stack'><div class='av'></div><div class='av'></div><div class='av'></div></div></body>",
            "body{margin:0} .stack{display:flex} .av{width:32px;height:32px;border-radius:16px;background:#B87333;margin-left:-10px}",
            width: 400, height: 200);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
    }

    /// <summary>A pinned-height box whose children run out of the bottom is CF0070, and the box
    /// below is where that lands. One failure, one finding.</summary>
    [Fact]
    public void A_fixed_height_overflow_is_CF0070_and_not_also_CF0075()
    {
        var report = CupriDoctor.Check(
            "<body><div class='panel'><div class='r'></div><div class='r'></div></div><div class='after'></div></body>",
            "body{margin:0} .panel{height:40px} .r{height:34px} .after{height:40px;background:#343}",
            width: 400, height: 300);
        output.WriteLine(report.ToString());
        Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
    }

    /// <summary>…but an AUTO-height box can still be spilled over by a child that was itself
    /// pinned: the grandchild runs out of the middle box and over the next sibling of the middle
    /// box's parent. That is what a hero with a fixed-height inner banner does on a phone.</summary>
    [Fact]
    public void A_spill_downwards_over_the_next_sibling_is_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='a'><div class='text'>One line of text that wraps onto several lines once the box is narrow enough to force it to</div></div><div class='b'>After</div></body>",
            "body{margin:0;font-family:sans-serif} .a{height:20px;overflow:visible} .b{background:#343;height:40px}",
            width: 200, height: 300);
        output.WriteLine(report.ToString());
        // The pinned box is CF0070; the pair is the same failure and must not be reported twice.
        Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
    }

    /// <summary>The spill check reasons from layout boxes, so anything painted elsewhere — faded
    /// out, or transformed — is left out, as CF0074 leaves it out.</summary>
    [Fact]
    public void A_transformed_or_faded_box_is_not_reported()
    {
        var report = CupriDoctor.Check(Cards, CardsCss + " .card{opacity:0}", width: 600, height: 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
        report = CupriDoctor.Check(Cards, CardsCss + " .card{transform:translateX(4px)}", width: 600, height: 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0075");
    }

    // ---- the sweep: one document, several sizes ----------------------------------------------

    private const string Chrome =
        "<body><div class='chrome'><div class='rail'></div><div class='side'></div><div class='roster'></div></div>"
        + "<p>{{Nope}}</p></body>";
    private const string ChromeCss =
        "body{margin:0} .chrome{display:flex} .rail{width:72px;height:40px} .side{width:248px;height:40px}"
        + " .roster{width:236px;height:40px} .rail,.side,.roster{flex-shrink:0}";

    [Fact]
    public void A_finding_present_at_the_phone_only_says_so()
    {
        var report = CupriDoctor.Check(Chrome, ChromeCss, CupriDoctor.DesktopAndPhone, model: new { });
        output.WriteLine(report.ToString());

        var off = Assert.Single(report.Findings, x => x.Code == "CF0072");
        Assert.Equal("412x915", off.Viewports);
        Assert.Contains("[at 412x915 only]", off.ToString());

        // The binding typo holds at every size, so it carries no stamp and appears once.
        var binding = Assert.Single(report.Findings, x => x.Code == "CF0060");
        Assert.Equal("", binding.Viewports);
        Assert.DoesNotContain("[at", binding.ToString());
    }

    [Fact]
    public void Several_sizes_are_listed_together()
    {
        var report = CupriDoctor.Check(Chrome, ChromeCss, CupriDoctor.DesktopTabletAndPhones);
        var off = Assert.Single(report.Findings, x => x.Code == "CF0072");
        Assert.Equal("412x915, 360x640", off.Viewports);
    }

    [Fact]
    public void One_size_is_the_ordinary_check()
    {
        var report = CupriDoctor.Check(Chrome, ChromeCss, [(412, 915)]);
        var off = Assert.Single(report.Findings, x => x.Code == "CF0072");
        Assert.Equal("", off.Viewports);
    }

    // ---- the device sheet ---------------------------------------------------------------------

    [Fact]
    public void The_sheet_holds_every_size_side_by_side_and_leaves_the_overlay_as_it_found_it()
    {
        using var doc = CupriDocument.Load(Cards, CardsCss);
        doc.Refresh();
        Assert.False(doc.DebugOverlay);

        using var sheet = DeviceSheet.Render(doc, [(600, 300), (412, 500)], SKColors.Black, gutter: 10);
        Assert.Equal(10 + 600 + 10 + 412 + 10, sheet.Width);
        Assert.Equal(500 + 22 + 20, sheet.Height);
        Assert.False(doc.DebugOverlay);

        // The outlines were drawn: the first card's top-left corner at the sheet's first render
        // is the overlay's box colour rather than the card's own background.
        using var bmp = SKBitmap.FromImage(sheet);
        var corner = bmp.GetPixel(10, 10 + 22);
        output.WriteLine($"corner={corner}");
        Assert.NotEqual(new SKColor(0x33, 0x33, 0x44), corner);
    }

    // ---- absolute boxes shrink to fit -----------------------------------------------------------

    /// <summary>A badge with <c>right:8px</c> and no width came out as wide as its card, placed at a
    /// negative x, and painted across everything in it. CSS says a width-auto absolute box with one
    /// edge pinned shrinks to fit, which is also what the engine already did for position:fixed.</summary>
    [Theory]
    [InlineData("right:8px;top:8px")]
    [InlineData("left:8px;top:8px")]
    [InlineData("top:8px")]
    public void An_absolute_box_with_one_edge_pinned_shrinks_to_its_content(string inset)
    {
        using var doc = CupriDocument.Load(
            "<body><div class='wrap'><div class='badge'>NEW</div><div class='p'>Paragraph.</div></div></body>",
            $"body{{margin:0;font-family:sans-serif}} .wrap{{position:relative;padding:8px}} .badge{{position:absolute;{inset};padding:4px 10px;background:#a33}}");
        doc.Refresh();
        using (doc.RenderToImage(412, 120)) { }
        output.WriteLine(doc.DumpTree(maxDepth: 3));

        var badge = FindByClass(doc.Root, "badge")!;
        Assert.InRange(badge.Width, 30, 120);        // "NEW" plus padding, not the 412px container
        Assert.True(badge.X >= 0, $"x={badge.X}");
        if (inset.StartsWith("right"))
            Assert.InRange(badge.X + badge.Width, 412 - 8 - 1, 412 - 8 + 1);
    }

    [Fact]
    public void An_absolute_box_with_both_edges_pinned_still_stretches()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='wrap'><div class='bar'>x</div></div></body>",
            "body{margin:0} .wrap{position:relative;height:100px} .bar{position:absolute;left:10px;right:10px;top:0}");
        doc.Refresh();
        using (doc.RenderToImage(400, 120)) { }
        var bar = FindByClass(doc.Root, "bar")!;
        Assert.InRange(bar.Width, 379, 381);
    }

    private static Dom.RenderNode? FindByClass(Dom.RenderNode n, string cls)
    {
        if (n.Element?.GetAttribute("class") == cls) return n;
        foreach (var c in n.Children) if (FindByClass(c, cls) is { } h) return h;
        return null;
    }
}
