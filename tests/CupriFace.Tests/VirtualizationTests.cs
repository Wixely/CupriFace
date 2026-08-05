using System.Linq;
using System.Text;
using CupriFace.Paint;
using Xunit;

namespace CupriFace.Tests;

public class VirtualizationTests
{
    // 200 rows, each a 20px div with a distinct background → one FillRect per painted row.
    private static string Rows(int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++) sb.Append("<div class='row'></div>");
        return sb.ToString();
    }

    private static readonly SkiaSharp.SKColor RowColor = new(0x33, 0x66, 0x99);

    private static int PaintedRows(DisplayList list) =>
        list.Commands.OfType<FillRect>().Count(f => f.Color == RowColor);

    [Fact]
    public void Scroll_container_paints_only_visible_rows()
    {
        const string css = "body{margin:0} .list{height:100px;overflow:scroll} .row{height:20px;background:#336699}";
        using var doc = CupriDocument.Load($"<body><div class='list'>{Rows(200)}</div></body>", css);

        var painted = PaintedRows(doc.BuildFrame(200, 200));
        Assert.InRange(painted, 4, 20);  // ~5 visible rows (+ margin), not 200
    }

    [Fact]
    public void Non_scrolling_container_paints_all_rows()
    {
        // overflow:visible → no viewport to cull against, every row is painted.
        const string css = "body{margin:0} .list{overflow:visible} .row{height:20px;background:#336699}";
        using var doc = CupriDocument.Load($"<body><div class='list'>{Rows(200)}</div></body>", css);

        Assert.Equal(200, PaintedRows(doc.BuildFrame(200, 5000)));
    }

    [Fact]
    public void Scrolling_down_paints_a_different_slice()
    {
        const string css = "body{margin:0} .list{height:100px;overflow:scroll} .row{height:20px;background:#336699}";
        using var doc = CupriDocument.Load($"<body><div class='list'>{Rows(200)}</div></body>", css);
        using var _ = doc.RenderToImage(200, 200);          // lay out so the list is scrollable

        var topSlice = PaintedRows(doc.BuildFrame(200, 200));
        doc.DispatchWheel(50, 50, 1500f);                    // scroll far down
        var downSlice = PaintedRows(doc.BuildFrame(200, 200));

        Assert.InRange(downSlice, 4, 20);                    // still only a window's worth
        // Both slices are small windows of the 200 rows — culling is active in both positions.
        Assert.True(topSlice < 30 && downSlice < 30);
    }
}
