using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class GridTests
{
    [Fact]
    public void Named_lines_place_items_by_name()
    {
        const string css = """
            body { margin:0; }
            .grid { display:grid; width:520px;
                    grid-template-columns: [side-start] 120px [side-end main-start] 1fr [main-end]; }
            .s { grid-column: side-start / side-end; }
            .m { grid-column: main-start / main-end; }
            """;
        using var t = new TestDoc("<body><div class='grid'><div class='s'>side</div><div class='m'>main</div></div></body>", css);

        var s = t.FindClass("s");
        var m = t.FindClass("m");
        Assert.Equal(120f, s.Width, 1);                     // the 120px named column
        Assert.Equal(400f, m.Width, 1);                     // the 1fr column (520 − 120)
        Assert.Equal(120f, HitTesting.AbsoluteBox(m).X, 1); // starts after the sidebar column
    }

    [Fact]
    public void Item_can_span_multiple_rows()
    {
        const string css = """
            body { margin:0; }
            .grid { display:grid; grid-template-columns: 1fr 1fr; grid-auto-rows: 50px; gap: 10px; }
            .tall { grid-row: span 2; }
            """;
        using var t = new TestDoc(
            "<body><div class='grid'><div class='tall'>t</div><div class='a'>a</div><div class='b'>b</div></div></body>", css);

        var tall = t.FindClass("tall");
        Assert.Equal(110f, tall.Height, 1);                 // two 50px rows + the 10px gap between them
    }
}
