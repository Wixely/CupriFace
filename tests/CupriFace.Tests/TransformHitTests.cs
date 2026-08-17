using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Hit-testing a transformed element. Reported from a phone: a pinch-scaled tile could only be
/// grabbed inside its ORIGINAL rectangle, so the shape moved and its handle stayed behind. The
/// painter has always applied transforms; the pointer never followed them.
/// </summary>
public class TransformHitTests
{
    private const string Css = """
        body { margin:0 }
        .tile { position:absolute; left:100px; top:100px; width:100px; height:100px; }
        """;

    private static CupriDocument Doc(string style)
    {
        var doc = CupriDocument.Load($"<body><div class='tile' id='t' style='{style}'>x</div></body>", Css);
        doc.BuildFrame(400, 400);
        return doc;
    }

    [Fact]
    public void A_scaled_element_is_grabbable_across_the_size_it_actually_paints()
    {
        using var doc = Doc("transform:scale(2)");

        // Scaled 2x about its centre (150,150), it covers 50..250 — but its layout box is 100..200.
        // A point at 120,150 is inside the painted shape and OUTSIDE the untransformed box.
        Assert.Equal("t", HitTesting.HitTest(doc.Root, 120, 150)?.Element?.Id);
        Assert.Equal("t", HitTesting.HitTest(doc.Root, 240, 150)?.Element?.Id);
        Assert.Equal("t", HitTesting.HitTest(doc.Root, 150, 60)?.Element?.Id);

        // …and beyond the painted shape it is still a miss, or the fix would just be "always hit".
        Assert.NotEqual("t", HitTesting.HitTest(doc.Root, 20, 150)?.Element?.Id);
        Assert.NotEqual("t", HitTesting.HitTest(doc.Root, 380, 150)?.Element?.Id);
    }

    [Fact]
    public void A_rotated_element_is_grabbable_where_its_corners_actually_are()
    {
        using var doc = Doc("transform:rotate(45deg)");

        // Rotated 45° about (150,150): the corners swing out to the axis midpoints, so a point just
        // beyond the original top edge — under the rotated corner — is now inside the shape.
        Assert.Equal("t", HitTesting.HitTest(doc.Root, 150, 85)?.Element?.Id);

        // The original corner region, meanwhile, has rotated AWAY and must now miss.
        Assert.NotEqual("t", HitTesting.HitTest(doc.Root, 105, 105)?.Element?.Id);
    }

    [Fact]
    public void An_untransformed_element_is_unaffected()
    {
        using var doc = Doc("");
        Assert.Equal("t", HitTesting.HitTest(doc.Root, 150, 150)?.Element?.Id);
        Assert.NotEqual("t", HitTesting.HitTest(doc.Root, 250, 150)?.Element?.Id);
    }

    [Fact]
    public void A_transformed_parent_carries_its_children_with_it()
    {
        // The mapping is inherited: a child paints through its parent's matrix, so the pointer has
        // to descend in the same space.
        using var doc = CupriDocument.Load(
            "<body><div class='tile' style='transform:scale(2)'><div id='k' style='width:50px;height:50px'>k</div></div></body>",
            Css);
        doc.BuildFrame(400, 400);

        // The child occupies the tile's top-left 50x50 (100..150), which paints scaled to 50..150.
        Assert.Equal("k", HitTesting.HitTest(doc.Root, 70, 70)?.Element?.Id);
    }
}
