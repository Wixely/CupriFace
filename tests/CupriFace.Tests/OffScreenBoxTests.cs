using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// CF0076: a positioned box — a popup, tooltip, popover, badge or toast — that lies partly outside
/// the viewport, where nothing can scroll it into view.
///
/// <para>Found while investigating state sweeps: a <c>cupri-menu</c> holding one long item laid out
/// its popup 509px wide at x=4 in a 412px viewport. Its items fit it, the viewport is not its
/// parent, so neither overflow check had anything to say — and 101px of the menu did not exist.</para>
/// </summary>
public class OffScreenBoxTests(ITestOutputHelper output)
{
    private sealed class Flags { public bool Open { get; set; } = true; }

    private const string Menu =
        "<body><cupri-menu open='{{Open}}' label='Menu'><cupri-menu-item>https://example.com/a/very/long/path/that/does/not/break/anywhere/at/all</cupri-menu-item></cupri-menu></body>";

    [Fact]
    public void A_menu_popup_wider_than_the_phone_it_opens_on_is_reported()
    {
        var report = CupriDoctor.Check(Menu, "", width: 412, height: 400, model: new Flags());
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0076");
        Assert.Contains("cupri-menu-popup", f.Message);
        Assert.Contains("right-hand edge", f.Message);
        Assert.Contains("wider than the viewport", f.Message);
        Assert.Contains("max-width", f.Fix);
    }

    [Fact]
    public void The_same_menu_on_a_desktop_is_not_reported()
    {
        var report = CupriDoctor.Check(Menu, "", width: 1024, height: 400, model: new Flags());
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0076");
    }

    [Fact]
    public void A_closed_menu_is_not_reported()
    {
        var report = CupriDoctor.Check(Menu, "", width: 412, height: 400, model: new Flags { Open = false });
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0076");
    }

    [Fact]
    public void A_fixed_toast_pushed_past_the_left_edge_is_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='page'>p</div><div class='toast'>Saved</div></body>",
            "body{margin:0} .toast{position:fixed;left:-60px;top:10px;width:120px;height:30px;background:#333;color:#fff}",
            width: 400, height: 300);
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0076");
        Assert.Contains("left-hand edge", f.Message);
        Assert.DoesNotContain("wider than the viewport", f.Message);
    }

    [Fact]
    public void A_fixed_box_below_the_viewport_is_reported_because_it_cannot_scroll()
    {
        var report = CupriDoctor.Check(
            "<body><div class='bar'>bar</div></body>",
            "body{margin:0} .bar{position:fixed;left:0;top:290px;width:100px;height:40px;background:#333;color:#fff}",
            width: 400, height: 300);
        Assert.Contains(report.Findings, x => x.Code == "CF0076" && x.Message.Contains("bottom edge"));
    }

    /// <summary>Below the fold is where a scrolling page keeps most of itself. An absolute box down
    /// there is reachable, so it is not a finding — only sideways and above are.</summary>
    [Fact]
    public void An_absolute_box_below_the_fold_is_not_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='tall'><div class='pop'>pop</div></div></body>",
            "body{margin:0} .tall{position:relative;height:2000px} .pop{position:absolute;left:10px;top:1500px;width:100px;height:40px;background:#333;color:#fff}",
            width: 400, height: 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0076");
    }

    [Fact]
    public void An_absolute_box_past_the_right_edge_is_reported_once_for_the_outermost()
    {
        var report = CupriDoctor.Check(
            "<body><div class='host'><div class='pop'><div class='inner'>deep</div></div></div></body>",
            "body{margin:0} .host{position:relative;height:100px} .pop{position:absolute;left:350px;top:10px;width:200px;height:40px;background:#333;color:#fff} .inner{position:absolute;left:0;top:0;width:200px;height:40px}",
            width: 400, height: 300);
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0076");
        Assert.Contains("<div class='pop'>", f.Message);
    }

    /// <summary>Inside something that scrolls sideways the box can be dragged to, which is the same
    /// exemption the overflow checks give.</summary>
    [Fact]
    public void A_box_inside_a_sideways_scroller_is_not_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='strip'><div class='wide'><div class='pop'>pop</div></div></div></body>",
            "body{margin:0} .strip{overflow:scroll;width:400px;height:100px} .wide{position:relative;width:1200px;height:80px} .pop{position:absolute;left:900px;top:10px;width:100px;height:40px;background:#333;color:#fff}",
            width: 400, height: 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0076");
    }

    [Fact]
    public void An_empty_or_faded_positioned_box_is_not_reported()
    {
        var report = CupriDoctor.Check(
            "<body><div class='host'><div class='empty'></div><div class='faded'>x</div></div></body>",
            "body{margin:0} .host{position:relative;height:100px} .empty{position:absolute;left:380px;top:0;width:100px;height:20px} .faded{position:absolute;left:380px;top:40px;width:100px;height:20px;opacity:0}",
            width: 400, height: 300);
        Assert.DoesNotContain(report.Findings, x => x.Code == "CF0076");
    }
}
