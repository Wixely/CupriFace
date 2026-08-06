using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The showcase app-shell: a fixed sidebar next to a <c>flex:1</c> content pane whose height comes
/// from flex-stretch (not an explicit height). <c>overflow:scroll</c> must still let a section taller
/// than the window scroll, so nothing goes off-screen unreachably.
/// </summary>
public class PageScrollTests
{
    private const string Css = """
        .app { display:flex; height:100%; }
        .side { width:80px; }
        .content { flex:1; overflow:scroll; }
        .tall { height:900px; }
        """;
    private const string Html =
        "<body><div class='app'><div class='side'></div>" +
        "<div class='content'><div class='tall'>x</div></div></div></body>";

    [Fact]
    public void Flex_stretched_content_pane_scrolls_when_its_section_overflows()
    {
        using var t = new TestDoc(Html, Css, null, width: 400, height: 300);

        var content = t.FindClass("content");
        Assert.True(content.IsScrollable, $"content should scroll; MaxScrollY={content.MaxScrollY}");
        Assert.True(content.MaxScrollY > 500, $"~900-300 expected; MaxScrollY={content.MaxScrollY}");

        var before = content.ScrollY;
        t.Doc.DispatchWheel(200, 150, 120); // wheel down over the content pane
        t.Layout();
        Assert.True(t.FindClass("content").ScrollY > before, "wheel should scroll the content down");
    }

    [Fact]
    public void A_short_section_does_not_become_scrollable()
    {
        const string html =
            "<body><div class='app'><div class='side'></div>" +
            "<div class='content'><div style='height:120px'>x</div></div></div></body>";
        using var t = new TestDoc(html, Css, null, width: 400, height: 300);
        Assert.False(t.FindClass("content").IsScrollable); // fits → no scrollbar
    }
}
