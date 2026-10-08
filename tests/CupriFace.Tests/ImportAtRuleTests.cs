using CupriFace.Diagnostics;
using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "An @import swallows the CSS rule that follows it." (#289)
///
/// The parser took everything up to the first <c>{</c> as the header, so a blockless at-rule — one
/// that ends at a <c>;</c> and has no body — rode into the next rule's selector. The header then
/// began with '@', the whole rule was skipped as an at-rule, and the author lost exactly one rule:
/// whichever they happened to write first. Which is why it read as one rule mysteriously not
/// applying rather than as a parse failure, and why two <c>@import</c>s still lost only one rule.
///
/// A font <c>@import</c> at the top of a <c>&lt;style&gt;</c> is the ordinary way to pull a web font
/// in CSS, so this landed on 18 of 165 corpus compositions — one of them losing its composition
/// root, and with it the background, the size and the clipping in one go.
/// </summary>
public class ImportAtRuleTests
{
    private const string ThreeRules = ".a{color:red} .b{color:green} .c{color:blue}";

    private static string[] Selectors(string css) =>
        CssParser.Parse(css).Select(r => r.Selector).ToArray();

    [Theory]
    // The issue's own table, which measured the first rule lost in every row but the first.
    [InlineData("")]
    [InlineData("@import url(nothing.css);")]
    [InlineData("@import url(a.css); @import url(b.css);")]
    [InlineData("@import url(nothing.css);\n")]
    // A semicolon inside the imported URL must not be mistaken for the statement's end — a data URI
    // and a query string both carry one.
    [InlineData("@import url(\"https://fonts.example/css2?family=X:wght@800;900&display=block\");")]
    [InlineData("@charset \"utf-8\";")]
    [InlineData("@layer base;")]
    [InlineData("@namespace svg url(http://www.w3.org/2000/svg);")]
    public void No_at_statement_takes_a_rule_with_it(string prefix)
    {
        Assert.Equal([".a", ".b", ".c"], Selectors(prefix + ThreeRules));
    }

    [Fact]
    public void An_at_statement_between_two_rules_costs_nothing_either()
    {
        Assert.Equal([".a", ".b"], Selectors(".a{color:red} @import url(x.css); .b{color:green}"));
    }

    [Fact]
    public void The_rule_after_an_import_keeps_its_declarations()
    {
        // Not just its selector: the symptom was a rule that did not apply, so what matters is that
        // what it declares survives the trip.
        var rule = Assert.Single(CssParser.Parse("@import url(x.css); .a{color:red;background:#00ff00}"));
        Assert.Equal(".a", rule.Selector);
        Assert.Equal("red", rule.Declarations["color"]);
        Assert.Equal("#00ff00", rule.Declarations["background"]);
    }

    [Fact]
    public void A_media_block_after_an_import_is_still_a_media_block()
    {
        // The at-rule skip is what routes @media to its own parse; stepping over the import must
        // not step over that.
        var rules = CssParser.Parse("@import url(x.css); @media (min-width:100px){ .a{color:red} }");
        var rule = Assert.Single(rules);
        Assert.Equal(".a", rule.Selector);
        Assert.NotNull(rule.Media);
        Assert.True(rule.Media!.Value.Matches(200, 200));
        Assert.False(rule.Media!.Value.Matches(50, 200));
    }

    [Fact]
    public void An_import_alone_still_parses_to_nothing()
    {
        Assert.Empty(CssParser.Parse("@import url(x.css);"));
    }

    [Fact]
    public void The_composition_root_case_paints()
    {
        // vfx-text-cursor's actual shape, and the worst case in the corpus: the rule the import ate
        // was the root, so the frame lost its background, its size and its clipping at once —
        // 100% of pixels differing from the browser, scoring 0.0% of content.
        const string Html = "<body><div class='stage'></div></body>";
        const string Css = "@import url(\"https://fonts.example/css2?family=Big+Shoulders&display=block\");\n" +
                           "body{margin:0}\n" +
                           ".stage{width:200px;height:100px;background:#0000ff}";
        using var doc = CupriDocument.Load(Html, Css);
        doc.Refresh();
        using var _ = doc.RenderToImage(200, 100);
        using var img = doc.RenderToImage(200, 100, SKColors.White);
        using var bmp = SKBitmap.FromImage(img);
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(100, 50));
    }

    [Fact]
    public void The_doctor_says_the_imported_sheet_is_never_fetched()
    {
        // Stepping past the import fixes the rule it ate; it does NOT make the font arrive. That is
        // the remaining silence: the text renders in a fallback face and nothing says why.
        var report = CupriDoctor.Check("<body><div>hi</div></body>",
            "@import url(\"https://fonts.googleapis.com/css2?family=Inter\");\nbody{margin:0}");
        var finding = Assert.Single(report.Findings, f => f.Code == "CF0052");
        Assert.Contains("@import", finding.Message);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void A_document_with_no_import_says_nothing_about_one()
    {
        var report = CupriDoctor.Check("<body><div>hi</div></body>", "body{margin:0}");
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0052");
    }
}
