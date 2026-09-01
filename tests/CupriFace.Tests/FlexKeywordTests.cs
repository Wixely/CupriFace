using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The keyword forms of the <c>flex</c> shorthand.
///
/// Only numbers were parsed, so <c>flex: none</c> — the ordinary way to say "do not let this shrink" —
/// matched nothing and left the item at the default <c>flex-shrink: 1</c>. It read as working
/// everywhere it was written, including this repository's own sidebar icons, and silently was not.
/// Surfaced by a carousel whose slides shrank to fit instead of overflowing into a scroller.
/// </summary>
public class FlexKeywordTests(ITestOutputHelper output)
{
    private const string Css = """
        body { margin:0 }
        .row  { display:flex; width:300px }
        .item { width:200px; height:20px }
        """;

    private static float ItemWidth(string flex, int count = 3)
    {
        var items = string.Concat(Enumerable.Repeat($"<div class='item' style='flex:{flex}'>x</div>", count));
        using var t = new TestDoc($"<body><div class='row'>{items}</div></body>", Css, width: 400, height: 200);
        return TestDoc.Find(t.Doc.Root, n => n.Element?.ClassList.Contains("item") == true)!.Width;
    }

    /// <summary>Three 200px items in a 300px row. Shrinking fits them; `none` must not.</summary>
    [Fact]
    public void Flex_none_stops_an_item_shrinking()
    {
        var none = ItemWidth("none");
        output.WriteLine($"flex:none -> {none} (unconstrained would be 200, shrunk would be 100)");
        Assert.Equal(200f, none, 1);
    }

    [Fact]
    public void The_default_still_shrinks_so_the_fix_did_not_disable_shrinking()
    {
        // No flex at all: the item shrinks to share the 300px row, as it always did.
        using var t = new TestDoc(
            "<body><div class='row'><div class='item'>x</div><div class='item'>x</div>" +
            "<div class='item'>x</div></div></body>", Css, width: 400, height: 200);
        var w = TestDoc.Find(t.Doc.Root, n => n.Element?.ClassList.Contains("item") == true)!.Width;
        output.WriteLine($"no flex declared -> {w}");
        Assert.Equal(100f, w, 1);
    }

    /// <summary><c>initial</c> is `0 1 auto`: it shrinks like the default. Included because a keyword
    /// that parses to the same thing as no keyword is exactly the case a fix can get wrong by
    /// accident.</summary>
    [Fact]
    public void Flex_initial_shrinks_like_the_default()
    {
        Assert.Equal(100f, ItemWidth("initial"), 1);
    }

    /// <summary><c>auto</c> is `1 1 auto`: it grows into spare room as well as shrinking.</summary>
    [Fact]
    public void Flex_auto_grows_into_spare_room()
    {
        using var t = new TestDoc(
            "<body><div class='row'><div style='flex:auto;height:20px'>x</div></div></body>",
            Css, width: 400, height: 200);
        var only = TestDoc.Find(t.Doc.Root, n => n.Element?.GetAttribute("style")?.Contains("flex:auto") == true)!;
        output.WriteLine($"flex:auto alone in a 300px row -> {only.Width}");
        Assert.Equal(300f, only.Width, 1);
    }

    /// <summary>The numeric form is unchanged — `flex: 1` is `1 1 0%`, so two of them split the row.</summary>
    [Fact]
    public void The_numeric_form_still_means_what_it_did()
    {
        using var t = new TestDoc(
            "<body><div class='row'><div class='a' style='flex:1;height:20px'>x</div>" +
            "<div style='flex:1;height:20px'>y</div></div></body>", Css, width: 400, height: 200);
        var a = TestDoc.Find(t.Doc.Root, n => n.Element?.ClassList.Contains("a") == true)!;
        Assert.Equal(150f, a.Width, 1);
    }
}
