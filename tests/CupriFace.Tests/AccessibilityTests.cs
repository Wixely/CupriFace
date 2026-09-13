using CupriFace.Accessibility;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

public class AccessibilityTests(ITestOutputHelper output)
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
        // No TAB STOPS, on purpose: the web host owns Tab and stops the browser's default, so a
        // tabindex="0" in the overlay would be a claim about reachability that is never true
        // (measured: focus never left the keyboard textarea). tabindex="-1" is the opposite claim —
        // focusable by script, so the page can put DOM focus where the engine's is.
        Assert.DoesNotContain("tabindex=\"0\"", aria);
        Assert.Contains("role=\"button\" aria-label=\"Save\" data-path=\"", aria);
        Assert.Contains("tabindex=\"-1\"", aria);
    }

    // ---- What makes the web overlay a bridge rather than a mirror (#133): every node placed at ----
    // ---- its bounds, addressable by path, and the focused one marked so DOM focus can follow. ----

    [Fact]
    public void Aria_html_places_every_node_at_its_bounds_relative_to_its_parent()
    {
        const string html = """
            <body>
              <div class="panel" role="group" aria-label="Panel">
                <cupri-button>Save</cupri-button>
              </div>
            </body>
            """;
        const string css = "body { margin:0 } .panel { position:absolute; left:40px; top:30px; width:200px; height:100px; } cupri-button { position:absolute; left:10px; top:20px; width:80px; height:24px; }";
        using var t = new TestDoc(html, css, new Model(), components: true, width: 400, height: 300);
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        var group = FindRole(tree, "group")!;
        var button = FindRole(tree, "button")!;
        Assert.Equal((40f, 30f), (group.Bounds.X, group.Bounds.Y));
        Assert.Equal((50f, 50f), (button.Bounds.X, button.Bounds.Y));    // absolute in the tree…

        var aria = t.Doc.BuildAriaHtml(400, 300);
        Assert.Contains("aria-label=\"Panel\" data-path=\"" + group.Path + "\" style=\"left:40px;top:30px;width:200px;height:100px\"", aria);
        // …but relative to the node it sits in on the page, because the overlay keeps the tree's
        // nesting (a group contains its button) and a nested absolute box is offset by its parent.
        // (The button's own size is whatever its padding makes it — the tree's word, not the CSS's.)
        Assert.Contains("data-path=\"" + button.Path + "\" tabindex=\"-1\" style=\"left:10px;top:20px;"
                        + $"width:{button.Bounds.W:0.##}px;height:{button.Bounds.H:0.##}px\"", aria);
    }

    [Fact]
    public void Aria_html_scales_geometry_to_the_page_pixels_the_host_presents_at()
    {
        // The web host lays out at a logical size and paints the canvas scaled (Hybrid-Zoom). The
        // overlay lives in the page's pixels, so its boxes must scale the same way or they land
        // beside the painted control rather than on it.
        const string css = "body { margin:0 } cupri-button { position:absolute; left:10px; top:20px; width:80px; height:24px; }";
        using var t = new TestDoc("<body><cupri-button>Save</cupri-button></body>", css, new Model(), components: true, width: 400, height: 300);
        var b = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "button")!.Bounds;
        var aria = t.Doc.BuildAriaHtml(400, 300, presentScale: 1.5f);
        Assert.Contains($"style=\"left:15px;top:30px;width:{b.W * 1.5f:0.##}px;height:{b.H * 1.5f:0.##}px\"", aria);
    }

    [Fact]
    public void Aria_html_marks_the_focused_node_and_nothing_else()
    {
        var m = new Model();
        const string html = """
            <body>
              <cupri-switch checked="{{On}}">A</cupri-switch>
              <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        Assert.DoesNotContain("data-focused", t.Doc.BuildAriaHtml(400, 300));

        t.Key(EditKey.Tab);
        var aria = t.Doc.BuildAriaHtml(400, 300);
        // One node, and it is the switch — the page puts DOM focus on that node so the screen
        // reader announces it, which is the web's UIA focus-changed event.
        Assert.Equal(1, aria.Split("data-focused=\"true\"").Length - 1);
        Assert.Contains("role=\"switch\"", aria[..aria.IndexOf("data-focused", StringComparison.Ordinal)]);
        Assert.DoesNotContain("role=\"slider\"", aria[..aria.IndexOf("data-focused", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Focus_on_a_roleless_clickable_row_is_announced_on_the_control_inside_it()
    {
        // The Showcase's sidebar: a row with a click handler wrapping the switch it toggles. Tab
        // stops on the ROW (Focusables counts a control once, at the outermost), which has no role
        // and so no node — measured on the web overlay: after Tab, no node was marked focused, and
        // focusing the switch by its path was refused because the switch is not a Tab stop.
        var m = new Model { On = false };
        const string html = """
            <body>
              <cupri-button>First</cupri-button>
              <div class="row"><span>Dark mode</span><cupri-switch checked="{{On}}"></cupri-switch></div>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        t.Doc.OnClick(".row", _ => { });     // what makes the row focusable

        t.Key(EditKey.Tab);
        t.Key(EditKey.Tab);                  // the row
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        Assert.Equal(1, CountFocused(tree));
        Assert.True(FindRole(tree, "switch")!.Focused, "focus on the row should be announced on its switch");

        // And the other direction: an AT focusing the switch lands on the Tab stop that owns it,
        // and the next tree agrees about who has focus — so the overlay's focus is never yanked
        // back by a publish that says nobody does.
        t.Key(EditKey.ShiftTab);
        Assert.False(FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!.Focused);
        Assert.True(t.Doc.AccessibilityFocus(FindRole(tree, "switch")!.Path));
        Assert.True(FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!.Focused);
    }

    [Fact]
    public void Focus_by_path_is_what_the_overlay_posts_when_an_AT_moves_focus()
    {
        var m = new Model();
        const string html = """
            <body>
              <cupri-switch checked="{{On}}">A</cupri-switch>
              <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        var slider = FindRole(t.Doc.BuildAccessibilityTree(400, 300), "slider")!;

        Assert.True(t.Doc.AccessibilityFocus(slider.Path));
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        Assert.Equal(1, CountFocused(tree));
        Assert.True(FindRole(tree, "slider")!.Focused);
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

    [Fact]
    public void Aria_html_reads_a_field_value_not_its_placeholder()
    {
        // A field's VALUE is its value and its placeholder is its name. It used to report the
        // placeholder as the value, so an empty field read its own hint back as though that had
        // been typed. (A leaf text field is mirrored as a real <input>, so its value is the value
        // attribute — the same claim, in the place an input keeps it.)
        var html = "<body><cupri-textfield placeholder=\"Type your name…\" value=\"{{Name}}\"></cupri-textfield></body>";
        using var filled = new TestDoc(html, "", new Person { Name = "Ada" }, components: true);
        var aria = filled.Doc.BuildAriaHtml(400, 300);
        Assert.Contains("role=\"textbox\"", aria);
        Assert.Contains("value=\"Ada\"", aria);

        using var empty = new TestDoc(html, "", new Person { Name = "" }, components: true);
        var ariaEmpty = empty.Doc.BuildAriaHtml(400, 300);
        Assert.DoesNotContain("value=\"Type your name…\"", ariaEmpty);   // the placeholder is the NAME
        Assert.Contains("placeholder=\"Type your name…\"", ariaEmpty);   // …and also, literally, the placeholder
    }

    // ---- a text field is mirrored as a REAL editing element, so the browser's own editor, IME ----
    // ---- and password manager work on it (#133, second half). -----------------------------------

    [Fact]
    public void A_text_field_is_mirrored_as_a_real_input_carrying_what_an_editor_needs()
    {
        const string html = """
            <body>
              <cupri-textfield placeholder="Email" value="{{Name}}" inputmode="email"
                               enterkeyhint="next" autocomplete="email"></cupri-textfield>
            </body>
            """;
        using var t = new TestDoc(html, "", new Person { Name = "ada@example.com" }, components: true);
        var aria = t.Doc.BuildAriaHtml(400, 300);
        output.WriteLine(aria);

        Assert.Contains("<input ", aria);
        Assert.Contains("type=\"text\"", aria);
        Assert.Contains("value=\"ada@example.com\"", aria);
        Assert.Contains("inputmode=\"email\"", aria);        // which keyboard a phone offers
        Assert.Contains("enterkeyhint=\"next\"", aria);      // what its action key says
        Assert.Contains("autocomplete=\"email\"", aria);     // how a password manager finds the field
        Assert.Contains("spellcheck=\"false\"", aria);       // the engine paints the text; nothing to correct
        // Still a tab stop for nobody: the engine owns Tab, and an <input> would otherwise be one
        // by default — which is exactly the claim the mirror must not make.
        Assert.Contains("tabindex=\"-1\"", aria);
        Assert.DoesNotContain("tabindex=\"0\"", aria);
    }

    [Fact]
    public void A_password_field_is_a_fill_target_and_never_publishes_its_value()
    {
        // The rule every bridge already keeps — a masked field's plaintext does not leave the
        // engine — holds here too, and it is the reason the web host goes on owning the typing for
        // one. What the input exists for is the other direction: a password manager can FIND it
        // (type + autocomplete) and fill it, which it cannot do to a canvas at all.
        const string html = "<body><cupri-password value=\"{{Name}}\" autocomplete=\"current-password\"></cupri-password></body>";
        using var t = new TestDoc(html, "", new Person { Name = "hunter2" }, components: true);
        var aria = t.Doc.BuildAriaHtml(400, 300);
        output.WriteLine(aria);

        Assert.Contains("type=\"password\"", aria);
        Assert.Contains("autocomplete=\"current-password\"", aria);
        Assert.DoesNotContain("hunter2", aria);
        Assert.DoesNotContain("value=\"", aria[aria.IndexOf("type=\"password\"", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void A_field_keeps_its_name_once_there_is_text_in_it()
    {
        // Found by the browser gate, and it was never a web bug: a component may keep the author's
        // attributes on the custom element, and <cupri-password> does. Its inner role="textbox"
        // carries neither the label nor the placeholder, and the placeholder is only RENDERED while
        // the field is empty — so a password field became nameless to EVERY bridge the moment
        // someone typed into it, and a screen reader announced it as just "edit".
        var m = new Secret();
        using var t = new TestDoc("<body><cupri-password value=\"{{Pw}}\" reveal=\"{{Show}}\" aria-label=\"Password\"></cupri-password></body>",
                                  "", m, components: true);

        Assert.Equal("Password", NameOfRole(t, "textbox"));      // empty: named by its placeholder

        m.Pw = "hunter2";
        t.Doc.Refresh(); t.Layout();
        Assert.Equal("Password", NameOfRole(t, "textbox"));      // filled: named by the author's label

        m.Show = true;
        t.Doc.Refresh(); t.Layout();
        Assert.Equal("Password", NameOfRole(t, "textbox"));      // revealed: still named

        // The label is the COMPONENT's, not any ancestor's: a control with no name of its own inside
        // a labelled group stays nameless rather than borrowing a name that reads as true.
        using var grouped = new TestDoc(
            "<body><div role=\"group\" aria-label=\"Filters\"><div role=\"textbox\"></div></div></body>");
        Assert.Null(NameOfRole(grouped, "textbox"));

        static string? NameOfRole(TestDoc t, string role) =>
            FindRole(t.Doc.BuildAccessibilityTree(400, 300), role)!.Name;
    }

    private sealed class Secret { public string Pw { get; set; } = ""; public bool Show { get; set; } }

    [Fact]
    public void A_multiline_field_is_a_textarea_and_a_container_stays_a_container()
    {
        using var area = new TestDoc("<body><cupri-textarea value=\"{{Name}}\"></cupri-textarea></body>",
                                     "", new Person { Name = "line one" }, components: true);
        var aria = area.Doc.BuildAriaHtml(400, 300);
        Assert.Contains("<textarea ", aria);
        Assert.Contains(">line one</textarea>", aria);       // a textarea's content IS its value

        // A combobox HOLDS the textbox you type in (and a picker holds its grid). Turning a
        // container into an input would drop everything inside it from the tree, which is the
        // mirror's whole job — so only a LEAF text field becomes one.
        using var combo = new TestDoc(
            "<body><div role=\"combobox\" aria-label=\"City\"><div role=\"textbox\" aria-label=\"City\"></div></div></body>");
        var comboAria = combo.Doc.BuildAriaHtml(400, 300);
        output.WriteLine(comboAria);
        Assert.Contains("<div role=\"combobox\"", comboAria);
        Assert.Contains("<input role=\"textbox\"", comboAria);
    }

    [Fact]
    public void The_focused_field_carries_its_buffer_verbatim_and_the_engine_s_selection()
    {
        // What an editor holds is the text being EDITED. AccessibilityNode.Value is the rendered
        // text and therefore trimmed, which is right for a screen reader and wrong here: typing a
        // trailing space into a field whose mirror reported the trimmed text had that space taken
        // straight back out again on the next frame.
        var m = new Person { Name = "" };
        using var t = new TestDoc("<body><cupri-textfield value=\"{{Name}}\"></cupri-textfield></body>",
                                  "", m, components: true);
        t.ClickNode(t.FindRole("textbox"));
        t.Type("hi ");

        var aria = t.Doc.BuildAriaHtml(400, 300);
        output.WriteLine(aria);
        Assert.Contains("value=\"hi \"", aria);
        Assert.Contains("data-sel=\"3,3\"", aria);   // the caret the ENGINE has, to put back after a rewrite
    }

    [Fact]
    public void Aria_html_hides_content_scrolled_out_of_view()
    {
        // The desktop bridge reports IsOffscreen so Narrator skips the control; the mirror says the
        // same thing with aria-hidden, instead of reading a whole scrolled document as if visible.
        var html = "<body><cupri-button>Top</cupri-button><div style=\"height:2000px\"></div><cupri-button>Below</cupri-button></body>";
        using var t = new TestDoc(html, "", new Model(), components: true, width: 400, height: 300);
        var aria = t.Doc.BuildAriaHtml(400, 300);
        Assert.Contains("aria-label=\"Below\" aria-hidden=\"true\"", aria);
        Assert.DoesNotContain("aria-label=\"Top\" aria-hidden", aria);
    }

    private sealed class Person { public string Name { get; set; } = ""; }

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

        var aria = t.Doc.BuildAriaHtml(400, 300);
        Assert.Contains("aria-selected=\"true\"", aria);
        Assert.Contains("aria-expanded=\"false\"", aria);
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
    public void A_positional_label_names_the_control_it_activates()
    {
        var m = new Model();
        const string html = """
            <body>
              <cupri-checkbox checked="{{On}}"></cupri-checkbox><span class="lbl">Notifications</span>
              <span class="lbl">Dark mode</span><cupri-switch checked="{{On}}"></cupri-switch>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        Assert.Equal("Notifications", FindRole(tree, "checkbox")!.Name);   // "[box] Label"
        Assert.Equal("Dark mode", FindRole(tree, "switch")!.Name);         // "Label [switch]"
    }

    [Fact]
    public void Automation_id_comes_from_the_binding_path()
    {
        var m = new Model();
        using var t = new TestDoc("<body><cupri-switch checked=\"{{On}}\">X</cupri-switch></body>", "", m, components: true);
        Assert.Equal("On", FindRole(t.Doc.BuildAccessibilityTree(400, 300), "switch")!.AutomationId);
    }

    // ---- off-screen ------------------------------------------------------------------------------
    // A screen reader should read the visible page, not the whole document. All three bridges hang
    // off the flag these cover: Narrator's IsOffscreen, Orca's not-SHOWING, VoiceOver's element
    // hiding. The Showcase's landing page carries 88 such controls, so the difference is not subtle.

    private static AccessibilityNode? FindNamed(AccessibilityNode n, string name)
    {
        if (n.Name == name) return n;
        foreach (var c in n.Children) { var f = FindNamed(c, name); if (f is not null) return f; }
        return null;
    }

    private const string ScrollerCss = ".box{height:100px;overflow:auto;} .row{height:100px;} .tall{height:1000px;}";

    [Fact]
    public void A_control_below_the_viewport_is_offscreen()
    {
        // The viewport is 300 tall; this button starts at 500.
        const string html =
            "<body><div style=\"height:500px\"></div>" +
            "<div role=\"button\" aria-label=\"Below\">x</div></body>";
        using var t = new TestDoc(html, "", height: 300);
        Assert.True(FindNamed(t.Doc.BuildAccessibilityTree(400, 300), "Below")!.Offscreen);
    }

    [Fact]
    public void A_control_clipped_away_by_an_overflow_ancestor_is_offscreen()
    {
        // Inside the viewport by its own coordinates, but its scroller crops it — which is why the
        // test cannot be "is it within the window" and has to follow the clip chain.
        const string html =
            "<body><div class=\"box\">" +
            "<div class=\"row\" role=\"button\" aria-label=\"First\">a</div>" +
            "<div class=\"tall\" role=\"button\" aria-label=\"Far\">b</div>" +
            "</div></body>";
        using var t = new TestDoc(html, ScrollerCss, height: 300);
        var tree = t.Doc.BuildAccessibilityTree(400, 300);
        Assert.False(FindNamed(tree, "First")!.Offscreen);   // fills the 100px scroller
        Assert.True(FindNamed(tree, "Far")!.Offscreen);      // starts below its clipped bottom edge
    }

    [Fact]
    public void A_control_only_half_scrolled_into_view_still_counts_as_visible()
    {
        // Reachable and readable, so hiding it would be worse than announcing it: any overlap counts.
        const string html =
            "<body><div style=\"height:280px\"></div>" +
            "<div class=\"row\" role=\"button\" aria-label=\"Straddling\">x</div></body>";
        using var t = new TestDoc(html, ScrollerCss, height: 300);
        Assert.False(FindNamed(t.Doc.BuildAccessibilityTree(400, 300), "Straddling")!.Offscreen);
    }

    [Fact]
    public void Controls_on_the_visible_page_are_not_offscreen()
    {
        using var t = new TestDoc("<body><div role=\"button\" aria-label=\"Here\">x</div></body>", "", height: 300);
        Assert.False(FindNamed(t.Doc.BuildAccessibilityTree(400, 300), "Here")!.Offscreen);
    }

    [Fact]
    public void A_visible_inline_link_uses_its_text_bounds_for_offscreen_state()
    {
        using var t = new TestDoc("<body><p>Read the <a href='about'>About page</a>.</p></body>", "",
            height: 300);

        var link = FindNamed(t.Doc.BuildAccessibilityTree(400, 300), "About page")!;
        Assert.True(link.Bounds.W > 0 && link.Bounds.H > 0);
        Assert.False(link.Offscreen);
    }
}
