using Xunit;

namespace CupriFace.Tests;

/// <summary>Hosts render once per frame and dispatch input in between, so input regularly arrives
/// against a tree that a hover or model change just replaced and nothing has laid out yet. Hit-testing
/// such a tree finds nothing (every box is zero), which silently swallowed the input — most visibly a
/// click landing in the same frame as the hover that preceded it. The engine now lays out on demand
/// before any hit-test, so these all work without the host rendering in between.</summary>
public class InputBeforeLayoutTests
{
    private sealed class Model { public string Section { get; set; } = "one"; public bool On { get; set; } }

    // Note: no TestDoc.Move/Click helpers here — those re-lay-out after each step, which is exactly the
    // step a real host does NOT take between two input events in the same frame.
    private static TestDoc Doc(Model m) => new TestDoc(
        "<body><div style='padding:16px'>" +
        "<div class='nav' style='width:120px;height:34px'>Charts</div>" +
        "<div class='other' style='width:120px;height:34px'>Elsewhere</div>" +
        "<cupri-switch checked=\"{{On}}\"></cupri-switch>" +
        "</div></body>",
        ".nav[data-hover]{background:#eee}", m, components: true, width: 300, height: 240);

    [Fact]
    public void A_click_in_the_same_frame_as_the_hover_that_preceded_it_still_lands()
    {
        var m = new Model();
        using var t = Doc(m);
        t.Doc.OnClick(".nav", _ => m.Section = "charts");

        var (ox, oy) = TestDoc.Center(t.FindClass("other"));
        var (nx, ny) = TestDoc.Center(t.FindClass("nav"));
        t.Move(ox, oy);                        // park elsewhere (laid out)

        // The host's real sequence for "move onto it and click" inside one frame — no render between.
        t.Doc.DispatchPointerMove(nx, ny);     // hover change → restyle → fresh, unlaid tree
        t.Doc.DispatchClick(nx, ny);

        Assert.Equal("charts", m.Section);
    }

    [Fact]
    public void A_control_toggles_when_clicked_in_the_same_frame_as_its_hover()
    {
        var m = new Model();
        using var t = Doc(m);
        var (ox, oy) = TestDoc.Center(t.FindClass("other"));
        var (sx, sy) = TestDoc.Center(t.FindRole("switch"));
        t.Move(ox, oy);

        t.Doc.DispatchPointerMove(sx, sy);
        t.Doc.DispatchClick(sx, sy);

        Assert.True(m.On);
    }

    [Fact]
    public void The_wheel_scrolls_a_container_hovered_in_the_same_frame()
    {
        using var t = new TestDoc(
            "<body><div class='away' style='height:20px'>x</div>" +
            "<div class='sc' style='height:80px;overflow:scroll'>" +
            "<div style='height:60px'>a</div><div style='height:60px'>b</div><div style='height:60px'>c</div>" +
            "</div></body>", ".sc[data-hover]{background:#fafafa}", components: true, width: 260, height: 200);

        var (ax, ay) = TestDoc.Center(t.FindClass("away"));
        var (sx, sy) = TestDoc.Center(t.FindClass("sc"));
        t.Move(ax, ay);

        t.Doc.DispatchPointerMove(sx, sy);     // hover change → unlaid tree
        t.Doc.DispatchWheel(sx, sy, 40f);
        t.Layout();

        Assert.True(t.FindClass("sc").ScrollY > 1f, "the wheel should have scrolled the hovered container");
    }
}
