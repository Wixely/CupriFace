using CupriFace;
using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The diagnostics that exist because this engine fails silently, each tested against the exact
/// failure it was built for.
///
/// <para>Every one of these was a real report before it was a check: a binding typo that rendered as
/// a blank field, a fixed-height container whose contents painted over the next section (twice, for
/// one developer, in two sittings), a card that laid out at zero height. What they have in common is
/// that nothing threw, nothing logged and no test failed — so the only thing that would have caught
/// them is a tool that goes looking.</para>
///
/// <para><b>The false-positive tests matter as much as the positive ones.</b> A checker that
/// accuses working markup gets switched off and stays off, taking the true findings with it, so
/// each rule here is tested for silence on the correct case as well as noise on the broken one.</para>
/// </summary>
public class AgentDiagnosticsTests(ITestOutputHelper output)
{
    private const string Css = """
        body { font-family:sans-serif; }
        .fits { height:120px; }
        .tooSmall { height:30px; }
        .row { height:26px; }
        .scrolls { height:30px; overflow:scroll; }
        .grows { }
        """;

    private static string Rows(string cls, int n) =>
        $"<body><div class='{cls}'>"
        + string.Concat(Enumerable.Repeat("<div class='row'>x</div>", n))
        + "</div></body>";

    // ---- CF0060: bindings that name nothing ----------------------------------------------------

    [Fact]
    public void A_misspelt_binding_is_an_error_with_a_suggestion()
    {
        var report = CupriDoctor.Check(
            "<body><div>{{Enviroment}}</div></body>", Css, model: new Model());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0060");
        output.WriteLine(f.ToString());
        Assert.Equal(Severity.Error, f.Severity);
        Assert.Contains("Did you mean {{Environment}}?", f.Fix);
    }

    /// <summary>The check must ask whether the property EXISTS, never whether it has a value. A
    /// model whose field is legitimately empty — every loading state ever written — has to pass, or
    /// the rule fires constantly and gets disabled.</summary>
    [Fact]
    public void A_property_that_exists_but_is_empty_is_not_a_finding()
    {
        var report = CupriDoctor.Check(
            "<body><div>{{Blank}}</div><div>{{Missing}}</div></body>", Css, model: new Model());

        Assert.DoesNotContain(report.Findings, f => f.Message.Contains("{{Blank}}"));
        Assert.Contains(report.Findings, f => f.Message.Contains("{{Missing}}"));
    }

    /// <summary>Inside data-repeat a path is relative to an ITEM, not the root. Reporting those as
    /// unresolved would make the rule useless on every list in the codebase.</summary>
    [Fact]
    public void A_path_valid_inside_a_repeat_scope_is_not_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div data-repeat='Items'>{{Label}}</div></body>", Css, model: new Model());

        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0060");
    }

    [Fact]
    public void Without_a_model_the_binding_check_is_skipped_rather_than_guessed()
    {
        var report = CupriDoctor.Check("<body><div>{{AnythingAtAll}}</div></body>", Css);
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0060");
    }

    // ---- CF0070: content that does not fit -----------------------------------------------------

    /// <summary>The field report this was built from: a fixed-height container smaller than its
    /// contents neither clips nor complains, so the children paint over whatever follows.</summary>
    [Fact]
    public void A_fixed_height_container_smaller_than_its_contents_is_reported()
    {
        var report = CupriDoctor.Check(Rows("tooSmall", 4), Css);

        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        output.WriteLine(f.ToString());
        Assert.Contains("overflow", f.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overflow:scroll", f.Fix);
    }

    [Fact]
    public void A_container_big_enough_for_its_contents_is_silent()
    {
        var report = CupriDoctor.Check(Rows("fits", 4), Css);
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0070");
    }

    /// <summary>An auto height grew to fit and cannot overflow by definition; overflow:scroll is the
    /// author saying they meant it. Both must stay silent or the rule is noise on normal code.</summary>
    [Theory]
    [InlineData("grows")]
    [InlineData("scrolls")]
    public void Growing_and_scrolling_containers_are_not_findings(string cls)
    {
        var report = CupriDoctor.Check(Rows(cls, 6), Css);
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0070");
    }

    // ---- CF0071: boxes with no area ------------------------------------------------------------

    [Fact]
    public void A_collapsed_box_holding_visible_content_is_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div style='height:0px'><div>text nobody can see</div></div></body>", Css);

        var f = Assert.Single(report.Findings, x => x.Code == "CF0071");
        output.WriteLine(f.ToString());
    }

    /// <summary>Two shapes that are normal and must not be accused: an element wrapping an empty
    /// string (every template mid-load), and an inline element, whose geometry lives in its line
    /// fragments rather than its box — a span reads 0x0 when perfectly healthy.</summary>
    [Fact]
    public void An_empty_wrapper_and_an_inline_element_are_not_findings()
    {
        var report = CupriDoctor.Check(
            "<body><div style='height:0px'><div></div></div><p>a <span>b</span> c</p></body>", Css);

        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0071");
    }

    // ---- CF0080: characters no font can draw ---------------------------------------------------

    /// <summary>
    /// Private Use plane 15 — a codepoint no designed font covers.
    ///
    /// <para><b>The check cannot see this on every platform, and that is the point of the test.</b>
    /// CF0080 fires when the system font manager returns NOTHING for a character. Some platforms
    /// always return something: macOS ships a LastResort face that matches every codepoint and draws
    /// a placeholder box for it, so the user still sees tofu while <c>MatchCharacter</c> reports
    /// success and the check stays silent. That is a false negative worth knowing about rather than
    /// papering over, so the test asserts the real behaviour on each platform and prints what the
    /// font manager actually answered.</para>
    /// </summary>
    [Fact]
    public void A_character_no_font_covers_is_reported_where_the_platform_admits_it()
    {
        const int cp = 0xF0000;
        var matched = SKFontManager.Default.MatchCharacter("sans-serif", cp);
        output.WriteLine($"U+{cp:X} matched by: {matched?.FamilyName ?? "(nothing)"}");

        var html = "<body><div>" + char.ConvertFromUtf32(cp) + "</div></body>";
        var report = CupriDoctor.Check(html, Css);
        var found = report.Findings.Where(x => x.Code == "CF0080").ToList();

        if (matched is null)
        {
            var f = Assert.Single(found);
            output.WriteLine(f.ToString());
            Assert.Contains("U+F0000", f.Message);
        }
        else
        {
            // The platform claims a face for it. The check has nothing to report, by construction.
            Assert.Empty(found);
        }
    }

    [Fact]
    public void Ordinary_text_reports_no_missing_glyphs()
    {
        var report = CupriDoctor.Check("<body><div>Ordinary text.</div></body>", Css);
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0080");
    }

    // ---- DumpTree ------------------------------------------------------------------------------

    /// <summary>The dump answers what an image cannot: a blank rectangle has many causes, a 0-height
    /// box has one. It must carry the geometry and flag the two shapes worth flagging.</summary>
    [Fact]
    public void The_tree_dump_carries_geometry_and_flags_overflow()
    {
        using var doc = CupriDocument.Load(Rows("tooSmall", 4), Css);
        doc.Refresh();
        using (doc.RenderToImage(400, 300)) { }

        var dump = doc.DumpTree();
        output.WriteLine(dump);

        Assert.Contains("div.tooSmall", dump, StringComparison.Ordinal);
        Assert.Contains("CONTENT OVERFLOWS", dump, StringComparison.Ordinal);
        Assert.Contains("x", dump, StringComparison.Ordinal);          // WxH present

        // Line endings are '\n' on every platform. The dump's whole claim is that it can be diffed
        // between runs and parsed by whatever reads it; Environment.NewLine would make the same tree
        // differ across platforms, and leave a '\r' glued to the last token of every line for anyone
        // splitting on '\n' — which is exactly how the first consumer of this tripped over it.
        Assert.DoesNotContain('\r', dump);
    }

    /// <summary>Coordinates are absolute so they can be handed straight to DispatchClick — the dump
    /// says where to click as well as what is there. Verified by clicking what it reports.</summary>
    [Fact]
    public void Dumped_coordinates_are_clickable()
    {
        const string html = "<body><div style='padding:20px'><cupri-button>Go</cupri-button></div></body>";
        using var doc = CupriDocument.Load(html, Css);
        doc.UseComponents(CupriFace.Components.ComponentRegistry.Default());
        doc.Refresh();
        using (doc.RenderToImage(400, 300)) { }

        var line = doc.DumpTree().Split('\n')
            .FirstOrDefault(l => l.Contains("cupri-button", StringComparison.Ordinal));
        Assert.NotNull(line);
        output.WriteLine(line!.Trim());

        var parts = line!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var xy = parts.First(p => p.Contains(',') && char.IsDigit(p[0])).Split(',');
        var wh = parts.First(p => p.Contains('x') && char.IsDigit(p[0])).Split('x');
        var cx = float.Parse(xy[0]) + float.Parse(wh[0]) / 2f;
        var cy = float.Parse(xy[1]) + float.Parse(wh[1]) / 2f;

        Assert.True(doc.DispatchClick(cx, cy), $"click at {cx},{cy} from the dump did not land");
    }

    // ---- ImageDiff -----------------------------------------------------------------------------

    /// <summary>"Did my change touch anything it should not have" is not answerable by looking at
    /// two screenshots in turn, and is exact by subtraction.</summary>
    [Fact]
    public void A_diff_finds_a_change_the_eye_would_miss_and_ignores_none()
    {
        using var a = Render("body { font-family:sans-serif; } .b { height:40px; background:#345; }");
        using var b = Render("body { font-family:sans-serif; } .b { height:43px; background:#345; }");

        var moved = ImageDiff.Compare(a, b);
        var same = ImageDiff.Compare(a, a);
        output.WriteLine($"3px taller: {moved}");
        output.WriteLine($"unchanged:  {same}");

        Assert.True(same.IsIdentical);
        Assert.False(moved.IsIdentical);
        Assert.True(moved.ChangedFraction is > 0 and < 1);
        Assert.NotNull(moved.Bounds);

        static SKBitmap Render(string css)
        {
            using var doc = CupriDocument.Load("<body><div class='b'>x</div></body>", css);
            doc.Refresh();
            using (doc.RenderToImage(200, 120)) { }
            return SKBitmap.FromImage(doc.RenderToImage(200, 120, SKColors.White));
        }
    }

    [Fact]
    public void Differently_sized_images_say_so_rather_than_reporting_zero_change()
    {
        using var a = new SKBitmap(10, 10);
        using var b = new SKBitmap(10, 12);

        var d = ImageDiff.Compare(a, b);
        Assert.True(d.SizeChanged);
        Assert.False(d.IsIdentical);          // must not read as "no change"
    }

    private sealed class Model
    {
        public string Environment => "staging";
        public string Blank => "";
        public List<Item> Items { get; } = [new()];
    }

    private sealed class Item
    {
        public string Label => "row";
    }
}
