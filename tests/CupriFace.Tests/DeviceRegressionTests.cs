using CupriFace.Accessibility;
using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Every test here is a bug a person found by using the app on a real phone, written back as the
/// thing that should have caught it. The emulator gate proves the platform seams; these prove the
/// behaviour behind them, in milliseconds, at the device's own dp geometry.
/// </summary>
public class DeviceRegressionTests
{
    private const int W = 393, H = 771;   // the reporting device's view, in dp

    private static AccessibilityNode? Named(AccessibilityNode n, string name)
    {
        if (n.Name == name) return n;
        foreach (var c in n.Children) if (Named(c, name) is { } f) return f;
        return null;
    }

    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> pred)
    {
        if (pred(n)) return n;
        foreach (var c in n.Children) if (Find(c, pred) is { } f) return f;
        return null;
    }

    private static void Nav(CupriDocument doc, string page)
    {
        var tree = doc.BuildAccessibilityTree(W, H);
        Assert.True(doc.AccessibilityActivate(Named(tree, page)!.Path));
        using var _ = doc.RenderToImage(W, H);
    }

    [Fact]
    public void A_link_inside_a_paragraph_can_be_tapped()
    {
        // Reported: "the thing that looks like a link is not clickable". Inline content owns no
        // box — layout zeroes it and positions text through fragments — so both hit-testing and
        // the reported accessibility rectangle were an empty point beside the words.
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }

        var link = Named(doc.BuildAccessibilityTree(W, H), "the repository");
        Assert.NotNull(link);
        Assert.Equal("link", link!.Role);
        Assert.True(link.Bounds.W > 20 && link.Bounds.H > 8,
            $"a screen reader cannot locate a {link.Bounds.W}x{link.Bounds.H} link");

        // Tap where a finger would land. Not the bounding box's centre: font metrics differ per
        // platform, and on Linux this link wraps — a wrapped link's box centre falls between its
        // two lines, on the paragraph rather than the link.
        var anchor = Find(doc.Root, n => n.Element?.LocalName == "a")!;
        var (tx, ty) = HitTesting.ActivationPoint(anchor);
        string? href = null;
        doc.Navigated += e => href = e.Href;
        doc.DispatchClick(tx, ty);
        Assert.Equal("https://github.com/Wixely/CupriFace", href);

        href = null;
        Assert.True(doc.AccessibilityActivate(link.Path));   // and from assistive tech
        Assert.Equal("https://github.com/Wixely/CupriFace", href);
    }

    [Fact]
    public void A_scrolled_list_comes_back_where_it_was_left()
    {
        // Reported: "scroll down the list, move to another page, then move back — blank until I
        // scroll again". A display:none page builds no children, so the scroller could not be
        // captured while hidden; it returned at 0 while the virtual window still described row 15,
        // and the rows were laid out past the bottom of an unscrolled viewport.
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "List");

        doc.DispatchWheel(W / 2f, 300, 700);
        using (doc.RenderToImage(W, H)) { }
        var scroller = Find(doc.Root, n => n.IsScrollable && n.MaxScrollY > 1000);
        Assert.NotNull(scroller);
        Assert.True(scroller!.ScrollY > 600);

        Nav(doc, "Home");
        Nav(doc, "List");

        scroller = Find(doc.Root, n => n.IsScrollable && n.MaxScrollY > 1000);
        Assert.NotNull(scroller);
        Assert.True(scroller!.ScrollY > 600, $"returned at {scroller.ScrollY:F0}, not where it was left");

        // …and the rows on screen must be the ones that offset implies, not an unrelated window.
        var first = int.MaxValue;
        void Rows(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("lrow") == true
                && int.TryParse(n.Element.TextContent.Trim().Split(' ')[1], out var i))
                first = Math.Min(first, i);
            foreach (var c in n.Children) Rows(c);
        }
        Rows(doc.Root);
        Assert.InRange(first, (int)(scroller.ScrollY / 48) - 6, (int)(scroller.ScrollY / 48) + 2);
    }

    [Fact]
    public void The_volume_slider_keeps_a_visible_track_in_a_settings_row()
    {
        // Reported: "the volume slider looks broken, stuck at 0 and moved all the way to the right,
        // no bar actually visible". As a flex item beside a flex:1 label its base size was its
        // content — and its content is absolutely positioned, so it collapsed to the thumb.
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "Settings");

        var slider = Find(doc.Root, n => n.Element?.GetAttribute("role") == "slider");
        Assert.NotNull(slider);
        Assert.True(slider!.Width >= 120, $"slider collapsed to {slider.Width:F0}px");

        var track = slider.Children.Count > 0 ? slider.Children[0] : null;
        Assert.NotNull(track);
        Assert.True(track!.Width > 100, $"track collapsed to {track.Width:F0}px — nothing to see or drag");

        // The fill has to reflect the bound value (60), not sit at zero.
        var fill = track.Children.Count > 0 ? track.Children[0] : null;
        Assert.NotNull(fill);
        Assert.InRange(fill!.Width / track.Width, 0.5, 0.7);
    }

    [Fact]
    public void Each_form_field_asks_for_its_own_kind_of_keyboard()
    {
        // Reported: "the name field only takes numbers, the amount field gives full keyboard with
        // text". The engine was right all along — the Android host answered the IME from the last
        // PAINTED frame, which described the previously focused field. This pins the engine half.
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "Form");

        (bool Numeric, bool Masked, bool Multiline) Kind(string tag)
        {
            var node = Find(doc.Root, n => n.Element?.LocalName == tag)
                       ?? throw new Xunit.Sdk.XunitException($"no <{tag}>");
            var (x, y, w, h) = HitTesting.ScreenBox(node);
            doc.DispatchClick(x + w / 2, y + h / 2);
            doc.DispatchPointerUp(x + w / 2, y + h / 2);
            using var _ = doc.RenderToImage(W, H);
            var s = doc.GetTextInputState();
            Assert.True(s.Focused, $"<{tag}> did not take focus");
            return (s.Numeric, s.Masked, s.Multiline);
        }

        Assert.Equal((false, false, false), Kind("cupri-textfield"));
        Assert.Equal((true, false, false), Kind("cupri-number"));
        Assert.Equal((false, true, false), Kind("cupri-password"));
        Assert.Equal((false, false, true), Kind("cupri-textarea"));
    }

    [Fact]
    public void The_command_palette_fits_a_phone_screen_and_takes_focus()
    {
        // Reported: "the search box appears and cuts off (it doesn't fit onto the screen), and when
        // I click the command box I don't get a keyboard". The panel was a fixed 520px — 74px off
        // BOTH edges of this screen — and its focus arrives without a focus event, which is the
        // host's cue for the keyboard (the host now diffs post-frame state as well).
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var p = app.Present(W, H);
        using (doc.RenderToImage((int)p.LogicalWidth, (int)p.LogicalHeight)) { }

        ((ShowcaseModel)app.Model!).PaletteOpen = true;
        doc.Refresh();
        using (doc.RenderToImage((int)p.LogicalWidth, (int)p.LogicalHeight)) { }

        var panel = Find(doc.Root, n => n.Element?.ClassList.Contains("cupri-cmdp-panel") == true);
        Assert.NotNull(panel);
        var (x, _, w, _) = HitTesting.ScreenBox(panel!);
        Assert.True(x >= 0 && x + w <= p.LogicalWidth,
            $"panel spans {x:F0}..{x + w:F0} on a {p.LogicalWidth:F0}-wide screen");

        var state = doc.GetTextInputState();
        Assert.True(state.Focused);            // what tells a host to raise the soft keyboard
        Assert.Equal("textbox", state.Role);
    }

    [Fact]
    public void The_sidebar_toggle_still_works_below_the_narrow_breakpoint()
    {
        // Asked: "when the zoom is set to anything except None, the left side panel cannot and will
        // not expand out — is this intended?" The auto-collapse is; being unable to override it is
        // not. Zoom divides the logical width, so an ordinary window lands under the breakpoint and
        // the @media rule beat the toggle's class.
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Scaling = "zoom";
        model.ZoomPct = 200;                                   // 1200px window -> 600 logical
        var p = app.Present(1200, 800);
        Assert.True(p.LogicalWidth <= 760, "this test needs to be under the breakpoint");
        using (doc.RenderToImage((int)p.LogicalWidth, (int)p.LogicalHeight)) { }

        var rail = Find(doc.Root, n => n.Element?.ClassList.Contains("sidebar") == true)!.Width;

        model.Sidebar = "expanded";                            // what the toggle sets
        doc.Refresh();
        using (doc.RenderToImage((int)p.LogicalWidth, (int)p.LogicalHeight)) { }
        var expanded = Find(doc.Root, n => n.Element?.ClassList.Contains("sidebar") == true)!.Width;

        Assert.True(expanded > rail + 60,
            $"an explicit expand did nothing: {rail:F0}px -> {expanded:F0}px");
    }

    [Fact]
    public void The_showcase_starts_at_one_to_one()
    {
        // Asked for: "in the desktop showcase, set the default zoom to 100%".
        var app = new ShowcaseApp();
        var p = app.Present(1400, 900);
        Assert.Equal(1f, p.Scale, 3);
        Assert.Equal(1400f, p.LogicalWidth, 1);
    }

    [Fact]
    public void The_fullscreen_switch_asks_the_host_for_immersive_mode()
    {
        // Asked for: "add an option to move to true fully fullscreen mode, like a fullscreen game
        // with no top bar and no home/back buttons."
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        var commands = new List<WindowCommand>();
        doc.WindowCommandRequested += commands.Add;
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "Settings");

        var tree = doc.BuildAccessibilityTree(W, H);
        var sw = Named(tree, "Fullscreen") ?? throw new Xunit.Sdk.XunitException("no fullscreen row");
        Assert.True(doc.AccessibilityActivate(sw.Path));
        Assert.Equal(new[] { WindowCommand.EnterFullscreen }, commands);
        Assert.True(((MobileModel)app.Model!).Fullscreen);      // the switch still owns its value

        using (doc.RenderToImage(W, H)) { }
        tree = doc.BuildAccessibilityTree(W, H);
        Assert.True(doc.AccessibilityActivate(Named(tree, "Fullscreen")!.Path));
        Assert.Equal(WindowCommand.ExitFullscreen, commands[^1]);
        Assert.False(((MobileModel)app.Model!).Fullscreen);
    }

    [Fact]
    public void The_dark_mode_row_toggles_when_the_sidebar_is_a_rail()
    {
        // Reported: "when the sidepanel is minimised, the Dark toggle is just an eye and tapping it
        // does nothing". The switch is display:none in the rail, so the only thing left to tap was
        // an icon with no behaviour behind it.
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Sidebar = "collapsed";
        var p = app.Present(420, 800);
        using (doc.RenderToImage((int)p.LogicalWidth, (int)p.LogicalHeight)) { }

        var row = Find(doc.Root, n => n.Element?.ClassList.Contains("side-toggle") == true);
        Assert.NotNull(row);
        var (x, y) = HitTesting.ActivationPoint(row!);
        Assert.False(model.DarkMode);
        doc.DispatchClick(x, y);
        Assert.True(model.DarkMode, "tapping the collapsed row did nothing");
    }

    [Fact]
    public void The_app_states_which_build_it_is()
    {
        // Asked for after a session spent chasing bugs that were already fixed, on a phone that
        // had silently kept the previous APK: "make sure to include some kind of version number or
        // build date in the app so i can not fall into this trap again."
        var describe = BuildInfo.Describe();
        Assert.StartsWith("v", describe);
        Assert.Contains("built", describe);          // the stamp is what moves between rebuilds

        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "About");

        var shown = Find(doc.Root, n => n.Element?.ClassList.Contains("build") == true);
        Assert.NotNull(shown);
        Assert.Equal(describe, shown!.Element!.TextContent.Trim());
    }

    [Fact]
    public void The_showcase_kanban_scrolls_sideways_on_a_phone()
    {
        // Reported: "the desktop version on the phone still has sections cut off". Shrinking them
        // to fit was the honest answer while the engine had one scrolling axis. Now a finger can
        // drag horizontally, so the desktop layout is REACHABLE rather than merely visible.
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        var model = (ShowcaseModel)app.Model!;
        model.Section = "components";
        var p = app.Present(W, H);
        using (doc.RenderToImage((int)p.LogicalWidth, (int)p.LogicalHeight)) { }

        var scroller = Find(doc.Root, n => n.Element?.ClassList.Contains("hscroll") == true);
        Assert.NotNull(scroller);
        Assert.True(scroller!.IsScrollableX,
            $"the board fits in {scroller.Width:F0}px, so there is nothing to drag");

        var (x, y, w, h) = HitTesting.ScreenBox(scroller);
        Assert.True(doc.DispatchWheel(x + w / 2, y + h / 2, 0, 100));
        Assert.True(scroller.ScrollX > 50);
    }
}
