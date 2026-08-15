using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The touch gesture layer, driven with a scripted clock — every timing behaviour here is
/// deterministic on purpose. The one fact underneath all of it: on the MOUSE path, activation
/// fires on pointer-down; on the TOUCH path it must not, or scrolling a page of buttons presses
/// one. These tests are the contract for that difference.
/// </summary>
public class TouchInputTests
{
    private sealed class Model { public bool On { get; set; } public int Volume { get; set; } = 50; }

    private static (float X, float Y) Center(TestDoc t, string role)
    {
        var tree = t.Doc.BuildAccessibilityTree(t.Width, t.Height);
        var n = Find(tree, role) ?? throw new Xunit.Sdk.XunitException($"no {role} in tree");
        return (n.Bounds.X + n.Bounds.W / 2, n.Bounds.Y + n.Bounds.H / 2);
    }

    private static CupriFace.Accessibility.AccessibilityNode? Find(
        CupriFace.Accessibility.AccessibilityNode n, string role)
    {
        if (n.Role == role) return n;
        foreach (var c in n.Children) if (Find(c, role) is { } f) return f;
        return null;
    }

    private static bool AnyAttr(TestDoc t, string attr) =>
        t.Find(n => n.Element?.HasAttribute(attr) == true) is not null;

    // ---- tap ----------------------------------------------------------------------------------

    [Fact]
    public void A_tap_activates_on_finger_up_not_down()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Down(x, y, 0.0);
        Assert.False(m.On);                    // the down did NOT toggle — the deferral is the point

        touch.Up(x, y, 0.05);
        Assert.True(m.On);                     // the up did
    }

    [Fact]
    public void A_press_that_scrolls_never_activates()
    {
        var m = new Model();
        const string css = ".box{height:100px;overflow:auto;} .pad{height:400px;}";
        const string html = """
            <body><div class="box">
              <cupri-switch checked="{{On}}">X</cupri-switch>
              <div class="pad"></div>
            </div></body>
            """;
        using var t = new TestDoc(html, css, m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Down(x, y, 0.00);
        touch.Move(x, y - 30, 0.02);           // well past the slop — this is a scroll
        touch.Move(x, y - 60, 0.04);
        touch.Up(x, y - 60, 0.06);

        Assert.False(m.On);                    // the switch under the finger was never pressed
        t.Layout();
        var scroller = t.Find(n => n.IsScrollable)!;
        Assert.True(scroller.ScrollY > 0, "the drag scrolled the container");
    }

    [Fact]
    public void Press_feedback_appears_on_down_and_leaves_when_the_press_becomes_a_scroll()
    {
        var m = new Model();
        const string css = ".box{height:100px;overflow:auto;} .pad{height:400px;}";
        const string html = """
            <body><div class="box">
              <cupri-switch checked="{{On}}">X</cupri-switch>
              <div class="pad"></div>
            </div></body>
            """;
        using var t = new TestDoc(html, css, m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Down(x, y, 0.0);
        Assert.True(AnyAttr(t, "data-active"));            // :active press feedback, immediately

        touch.Move(x, y - 40, 0.02);                       // becomes a scroll
        Assert.False(AnyAttr(t, "data-active"));           // feedback withdrawn — no phantom press
    }

    [Fact]
    public void Touch_never_sets_hover()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Down(x, y, 0.0);
        touch.Move(x + 2, y + 2, 0.01);
        touch.Up(x, y, 0.05);

        Assert.False(AnyAttr(t, "data-hover"));            // structural suppression: no code path
    }

    [Fact]
    public void A_cancelled_press_activates_nothing_and_leaves_no_feedback()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Down(x, y, 0.0);
        touch.Cancel(0.1);

        Assert.False(m.On);
        Assert.False(AnyAttr(t, "data-active"));
    }

    [Fact]
    public void A_rebuild_mid_press_does_not_strand_active_marks()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "switch");

        touch.Down(x, y, 0.0);
        t.Doc.Refresh();                        // the per-keystroke rebuild, mid-press
        t.Layout();
        touch.Up(x, y, 0.05);                   // must not throw, must not leave marks

        Assert.False(AnyAttr(t, "data-active"));
    }

    // ---- drag surfaces ------------------------------------------------------------------------

    [Fact]
    public void A_slider_drags_from_the_first_touch()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body><cupri-slider min=\"0\" max=\"100\" value=\"{{Volume}}\"></cupri-slider></body>",
            "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "slider");

        touch.Down(x, y, 0.0);                  // no deferral for a dedicated drag affordance
        touch.Move(x + 60, y, 0.02);
        var during = m.Volume;
        touch.Up(x + 60, y, 0.04);

        Assert.NotEqual(50, during);            // the value moved WHILE dragging, not at release
    }

    // ---- double tap ---------------------------------------------------------------------------

    [Fact]
    public void A_double_tap_selects_the_word_under_the_finger()
    {
        var m = new BoundText();
        using var t = new TestDoc("<body><cupri-textfield value=\"{{Text}}\"></cupri-textfield></body>",
            "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "textbox");

        touch.Down(x, y, 0.00); touch.Up(x, y, 0.05);      // tap 1: focus + caret
        touch.Down(x, y, 0.15); touch.Up(x, y, 0.20);      // tap 2, inside the window: word select

        var selection = t.Doc.CopySelection();
        Assert.False(string.IsNullOrEmpty(selection));
        Assert.Contains(selection!, m.Text);               // a real word from the field
    }

    private sealed class BoundText { public string Text { get; set; } = "alpha bravo charlie delta echo foxtrot golf hotel"; }

    // ---- long press ---------------------------------------------------------------------------

    [Fact]
    public void A_still_long_press_opens_the_context_menu_and_swallows_the_up()
    {
        var m = new BoundText();
        using var t = new TestDoc("<body><cupri-textfield value=\"{{Text}}\"></cupri-textfield></body>",
            "", m, components: true);
        var touch = new TouchInput(t.Doc);
        var (x, y) = Center(t, "textbox");

        touch.Down(x, y, 0.0);
        Assert.NotNull(touch.NextDeadline);                // the host would arm a timer for this
        Assert.False(touch.Tick(0.3));                     // too early — nothing fires
        Assert.True(touch.Tick(0.6));                      // past the deadline — menu opens

        var upChanged = touch.Up(x, y, 0.7);
        Assert.False(upChanged);                           // the release is swallowed, no tap
    }
}
