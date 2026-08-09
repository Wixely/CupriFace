using System.Collections.Generic;
using CupriFace.Dom;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Element-based charts (bars, stacked segments, heatmap cells) reveal a value tooltip on hover —
/// a fixed bubble anchored above the hovered element via the shared <c>.cupri-chart-tip</c>.</summary>
public class ChartTooltipTests
{
    private static RenderNode? VisibleTip(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-chart-tip") == true && n.Style.Display != DisplayType.None);

    private static List<RenderNode> ByClass(TestDoc t, string cls)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains(cls) == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Hovering_a_bar_reveals_its_label_and_value()
    {
        using var t = new TestDoc(
            "<body><div style='padding:30px'><cupri-bar-chart values=\"12,19,7\" labels=\"Mon,Tue,Wed\"></cupri-bar-chart></div></body>",
            "", components: true, width: 420, height: 280);
        Assert.Null(VisibleTip(t));                          // nothing at rest

        var (x, y) = TestDoc.Center(ByClass(t, "cupri-bc-bar")[1]); // the "Tue" bar (value 19)
        t.Move(x, y);

        var tip = VisibleTip(t);
        Assert.NotNull(tip);
        Assert.True(tip!.Width > 0 && tip.Height > 0);       // anchored + laid out
        Assert.Contains("Tue: 19", tip.Element!.TextContent);

        t.Move(2, 2);                                        // move away
        Assert.Null(VisibleTip(t));                          // hidden again
    }

    [Fact]
    public void Hovering_a_line_point_reveals_its_value()
    {
        using var t = new TestDoc(
            "<body><div style='padding:30px'><cupri-line-chart values=\"5,8,6,11\" labels=\"W1,W2,W3,W4\"></cupri-line-chart></div></body>",
            "", components: true, width: 440, height: 300);
        var dots = ByClass(t, "cupri-lc-dot");
        Assert.Equal(4, dots.Count);                         // one hover target per point

        var (x, y) = TestDoc.Center(dots[1]);                // W2, value 8
        t.Move(x, y);
        var tip = VisibleTip(t);
        Assert.NotNull(tip);
        Assert.Contains("W2: 8", tip!.Element!.TextContent);
    }

    [Fact]
    public void Hovering_a_heatmap_cell_reveals_its_value()
    {
        using var t = new TestDoc(
            "<body><div style='padding:30px'><cupri-heatmap columns=\"3\" values=\"1,5,9,3,7,2\"></cupri-heatmap></div></body>",
            "", components: true, width: 320, height: 220);

        var (x, y) = TestDoc.Center(ByClass(t, "cupri-hm-cell")[2]); // value 9
        t.Move(x, y);

        var tip = VisibleTip(t);
        Assert.NotNull(tip);
        Assert.Contains("9", tip!.Element!.TextContent);
    }
}
