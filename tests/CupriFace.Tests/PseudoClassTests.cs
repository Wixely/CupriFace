using Xunit;

namespace CupriFace.Tests;

public class PseudoClassTests
{
    private const string Css = """
        body { background:#ffffff; }
        .btn { width:120px; height:48px; margin:20px; background:#0000ff; }
        .btn:hover  { background:#00ff00; }
        .btn:active { background:#ff0000; }
        """;
    private const string Html = "<body><div class='btn'>press</div></body>";

    private static string Hex(SkiaSharp.SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";

    [Fact]
    public void Hover_and_active_press_cycle_drive_the_background()
    {
        using var t = new TestDoc(Html, Css, width: 300, height: 160);
        Assert.Equal("#0000ff", Hex(t.FindClass("btn").Style.Background)); // base blue

        var (cx, cy) = TestDoc.Center(t.FindClass("btn"));
        t.Move(cx, cy);
        Assert.Equal("#00ff00", Hex(t.FindClass("btn").Style.Background)); // :hover green

        t.Click(cx, cy);
        Assert.Equal("#ff0000", Hex(t.FindClass("btn").Style.Background)); // :active red (pressed)

        var repaint = t.Doc.DispatchPointerUp(cx, cy); t.Layout();
        Assert.True(repaint);                                             // pointer-up requests a repaint
        Assert.Equal("#00ff00", Hex(t.FindClass("btn").Style.Background)); // back to hover

        t.Move(290, 150);
        Assert.Equal("#0000ff", Hex(t.FindClass("btn").Style.Background)); // move off → base
    }

    [Fact]
    public void Model_refresh_preserves_hover_under_a_stationary_pointer()
    {
        using var t = new TestDoc(Html, Css, width: 300, height: 160);
        var (cx, cy) = TestDoc.Center(t.FindClass("btn"));
        t.Move(cx, cy);
        Assert.Equal("#00ff00", Hex(t.FindClass("btn").Style.Background));

        t.Doc.Refresh();
        t.Layout();

        Assert.Equal("#00ff00", Hex(t.FindClass("btn").Style.Background));
    }
}
