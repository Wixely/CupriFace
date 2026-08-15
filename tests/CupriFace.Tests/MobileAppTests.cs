using CupriFace.Accessibility;
using CupriFace.Demo;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The phone-first sample, built and driven headlessly — so a markup typo, a broken binding or a
/// renamed model property fails here in milliseconds on any OS, not on an emulator gate twenty
/// minutes later. Also pins the two behaviours the Android CI gate taps for.
/// </summary>
public class MobileAppTests
{
    private static CupriFace.CupriDocument Doc(MobileApp app)
    {
        var doc = app.CreateDocument();
        using var img = doc.RenderToImage(400, 800);       // layout + paint: the whole page builds
        return doc;
    }

    private static int Count(AccessibilityNode n, string role)
    {
        var c = n.Role == role ? 1 : 0;
        foreach (var k in n.Children) c += Count(k, role);
        return c;
    }

    private static AccessibilityNode? FindNamed(AccessibilityNode n, string name)
    {
        if (n.Name == name) return n;
        foreach (var k in n.Children) if (FindNamed(k, name) is { } f) return f;
        return null;
    }

    [Fact]
    public void The_mobile_app_builds_renders_and_exposes_its_nav()
    {
        var app = new MobileApp();
        using var doc = Doc(app);
        var tree = doc.BuildAccessibilityTree(400, 800);

        // Five named bottom-nav targets — the controls the CI gate computes tap points for.
        foreach (var nav in new[] { "Home", "List", "Form", "Settings", "About" })
            Assert.NotNull(FindNamed(tree, nav));
    }

    [Fact]
    public void The_gate_toggle_flips_the_model_through_the_action_seam()
    {
        var app = new MobileApp();
        using var doc = Doc(app);

        // Navigate to Settings the way a finger would (activate the nav item)…
        var tree = doc.BuildAccessibilityTree(400, 800);
        Assert.True(doc.AccessibilityActivate(FindNamed(tree, "Settings")!.Path));
        using var _ = doc.RenderToImage(400, 800);

        // …then activate the marked switch: the OnAction handler owns the toggle.
        tree = doc.BuildAccessibilityTree(400, 800);
        var sw = FindNamed(tree, "Notifications") ?? throw new Xunit.Sdk.XunitException("no switch");
        var model = (MobileModel)app.Model!;
        Assert.False(model.Notify);
        Assert.True(doc.AccessibilityActivate(sw.Path));
        Assert.True(model.Notify);
        Assert.True(doc.AccessibilityActivate(sw.Path));
        Assert.False(model.Notify);                        // alternation — one toggle per activation
    }

    [Fact]
    public void The_list_page_virtualises_and_scrolls()
    {
        var app = new MobileApp();
        using var doc = Doc(app);
        var tree = doc.BuildAccessibilityTree(400, 800);
        Assert.True(doc.AccessibilityActivate(FindNamed(tree, "List")!.Path));
        using var _ = doc.RenderToImage(400, 800);

        // 500 rows exist in the model; only a screenful in the tree (virtualisation is on).
        RenderNodeAssert(doc);
    }

    private static void RenderNodeAssert(CupriFace.CupriDocument doc)
    {
        var rows = 0;
        void Walk(CupriFace.Dom.RenderNode n)
        {
            if (n.Element?.ClassList.Contains("lrow") == true) rows++;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        Assert.InRange(rows, 5, 40);

        var scroller = TestDoc.Find(doc.Root, n => n.IsScrollable && n.MaxScrollY > 1000);
        Assert.NotNull(scroller);                          // 500×48 = 24000px of extent survives
    }

    [Fact]
    public void The_launch_seam_raises_with_the_attribute_value()
    {
        var app = new MobileApp();
        string? launched = null;
        app.LaunchRequested = id => launched = id;
        using var doc = Doc(app);

        var tree = doc.BuildAccessibilityTree(400, 800);
        Assert.True(doc.AccessibilityActivate(FindNamed(tree, "About")!.Path));
        using var _ = doc.RenderToImage(400, 800);

        tree = doc.BuildAccessibilityTree(400, 800);
        var open = FindNamed(tree, "Open the desktop Showcase")
                   ?? throw new Xunit.Sdk.XunitException("no launch button");
        Assert.True(doc.AccessibilityActivate(open.Path));
        Assert.Equal("showcase", launched);
    }

    [Fact]
    public void The_list_rows_are_readable_by_assistive_tech()
    {
        // role=listitem puts each materialised row in the semantics tree — what the Android
        // bridge exposes to TalkBack and what the CI gate's uiautomator dump greps for.
        var app = new MobileApp();
        using var doc = Doc(app);
        var tree = doc.BuildAccessibilityTree(400, 800);
        Assert.True(doc.AccessibilityActivate(FindNamed(tree, "List")!.Path));
        using var _ = doc.RenderToImage(400, 800);

        tree = doc.BuildAccessibilityTree(400, 800);
        var row = FindNamed(tree, "Row 3 — tap and fling")
                  ?? throw new Xunit.Sdk.XunitException("row 3 not in the a11y tree");
        Assert.Equal("listitem", row.Role);
        Assert.Null(FindNamed(tree, "Row 400 — tap and fling"));   // 19000px away: virtualised out

        // The list CONTAINER must not name itself from its content — that name would be every
        // materialised row concatenated, read aloud in full whenever a reader lands on the list.
        AccessibilityNode? list = null;
        void FindList(AccessibilityNode n)
        {
            if (n.Role == "list") list = n;
            foreach (var c in n.Children) FindList(c);
        }
        FindList(tree);
        Assert.NotNull(list);
        Assert.True(string.IsNullOrEmpty(list!.Name), $"list container is named its own content: '{list.Name}'");
    }

    [Fact]
    public void The_gate_anchors_hold_in_phone_landscape_too()
    {
        // The CI gate's rotation leg taps (dpW-56, 99) — valid only while the phone layout stays
        // full-width in landscape. The desktop courtesy cap is height-qualified precisely so it
        // does NOT fire at 850x312 (run 11: the un-qualified cap pulled the switch to x≈528 while
        // the gate tapped x=794). This pins the anchor headlessly at the emulator's landscape dp.
        var app = new MobileApp();
        var doc = app.CreateDocument();
        using (doc)
        {
            using (doc.RenderToImage(850, 312)) { }
            var tree = doc.BuildAccessibilityTree(850, 312);
            Assert.True(doc.AccessibilityActivate(FindNamed(tree, "Settings")!.Path));
            using (doc.RenderToImage(850, 312)) { }

            tree = doc.BuildAccessibilityTree(850, 312);
            var sw = FindNamed(tree, "Notifications") ?? throw new Xunit.Sdk.XunitException("no switch");
            var (cx, cy) = (sw.Bounds.X + sw.Bounds.W / 2, sw.Bounds.Y + sw.Bounds.H / 2);
            Assert.InRange(cx, 850 - 56 - 20, 850 - 56 + 20);
            Assert.InRange(cy, 99 - 20, 99 + 20);
        }
    }

    [Fact]
    public void The_showcase_still_builds_too()
    {
        // The other app the Android sample can push — a broken Showcase would fail the same gate.
        var app = new ShowcaseApp();
        using var doc = app.CreateDocument();
        using var img = doc.RenderToImage(940, 720);
        Assert.True(doc.BuildAccessibilityTree(940, 720).Children.Count > 0);
    }
}
