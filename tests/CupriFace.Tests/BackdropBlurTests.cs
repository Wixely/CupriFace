using CupriFace.Paint;
using CupriFace.Style;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// backdrop-filter: a modal / drawer / shelf can frost the page behind it. The engine has no Skia
/// backdrop-capture, so a full-viewport top-layer scrim's backdrop-filter blurs the whole main-content
/// pass as one group (it paints first; the scrim + panel paint sharp on top).
/// </summary>
public class BackdropBlurTests
{
    private sealed class M { public bool Open { get; set; } = true; public bool Blur { get; set; } }

    private static bool HasBlur(System.Collections.Generic.IReadOnlyList<FilterOp>? ops)
    {
        if (ops is null) return false;
        foreach (var o in ops) if (o.Kind == FilterKind.Blur && o.A > 0) return true;
        return false;
    }

    [Fact]
    public void Dialog_blur_flag_frosts_the_backdrop_scrim()
    {
        using var t = new TestDoc(
            "<body><div style='height:300px'>bg</div><cupri-dialog open=\"true\" blur=\"true\"><p>Hi</p></cupri-dialog></body>",
            "", null, components: true, width: 400, height: 400);

        var backdrop = t.FindClass("cupri-backdrop");
        Assert.True(backdrop.Element!.ClassList.Contains("blurred"));
        Assert.True(HasBlur(backdrop.Style.BackdropFilter));
    }

    [Fact]
    public void Dialog_without_blur_is_a_plain_scrim()
    {
        using var t = new TestDoc(
            "<body><cupri-dialog open=\"true\"><p>Hi</p></cupri-dialog></body>",
            "", null, components: true, width: 400, height: 400);

        var backdrop = t.FindClass("cupri-backdrop");
        Assert.False(backdrop.Element!.ClassList.Contains("blurred"));
        Assert.Null(backdrop.Style.BackdropFilter);
    }

    [Fact]
    public void An_open_blurred_overlay_wraps_the_page_in_one_blur_layer()
    {
        using var t = new TestDoc(
            "<body><div>content</div><cupri-dialog open=\"true\" blur=\"true\"><p>Hi</p></cupri-dialog></body>",
            "", null, components: true, width: 400, height: 400);

        var cmds = t.Doc.BuildFrame(400, 400).Commands;
        // The main content is wrapped first in a blur layer; a matching PopFilter closes it before the
        // scrim/panel paint on top.
        Assert.IsType<PushFilter>(cmds[0]);
        Assert.True(HasBlur(((PushFilter)cmds[0]).Ops));
        Assert.Contains(cmds, c => c is PopFilter);
    }

    [Fact]
    public void No_blur_layer_when_the_modal_is_closed_or_unblurred()
    {
        using var closed = new TestDoc(
            "<body><div>content</div><cupri-dialog open=\"true\"><p>Hi</p></cupri-dialog></body>",
            "", null, components: true, width: 400, height: 400);
        Assert.IsNotType<PushFilter>(closed.Doc.BuildFrame(400, 400).Commands[0]); // scrim present, but not blurred
    }

    [Fact]
    public void Blur_actually_softens_the_pixels_behind_the_scrim()
    {
        // A hard black bar on white, behind a transparent-scrim overlay: with backdrop-filter the bar's
        // edge bleeds into the white below it; without it, the pixel there stays pure white.
        const string page = "<div style='width:400px;height:400px;background:white'>" +
                            "<div style='width:400px;height:20px;background:black'></div></div>";
        const string overlay = "<div style='position:fixed;top:0;left:0;width:100%;height:100%;background:#00000000;{F}'></div>";

        using var sharp = new TestDoc($"<body>{page}{overlay.Replace("{F}", "")}</body>", "", null, width: 400, height: 400);
        using var blurred = new TestDoc($"<body>{page}{overlay.Replace("{F}", "backdrop-filter:blur(6px)")}</body>", "", null, width: 400, height: 400);

        var pSharp = sharp.Render(SKColors.White).GetPixel(50, 24);   // just below the bar
        var pBlur = blurred.Render(SKColors.White).GetPixel(50, 24);
        Assert.Equal(255, pSharp.Red);                                // sharp: still white
        Assert.True(pBlur.Red < 235, $"blur should darken the pixel below the bar; got {pBlur}"); // softened toward black
    }

    [Fact]
    public void Shelf_is_a_full_width_bottom_sheet_that_can_blur()
    {
        using var t = new TestDoc(
            "<body><cupri-shelf open=\"true\" blur=\"true\"><p>Sheet</p></cupri-shelf></body>",
            "", null, components: true, width: 500, height: 400);

        var panel = t.FindClass("cupri-shelf-panel");
        Assert.Equal(500, panel.Width, 1);                            // spans the viewport width
        Assert.True(HasBlur(t.FindClass("cupri-backdrop").Style.BackdropFilter));
    }

    [Fact]
    public void Blur_toggles_live_with_a_bound_flag()
    {
        var m = new M { Open = true, Blur = false };
        using var t = new TestDoc(
            "<body><cupri-drawer open=\"{{Open}}\" blur=\"{{Blur}}\">" +
            "<cupri-switch checked=\"{{Blur}}\"></cupri-switch></cupri-drawer></body>",
            "", m, components: true, width: 420, height: 400);

        Assert.False(t.FindClass("cupri-backdrop").Element!.ClassList.Contains("blurred"));
        t.ClickNode(t.FindRole("switch"));                            // toggle the in-panel switch
        Assert.True(m.Blur);
        Assert.True(t.FindClass("cupri-backdrop").Element!.ClassList.Contains("blurred"));
    }
}
