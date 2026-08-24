using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>transform-origin</c> — the transform's fixed point (issue #54). Every transform used to pivot
/// about the border-box centre, so <c>scaleY</c> on a bar grew it equally up and down: a rising
/// series rendered as a bowtie rather than a chart. Since layout properties deliberately do not
/// animate, <c>scaleY</c> + an origin is the only route to a bar anchored to a baseline.
/// </summary>
public class TransformOriginTests
{
    private static ComputedStyle StyleOf(string css)
    {
        using var doc = CupriDocument.Load("<body><div class='t'></div></body>",
            "body{margin:0} .t { width:40px; height:20px; transform:scale(2); " + css + " }");
        doc.BuildFrame(200, 100);
        RenderNode? hit = null;
        void Walk(RenderNode n)
        {
            if (hit is null && n.Element?.ClassList.Contains("t") == true) hit = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        return hit!.Style;
    }

    [Theory]
    // keyword pairs
    [InlineData("transform-origin: left top", 0f, 0f)]
    [InlineData("transform-origin: right bottom", 40f, 20f)]
    [InlineData("transform-origin: center center", 20f, 10f)]
    // vertical keyword FIRST — legal CSS, and the reversed pair must not be read as (x, y)
    [InlineData("transform-origin: top left", 0f, 0f)]
    [InlineData("transform-origin: bottom right", 40f, 20f)]
    // a keyword paired with `center` — the most common chart form (#63). `center` names no axis,
    // so only the OTHER word can say which slot it fills; requiring both to agree read `bottom
    // center` positionally, made `bottom` an X of 100%, and silently centred the Y again.
    [InlineData("transform-origin: bottom center", 20f, 20f)]
    [InlineData("transform-origin: top center", 20f, 0f)]
    [InlineData("transform-origin: center bottom", 20f, 20f)]
    [InlineData("transform-origin: center left", 0f, 10f)]
    [InlineData("transform-origin: center right", 40f, 10f)]
    // a lone vertical keyword sets Y and leaves X centred (the bar-chart case)
    [InlineData("transform-origin: bottom", 20f, 20f)]
    [InlineData("transform-origin: top", 20f, 0f)]
    // a lone horizontal keyword sets X and leaves Y centred
    [InlineData("transform-origin: right", 40f, 10f)]
    // percentages and lengths
    [InlineData("transform-origin: 50% 100%", 20f, 20f)]
    [InlineData("transform-origin: 0% 0%", 0f, 0f)]
    [InlineData("transform-origin: 10px 4px", 10f, 4f)]
    [InlineData("transform-origin: 25%", 10f, 10f)]           // one value → Y defaults to centre
    // the initial value, and what every element got before this existed
    [InlineData("", 20f, 10f)]
    public void Origin_resolves_to_the_expected_pivot(string css, float expectX, float expectY)
    {
        var (x, y) = StyleOf(css).TransformPivot(40f, 20f);
        Assert.Equal(expectX, x, 2);
        Assert.Equal(expectY, y, 2);
    }

    [Fact]
    public void Bars_scaled_about_the_bottom_share_a_baseline()
    {
        // The reported repro, measured the way the report measured it: paint, then find the lowest
        // painted row of each bar. Anchored to the bottom, three differently-scaled bars must end on
        // the same line — that IS the chart.
        using var t = new TestDoc(
            """
            <body><div class='plot'>
              <div class='bar b1'></div><div class='bar b2'></div><div class='bar b3'></div>
            </div></body>
            """,
            """
            body  { margin:0 }
            .plot { display:flex; align-items:flex-end; gap:10px; height:50px; }
            .bar  { width:14px; height:50px; background:#0000ff; transform-origin: bottom; }
            .b1 { transform: scaleY(0.25) } .b2 { transform: scaleY(0.60) } .b3 { transform: scaleY(1.00) }
            """,
            width: 100, height: 60);

        using var bmp = t.Render(SKColors.White);
        var bottoms = new[] { BottomOfColumn(bmp, 7), BottomOfColumn(bmp, 31), BottomOfColumn(bmp, 55) };
        var tops = new[] { TopOfColumn(bmp, 7), TopOfColumn(bmp, 31), TopOfColumn(bmp, 55) };

        Assert.All(bottoms, b => Assert.True(b >= 0, "a bar painted nothing — check the column probes"));
        Assert.Equal(bottoms[0], bottoms[1]);
        Assert.Equal(bottoms[1], bottoms[2]);

        // ...and they must actually differ in height, or a flat baseline would prove nothing.
        Assert.True(tops[0] > tops[1] && tops[1] > tops[2],
            $"expected increasing heights, got tops {tops[0]}, {tops[1]}, {tops[2]}");
    }

    [Fact]
    public void All_three_bottom_spellings_paint_the_same_bar()
    {
        // Issue #63's repro shape: `bottom`, `bottom center` and `50% 100%` are the same origin,
        // so three quarter-scale bars anchored by each must paint identically — same bottom edge,
        // same top edge. The middle spelling used to float centred while its neighbours sat on
        // the baseline, which read as #54 having regressed.
        using var t = new TestDoc(
            "<body><div class='plot'><div class='bar a'></div><div class='bar b'></div><div class='bar c'></div></div></body>",
            """
            body  { margin:0 }
            .plot { display:flex; align-items:flex-end; gap:10px; height:50px; }
            .bar  { width:14px; height:50px; background:#0000ff; transform: scaleY(0.25); }
            .a { transform-origin: bottom; }
            .b { transform-origin: bottom center; }
            .c { transform-origin: 50% 100%; }
            """,
            width: 100, height: 60);

        using var bmp = t.Render(SKColors.White);
        var columns = new[] { 7, 31, 55 };
        var bottoms = columns.Select(x => BottomOfColumn(bmp, x)).ToArray();
        var tops = columns.Select(x => TopOfColumn(bmp, x)).ToArray();

        Assert.All(bottoms, b => Assert.InRange(b, 47, 50));   // all on the baseline
        Assert.All(tops, tp => Assert.InRange(tp, 36, 39));    // all the same quarter height
    }

    [Fact]
    public void The_default_origin_still_scales_about_the_centre()
    {
        // The other half: nothing changes for an element that says nothing. A quarter-scale bar with
        // no origin stays centred in its 50px row — margins above and below within a pixel.
        using var t = new TestDoc(
            "<body><div class='plot'><div class='bar'></div></div></body>",
            """
            body  { margin:0 }
            .plot { display:flex; align-items:flex-end; height:50px; }
            .bar  { width:14px; height:50px; background:#0000ff; transform: scaleY(0.25); }
            """,
            width: 40, height: 60);

        using var bmp = t.Render(SKColors.White);
        int top = TopOfColumn(bmp, 7), bottom = BottomOfColumn(bmp, 7);
        Assert.True(top >= 0);
        Assert.True(System.Math.Abs(top - (50 - 1 - bottom)) <= 1,
            $"bar spans {top}..{bottom} in a 50px row — not centred");
    }

    [Fact]
    public void Hit_testing_pivots_where_the_paint_does()
    {
        // A bar-chart bar has to be clickable where it is DRAWN. The pointer mapping and the painter
        // each build their own matrix, so an origin honoured by one and not the other gives an
        // element you can see but cannot hit.
        using var t = new TestDoc(
            "<body><div class='plot'><div class='bar' id='b'></div></div></body>",
            """
            body  { margin:0 }
            .plot { display:flex; align-items:flex-end; height:50px; }
            .bar  { width:14px; height:50px; background:#0000ff;
                    transform: scaleY(0.25); transform-origin: bottom; }
            """,
            width: 40, height: 60);

        var node = t.Find(n => n.Element?.GetAttribute("id") == "b")!;

        // Assert against the geometry the CSS asks for, not against wherever it happened to land:
        // a 50px bar at scaleY(0.25) anchored to its bottom occupies y 37.5–50. y=45 is inside it
        // and y=10 is the space it vacated. Anchored to the centre instead it would span 18.75–31.25,
        // so these two points swap answers — which is what makes this fail without the fix rather
        // than merely agree with whatever the painter did.
        Assert.Same(node, HitTesting.HitTest(t.Root, 7, 45));
        Assert.NotSame(node, HitTesting.HitTest(t.Root, 7, 10));

        // ...and the painter agrees, which is the half that guards the two matrices drifting apart.
        using var bmp = t.Render(SKColors.White);
        Assert.InRange(BottomOfColumn(bmp, 7), 47, 50);
        Assert.InRange(TopOfColumn(bmp, 7), 36, 39);
    }

    // Topmost / bottommost painted (blue) row in a column, or -1 if the column is blank.
    private static int TopOfColumn(SKBitmap bmp, int x)
    {
        for (var y = 0; y < bmp.Height; y++) if (IsBar(bmp.GetPixel(x, y))) return y;
        return -1;
    }

    private static int BottomOfColumn(SKBitmap bmp, int x)
    {
        for (var y = bmp.Height - 1; y >= 0; y--) if (IsBar(bmp.GetPixel(x, y))) return y;
        return -1;
    }

    private static bool IsBar(SKColor p) => p.Blue > 180 && p.Red < 80 && p.Green < 80;
}
