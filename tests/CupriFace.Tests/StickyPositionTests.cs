using System.Linq;
using CupriFace.Dom;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>position:sticky — an element flows normally, but while its scroll container is scrolled it
/// holds at the top (its `top` offset) instead of scrolling away, until its containing block rides out.</summary>
public class StickyPositionTests
{
    // 200px scroll box: a section (30px sticky header + 200px body) then 600px of following content.
    private const string Css =
        "body{margin:0} .scroll{height:200px;overflow:scroll} " +
        ".header{position:sticky;top:0;height:30px;background:#aa0000} " +
        ".body{height:200px;background:#eeeeee} .after{height:600px;background:#00aa00}";
    private const string Html =
        "<body><div class='scroll'>" +
          "<div class='section'><div class='header'></div><div class='body'></div></div>" +
          "<div class='after'></div>" +
        "</div></body>";
    private static readonly SKColor HeaderColor = new(0xaa, 0, 0);
    private static readonly SKColor BodyColor = new(0xee, 0xee, 0xee);

    private static RenderNode? Find(RenderNode n, string cls)
    {
        if (n.Element?.ClassList.Contains(cls) == true) return n;
        foreach (var c in n.Children) { var f = Find(c, cls); if (f is not null) return f; }
        return null;
    }
    private static float HeaderY(CupriDocument doc) =>
        doc.BuildFrame(300, 200).Commands.OfType<FillRect>().First(f => f.Color == HeaderColor).Y;

    [Fact]
    public void Sticky_header_holds_at_the_top_then_releases_with_its_section()
    {
        using var doc = CupriDocument.Load(Html, Css);
        using var _ = doc.RenderToImage(300, 200);   // lay out so the container is scrollable
        var scroll = Find(doc.Root, "scroll")!;

        scroll.ScrollY = 0;   Assert.Equal(0f, HeaderY(doc), 1);            // at rest: at the very top
        scroll.ScrollY = 100; Assert.Equal(0f, HeaderY(doc), 1.5);         // scrolled past → still stuck at the top
        scroll.ScrollY = 150; Assert.Equal(0f, HeaderY(doc), 1.5);         // still within its section → still stuck
        scroll.ScrollY = 215; Assert.True(HeaderY(doc) < -4f, "released — rides out as its section ends");
    }

    [Fact]
    public void A_non_scrolled_sticky_element_sits_in_normal_flow()
    {
        using var doc = CupriDocument.Load(Html, Css);
        using var _ = doc.RenderToImage(300, 200);
        Find(doc.Root, "scroll")!.ScrollY = 0;
        Assert.Equal(0f, HeaderY(doc), 1);   // top:0 header at the top of the content — its natural place
    }

    [Fact]
    public void Stuck_header_paints_over_the_scrolled_content()
    {
        using var doc = CupriDocument.Load(Html, Css);
        using var _ = doc.RenderToImage(300, 200);
        Find(doc.Root, "scroll")!.ScrollY = 100;

        var cmds = doc.BuildFrame(300, 200).Commands.ToList();
        var headerIdx = cmds.FindIndex(c => c is FillRect { } f && f.Color == HeaderColor);
        var bodyIdx = cmds.FindLastIndex(c => c is FillRect { } f && f.Color == BodyColor);
        Assert.True(headerIdx > bodyIdx, $"sticky header ({headerIdx}) paints after/over the body ({bodyIdx})");
    }
}
