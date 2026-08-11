using CupriFace.Accessibility;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class AccessibilityTests
{
    private sealed class Model { public bool On { get; set; } = true; public int Volume { get; set; } = 60; }

    private static AccessibilityNode? FindRole(AccessibilityNode n, string role)
    {
        if (n.Role == role) return n;
        foreach (var c in n.Children) { var f = FindRole(c, role); if (f is not null) return f; }
        return null;
    }

    private static int CountFocused(AccessibilityNode n)
    {
        var count = n.Focused ? 1 : 0;
        foreach (var c in n.Children) count += CountFocused(c);
        return count;
    }

    [Fact]
    public void Aria_html_mirrors_roles_labels_and_states()
    {
        const string html = """
            <body>
              <h1>Dashboard</h1>
              <cupri-button>Save</cupri-button>
              <cupri-switch checked="{{On}}">Notifications</cupri-switch>
              <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
            </body>
            """;
        using var t = new TestDoc(html, "", new Model(), components: true, width: 400, height: 300);
        var aria = t.Doc.BuildAriaHtml(400, 300);

        Assert.Contains("role=\"heading\"", aria);
        Assert.Contains("Dashboard", aria);
        Assert.Contains("role=\"button\"", aria);
        Assert.Contains("Save", aria);                          // button reads its label
        Assert.Contains("role=\"switch\"", aria);
        Assert.Contains("aria-checked=\"true\"", aria);         // switch state
        Assert.Contains("role=\"slider\"", aria);
        Assert.Contains("aria-valuenow=\"60\"", aria);          // slider value/range
        Assert.Contains("aria-valuemin=\"0\"", aria);
        Assert.Contains("aria-valuemax=\"100\"", aria);
        Assert.Contains("tabindex=\"0\"", aria);                // focusable controls are reachable
    }

    [Fact]
    public void Aria_html_updates_when_the_model_changes()
    {
        var m = new Model { On = false };
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        Assert.Contains("aria-checked=\"false\"", t.Doc.BuildAriaHtml(400, 300));

        m.On = true;
        t.Doc.Refresh();
        Assert.Contains("aria-checked=\"true\"", t.Doc.BuildAriaHtml(400, 300));
    }

    // ---- The additions that carry the desktop AT bridges (UIA first): identity, focus, ---------
    // ---- actions, and bounds that are true on screen rather than true before scrolling. --------

    [Fact]
    public void Node_path_survives_a_rebuild_and_resolves_back()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        var sw = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch");
        Assert.NotNull(sw);
        Assert.NotEmpty(sw!.Path);

        t.Doc.Refresh();   // the per-keystroke rebuild replaces every node object…
        t.Layout();
        var again = t.Doc.NodeAtPath(sw.Path);
        Assert.Equal("switch", again?.Element?.GetAttribute("role"));   // …but the path still lands
    }

    [Fact]
    public void Activate_by_path_behaves_like_a_click()
    {
        var m = new Model { On = false };
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        var sw = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!;

        Assert.True(t.Doc.AccessibilityActivate(sw.Path));
        Assert.True(m.On);
    }

    [Fact]
    public void SetValue_by_path_writes_through_the_slider_binding_and_clamps()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-slider min=\"0\" max=\"100\" value=\"{{Volume}}\"></cupri-slider></body>", "", m, components: true);
        var slider = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "slider")!;

        Assert.True(t.Doc.AccessibilitySetValue(slider.Path, 30));
        Assert.Equal(30, m.Volume);
        Assert.True(t.Doc.AccessibilitySetValue(slider.Path, 500));   // beyond max
        Assert.Equal(100, m.Volume);                                  // → clamped, like a drag
    }

    [Fact]
    public void Tab_focus_shows_up_on_exactly_one_node()
    {
        var m = new Model();
        const string html = """
            <body>
              <cupri-switch checked="{{On}}">A</cupri-switch>
              <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        Assert.Equal(0, CountFocused(t.Doc.BuildAccessibilityTree(400, 300)));

        t.Key(EditKey.Tab);
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        Assert.Equal(1, CountFocused(tree));
    }

    [Fact]
    public void Selected_and_expanded_states_are_carried()
    {
        const string html = """
            <body>
              <div role="tab" aria-selected="true">Active</div>
              <div role="treeitem" aria-expanded="false">Folder</div>
            </body>
            """;
        using var t = new TestDoc(html);
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        Assert.True(FindRole(tree, "tab")!.Selected);
        Assert.False(FindRole(tree, "treeitem")!.Expanded);
    }

    [Fact]
    public void The_disabled_class_the_components_emit_counts_as_disabled()
    {
        // Components signal disabled with class="disabled" (e.g. a pagination arrow on page one);
        // the tree must agree with the cursor about that, not just with the attribute form.
        using var t = new TestDoc("<body><div role=\"button\" class=\"disabled\">Prev</div></body>");
        Assert.True(FindRole(t.Doc.BuildAccessibilityTree(400, 300), "button")!.Disabled);
    }

    [Fact]
    public void Bounds_track_scrolling_and_activation_still_lands()
    {
        var m = new Model { On = false };
        const string html = """
            <body>
              <div class="scroller">
                <div class="spacer"></div>
                <cupri-switch checked="{{On}}">Below the fold</cupri-switch>
              </div>
            </body>
            """;
        const string css = ".scroller { height: 100px; overflow: scroll; } .spacer { height: 150px; }";
        using var t = new TestDoc(html, css, m, components: true);

        var before = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!;
        t.Doc.DispatchWheel(50, 50, 120);   // scroll the container down
        t.Layout();
        var after = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!;

        Assert.True(after.Bounds.Y < before.Bounds.Y,
            $"bounds must move up with the content (before {before.Bounds.Y}, after {after.Bounds.Y})");

        // And the box is where the control really is: a click synthesized at its centre hits it.
        Assert.True(t.Doc.AccessibilityActivate(after.Path));
        Assert.True(m.On);
    }

    [Fact]
    public void Automation_id_comes_from_the_binding_path()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        Assert.Equal("On", FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!.AutomationId);
    }
}
