using CupriFace.Diagnostics;
using CupriFace.Style;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>text-transform</c> decides the case the text is DRAWN in (#266).
///
/// <para>It was reported unsupported and the text drew as typed. 52 of 165 designed compositions in
/// one corpus write it — the source says <c>Live</c>, the stylesheet makes it <c>LIVE</c> with
/// tracking tuned for capitals — and a translator cannot do it on the engine's behalf without
/// rewriting the author's text. Applied once the cascade has reached the text node, so the DOM
/// keeps what was typed and the render tree carries what is shown.</para>
/// </summary>
public class TextTransformTests(ITestOutputHelper output)
{
    private static string Drawn(string html, string css)
    {
        using var t = new TestDoc(html, "body{margin:0} " + css, width: 400, height: 100);
        var text = t.Find(n => n.IsText)!;
        return string.Concat(text.Lines!.Select(l => l.Text));
    }

    /// <summary>The issue's row, and the other two values.</summary>
    [Theory]
    [InlineData("uppercase", "HANDGLOVES")]
    [InlineData("lowercase", "handgloves")]
    [InlineData("capitalize", "Handgloves")]
    [InlineData("none", "Handgloves")]
    public void The_text_draws_in_the_case_the_stylesheet_asks_for(string value, string expected)
    {
        var drawn = Drawn("<body><div class='p'>Handgloves</div></body>", ".p{font-size:40px;text-transform:" + value + "}");
        output.WriteLine($"{value}: {drawn}");
        Assert.Equal(expected, drawn);
    }

    [Theory]
    [InlineData("global desk", "Global Desk")]
    [InlineData("breaking: the news", "Breaking: The News")]
    [InlineData("o'neil's 3rd", "O'neil's 3rd")]          // only the first letter of a word; a digit is left
    [InlineData("  two  spaces", "  Two  Spaces")]
    public void Capitalize_raises_the_first_letter_of_each_word(string typed, string expected) =>
        Assert.Equal(expected, TextCase.Apply(typed, TextTransform.Capitalize));

    /// <summary>Inherited: the declaration goes on the element, the text node inside it is what
    /// draws — and an inner element can say otherwise.</summary>
    [Fact]
    public void It_inherits_and_an_inner_rule_overrides()
    {
        using var t = new TestDoc("<body><div class='kicker'>Live <span class='plain'>now</span></div></body>",
            "body{margin:0} .kicker{text-transform:uppercase} .plain{text-transform:none}", width: 400, height: 100);
        var texts = new List<string>();
        void Walk(Dom.RenderNode n) { if (n.IsText) texts.Add(n.Text!); foreach (var c in n.Children) Walk(c); }
        Walk(t.Root);
        output.WriteLine(string.Join(" | ", texts));
        Assert.Equal(["LIVE", "now"], texts.Select(x => x.Trim()).ToArray());
    }

    /// <summary>The DOM is untouched: anything reading the document back gets the author's text.</summary>
    [Fact]
    public void The_markup_keeps_the_typed_case()
    {
        using var t = new TestDoc("<body><div class='p'>Handgloves</div></body>", "body{margin:0} .p{text-transform:uppercase}", width: 400, height: 100);
        Assert.Equal("Handgloves", t.FindClass("p").Element!.TextContent);
    }

    /// <summary>Generated content takes it too — a <c>::before</c> label under an uppercase rule.</summary>
    [Fact]
    public void A_pseudo_element_is_transformed_like_any_text()
    {
        var drawn = Drawn("<body><div class='p'></div></body>", ".p::before{content:'live';text-transform:uppercase}");
        Assert.Equal("LIVE", drawn);
    }

    /// <summary>Uppercase draws wider, which is the layout consequence a designer tunes for.</summary>
    [Fact]
    public void Uppercase_text_measures_wider_than_typed()
    {
        using var typed = new TestDoc("<body><span class='p'>Handgloves</span></body>", "body{margin:0} .p{font-size:40px}", width: 600, height: 100);
        using var upper = new TestDoc("<body><span class='p'>Handgloves</span></body>", "body{margin:0} .p{font-size:40px;text-transform:uppercase}", width: 600, height: 100);
        var a = typed.Find(n => n.IsText)!.Lines![0].Width;
        var b = upper.Find(n => n.IsText)!.Lines![0].Width;
        output.WriteLine($"typed {a:0}px, uppercase {b:0}px");
        Assert.True(b > a * 1.1f, "uppercase should be clearly wider");
    }

    [Fact]
    public void The_doctor_accepts_the_three_values_and_reports_the_rest()
    {
        var clean = CupriDoctor.Check("<body><div class='p'>x</div></body>",
            ".p{text-transform:uppercase} .q{text-transform:capitalize} .r{text-transform:lowercase}");
        Assert.True(clean.IsClean, clean.ToString());
        var kana = CupriDoctor.Check("<body><div class='p'>x</div></body>", ".p{text-transform:full-width}");
        output.WriteLine(kana.ToString());
        Assert.Contains(kana.Findings, f => f.Code == "CF0050" && f.Message.Contains("text-transform"));
    }
}
