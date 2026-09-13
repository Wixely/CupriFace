using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The cross axis of a COLUMN flex container: what size an item with an auto width takes, and
/// therefore whether <c>align-items</c> has anything to move (#161).
///
/// <para>Centring a heading in a column is the most common flex idiom there is, and it did nothing.
/// The cause is that a column's cross axis is the WIDTH, and an auto width on a block fills its
/// container — so the item was already the full width, there was no free space to centre it in, and
/// its text simply sat at the left. An item with an explicit width centred correctly, which is what
/// made it look like an alignment bug rather than a sizing one.</para>
///
/// <para>Row containers were always right: an auto HEIGHT is content-driven, so a row item
/// shrink-wraps and centres. That asymmetry is the diagnostic, and it is asserted here so the two
/// axes cannot drift apart again.</para>
/// </summary>
public class FlexColumnAlignTests(ITestOutputHelper output)
{
    private const float Container = 480f;

    private const string Css = """
        body { margin:0; font-family:sans-serif }
        .col { width:480px; height:60px; display:flex; flex-direction:column; }
        .row { width:480px; height:60px; display:flex; flex-direction:row; }
        .fixed { width:120px; height:20px }
        h1 { font-size:18px; margin:0 }
        """;

    private static RenderNode Item(string containerStyle, string item = "<div id='it'>auto width</div>")
    {
        var t = new TestDoc($"<body><div class='col' style='{containerStyle}'>{item}</div></body>",
                            Css, width: 520, height: 120);
        var node = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "it")!;
        t.Dispose();
        return node;
    }

    /// <summary>The report: an auto-width item under <c>align-items: center</c>. It has to shrink to
    /// its content first — there is nothing to centre otherwise.</summary>
    [Fact]
    public void An_auto_width_item_shrinks_to_its_content_and_centres()
    {
        var it = Item("align-items:center");
        output.WriteLine($"x={it.X:0.0} w={it.Width:0.0}");

        Assert.True(it.Width < Container - 100, $"the item should shrink to its text, got {it.Width:0}px of {Container:0}");
        Assert.Equal((Container - it.Width) / 2f, it.X, 1);
    }

    /// <summary>A heading, which is the shape the package README uses as its example.</summary>
    [Fact]
    public void A_heading_centres_too()
    {
        var it = Item("align-items:center", "<h1 id='it'>heading</h1>");
        Assert.True(it.Width < Container - 100);
        Assert.Equal((Container - it.Width) / 2f, it.X, 1);
    }

    /// <summary>An explicit width always worked, and must keep working — it is the control that made
    /// the bug look like an alignment problem.</summary>
    [Fact]
    public void An_explicit_width_still_centres()
    {
        var it = Item("align-items:center", "<div id='it' class='fixed'>fixed</div>");
        Assert.Equal(120f, it.Width, 1);
        Assert.Equal((Container - 120f) / 2f, it.X, 1);
    }

    /// <summary>
    /// <c>flex-start</c> was reported as a working control, and it was not: the item stretched there
    /// too, and only LOOKED right because its text starts at the left. The distinction is visible
    /// the moment the item has a background — which is exactly how the reporter saw it.
    /// </summary>
    [Fact]
    public void Flex_start_shrinks_the_item_rather_than_only_looking_right()
    {
        var it = Item("align-items:flex-start");
        output.WriteLine($"x={it.X:0.0} w={it.Width:0.0}");

        Assert.Equal(0f, it.X, 1);
        Assert.True(it.Width < Container - 100, $"a flex-start item is not stretched either; got {it.Width:0}px");
    }

    [Fact]
    public void Flex_end_puts_the_shrunk_item_at_the_far_edge()
    {
        var it = Item("align-items:flex-end");
        Assert.Equal(Container - it.Width, it.X, 1);
    }

    /// <summary>…and stretch, the DEFAULT, still fills the container. This is the half that was
    /// right, and the half a fix like this can most easily break.</summary>
    [Theory]
    [InlineData("align-items:stretch")]
    [InlineData("")]                        // no align-items at all — stretch is the initial value
    public void Stretch_still_fills_the_container(string style)
    {
        var it = Item(style);
        Assert.Equal(Container, it.Width, 1);
        Assert.Equal(0f, it.X, 1);
    }

    /// <summary>The row axis, which was always correct: an auto HEIGHT is content-driven, so the item
    /// shrink-wraps and centres vertically. Asserted so the two axes cannot drift apart.</summary>
    [Fact]
    public void A_row_container_centres_an_auto_height_item_as_it_always_did()
    {
        using var t = new TestDoc(
            "<body><div class='row' style='align-items:center'><div id='it'>auto height</div></div></body>",
            Css, width: 520, height: 120);
        var it = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "it")!;

        Assert.True(it.Height < 40, $"a row item shrink-wraps its height, got {it.Height:0}");
        Assert.Equal((60f - it.Height) / 2f, it.Y, 1);
    }

    /// <summary>
    /// A column container whose own width comes from its CONTENT — here as an item of a row — sizes
    /// to its widest child. That is the other half of the same fix: the item's natural cross feeds
    /// the line's cross size, so reporting the stretched width there would have made this container
    /// as wide as whatever it happened to sit in.
    ///
    /// <para>(A block-level column container still fills its parent, and should: an auto width on a
    /// block is the containing block's width, flex or not.)</para>
    /// </summary>
    [Fact]
    public void A_column_sized_by_its_content_takes_its_widest_child()
    {
        using var t = new TestDoc(
            "<body><div style='display:flex;flex-direction:row;width:480px;align-items:flex-start'>"
            + "<div id='wrap' style='display:flex;flex-direction:column;align-items:center'>"
            + "<div class='fixed'>a</div></div></div></body>",
            Css, width: 520, height: 120);
        var wrap = TestDoc.Find(t.Root, n => n.Element?.GetAttribute("id") == "wrap")!;

        output.WriteLine($"wrapper {wrap.Width:0.0} wide, child 120, row 480");
        Assert.Equal(120f, wrap.Width, 1);
    }
}
