using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Paint;
using Xunit;

namespace CupriFace.Tests;

/// <summary>The chart controls: bar, line, sparkline and heatmap — data via attributes or children.</summary>
public class ChartTests
{
    private static List<RenderNode> All(TestDoc t, System.Func<RenderNode, bool> match)
    {
        var found = new List<RenderNode>();
        void Walk(RenderNode n) { if (match(n)) found.Add(n); foreach (var c in n.Children) Walk(c); }
        Walk(t.Root);
        return found;
    }
    private static List<RenderNode> Bars(TestDoc t) => All(t, n => n.Element?.ClassList.Contains("cupri-bc-bar") == true);

    [Fact]
    public void Bar_chart_heights_are_proportional_to_the_values()
    {
        using var t = new TestDoc(
            "<body><cupri-bar-chart values=\"12,19,7,15,22,9,14\" labels=\"Mo,Tu,We,Th,Fr,Sa,Su\"></cupri-bar-chart></body>",
            "", null, components: true, width: 500, height: 260);

        var bars = Bars(t);
        Assert.Equal(7, bars.Count);
        Assert.Equal(7, All(t, n => n.Element?.LocalName == "span").Count(n => n.Parent?.Element?.ClassList.Contains("cupri-bc-labels") == true));

        double H(int i) => bars[i].Height;
        Assert.True(H(4) > H(1) && H(1) > H(3) && H(3) > H(0)); // 22 > 19 > 15 > 12
        Assert.True(H(2) < H(5));                               // 7 < 9 (shortest ordering)
        Assert.Equal(12.0 / 22.0, H(0) / H(4), 1);             // heights track the value ratio (flex % resolves)
    }

    [Fact]
    public void Bar_chart_children_override_attributes_and_set_per_bar_colour()
    {
        using var t = new TestDoc(
            "<body><cupri-bar-chart>" +
            "<cupri-bar value=\"10\" label=\"A\" color=\"#ff0000\"></cupri-bar>" +
            "<cupri-bar value=\"5\" label=\"B\"></cupri-bar></cupri-bar-chart></body>",
            "", null, components: true, width: 300, height: 240);

        var bars = Bars(t);
        Assert.Equal(2, bars.Count);
        Assert.Contains("#ff0000", bars[0].Element!.GetAttribute("style")!); // per-bar colour
        Assert.True(bars[0].Height > bars[1].Height);                        // 10 > 5
    }

    [Fact]
    public void Line_chart_emits_a_polyline_with_area_and_dots()
    {
        using var t = new TestDoc(
            "<body><cupri-line-chart values=\"4,8,5,10\" area dots></cupri-line-chart></body>",
            "", null, components: true, width: 400, height: 220);

        var plot = t.FindClass("cupri-lc-plot");
        Assert.True(plot.Element!.HasAttribute("data-cupri-area"));
        Assert.True(plot.Element.HasAttribute("data-cupri-dots"));

        var cmds = t.Doc.BuildFrame(400, 220).Commands;
        var poly = cmds.OfType<Polyline>().Single();
        Assert.Equal(8, poly.Points.Count);          // 4 points × (x,y)
        Assert.True(poly.Fill.Alpha > 0);            // area fill on
        Assert.True(poly.Width > 0);                 // line stroked
        Assert.Equal(4, cmds.OfType<FillRect>().Count(r => r.Radius >= 3 && r.W == r.H)); // 4 round dots
    }

    [Fact]
    public void Line_without_area_or_dots_is_just_a_stroke()
    {
        using var t = new TestDoc(
            "<body><cupri-line-chart values=\"1,2,3\"></cupri-line-chart></body>",
            "", null, components: true, width: 300, height: 200);
        var poly = t.Doc.BuildFrame(300, 200).Commands.OfType<Polyline>().Single();
        Assert.Equal(0, (int)poly.Fill.Alpha); // no area
        Assert.True(poly.Width > 0);
    }

    [Fact]
    public void Sparkline_spans_the_full_width()
    {
        using var t = new TestDoc(
            "<body><cupri-sparkline values=\"1,2,3,4,5\"></cupri-sparkline></body>",
            "", null, components: true, width: 300, height: 120);

        var line = t.FindClass("cupri-sparkline").Element!.GetAttribute("data-cupri-line")!;
        var pts = line.Split(' ');
        Assert.Equal("0", pts[0].Split(',')[0]);   // first point at x=0
        Assert.Equal("1", pts[^1].Split(',')[0]);  // last point at x=1 (full width)
    }

    [Fact]
    public void Rolling_chart_uses_a_fixed_zero_to_max_range_and_clamps()
    {
        // A time-series monitor: fixed 0..max range (so the baseline is stable as the window scrolls),
        // full width, area fill, no dots. Values above max are clamped into the box.
        using var t = new TestDoc(
            "<body><cupri-rolling-chart values=\"0,50,100,200\" max=\"100\"></cupri-rolling-chart></body>",
            "", null, components: true, width: 400, height: 200);

        var plot = t.FindClass("cupri-rl-plot");
        Assert.True(plot.Element!.HasAttribute("data-cupri-area"));
        Assert.False(plot.Element.HasAttribute("data-cupri-dots"));

        var pts = plot.Element.GetAttribute("data-cupri-line")!.Split(' ');
        Assert.Equal(4, pts.Length);
        Assert.Equal("0", pts[0].Split(',')[0]);   // full width: first x=0
        Assert.Equal("1", pts[^1].Split(',')[0]);  // last x=1

        double Y(int i) => double.Parse(pts[i].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Y(0) > 0.9);        // value 0 → near the bottom (fixed range, not auto-scaled)
        Assert.True(Y(2) < 0.1);        // value 100 (== max) → near the top
        Assert.Equal(Y(2), Y(3), 3);    // value 200 clamped to the same top as 100
    }

    [Fact]
    public void Rolling_chart_paints_an_area_polyline()
    {
        using var t = new TestDoc(
            "<body><cupri-rolling-chart values=\"10,20,15,25,30\" max=\"40\"></cupri-rolling-chart></body>",
            "", null, components: true, width: 400, height: 200);
        var poly = t.Doc.BuildFrame(400, 200).Commands.OfType<Polyline>().Single();
        Assert.Equal(10, poly.Points.Count); // 5 points
        Assert.True(poly.Fill.Alpha > 0);    // area filled
    }

    [Fact]
    public void Heatmap_lays_out_a_grid_and_tints_cells_by_intensity()
    {
        using var t = new TestDoc(
            "<body><cupri-heatmap columns=\"4\" values=\"0,2,4,1,3\"></cupri-heatmap></body>",
            "", null, components: true, width: 300, height: 160);

        Assert.Contains("repeat(4,1fr)", t.FindClass("cupri-heatmap").Element!.GetAttribute("style")!);
        var cells = All(t, n => n.Element?.ClassList.Contains("cupri-hm-cell") == true);
        Assert.Equal(5, cells.Count);
        Assert.Contains("0.12)", cells[0].Element!.GetAttribute("style")!); // value 0 → faintest
        Assert.Contains(",1)", cells[2].Element!.GetAttribute("style")!);   // value 4 (max) → solid
    }
}
