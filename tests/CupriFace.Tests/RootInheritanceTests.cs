using CupriFace.Dom;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Rules on <c>:root</c> / <c>html</c> reach the page through INHERITANCE (issue #53). The render
/// tree starts at &lt;body&gt;, so the document element used to match nothing: a palette declared
/// the conventional way silently vanished, and every <c>var()</c> behaved like an undefined token.
/// The document element is an inheritance environment, not a box — custom properties and inherited
/// text properties flow down; its own layout/paint properties stay inert.
/// </summary>
public class RootInheritanceTests
{
    private static RenderNode Find(CupriDocument doc, string cls)
    {
        RenderNode? hit = null;
        void Walk(RenderNode n)
        {
            if (hit is null && n.Element?.ClassList.Contains(cls) == true) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        return hit!;
    }

    [Fact]
    public void Custom_properties_on_root_and_html_reach_descendants()
    {
        // The issue's own repro: a palette on :root, html and body — all three must paint.
        using var doc = CupriDocument.Load(
            "<body><div class='s root'></div><div class='s htm'></div><div class='s bod'></div></body>",
            """
            :root { --a: #6d4aff; }
            html  { --b: #e8590c; }
            body  { --c: #2f9e44; }
            .s { width:150px; height:20px }
            .root { background: var(--a); }
            .htm  { background: var(--b); }
            .bod  { background: var(--c); }
            """);
        doc.BuildFrame(400, 200);

        Assert.Equal(new SKColor(0x6d, 0x4a, 0xff), Find(doc, "root").Style.Background);
        Assert.Equal(new SKColor(0xe8, 0x59, 0x0c), Find(doc, "htm").Style.Background);
        Assert.Equal(new SKColor(0x2f, 0x9e, 0x44), Find(doc, "bod").Style.Background);
    }

    [Fact]
    public void Root_tokens_survive_deep_nesting_and_fallbacks_still_work()
    {
        using var doc = CupriDocument.Load(
            "<body><div><div><div class='deep'></div><div class='fb'></div></div></div></body>",
            """
            :root { --accent: #B87333; }
            .deep { background: var(--accent); width:20px; height:20px }
            .fb   { background: var(--nope, #c92a2a); width:20px; height:20px }
            """);
        doc.BuildFrame(400, 200);

        Assert.Equal(new SKColor(0xB8, 0x73, 0x33), Find(doc, "deep").Style.Background);
        Assert.Equal(new SKColor(0xc9, 0x2a, 0x2a), Find(doc, "fb").Style.Background);   // fallback path unchanged
    }

    [Fact]
    public void A_body_declaration_overrides_the_inherited_root_value()
    {
        // Ordinary cascade-through-inheritance: the nearer declaration wins for everything below it.
        using var doc = CupriDocument.Load(
            "<body><div class='x'></div></body>",
            ":root { --x: #ff0000; } body { --x: #00ff00; } .x { background: var(--x); width:20px; height:20px }");
        doc.BuildFrame(400, 200);

        Assert.Equal(new SKColor(0x00, 0xff, 0x00), Find(doc, "x").Style.Background);
    }

    [Fact]
    public void Inherited_text_properties_on_root_flow_down_too()
    {
        // Not just custom properties: :root { color } is the same inheritance channel, so it comes
        // along for free — and pins that the environment carries the INHERITED set, nothing else.
        using var doc = CupriDocument.Load(
            "<body><div class='t'>text</div></body>",
            ":root { color: #6d4aff; } .t { width:100px; height:20px }");
        doc.BuildFrame(400, 200);

        Assert.Equal(new SKColor(0x6d, 0x4a, 0xff), Find(doc, "t").Style.Color);
    }

    [Fact]
    public void The_document_element_is_not_a_box()
    {
        // html { background } is deliberately inert: the environment inherits, it does not paint.
        // body's own background must stay untouched by it.
        using var doc = CupriDocument.Load(
            "<body><div class='probe'></div></body>",
            "html { background: #ff0000; } .probe { width:20px; height:20px }");
        doc.BuildFrame(400, 200);

        Assert.Equal(SKColors.Transparent, doc.Root.Style.Background);
        // Background is NOT an inherited property, so the probe stays transparent as well.
        Assert.Equal(SKColors.Transparent, Find(doc, "probe").Style.Background);
    }
}
