using System.Linq;
using System.Threading.Tasks;
using CupriFace.Components;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-markdown&gt;</c> — the subset it renders, and the shapes that used to break it.
///
/// <para><b>The hang is the reason this file exists.</b> Any line starting with <c>#</c> that was not
/// <c>#&#160;</c>, <c>##&#160;</c> or <c>###&#160;</c> — an h4, or a bare <c>#hashtag</c> — matched no
/// heading branch, fell through to the paragraph branch, and was then rejected by that branch's own
/// <c>!StartsWith("#")</c> guard. Nothing was consumed, the index never advanced, and the renderer
/// spun forever on one line. Markdown is frequently text somebody else wrote, so that is a denial of
/// service and not a cosmetic bug.</para>
///
/// <para>The hang tests are <c>async</c> deliberately: xunit only honours <c>Timeout</c> on async
/// tests, and without it a regression hangs the whole suite instead of failing one case.</para>
/// </summary>
public class MarkdownTests
{
    private static TestDoc Md(string markdown) =>
        new($"<body><cupri-markdown>{markdown}</cupri-markdown></body>", "", components: true);

    /// <summary>Every element in the rendered tree, by tag name — enough to assert structure without
    /// pinning the exact markup the component happens to emit.</summary>
    private static string[] Tags(TestDoc t)
    {
        var tags = new List<string>();
        void Walk(RenderNode n)
        {
            if (n.Element?.LocalName is { } l) tags.Add(l);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Doc.Root);
        return [.. tags];
    }

    private static string Text(TestDoc t)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(RenderNode n)
        {
            if (!string.IsNullOrWhiteSpace(n.Text)) sb.Append(n.Text.Trim()).Append(' ');
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Doc.Root);
        return sb.ToString().Trim();
    }

    // ---- the hang -------------------------------------------------------------------------------

    /// <summary>The regression fence. Each of these used to spin forever.</summary>
    [Theory(Timeout = 5000)]
    [InlineData("#### four")]
    [InlineData("##### five")]
    [InlineData("###### six")]
    [InlineData("####### seven (too deep to be a heading)")]
    [InlineData("#hashtag")]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("#no space\nand a second line")]
    [InlineData("a paragraph\n#### then a heading")]
    public async Task NeverHangs(string markdown)
    {
        await Task.Yield();
        using var t = Md(markdown);
        Assert.NotEmpty(Tags(t));
    }

    /// <summary>Forward progress is a property of the paragraph branch, not of its guard: whatever
    /// the line is, it is consumed. This is the invariant that stops a future block type from
    /// reintroducing the hang, so it is asserted on input designed to match no branch at all.</summary>
    [Fact(Timeout = 5000)]
    public async Task ConsumesLinesThatMatchNoBlock()
    {
        await Task.Yield();
        using var t = Md("#not a heading\n>not a quote\n1.not a list\n```\nunclosed fence");
        Assert.NotEmpty(Tags(t));
    }

    // ---- headings -------------------------------------------------------------------------------

    [Theory]
    [InlineData("# one", "h1")]
    [InlineData("## two", "h2")]
    [InlineData("### three", "h3")]
    [InlineData("#### four", "h4")]
    [InlineData("##### five", "h5")]
    [InlineData("###### six", "h6")]
    public void HeadingLevels(string markdown, string tag)
    {
        using var t = Md(markdown);
        Assert.Contains(tag, Tags(t));
    }

    /// <summary>CommonMark requires the space, so a hashtag is prose. It must render as a paragraph
    /// with its text intact — not be silently eaten, and not become a heading.</summary>
    [Fact]
    public void HashWithoutASpaceIsProseNotAHeading()
    {
        using var t = Md("#hashtag stays text");

        Assert.Contains("p", Tags(t));
        Assert.DoesNotContain("h1", Tags(t));
        Assert.Contains("#hashtag stays text", Text(t));
    }

    /// <summary>Seven hashes is past h6, so it is a paragraph — and, critically, still terminates.</summary>
    [Fact]
    public void SevenHashesIsAParagraph()
    {
        using var t = Md("####### too deep");

        Assert.Contains("p", Tags(t));
        Assert.DoesNotContain("h6", Tags(t));
    }

    // ---- images ---------------------------------------------------------------------------------

    /// <summary>An image used to render as a literal "!" followed by a link, because the link rule
    /// matched from index 1 of <c>![alt](src)</c>. Images are now matched first and the link rule
    /// refuses a leading bang.</summary>
    [Fact]
    public void ImageIsAnImageNotABangAndALink()
    {
        using var t = Md("![a picture](http://example.com/x.png)");

        Assert.Contains("cupri-image", Tags(t));
        Assert.DoesNotContain("a", Tags(t));
        Assert.DoesNotContain("!", Text(t));
    }

    /// <summary>An ordinary link keeps working beside the new image rule.</summary>
    [Fact]
    public void LinkStillWorks()
    {
        using var t = Md("[a link](http://example.com)");

        Assert.Contains("a", Tags(t));
        Assert.DoesNotContain("cupri-image", Tags(t));
    }

    /// <summary>The two side by side, which is where an over-eager rule shows itself.</summary>
    [Fact]
    public void ImageAndLinkOnOneLine()
    {
        using var t = Md("see ![pic](http://x/p.png) and [docs](http://x/d) too");

        Assert.Contains("cupri-image", Tags(t));
        Assert.Contains("a", Tags(t));
    }

    // ---- lists, quotes, rules -------------------------------------------------------------------

    [Fact]
    public void OrderedListRenders()
    {
        using var t = Md("1. first\n2. second\n3. third");
        var tags = Tags(t);

        Assert.Contains("ol", tags);
        Assert.Equal(3, tags.Count(x => x == "li"));
    }

    [Fact]
    public void BulletListStillRenders()
    {
        using var t = Md("- one\n- two");
        var tags = Tags(t);

        Assert.Contains("ul", tags);
        Assert.Equal(2, tags.Count(x => x == "li"));
    }

    [Fact]
    public void BlockquoteRenders()
    {
        using var t = Md("> quoted words");

        Assert.Contains("blockquote", Tags(t));
        Assert.Contains("quoted words", Text(t));
    }

    [Fact]
    public void ThematicBreakRenders()
    {
        using var t = Md("above\n\n---\n\nbelow");

        Assert.Contains("hr", Tags(t));
    }

    /// <summary>A bullet list must not be read as a thematic break, and vice versa — both start
    /// with <c>-</c>, and the rule only applies when the line is nothing else.</summary>
    [Fact]
    public void DashRulesAndDashBulletsDoNotCollide()
    {
        using var rule = Md("---");
        using var bullet = Md("- an item");

        Assert.Contains("hr", Tags(rule));
        Assert.DoesNotContain("ul", Tags(rule));
        Assert.Contains("ul", Tags(bullet));
        Assert.DoesNotContain("hr", Tags(bullet));
    }

    // ---- inline ---------------------------------------------------------------------------------

    [Fact]
    public void InlineEmphasisAndCode()
    {
        using var t = Md("**bold** and *italic* and `code` and ~~struck~~");
        var tags = Tags(t);

        Assert.Contains("strong", tags);
        Assert.Contains("em", tags);
        Assert.Contains("code", tags);
        Assert.Contains("s", tags);
    }

    [Fact]
    public void FencedCodeBlockRenders()
    {
        using var t = Md("```\nvar x = 1;\n```");

        Assert.Contains("pre", Tags(t));
        Assert.Contains("var x = 1;", Text(t));
    }

    // ---- the security property ------------------------------------------------------------------

    /// <summary>
    /// The component's most valuable property: markdown is escaped BEFORE any inline rule runs, so
    /// raw HTML in the source can never become live markup. Worth a test of its own, because the
    /// obvious "improvement" — handing the job to a Markdown library that emits HTML — would quietly
    /// remove it.
    /// </summary>
    [Fact]
    public void RawHtmlInMarkdownIsTextNotMarkup()
    {
        // Fed through the `text` ATTRIBUTE, which is how untrusted markdown actually arrives — bound
        // from a model. Putting it in the element's body instead would let AngleSharp parse it while
        // loading the page, before the component ever saw it, and the test would pass without
        // proving anything about the component.
        using var t = new TestDoc(
            "<body><cupri-markdown text=\"a <script>alert(1)</script> and <b>bold?</b>\"></cupri-markdown></body>",
            "", components: true);
        var tags = Tags(t);

        Assert.DoesNotContain("script", tags);
        Assert.DoesNotContain("b", tags);
        Assert.Contains("<script>", Text(t));
        Assert.Contains("<b>", Text(t));
    }

    /// <summary>An image's src comes from the document, so it goes in as an attribute value; a quote
    /// in it must not break out of the attribute.</summary>
    [Fact(Timeout = 5000)]
    public async Task QuotesInAUrlDoNotEscapeTheAttribute()
    {
        await Task.Yield();
        using var t = Md("![x](http://e/\"onerror=\"alert(1))");

        Assert.DoesNotContain("onerror", Tags(t));
    }
}
