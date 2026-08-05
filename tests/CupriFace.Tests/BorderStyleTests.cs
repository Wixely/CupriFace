using Xunit;

namespace CupriFace.Tests;

public class BorderStyleTests
{
    // Count red pixels along a horizontal line through the top border stroke.
    private static int RedRun(string style)
    {
        var css = $"body{{background:#ffffff}} .b{{width:200px;height:60px;margin:10px;border:6px {style} #ff0000;}}";
        using var t = new TestDoc("<body><div class='b'>x</div></body>", css, width: 240, height: 100);
        using var bmp = t.Render(SkiaSharp.SKColors.White);
        var red = 0;
        for (var x = 16; x < 206; x++) // across the top border span (y within the 6px border at margin 10)
        {
            var p = bmp.GetPixel(x, 13);
            if (p.Red > 180 && p.Green < 80 && p.Blue < 80) red++;
        }
        return red;
    }

    [Fact]
    public void Solid_is_continuous_dashed_and_dotted_leave_gaps()
    {
        var solid = RedRun("solid");
        var dashed = RedRun("dashed");
        var dotted = RedRun("dotted");
        var none = RedRun("none");

        Assert.True(solid > 170, $"solid={solid}");
        Assert.True(dashed < solid - 30 && dashed > 20, $"dashed={dashed} solid={solid}");
        Assert.True(dotted < solid - 30 && dotted > 10, $"dotted={dotted} solid={solid}");
        Assert.True(dotted <= dashed, $"dotted={dotted} dashed={dashed}");
        Assert.True(none < 5, $"none={none}");
    }
}
