using CupriFace;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A child of a padded <c>&lt;body&gt;</c> gets the body's CONTENT width, not the viewport's.
///
/// The root was laid out with its content width FORCED to the viewport width. In a content-box model
/// the insets are then added outside it, so a padded body's border box came out wider than the window
/// and every child was measured against the full viewport: a block child of a 600px body with 20px
/// padding was 600 wide and ran 20px off the right edge. Nested padded elements were always correct —
/// only the root was forced — which is what made it look like a component bug wherever it surfaced.
///
/// The force existed for HEIGHT, so <c>height:100%</c> fills the window and percentage heights
/// resolve. That is still forced; the width simply takes the ordinary auto path, which already
/// subtracts margins and insets from the containing block.
/// </summary>
public class BodyPaddingTests(ITestOutputHelper output)
{
    private static RenderNode Find(RenderNode n, string cls) =>
        TestDoc.Find(n, x => x.Element?.ClassList.Contains(cls) == true)!;

    [Theory]
    [InlineData(0, 600)]
    [InlineData(20, 560)]
    [InlineData(37, 526)]
    public void A_block_child_of_a_padded_body_gets_the_bodys_content_width(float pad, float expected)
    {
        using var t = new TestDoc(
            $"<body style='margin:0;padding:{pad}px'><div class='kid'>x</div></body>",
            "", width: 600, height: 300);

        var kid = Find(t.Doc.Root, "kid");
        output.WriteLine($"padding {pad}: kid x={kid.X} w={kid.Width} right={kid.X + kid.Width}");

        Assert.Equal(pad, kid.X, 1);
        Assert.Equal(expected, kid.Width, 1);
        Assert.Equal(600 - pad, kid.X + kid.Width, 1);      // …and it stops at the content edge
    }

    /// <summary>The body itself still fills the window: the padding comes out of the inside, as it
    /// does for every other element, rather than being added to the outside.</summary>
    [Fact]
    public void The_body_still_fills_the_viewport()
    {
        using var t = new TestDoc(
            "<body style='margin:0;padding:25px'><div class='kid'>x</div></body>",
            "", width: 480, height: 300);

        Assert.Equal(480, t.Doc.Root.Width, 1);
    }

    /// <summary>Nested padded containers were never wrong, and must stay right — this is the control
    /// that says the fix was aimed at the root and not at padding in general.</summary>
    [Fact]
    public void A_nested_padded_container_is_unchanged()
    {
        using var t = new TestDoc(
            "<body style='margin:0'><div class='wrap' style='padding:30px'><div class='kid'>x</div></div></body>",
            "", width: 600, height: 300);

        var kid = Find(t.Doc.Root, "kid");
        Assert.Equal(30, kid.X, 1);
        Assert.Equal(540, kid.Width, 1);
    }

    /// <summary>The reason the force was there: <c>height:100%</c> must still fill the window, so the
    /// height stays forced even though the width no longer is.</summary>
    [Fact]
    public void A_percentage_height_still_resolves_against_the_window()
    {
        using var t = new TestDoc(
            "<body style='margin:0'><div class='tall' style='height:100%'>x</div></body>",
            "", width: 400, height: 300);

        Assert.Equal(300, Find(t.Doc.Root, "tall").Height, 1);
    }

    /// <summary>A body MARGIN is still ignored, and deliberately so.
    ///
    /// <para>Letting the ordinary auto-width path handle the root would have subtracted it — but
    /// <c>root.X</c> is pinned to 0, so the content would have narrowed without shifting and opened a
    /// gap on one side only. Half-applying a margin is worse than not applying it, so this fix is
    /// padding-only and the margin behaves exactly as it did before.</para></summary>
    [Fact]
    public void A_body_margin_is_still_ignored_rather_than_half_applied()
    {
        using var t = new TestDoc(
            "<body style='margin:15px;padding:0'><div class='kid'>x</div></body>",
            "", width: 500, height: 300);

        var kid = Find(t.Doc.Root, "kid");
        output.WriteLine($"margin 15: kid x={kid.X} w={kid.Width}");
        Assert.Equal(0, kid.X, 1);
        Assert.Equal(500, kid.Width, 1);   // unchanged: not narrowed, not shifted
    }
}
