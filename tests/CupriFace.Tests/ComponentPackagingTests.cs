using AngleSharp.Dom;
using CupriFace.Binding;
using CupriFace.Components;
using CupriFace.Components.Controls;
using CupriFace.Diagnostics;
using CupriFace.Dom;
using Leaf.Controls;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The component-packaging contract (COMPONENT-PACKAGING.md): how a library registers, what happens
/// when two registrations want one tag, where each layer's CSS sits in the cascade, how a component
/// carries behaviour across rebuilds, and that a built-in can be subclassed. Each of these is a
/// promise made to someone shipping or consuming a library, so each is a test rather than a hope —
/// the CSS order in particular is exactly the sort of thing that gets "tidied" away.
/// </summary>
public class ComponentPackagingTests
{
    // ---- registration and conflicts --------------------------------------------------------------

    private sealed class Probe(string tag, string css = "") : ComponentBase
    {
        public override string Tag => tag;
        public override string DefaultCss => css;
        public override void Expand(IElement el) => el.ClassList.Add(tag);
    }

    private sealed class TestLibrary(string prefix, string css, params ICupriComponent[] components) : ICupriComponentLibrary
    {
        public string Name => "Test." + prefix.TrimEnd('-');
        public string Prefix => prefix;
        public IEnumerable<ICupriComponent> Components => components;
        public string Css => css;
    }

    [Fact]
    public void Add_refuses_a_tag_that_is_already_registered_and_names_the_owner()
    {
        var registry = ComponentRegistry.Default();
        var ex = Assert.Throws<ComponentConflictException>(() => registry.Add(new Probe("cupri-button")));
        Assert.Contains("built-in ButtonComponent", ex.Message);
        Assert.Contains("Replace", ex.Message);
        Assert.IsType<ButtonComponent>(registry.Find("cupri-button"));
    }

    [Fact]
    public void Replace_refuses_a_tag_nobody_registered_so_a_typo_cannot_add_a_dead_control()
    {
        var registry = ComponentRegistry.Default();
        Assert.Throws<ComponentConflictException>(() => registry.Replace(new Probe("cupri-buton")));
        Assert.DoesNotContain("cupri-buton", registry.Tags);
    }

    [Fact]
    public void Replace_swaps_the_component_and_moves_it_to_the_app_layer()
    {
        var registry = ComponentRegistry.Default();
        var mine = new Probe("cupri-button");
        registry.Replace(mine);
        Assert.Same(mine, registry.Find("cupri-button"));
        var reg = registry.Registrations.Single(r => r.Tag == "cupri-button");
        Assert.Equal(ComponentLayer.App, reg.Layer);
        Assert.Empty(registry.Conflicts);           // stated intent: nothing to warn about
    }

    [Fact]
    public void Register_still_lets_the_last_one_win_but_the_doctor_reports_it_as_CF0022()
    {
        var registry = ComponentRegistry.Default().Register(new Probe("cupri-badge"));
        Assert.IsType<Probe>(registry.Find("cupri-badge"));
        var conflict = Assert.Single(registry.Conflicts);
        Assert.Equal("cupri-badge", conflict.Tag);
        Assert.IsType<BadgeComponent>(conflict.Previous);

        var report = CupriDoctor.Check("<body><cupri-badge>x</cupri-badge></body>", "", registry);
        var finding = Assert.Single(report.Findings, f => f.Code == "CF0022");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("Replace", finding.Fix);

        // The same override made with Replace is quiet.
        var quiet = ComponentRegistry.Default().Replace(new Probe("cupri-badge"));
        Assert.DoesNotContain(CupriDoctor.Check("<body><cupri-badge>x</cupri-badge></body>", "", quiet).Findings,
            f => f.Code == "CF0022");
    }

    [Fact]
    public void A_library_is_added_whole_or_not_at_all()
    {
        var registry = ComponentRegistry.Default();
        var tags = registry.Tags.Count;
        // The second component lacks the prefix: the first must not have been registered either.
        var bad = new TestLibrary("acme-", "", new Probe("acme-one"), new Probe("other-two"));
        var ex = Assert.Throws<ComponentConflictException>(() => registry.Add(bad));
        Assert.Contains("prefix", ex.Message);
        Assert.Equal(tags, registry.Tags.Count);
        Assert.Null(registry.Find("acme-one"));
        Assert.Empty(registry.Libraries);
    }

    [Fact]
    public void A_library_cannot_take_a_built_in_tag_even_under_the_cupri_prefix()
    {
        var registry = ComponentRegistry.Default();
        var ex = Assert.Throws<ComponentConflictException>(() =>
            registry.Add(new TestLibrary("cupri-", "", new Probe("cupri-slider"))));
        Assert.Contains("only adds tags", ex.Message);
        Assert.IsType<SliderComponent>(registry.Find("cupri-slider"));
    }

    [Fact]
    public void Two_libraries_cannot_share_a_prefix_and_one_cannot_be_added_twice()
    {
        var registry = ComponentRegistry.Default().AddLeafControls();
        Assert.Throws<ComponentConflictException>(() => registry.AddLeafControls());
        Assert.Throws<ComponentConflictException>(() =>
            registry.Add(new TestLibrary("leaf-", "", new Probe("leaf-other"))));
        Assert.Throws<ComponentConflictException>(() =>
            registry.Add(new TestLibrary("nohyphen", "", new Probe("nohyphen-x"))));
    }

    [Fact]
    public void The_real_library_registers_its_tags_in_the_library_layer()
    {
        var registry = ComponentRegistry.Default().AddLeafControls();
        var leaf = registry.Registrations.Where(r => r.Tag.StartsWith("leaf-")).ToList();
        Assert.Equal(["leaf-counter", "leaf-card", "leaf-status"], leaf.Select(r => r.Tag));
        Assert.All(leaf, r => Assert.Equal(ComponentLayer.Library, r.Layer));
        Assert.All(leaf, r => Assert.Equal("Leaf.Controls", r.Library!.Name));
        // Its CSS came out of the assembly's embedded Assets/, not out of a string in code.
        Assert.Contains("--leaf-accent", registry.AggregatedCss);
        Assert.Contains(".leaf-counter-value", registry.AggregatedCss);
    }

    // ---- the CSS order: built-in < library < app --------------------------------------------------

    private static SKColor BackgroundOf(CupriDocument doc, string cls)
    {
        using (doc.RenderToImage(400, 300)) { }
        return TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains(cls) == true)!.Style.Background;
    }

    private const string ButtonPage = "<body><cupri-button>Go</cupri-button></body>";

    [Fact]
    public void A_library_rule_beats_a_built_in_rule_at_equal_specificity()
    {
        var lib = new TestLibrary("t-", ".cupri-button { background: rgb(1, 2, 3); }", new Probe("t-x"));
        using var doc = CupriDocument.Load(ButtonPage).UseComponents(ComponentRegistry.Default().Add(lib));
        Assert.Equal(new SKColor(1, 2, 3), BackgroundOf(doc, "cupri-button"));
    }

    [Fact]
    public void The_app_stylesheet_beats_the_library_which_beats_the_built_in()
    {
        var lib = new TestLibrary("t-", ".cupri-button { background: rgb(1, 2, 3); }", new Probe("t-x"));
        using var doc = CupriDocument.Load(ButtonPage, ".cupri-button { background: rgb(9, 8, 7); }")
                                     .UseComponents(ComponentRegistry.Default().Add(lib));
        Assert.Equal(new SKColor(9, 8, 7), BackgroundOf(doc, "cupri-button"));
    }

    [Fact]
    public void Layer_order_does_not_depend_on_the_order_things_were_added()
    {
        // The app's own component is added FIRST and the library second; the app layer still wins.
        var app = new Probe("my-thing", ".cupri-button { background: rgb(40, 50, 60); }");
        var lib = new TestLibrary("t-", ".cupri-button { background: rgb(1, 2, 3); }", new Probe("t-x"));
        var registry = ComponentRegistry.Default().Add(app).Add(lib);
        using var doc = CupriDocument.Load(ButtonPage).UseComponents(registry);
        Assert.Equal(new SKColor(40, 50, 60), BackgroundOf(doc, "cupri-button"));

        var order = registry.Registrations.Select(r => r.Layer).ToList();
        Assert.Equal(order.OrderBy(l => l), order);
    }

    [Fact]
    public void A_replacement_styles_over_the_component_it_replaced()
    {
        // A built-in's slot is at the front of the CSS; its replacement's rules must not stay there.
        var registry = ComponentRegistry.Default().Replace(
            new Probe("cupri-badge", ".cupri-badge { background: rgb(5, 5, 5); }"));
        var css = registry.AggregatedCss;
        Assert.True(css.IndexOf("rgb(5, 5, 5)", StringComparison.Ordinal)
                    > css.IndexOf(".cupri-button", StringComparison.Ordinal));
    }

    [Fact]
    public void An_app_restyles_a_packaged_component_with_plain_css()
    {
        const string html = """<body><leaf-status state="down">Down</leaf-status></body>""";
        using var plain = CupriDocument.Load(html).UseComponents(ComponentRegistry.Default().AddLeafControls());
        Assert.Equal(SKColor.Parse("#fdeceb"), BackgroundOf(plain, "leaf-status"));

        // Same specificity as the library's own `.leaf-status.down` rule — the app wins on order alone.
        using var styled = CupriDocument.Load(html, ".leaf-status.down { background: #000080; }")
                                        .UseComponents(ComponentRegistry.Default().AddLeafControls());
        Assert.Equal(SKColor.Parse("#000080"), BackgroundOf(styled, "leaf-status"));
    }

    [Fact]
    public void An_app_retunes_a_library_token_on_root()
    {
        const string html = """<body><leaf-card heading="H">x</leaf-card></body>""";
        using var doc = CupriDocument.Load(html, ":root { --leaf-surface: #102030; }")
                                     .UseComponents(ComponentRegistry.Default().AddLeafControls());
        Assert.Equal(SKColor.Parse("#102030"), BackgroundOf(doc, "leaf-card"));
    }

    // ---- behaviour ----------------------------------------------------------------------------

    [CupriBindable]
    public sealed partial class CounterModel
    {
        public int Guests { get; set; } = 2;
    }

    private const string CounterPage =
        """<body><leaf-counter label="Guests" value="{{Guests}}" min="0" max="3"></leaf-counter></body>""";

    private static void ClickStep(TestDoc t, bool up) =>
        t.ClickMatch(n => n.Element?.GetAttribute("data-leaf-step") is { } s && s.StartsWith('-') != up
                          && n.Element.ClassList.Contains("cupri-button"));

    [Fact]
    public void A_packaged_controls_own_click_handler_fires_with_nothing_wired_by_the_app()
    {
        var model = new CounterModel();
        using var t = new TestDoc(CounterPage, model: model, beforeFirstLayout: d =>
            d.UseComponents(ComponentRegistry.Default().AddLeafControls()));
        ClickStep(t, up: true);
        Assert.Equal(3, model.Guests);
        Assert.Equal("3", t.Find(n => n.Element?.ClassList.Contains("leaf-counter-value") == true)!
                            .Element!.TextContent.Trim());
        ClickStep(t, up: true);                            // clamped at max
        Assert.Equal(3, model.Guests);
        ClickStep(t, up: false);
        Assert.Equal(2, model.Guests);
    }

    [Fact]
    public void Behaviour_survives_a_refresh_and_a_rebind()
    {
        var model = new CounterModel { Guests = 0 };
        using var t = new TestDoc(CounterPage, model: model, beforeFirstLayout: d =>
            d.UseComponents(ComponentRegistry.Default().AddLeafControls()));
        ClickStep(t, up: true);
        t.Doc.Refresh(); t.Layout();
        ClickStep(t, up: true);
        var fresh = new CounterModel { Guests = 1 };
        t.Doc.Bind(fresh); t.Layout();
        ClickStep(t, up: true);
        Assert.Equal(2, model.Guests);
        Assert.Equal(2, fresh.Guests);
    }

    private sealed class CountingBehaviour : ComponentBase, ICupriBehaviour
    {
        public int Attached;
        public override string Tag => "zz-ping";
        public override string DefaultCss => "";
        public override void Expand(IElement el) => el.SetAttribute("data-zz-ping", "1");
        public void Attach(CupriDocument document)
        {
            Attached++;
            // A behaviour may use the seams that rebuild the document while it is being attached.
            document.OnRebuilt(_ => { });
        }
    }

    [Fact]
    public void Attach_runs_once_per_document_however_often_it_rebuilds()
    {
        var b = new CountingBehaviour();
        var registry = ComponentRegistry.Default().Add(b);
        using var one = CupriDocument.Load("<body><zz-ping></zz-ping></body>").UseComponents(registry);
        one.Refresh(); one.Refresh();
        Assert.Equal(1, b.Attached);
        using var two = CupriDocument.Load("<body><zz-ping></zz-ping></body>").UseComponents(registry);
        Assert.Equal(2, b.Attached);                       // one per document: state lives per document
        one.UseComponents(registry);
        Assert.Equal(2, b.Attached);                       // handing the same registry over again adds nothing
    }

    [Fact]
    public void A_library_added_after_the_registry_was_handed_over_still_gets_its_css_and_behaviour()
    {
        var registry = ComponentRegistry.Default();
        var model = new CounterModel { Guests = 1 };
        using var t = new TestDoc(CounterPage, model: model, beforeFirstLayout: d => d.UseComponents(registry));
        registry.AddLeafControls();                        // e.g. from a later Configure step
        t.Doc.Refresh(); t.Layout();
        Assert.Equal(new SKColor(0xff, 0xff, 0xff), t.FindClass("leaf-counter").Style.Background);
        ClickStep(t, up: true);
        Assert.Equal(2, model.Guests);
    }

    // ---- subclassing what used to be sealed --------------------------------------------------------

    private sealed class SquareButton : ButtonComponent
    {
        public override string DefaultCss => base.DefaultCss + ".cupri-button.square { border-radius: 0; background: rgb(0, 100, 0); }";
        public override void Expand(IElement el)
        {
            base.Expand(el);
            el.ClassList.Add("square");
        }
    }

    [Fact]
    public void A_built_in_can_be_subclassed_and_replaced()
    {
        var registry = ComponentRegistry.Default().Replace(new SquareButton());
        using var t = new TestDoc(ButtonPage, beforeFirstLayout: d => d.UseComponents(registry));
        var button = t.FindClass("square");
        Assert.Equal("button", button.Element!.GetAttribute("role"));           // base expansion kept
        Assert.Equal(new SKColor(0, 100, 0), button.Style.Background);          // the subclass's CSS
        Assert.True(CupriDoctor.Check(ButtonPage, "", registry).IsClean);
    }

    private sealed class FancyButton : ButtonComponent
    {
        public override string Tag => "my-fancy-button";
        public override void Expand(IElement el) { base.Expand(el); el.ClassList.Add("fancy"); }
    }

    [Fact]
    public void A_built_in_subclass_can_live_under_a_new_tag_beside_the_original()
    {
        var registry = ComponentRegistry.Default().Add(new FancyButton());
        using var t = new TestDoc("<body><cupri-button>A</cupri-button><my-fancy-button>B</my-fancy-button></body>",
            beforeFirstLayout: d => d.UseComponents(registry));
        Assert.NotNull(t.FindClass("fancy"));
        Assert.Equal(2, CountClass(t.Root, "cupri-button"));
    }

    private sealed class LoudStatus : StatusComponent
    {
        public override void Expand(IElement el) { base.Expand(el); el.SetAttribute("aria-live", "assertive"); }
    }

    [Fact]
    public void A_library_control_can_be_subclassed_and_replaced_by_the_app()
    {
        var registry = ComponentRegistry.Default().AddLeafControls().Replace(new LoudStatus());
        using var t = new TestDoc("""<body><leaf-status state="warn">W</leaf-status></body>""",
            beforeFirstLayout: d => d.UseComponents(registry));
        var pill = t.FindClass("leaf-status");
        Assert.Equal("assertive", pill.Element!.GetAttribute("aria-live"));
        Assert.Equal(SKColor.Parse("#fdf3e1"), pill.Style.Background);          // library CSS still applies
    }

    private sealed class Logged(ICupriComponent inner, List<string> log) : ICupriComponent
    {
        public string Tag => inner.Tag;
        public string DefaultCss => inner.DefaultCss;
        public void Expand(IElement el) { log.Add(el.LocalName); inner.Expand(el); }
    }

    [Fact]
    public void Find_and_Replace_wrap_a_component_without_subclassing_it()
    {
        var log = new List<string>();
        var registry = ComponentRegistry.Default().AddLeafControls();
        registry.Replace(new Logged(registry.Find("leaf-card")!, log));
        using var t = new TestDoc("""<body><leaf-card heading="H">x</leaf-card></body>""",
            beforeFirstLayout: d => d.UseComponents(registry));
        Assert.Equal(["leaf-card"], log.Distinct());
        Assert.NotNull(t.FindClass("leaf-card-heading"));
    }

    // ---- the doctor knows about libraries ------------------------------------------------------------

    [Fact]
    public void An_unregistered_tag_under_a_library_prefix_is_an_error_with_a_suggestion()
    {
        var registry = ComponentRegistry.Default().AddLeafControls();
        var report = CupriDoctor.Check("<body><leaf-cuonter></leaf-cuonter></body>", "", registry);
        var f = Assert.Single(report.Findings, f => f.Code == "CF0020");
        Assert.Equal(Severity.Error, f.Severity);
        Assert.Contains("leaf-counter", f.Fix);
    }

    [Fact]
    public void A_library_tag_checked_without_its_library_is_noted_not_failed()
    {
        // What `cupri preview` sees: only the built-ins. The tag is named, so a blank box is explained.
        var report = CupriDoctor.Check("<body><leaf-card heading='H'>x</leaf-card></body>", "");
        var f = Assert.Single(report.Findings, f => f.Code == "CF0020");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_page_using_the_library_checks_clean_with_its_registry_and_model()
    {
        const string html = """
            <body>
              <leaf-card heading="Booking" subtitle="Tonight">
                <leaf-counter label="Guests" value="{{Guests}}" min="0" max="9"></leaf-counter>
                <leaf-status state="ok">Open</leaf-status>
              </leaf-card>
            </body>
            """;
        var report = CupriDoctor.Check(html, "body { margin: 0; padding: 16px; }", CupriDoctor.DesktopAndPhone,
            ComponentRegistry.Default().AddLeafControls(), new CounterModel());
        Assert.False(report.HasErrors, report.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code is "CF0020" or "CF0022" or "CF0060");
    }

    // ---- AOT-friendliness, where a unit test can see it ------------------------------------------------

    [Fact]
    public void Registration_takes_instances_never_types_or_assemblies()
    {
        // The trim-safety of the packaging story rests on there being no reflective way in: every
        // public way to put a component in a registry takes an object someone constructed in code, so
        // the trimmer sees each one. A Type- or Assembly-taking overload would quietly break that.
        var methods = typeof(ComponentRegistry).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(ComponentRegistry));
        foreach (var p in methods.SelectMany(m => m.GetParameters()))
            Assert.False(p.ParameterType == typeof(Type) || p.ParameterType == typeof(System.Reflection.Assembly),
                $"{p.Member.Name}({p.ParameterType.Name}) would make registration reflective");
    }

    private static int CountClass(RenderNode n, string cls) =>
        (n.Element?.ClassList.Contains(cls) == true ? 1 : 0) + n.Children.Sum(c => CountClass(c, cls));
}
