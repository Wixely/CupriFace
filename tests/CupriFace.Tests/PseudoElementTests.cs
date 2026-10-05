using CupriFace.Diagnostics;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>::before</c> and <c>::after</c> generate a box when <c>content</c> is set (#261).
///
/// <para>They never did: a solid, explicitly sized <c>::after</c> on a sized, relative parent
/// painted nothing, and no diagnostic named it. 60 of 165 designed compositions in one surveyed
/// corpus use one — a glow along the bottom of a card, an underline that grows under a title, a
/// scrim over a photo — and none of them drew.</para>
/// </summary>
public class PseudoElementTests(ITestOutputHelper output)
{
    private const string Parent = "body{margin:0;background:#fff} .p{position:relative;width:120px;height:80px}";

    private static int Painted(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    private int Ink(string html, string css)
    {
        using var t = new TestDoc(html, css, width: 200, height: 150);
        using var bmp = t.Render();
        var n = Painted(bmp);
        output.WriteLine($"{css[Parent.Length..].Trim()}\n   -> {n} px painted\n{t.Doc.DumpTree(maxDepth: 3)}");
        return n;
    }

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>The issue's document: the pseudo-element paints exactly what the same box as a real
    /// child paints.</summary>
    [Theory]
    [InlineData("::after")]
    [InlineData("::before")]
    [InlineData(":after")]     // the legacy single-colon spelling
    public void A_sized_pseudo_element_with_content_paints_like_a_real_child(string pseudo)
    {
        var control = Ink("<body><div class='p'><div class='k'></div></div></body>",
            Parent + " .k{position:absolute;left:0;top:0;width:120px;height:80px;background:#d9642a}");
        var generated = Ink("<body><div class='p'></div></body>",
            Parent + $" .p{pseudo}{{content:\"\";position:absolute;left:0;top:0;width:120px;height:80px;background:#d9642a}}");
        Assert.True(control > 9000, "the control did not draw");
        Assert.Equal(control, generated);
    }

    /// <summary>The rule CSS has for them: no <c>content</c>, no box. A rule that only styles the
    /// pseudo-element generates nothing, and <c>content: none</c> says so explicitly.</summary>
    [Theory]
    [InlineData("position:absolute;inset:0;background:#d9642a")]
    [InlineData("content:none;position:absolute;inset:0;background:#d9642a")]
    public void Without_content_no_box_is_generated(string decls)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>", Parent + " .p::after{" + decls + "}", width: 200, height: 150);
        Assert.Null(t.Find(n => n.Tag == PseudoElements.Tag));
        using var bmp = t.Render();
        Assert.Equal(0, Painted(bmp));
    }

    // ---- where it sits -------------------------------------------------------------------------

    [Fact]
    public void Before_is_the_first_child_and_after_the_last()
    {
        using var t = new TestDoc("<body><div class='p'><span>mid</span></div></body>",
            Parent + " .p::before{content:'a'} .p::after{content:'z'}", width: 200, height: 150);
        var p = t.FindClass("p");
        Assert.Equal(3, p.Children.Count);
        Assert.Equal("before", p.Children[0].Element?.GetAttribute(PseudoElements.SideAttribute));
        Assert.Equal("span", p.Children[1].Tag);
        Assert.Equal("after", p.Children[2].Element?.GetAttribute(PseudoElements.SideAttribute));
        Assert.Equal("a", p.Children[0].Children[0].Text);
        Assert.Equal("z", p.Children[2].Children[0].Text);
        output.WriteLine(t.Doc.DumpTree(maxDepth: 4));
        Assert.Contains("div::before", t.Doc.DumpTree(maxDepth: 4));
        Assert.Contains("div::after", t.Doc.DumpTree(maxDepth: 4));
    }

    /// <summary>In flow, with a display: an underline that grows under a title is a block
    /// pseudo-element below the text, and it takes space like any block.</summary>
    [Fact]
    public void A_block_pseudo_element_takes_its_place_in_flow()
    {
        using var t = new TestDoc("<body><div class='t'>Title</div><div class='next'></div></body>",
            "body{margin:0} .t{font-size:20px;line-height:20px} .t::after{content:'';display:block;height:4px;width:60px;background:#d9642a} .next{height:10px}",
            width: 200, height: 150);
        var after = t.Find(n => n.Tag == PseudoElements.Tag)!;
        var next = t.FindClass("next");
        output.WriteLine(t.Doc.DumpTree(maxDepth: 3));
        Assert.Equal((60f, 4f), (after.Width, after.Height));
        Assert.Equal(20f, after.Y, 0.5);                 // under the line of text
        Assert.Equal(24f, next.Y, 0.5);                  // and the next block is pushed down by it
    }

    // ---- content values ------------------------------------------------------------------------

    [Theory]
    [InlineData("content:'★ '", "★")]
    [InlineData("content:\"\\2014\"", "\u2014")]              // a hex escape: the em dash
    [InlineData("content:\"\\2014 x\"", "\u2014x")]           // …whose terminating space is swallowed
    [InlineData("content:'a' 'b'", "ab")]                      // strings concatenate
    [InlineData("content:'say \\'hi\\''", "say 'hi'")]         // an escaped quote
    [InlineData("content:open-quote", "“")]
    public void Content_strings_are_decoded(string decl, string expected)
    {
        using var t = new TestDoc("<body><div class='p'></div></body>", Parent + " .p::before{" + decl + "}", width: 200, height: 150);
        var text = t.Find(n => n.Tag == PseudoElements.Tag)!.Children[0].Text;
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Content_attr_reads_the_owners_attribute()
    {
        using var t = new TestDoc("<body><div class='p' data-label='Hello'></div></body>",
            Parent + " .p::after{content:attr(data-label)}", width: 200, height: 150);
        Assert.Equal("Hello", t.Find(n => n.Tag == PseudoElements.Tag)!.Children[0].Text);
    }

    /// <summary>A <c>content</c> the engine cannot produce — a counter — is reported, and still
    /// generates an (empty) box: the decoration the rule describes is closer to the design than
    /// nothing.</summary>
    [Fact]
    public void An_unsupported_content_value_is_reported_and_still_makes_a_box()
    {
        var report = CupriDoctor.Check("<body><div class='p'></div></body>",
            Parent + " .p::after{content:counter(n);display:block;height:4px;background:#d9642a}");
        output.WriteLine(report.ToString());
        Assert.Contains(report.Findings, f => f.Code == "CF0050" && f.Message.Contains("content"));
        using var t = new TestDoc("<body><div class='p'></div></body>",
            Parent + " .p::after{content:counter(n);display:block;height:4px;background:#d9642a}", width: 200, height: 150);
        Assert.NotNull(t.Find(n => n.Tag == PseudoElements.Tag));
    }

    // ---- the cascade reaches it ----------------------------------------------------------------

    /// <summary>Two rules on the same pseudo-element cascade as two rules on an element do: the
    /// later, equally specific one wins, and a more specific one beats it.</summary>
    [Fact]
    public void Pseudo_element_rules_cascade()
    {
        using var t = new TestDoc("<body><div class='p' id='x'></div></body>",
            Parent + " .p::after{content:'';display:block;width:10px;height:10px;background:#f00}"
                   + " .p::after{background:#0f0}"
                   + " #x::after{background:#00f}"
                   + " div::after{background:#000}",
            width: 200, height: 150);
        var after = t.Find(n => n.Tag == PseudoElements.Tag)!;
        Assert.Equal(SKColors.Blue, after.Style.Background);   // the id rule
    }

    /// <summary>A pseudo-element revealed on hover exists before the hover, so the restyle has
    /// something to give a box to.</summary>
    [Fact]
    public void A_hover_only_pseudo_element_appears_on_hover()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            Parent + " .p:hover::after{content:'';position:absolute;inset:0;background:#d9642a}", width: 200, height: 150);
        using (var idle = t.Render()) Assert.Equal(0, Painted(idle));
        t.HoverClass("p");
        using var hovered = t.Render();
        Assert.True(Painted(hovered) > 9000, "the hover pseudo-element did not appear");
    }

    /// <summary>The same cascade is the one the animation engine reads: a keyframed pseudo-element
    /// animates.</summary>
    [Fact]
    public void A_pseudo_element_animates()
    {
        using var t = new TestDoc("<body><div class='p'></div></body>",
            Parent + " @keyframes grow{0%{width:0px}100%{width:100px}}"
                   + " .p::after{content:'';display:block;height:4px;animation:grow 1s linear forwards}",
            width: 200, height: 150);
        t.Doc.Animate(0.5); t.Layout();
        Assert.Equal(50f, t.Find(n => n.Tag == PseudoElements.Tag)!.Width, 0.5);
    }

    // ---- the doctor ----------------------------------------------------------------------------

    /// <summary>The stand-in element is the engine's own: not an unregistered component, not an
    /// element that failed to draw.</summary>
    [Fact]
    public void The_doctor_does_not_report_the_stand_in()
    {
        var report = CupriDoctor.Check("<body><div class='p'></div><div class='q'></div></body>",
            Parent + " .p::after{content:'';position:absolute;inset:0;background:#d9642a} .q::before{position:absolute}");
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code is "CF0020" or "CF0030" or "CF0031");
    }
}
