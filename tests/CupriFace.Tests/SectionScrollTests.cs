using CupriFace.Demo;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "On Styling page I scroll down, change to Diagnostics page and that page renders as if I'm
/// scrolled down." All ten Showcase sections shared ONE scroller (.content), so the offset simply
/// survived the section swap on the same node. Each section now scrolls itself: a fresh page
/// starts at its top, and a page you RETURN to is where you left it — the second half is a
/// feature, not an accident, and it gets asserted so it survives.
/// </summary>
public class SectionScrollTests
{
    private const int W = 393, H = 771;

    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> p)
    {
        if (p(n)) return n;
        foreach (var c in n.Children) if (Find(c, p) is { } f) return f;
        return null;
    }

    private static RenderNode VisibleSection(CupriDocument doc) =>
        Find(doc.Root, n => n.Element?.ClassList.Contains("section") == true && n.Height > 0)!;

    [Fact]
    public void Switching_sections_does_not_inherit_the_previous_scroll()
    {
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Section = "styling";
        var p = app.Present(W, H);
        int lw = (int)p.LogicalWidth, lh = (int)p.LogicalHeight;
        doc.Refresh();
        using (doc.RenderToImage(lw, lh)) { }

        var styling = VisibleSection(doc);
        Assert.True(styling.IsScrollable, "the styling section is not its own scroller");
        Assert.True(doc.DispatchWheel(lw / 2f, lh / 2f, 400));
        using (doc.RenderToImage(lw, lh)) { }
        var scrolled = VisibleSection(doc).ScrollY;
        Assert.True(scrolled > 100, $"styling only scrolled {scrolled:F0}px — the setup proves nothing");

        model.Section = "diag";   // the nav label is Diagnostics; the key is diag
        doc.Refresh();
        using (doc.RenderToImage(lw, lh)) { }

        Assert.Equal(0f, VisibleSection(doc).ScrollY, 1);
    }

    [Fact]
    public void Returning_to_a_section_finds_it_where_it_was_left()
    {
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Section = "styling";
        var p = app.Present(W, H);
        int lw = (int)p.LogicalWidth, lh = (int)p.LogicalHeight;
        doc.Refresh();
        using (doc.RenderToImage(lw, lh)) { }

        doc.DispatchWheel(lw / 2f, lh / 2f, 400);
        using (doc.RenderToImage(lw, lh)) { }
        var left = VisibleSection(doc).ScrollY;
        Assert.True(left > 100);

        model.Section = "diag";   // the nav label is Diagnostics; the key is diag
        doc.Refresh();
        using (doc.RenderToImage(lw, lh)) { }
        model.Section = "styling";
        doc.Refresh();
        using (doc.RenderToImage(lw, lh)) { }

        Assert.Equal(left, VisibleSection(doc).ScrollY, 0);
    }
}
