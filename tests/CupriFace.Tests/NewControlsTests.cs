using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Five controls the library was missing: breadcrumb, toolbar, form, range and tag input.
///
/// Each is tested through what it does rather than what it renders — a crumb routes, a range drags,
/// a tag is added and removed, a form scopes its own validation — because markup that looks right
/// and does nothing is the failure mode a component library actually has.
/// </summary>
public class NewControlsTests(ITestOutputHelper output)
{
    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> p) => TestDoc.Find(n, p);
    private static bool HasClass(RenderNode n, string c) => n.Element?.ClassList.Contains(c) == true;

    private static List<RenderNode> All(RenderNode n, Func<RenderNode, bool> p)
    {
        var list = new List<RenderNode>();
        void Walk(RenderNode x) { if (p(x)) list.Add(x); foreach (var c in x.Children) Walk(c); }
        Walk(n);
        return list;
    }

    // ---- breadcrumb ---------------------------------------------------------

    private sealed class NavModel { public string Section { get; set; } = "root"; }

    [Fact]
    public void A_breadcrumbs_last_crumb_is_the_page_you_are_on_and_is_not_a_link()
    {
        using var t = new TestDoc(
            "<body><cupri-breadcrumb value=\"{{Section}}\">" +
            "<cupri-crumb value='root'>Home</cupri-crumb>" +
            "<cupri-crumb value='reports'>Reports</cupri-crumb>" +
            "<cupri-crumb>March</cupri-crumb>" +
            "</cupri-breadcrumb></body>", "", new NavModel(), width: 500, height: 200, components: true);

        var current = Find(t.Doc.Root, n => n.Element?.GetAttribute("aria-current") == "page");
        Assert.NotNull(current);
        // The one you are on must not be clickable: a link to here is a dead control that looks live.
        Assert.Null(current!.Element!.GetAttribute("data-set-path"));

        var links = All(t.Doc.Root, n => HasClass(n, "cupri-crumb-link"));
        Assert.Equal(2, links.Count);                       // Home and Reports, not March
    }

    [Fact]
    public void Clicking_a_crumb_routes_by_writing_the_bound_value()
    {
        var m = new NavModel { Section = "march" };
        using var t = new TestDoc(
            "<body><cupri-breadcrumb value=\"{{Section}}\">" +
            "<cupri-crumb value='root'>Home</cupri-crumb>" +
            "<cupri-crumb value='reports'>Reports</cupri-crumb>" +
            "<cupri-crumb>March</cupri-crumb>" +
            "</cupri-breadcrumb></body>", "", m, width: 500, height: 200, components: true);

        var home = All(t.Doc.Root, n => HasClass(n, "cupri-crumb-link"))[0];
        t.ClickNode(home);
        Assert.Equal("root", m.Section);
    }

    // ---- toolbar ------------------------------------------------------------

    [Fact]
    public void A_toolbar_is_one_group_to_assistive_technology_and_push_pins_a_cluster_right()
    {
        using var t = new TestDoc(
            "<body style='margin:0'><cupri-toolbar label='Editing' style='width:400px'>" +
            "<cupri-toolbar-group><cupri-button>Bold</cupri-button></cupri-toolbar-group>" +
            "<cupri-toolbar-sep></cupri-toolbar-sep>" +
            "<cupri-toolbar-group push><cupri-button>Done</cupri-button></cupri-toolbar-group>" +
            "</cupri-toolbar></body>", "", width: 500, height: 200, components: true);

        var bar = Find(t.Doc.Root, n => n.Element?.GetAttribute("role") == "toolbar");
        Assert.NotNull(bar);
        Assert.Equal("Editing", bar!.Element!.GetAttribute("aria-label"));

        var groups = All(t.Doc.Root, n => HasClass(n, "cupri-toolbar-group"));
        Assert.Equal(2, groups.Count);
        // The pushed cluster is driven right: it must start beyond the halfway mark of a 400px bar.
        Assert.True(groups[1].X > groups[0].X + 150,
            $"push should pin the last group right; got {groups[0].X} then {groups[1].X}");
        // The separator is decorative and must not be announced.
        var sep = Find(t.Doc.Root, n => HasClass(n, "cupri-toolbar-sep"));
        Assert.Equal("true", sep!.Element!.GetAttribute("aria-hidden"));
    }

    /// <summary>A toolbar fills its container and keeps its pushed cluster inside itself.
    ///
    /// <para>Written first against a padded <c>&lt;body&gt;</c>, where it failed — the root was laid
    /// out at the full viewport width, so EVERY child overflowed and a plain <c>&lt;div&gt;</c> did
    /// the same. That was a layout bug this control surfaced rather than caused, and it is fixed
    /// separately; the container here is a padded div because that is the case the toolbar is
    /// responsible for, and <c>BodyPaddingTests</c> owns the root.</para></summary>
    [Theory]
    [InlineData("<div class='wrap' style='padding:20px'>", "</div>")]   // a padded container…
    [InlineData("<div class='wrap'>", "</div>")]                        // …and one with none
    public void A_toolbar_fills_its_container_and_keeps_its_pushed_cluster_inside(string open, string close)
    {
        using var t = new TestDoc(
            $"<body style='margin:0'>{open}<cupri-toolbar label='T'>" +
            "<cupri-toolbar-group><cupri-button>Bold</cupri-button></cupri-toolbar-group>" +
            "<cupri-toolbar-group push><cupri-button>Publish</cupri-button></cupri-toolbar-group>" +
            $"</cupri-toolbar>{close}</body>", "", width: 600, height: 220, components: true);

        var wrap = Find(t.Doc.Root, n => HasClass(n, "wrap"))!;
        var bar = Find(t.Doc.Root, n => n.Element?.GetAttribute("role") == "toolbar")!;
        output.WriteLine($"wrap w={wrap.Width}; toolbar x={bar.X} w={bar.Width} right={bar.X + bar.Width}");

        Assert.True(bar.X + bar.Width <= wrap.X + wrap.Width,
            $"toolbar runs to {bar.X + bar.Width}, past its container's {wrap.X + wrap.Width}");

        var pushed = All(t.Doc.Root, n => HasClass(n, "cupri-toolbar-group"))[1];
        Assert.True(pushed.X + pushed.Width <= bar.X + bar.Width + 0.5f,
            "the pushed group spills out of the toolbar");
        Assert.True(pushed.X > bar.X + bar.Width / 2, "push should pin the cluster to the far end");
    }

    // ---- form ---------------------------------------------------------------

    private sealed class TwoForms
    {
        public string LoginUser { get; set; } = "";
        public string SignupEmail { get; set; } = "";
    }

    private const string TwoFormsHtml =
        "<body><cupri-form name='login'>" +
        "<cupri-textfield value=\"{{LoginUser}}\" required error='Who are you?'></cupri-textfield>" +
        "</cupri-form>" +
        "<cupri-form name='signup'>" +
        "<cupri-textfield value=\"{{SignupEmail}}\" required error='Email required'></cupri-textfield>" +
        "</cupri-form></body>";

    /// <summary>The reason the component exists: two forms on one page validate apart. ValidateAll is
    /// document-wide, so submitting one used to report — and reveal — the other's errors.</summary>
    [Fact]
    public void Validating_one_form_does_not_report_or_reveal_the_others_errors()
    {
        var m = new TwoForms { LoginUser = "" };
        using var t = new TestDoc(TwoFormsHtml, "", m, width: 500, height: 400, components: true);

        // Both are empty and both required, so the document-wide answer is "invalid".
        Assert.False(t.Doc.ValidateAll());

        m.LoginUser = "ada";
        t.Doc.Refresh();
        t.Layout();

        // Login is now satisfied; signup is still empty. Scoped validation must say so.
        Assert.True(t.Doc.Validate("login"), "the login form alone is valid");
        Assert.False(t.Doc.Validate("signup"), "the signup form alone is not");

        // …and validating login must not put an error message under the signup field.
        t.Doc.Validate("login");
        t.Layout();
        var errors = All(t.Doc.Root, n => HasClass(n, "cupri-field-error"));
        output.WriteLine($"errors shown after validating 'login': {errors.Count}");
        Assert.Empty(errors);
    }

    [Fact]
    public void A_form_is_the_submit_scope_and_names_itself_to_the_handler()
    {
        var m = new TwoForms();
        using var t = new TestDoc(TwoFormsHtml, "", m, width: 500, height: 400, components: true);
        string? submitted = null;
        t.Doc.OnSubmit("data-cupri-form", e => { submitted = e.Value; return true; });

        var boxes = All(t.Doc.Root, n => n.Element?.GetAttribute("role") == "textbox");
        var (x, y) = TestDoc.Center(boxes[1]);              // the signup field
        t.Click(x, y);
        t.Doc.DispatchKey("a@b.c", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);

        Assert.Equal("signup", submitted);                 // e.Value is the form's name
    }

    [Fact]
    public void An_unknown_form_name_validates_nothing_and_says_so()
    {
        using var t = new TestDoc(TwoFormsHtml, "", new TwoForms(), width: 500, height: 400, components: true);
        Assert.True(t.Doc.Validate("no-such-form"));
    }

    // ---- range --------------------------------------------------------------

    private sealed class Span { public double From { get; set; } = 20; public double To { get; set; } = 80; }

    private const string RangeHtml =
        "<body style='margin:0'><cupri-range low=\"{{From}}\" high=\"{{To}}\" min='0' max='100' " +
        "style='width:400px'></cupri-range></body>";

    [Fact]
    public void A_range_has_two_thumbs_each_a_slider_in_its_own_right()
    {
        using var t = new TestDoc(RangeHtml, "", new Span(), width: 500, height: 200, components: true);
        var thumbs = All(t.Doc.Root, n => n.Element?.GetAttribute("role") == "slider");

        Assert.Equal(2, thumbs.Count);
        Assert.Equal("20", thumbs[0].Element!.GetAttribute("aria-valuenow"));
        Assert.Equal("80", thumbs[1].Element!.GetAttribute("aria-valuenow"));
    }

    /// <summary>The constraint is expressed as data, not enforced in code: each thumb's bound is the
    /// other's value, so the drag's existing clamp stops them crossing.</summary>
    [Fact]
    public void Neither_thumb_can_pass_the_other_because_each_bounds_the_other()
    {
        using var t = new TestDoc(RangeHtml, "", new Span(), width: 500, height: 200, components: true);
        var thumbs = All(t.Doc.Root, n => n.Element?.GetAttribute("role") == "slider");

        // The SCALE is the whole range on both thumbs — that is what the track measures.
        Assert.Equal("0", thumbs[0].Element!.GetAttribute("min"));
        Assert.Equal("100", thumbs[0].Element!.GetAttribute("max"));
        // The LIMIT is separate, and is each thumb's neighbour.
        Assert.Equal("80", thumbs[0].Element!.GetAttribute("data-clamp-max"));  // low may not exceed high
        Assert.Equal("20", thumbs[1].Element!.GetAttribute("data-clamp-min"));  // high may not fall below low
    }

    /// <summary>Dragging maps across the TRACK, not the 18px thumb the pointer landed on — the engine
    /// change this control needed.
    ///
    /// <para>The assertion drags to a PROPORTION of the track and demands the value match it. An
    /// earlier version dragged to the far end and checked the value was high, which passes either
    /// way: any mapping saturates at the end, so it could not tell the track from the thumb and the
    /// engine change it was meant to guard could be deleted with the test still green.</para></summary>
    [Theory]
    [InlineData(0.60, 60)]
    [InlineData(0.45, 45)]
    public void Dragging_a_thumb_maps_across_the_track_not_the_thumb(double fraction, double expected)
    {
        var m = new Span();
        using var t = new TestDoc(RangeHtml, "", m, width: 500, height: 200, components: true);
        var track = Find(t.Doc.Root, n => n.Element?.HasAttribute("data-slider-track") == true)!;
        var thumbs = All(t.Doc.Root, n => n.Element?.GetAttribute("role") == "slider");

        // Press ON the high thumb (at 80%), then drag to a known fraction of the track.
        var (hx, hy) = TestDoc.Center(thumbs[1]);
        var target = track.X + (float)(track.Width * fraction);
        t.Click(hx, hy);
        t.Move(target, hy);
        t.Up(target, hy);

        output.WriteLine($"track x={track.X} w={track.Width}; dragged to {fraction:P0} -> To={m.To}");
        Assert.Equal(expected, m.To, 1);          // the track's scale, within a pixel's worth
        Assert.Equal(20, m.From);                 // …and the low thumb did not move
    }

    /// <summary>The clamp does its job under a drag, not merely in the markup.
    ///
    /// <para>Added because removing the clamp entirely left every other test in this file green: the
    /// attributes were asserted but nothing ever dragged a thumb PAST its neighbour, so the constraint
    /// was described and never exercised.</para></summary>
    [Theory]
    [InlineData("low", 0.95, 80.0, 80.0)]    // drag low far right → stops at the high thumb (80)
    [InlineData("high", 0.02, 20.0, 20.0)]   // drag high far left → stops at the low thumb (20)
    public void A_thumb_stops_at_its_neighbour_instead_of_crossing(
        string which, double fraction, double expectedLow, double expectedHigh)
    {
        var m = new Span();
        using var t = new TestDoc(RangeHtml, "", m, width: 500, height: 200, components: true);
        var track = Find(t.Doc.Root, n => n.Element?.HasAttribute("data-slider-track") == true)!;
        var thumbs = All(t.Doc.Root, n => n.Element?.GetAttribute("role") == "slider");

        var thumb = which == "low" ? thumbs[0] : thumbs[1];
        var (tx, ty) = TestDoc.Center(thumb);
        var target = track.X + (float)(track.Width * fraction);
        t.Click(tx, ty);
        t.Move(target, ty);
        t.Up(target, ty);

        output.WriteLine($"dragged {which} to {fraction:P0} -> From={m.From} To={m.To}");
        Assert.Equal(expectedLow, m.From, 1);
        Assert.Equal(expectedHigh, m.To, 1);
    }

    // ---- tag input ----------------------------------------------------------

    private sealed class Tagged { public string Tags { get; set; } = "alpha,beta"; }

    private const string TagHtml =
        "<body><cupri-taginput value=\"{{Tags}}\" placeholder='Add…'></cupri-taginput></body>";

    [Fact]
    public void Existing_tags_render_as_chips()
    {
        using var t = new TestDoc(TagHtml, "", new Tagged(), width: 500, height: 200, components: true);
        Assert.Equal(2, All(t.Doc.Root, n => HasClass(n, "cupri-tag")).Count);
    }

    [Fact]
    public void Typing_and_pressing_enter_appends_a_tag()
    {
        var m = new Tagged();
        using var t = new TestDoc(TagHtml, "", m, width: 500, height: 200, components: true);

        var entry = Find(t.Doc.Root, n => HasClass(n, "cupri-tag-entry"))!;
        t.ClickNode(entry);
        t.Doc.DispatchKey("gamma", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);
        t.Layout();

        Assert.Equal("alpha,beta,gamma", m.Tags);
        Assert.Equal(3, All(t.Doc.Root, n => HasClass(n, "cupri-tag")).Count);
    }

    [Fact]
    public void The_same_tag_is_not_added_twice()
    {
        var m = new Tagged();
        using var t = new TestDoc(TagHtml, "", m, width: 500, height: 200, components: true);

        var entry = Find(t.Doc.Root, n => HasClass(n, "cupri-tag-entry"))!;
        t.ClickNode(entry);
        t.Doc.DispatchKey("ALPHA", EditKey.None);            // same tag, different case
        t.Doc.DispatchKey(null, EditKey.Enter);

        Assert.Equal("alpha,beta", m.Tags);
    }

    /// <summary>Removal needs no engine primitive: each chip carries the list it would leave behind,
    /// precomputed, so the × is the same click-to-set that drives a tab strip.</summary>
    [Fact]
    public void Clicking_a_chips_cross_removes_just_that_tag()
    {
        var m = new Tagged { Tags = "alpha,beta,gamma" };
        using var t = new TestDoc(TagHtml, "", m, width: 500, height: 200, components: true);

        var crosses = All(t.Doc.Root, n => HasClass(n, "cupri-tag-x"));
        Assert.Equal(3, crosses.Count);
        t.ClickNode(crosses[1]);                             // remove "beta"

        Assert.Equal("alpha,gamma", m.Tags);
    }

    /// <summary>Backspace on an EMPTY entry takes back the last chip — the tag-box idiom, so a
    /// mistyped tag is undone where the hand already is instead of by hunting for its ×.</summary>
    [Fact]
    public void Backspace_on_an_empty_entry_removes_the_last_tag()
    {
        var m = new Tagged { Tags = "alpha,beta,gamma" };
        using var t = new TestDoc(TagHtml, "", m, width: 500, height: 200, components: true);

        var entry = Find(t.Doc.Root, n => HasClass(n, "cupri-tag-entry"))!;
        t.ClickNode(entry);
        t.Doc.DispatchKey(null, EditKey.Backspace);
        t.Layout();

        Assert.Equal("alpha,beta", m.Tags);
    }

    /// <summary>…but only when it IS empty. While there is text to delete, Backspace deletes text —
    /// eating a chip mid-word would be its own small disaster.</summary>
    [Fact]
    public void Backspace_with_text_in_the_entry_deletes_text_not_a_tag()
    {
        var m = new Tagged { Tags = "alpha,beta" };
        using var t = new TestDoc(TagHtml, "", m, width: 500, height: 200, components: true);

        var entry = Find(t.Doc.Root, n => HasClass(n, "cupri-tag-entry"))!;
        t.ClickNode(entry);
        t.Doc.DispatchKey("draft", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Backspace);      // deletes the "t"
        t.Doc.DispatchKey(null, EditKey.Enter);          // …and commits "draf"

        Assert.Equal("alpha,beta,draf", m.Tags);
    }

    /// <summary>An empty tag box has nothing to take back, so Backspace stays inert rather than
    /// appearing to do something.</summary>
    [Fact]
    public void Backspace_on_an_empty_list_does_nothing()
    {
        var m = new Tagged { Tags = "" };
        using var t = new TestDoc(TagHtml, "", m, width: 500, height: 200, components: true);

        var entry = Find(t.Doc.Root, n => HasClass(n, "cupri-tag-entry"))!;
        t.ClickNode(entry);
        t.Doc.DispatchKey(null, EditKey.Backspace);

        Assert.Equal("", m.Tags);
    }

    /// <summary>Enter in a tag box means "add this tag", so it must NOT also submit the form around
    /// it — otherwise every tag added sends a half-filled form.</summary>
    [Fact]
    public void Adding_a_tag_does_not_submit_the_surrounding_form()
    {
        var m = new Tagged();
        using var t = new TestDoc(
            "<body><cupri-form name='post'>" +
            "<cupri-taginput value=\"{{Tags}}\"></cupri-taginput>" +
            "</cupri-form></body>", "", m, width: 500, height: 240, components: true);
        var submits = 0;
        t.Doc.OnSubmit("data-cupri-form", _ => { submits++; return true; });

        var entry = Find(t.Doc.Root, n => HasClass(n, "cupri-tag-entry"))!;
        t.ClickNode(entry);
        t.Doc.DispatchKey("draft", EditKey.None);
        t.Doc.DispatchKey(null, EditKey.Enter);

        Assert.Equal("alpha,beta,draft", m.Tags);
        Assert.Equal(0, submits);
    }
}
