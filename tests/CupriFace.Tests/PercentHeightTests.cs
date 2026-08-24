using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A percentage height resolves against the CONTAINING BLOCK — the parent — and the block path used
/// to hand children its own parent's height instead. The meter/fill shape (issue #55) is where that
/// shows: an 18px track with a `height:100%` fill produced a fill as tall as the viewport, painted
/// over everything below it. The flex path was always right, which is what made it look like a
/// display-mode quirk rather than one wrong argument.
/// </summary>
public class PercentHeightTests
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
    public void Percentage_height_resolves_against_a_fixed_height_block_parent()
    {
        using var doc = CupriDocument.Load(
            """
            <body>
              <div class='meter'><div class='fill'></div></div>
              <div class='mflex'><div class='fflex'></div></div>
              <p class='below'>text below</p>
            </body>
            """,
            """
            body   { margin:0 }
            .meter { height:18px }              .fill  { height:100%; width:50% }
            .mflex { height:18px; display:flex } .fflex { height:100%; width:50% }
            """);
        doc.BuildFrame(400, 200);

        Assert.Equal(18f, Find(doc, "fill").Height, 1);    // was 200 — the whole viewport
        Assert.Equal(18f, Find(doc, "fflex").Height, 1);   // the flex path, unchanged
        Assert.Equal(200f, Find(doc, "fill").Width, 1);    // width was never in question

        // The point of the bug report: the fill stopped swallowing the page. Assert the consequence,
        // not just the number — the fill must end above the paragraph that follows its track.
        var meter = Find(doc, "meter");
        var below = Find(doc, "below");
        Assert.True(meter.Y + meter.Height <= below.Y + 0.5f,
            $"track ends at {meter.Y + meter.Height:F0}, but the text below starts at {below.Y:F0}");
    }

    [Fact]
    public void Half_of_a_fixed_height_parent_is_half_not_the_viewport()
    {
        // 100% is the easy case to get accidentally right. A fraction pins that the BASIS is the
        // parent's height rather than any pass-through of something larger.
        using var doc = CupriDocument.Load(
            "<body><div class='box'><div class='half'></div></div></body>",
            "body{margin:0} .box{height:80px} .half{height:25%}");
        doc.BuildFrame(400, 600);

        Assert.Equal(20f, Find(doc, "half").Height, 1);
    }

    [Fact]
    public void Percentage_height_nests_through_several_fixed_blocks()
    {
        // Each level re-bases: 50% of 200 = 100, then 50% of 100 = 50. Forwarding the grandparent's
        // height would have made both levels 100.
        using var doc = CupriDocument.Load(
            "<body><div class='outer'><div class='mid'><div class='inner'></div></div></div></body>",
            "body{margin:0} .outer{height:200px} .mid{height:50%} .inner{height:50%}");
        doc.BuildFrame(400, 600);

        Assert.Equal(100f, Find(doc, "mid").Height, 1);
        Assert.Equal(50f, Find(doc, "inner").Height, 1);
    }

    [Fact]
    public void An_auto_height_parent_still_forwards_its_own_containing_block()
    {
        // Deliberately unchanged. A parent whose height comes from its content is no basis for a
        // percentage, so the child keeps resolving against the nearest ancestor that has one —
        // the pre-existing behaviour on every path, and not what #55 was about.
        using var doc = CupriDocument.Load(
            "<body><div class='fixed'><div class='auto'><div class='child'></div></div></div></body>",
            "body{margin:0} .fixed{height:120px} .auto{} .child{height:50%}");
        doc.BuildFrame(400, 600);

        Assert.Equal(60f, Find(doc, "child").Height, 1);
    }
}
