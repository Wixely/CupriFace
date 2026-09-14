using CupriFace.Demo;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The Showcase's sidebar and the set of section ids it will route to, which have to agree.
///
/// <para>They stopped agreeing. The list of routable ids was written out by hand beside the markup,
/// and the Markdown page was added to the sidebar and never to the list — so <c>--section
/// markdown</c> silently opened Inputs, and an <c>&lt;a href="markdown"&gt;</c> did nothing at all.
/// Neither failure says anything: you get the default page and assume you mistyped the id.</para>
///
/// <para>The fix reads the ids out of the markup, so the two cannot drift. This asserts that they
/// have not, page by page, rather than asserting the count — a count passes the moment someone adds
/// a page and removes another.</para>
/// </summary>
public class ShowcaseSectionTests(ITestOutputHelper output)
{
    /// <summary>Every <c>data-section</c> the sidebar offers, read the way a person reads it: from
    /// the built document, not from the file the app happens to embed.</summary>
    private static List<string> SidebarSections()
    {
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        doc.Refresh();
        using (doc.RenderToImage(940, 720)) { }

        var ids = new List<string>();
        void Walk(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("nav") == true
                && n.Element.GetAttribute("data-section") is { Length: > 0 } id && !ids.Contains(id))
                ids.Add(id);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        return ids;
    }

    [Fact]
    public void The_sidebar_offers_the_pages_it_is_supposed_to()
    {
        var ids = SidebarSections();
        output.WriteLine(string.Join(", ", ids));
        Assert.Contains("markdown", ids);
        Assert.Contains("controls", ids);
        Assert.True(ids.Count >= 12, $"the sidebar lost pages; it offers {ids.Count}");
    }

    /// <summary>The report: naming a page on the command line has to open that page. Run over every
    /// id the sidebar offers, so a page added later is covered without anyone remembering to.</summary>
    [Fact]
    public void Every_page_the_sidebar_offers_can_be_opened_by_name()
    {
        var missed = new List<string>();
        foreach (var id in SidebarSections())
        {
            var app = new ShowcaseApp(id);
            var actual = ((ShowcaseModel)app.Model).Section;
            if (actual != id) missed.Add($"{id} -> {actual}");
        }

        Assert.True(missed.Count == 0,
            "these pages are in the sidebar but cannot be opened by name: " + string.Join(", ", missed));
    }

    /// <summary>The specific page that was broken, on its own, so the failure names itself.</summary>
    [Fact]
    public void The_markdown_page_opens_by_name()
    {
        var app = new ShowcaseApp("markdown");
        Assert.Equal("markdown", ((ShowcaseModel)app.Model).Section);
    }

    /// <summary>…and the same set routes a LINK, which is the other half that silently did nothing:
    /// an <c>&lt;a href="charts"&gt;</c> whose id is not in the set opens no page and reports no
    /// error. Clicked for real, through the document, rather than by calling the router.</summary>
    [Fact]
    public void An_internal_link_routes_to_the_page_it_names()
    {
        var app = new ShowcaseApp("components");      // the page that carries the "Go to Charts" link
        using var doc = app.CreateDocument();
        doc.Refresh();
        // Tall enough that the whole page is on screen: the link sits well below a 720px fold, and a
        // click outside the viewport hits nothing at all - which looks exactly like a link that does
        // not route, the very thing under test.
        using (doc.RenderToImage(940, 3000)) { }

        var link = Find(doc.Root, n => n.Element?.LocalName == "a" && n.Element.GetAttribute("href") == "charts");
        Assert.NotNull(link);

        var box = CupriFace.Interaction.HitTesting.ScreenBox(link);
        output.WriteLine($"link at {box.X:0},{box.Y:0} {box.W:0}x{box.H:0}");
        doc.DispatchClick(box.X + box.W / 2f, box.Y + box.H / 2f);

        Assert.Equal("charts", ((ShowcaseModel)app.Model).Section);
    }

    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
    {
        if (match(n)) return n;
        foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
        return null;
    }

    /// <summary>An id that names nothing keeps the default page rather than blanking every section —
    /// the reason the set is checked at all.</summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    public void An_unknown_id_keeps_the_default_page(string id)
    {
        var app = new ShowcaseApp(id);
        Assert.Equal("controls", ((ShowcaseModel)app.Model).Section);
    }
}
