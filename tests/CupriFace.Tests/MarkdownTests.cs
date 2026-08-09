using System;
using System.Collections.Generic;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary><c>&lt;cupri-markdown&gt;</c> parses a Markdown subset into the toolkit's own elements —
/// headings, bold, italic (upright, but the markers are consumed), inline + fenced code, bullet lists,
/// and links — which then lay out and paint like any other markup.</summary>
public class MarkdownTests
{
    private static List<RenderNode> ByTag(TestDoc t, string tag)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n)
        {
            if (string.Equals(n.Element?.LocalName, tag, StringComparison.OrdinalIgnoreCase)) outp.Add(n);
            foreach (var c in n.Children) W(c);
        }
        W(t.Root);
        return outp;
    }

    private static TestDoc Md(string markdown) => new TestDoc(
        $"<body><cupri-markdown text=\"{markdown.Replace("\"", "&quot;")}\"></cupri-markdown></body>",
        "", components: true, width: 480, height: 360);

    [Fact]
    public void Headings_bold_code_and_links_become_real_elements()
    {
        using var t = Md("# Title\n\nSome **bold** and `code` and [a link](https://ex.com) here.");

        var h1 = ByTag(t, "h1");
        Assert.Single(h1);
        Assert.Equal("Title", h1[0].Element!.TextContent.Trim());

        Assert.Contains(ByTag(t, "strong"), n => n.Element!.TextContent.Trim() == "bold");
        Assert.Contains(ByTag(t, "code"), n => n.Element!.TextContent.Trim() == "code");

        var a = ByTag(t, "a");
        Assert.Single(a);
        Assert.Equal("https://ex.com", a[0].Element!.GetAttribute("href"));
        Assert.Equal("a link", a[0].Element!.TextContent.Trim());
    }

    [Fact]
    public void Bullet_lines_become_list_items()
    {
        using var t = Md("Shopping:\n\n- apples\n- pears\n* oranges");
        var li = ByTag(t, "li");
        Assert.Equal(3, li.Count);
        Assert.Contains("apples", li[0].Element!.TextContent);
        Assert.Contains("oranges", li[2].Element!.TextContent);
    }

    [Fact]
    public void Italic_markers_are_consumed_even_though_the_font_is_upright()
    {
        using var t = Md("An *emphatic* and _underscored_ word.");
        var em = ByTag(t, "em");
        Assert.Equal(2, em.Count);                         // both * and _ forms parsed
        Assert.Equal("emphatic", em[0].Element!.TextContent);
        Assert.Equal("underscored", em[1].Element!.TextContent);
        // no raw markers leak into the rendered text
        Assert.DoesNotContain('*', t.FindClass("cupri-md").Element!.TextContent);
        Assert.DoesNotContain('_', t.FindClass("cupri-md").Element!.TextContent);
    }

    [Fact]
    public void Fenced_code_block_keeps_each_line_as_its_own_block()
    {
        using var t = Md("Run:\n\n```\nline one\nline two\n```");
        Assert.Single(ByTag(t, "pre"));
        var codeLines = ByTag(t, "div").FindAll(n => n.Element!.ClassList.Contains("cupri-md-cl"));
        Assert.Equal(2, codeLines.Count);                  // two separate lines, not one flattened run
        Assert.Contains("line one", codeLines[0].Element!.TextContent);
        Assert.Contains("line two", codeLines[1].Element!.TextContent);
    }

    [Fact]
    public void Falls_back_to_the_element_body_when_no_text_attribute()
    {
        using var t = new TestDoc(
            "<body><cupri-markdown>## Inline\n\nbody **words**.</cupri-markdown></body>",
            "", components: true, width: 480, height: 300);
        Assert.Single(ByTag(t, "h2"));
        Assert.Equal("Inline", ByTag(t, "h2")[0].Element!.TextContent.Trim());
        Assert.Contains(ByTag(t, "strong"), n => n.Element!.TextContent.Trim() == "words");
    }
}
