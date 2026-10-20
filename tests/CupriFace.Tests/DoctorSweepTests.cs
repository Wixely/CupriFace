using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>CupriDoctor.Sweep</c>: the document in the states it can get into — each flag on the model
/// flipped, all of them, every pair, and the focus on each control — with findings stamped by the
/// state they appear in.
/// </summary>
public class DoctorSweepTests(ITestOutputHelper output)
{
    private sealed class Model
    {
        public bool Open { get; set; }
        public bool Dark { get; set; }
        public bool A { get; set; }
        public bool B { get; set; }
        public string Name { get; set; } = "x";
        public int Count { get; set; } = 3;
    }

    // ---- discovering the flags ---------------------------------------------------------------

    [Fact]
    public void Every_bool_the_markup_binds_is_a_flag_and_nothing_else_is()
    {
        var html = "<body class='{{Dark}}'><cupri-dialog open='{{Open}}'>{{Name}} {{Count}} {{Nope}}</cupri-dialog><p data-a='{{A}}'>p</p></body>";
        var flags = CupriDoctor.DiscoverFlags(html, new Model());
        Assert.Equal(["Dark", "Open", "A"], flags);          // in the order written; strings, ints and unknowns skipped
    }

    [Fact]
    public void A_read_only_model_discovers_no_flags()
    {
        var flags = CupriDoctor.DiscoverFlags("<body><cupri-dialog open='{{Open}}'>x</cupri-dialog></body>", new { Open = false });
        Assert.Empty(flags);
    }

    // ---- states and their stamps --------------------------------------------------------------

    private const string Dialog =
        "<body><div class='page'>Page</div><cupri-dialog open='{{Open}}' title='T'>"
        + "<div class='panel'><div class='r'>one</div><div class='r'>two</div><div class='r'>three</div></div>"
        + "<cupri-button>OK</cupri-button></cupri-dialog></body>";
    private const string DialogCss = "body{font-family:sans-serif} .panel{height:30px} .r{height:20px}";

    [Fact]
    public void A_fault_only_visible_with_the_dialog_open_is_stamped_with_that_state()
    {
        var model = new Model();
        var report = CupriDoctor.Sweep(Dialog, DialogCss, model: model, viewports: [(412, 400)],
                                       options: new SweepOptions { Focus = false });
        output.WriteLine(report.ToString());

        Assert.DoesNotContain(CupriDoctor.Check(Dialog, DialogCss, width: 412, height: 400, model: model).Findings,
                              f => f.Code == "CF0070");                      // the plain check cannot see it
        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Equal("with Open", f.State);
        Assert.Contains("[with Open]", f.ToString());
        Assert.False(model.Open, "the model must be put back");
        Assert.Equal(2, report.Checks);                                       // as it loads, and with Open
    }

    [Fact]
    public void A_finding_present_as_the_document_loads_carries_no_state()
    {
        var model = new Model();
        var report = CupriDoctor.Sweep(
            "<body><div class='fixed'><div class='r'>a</div><div class='r'>b</div></div><cupri-dialog open='{{Open}}'>d</cupri-dialog></body>",
            ".fixed{height:10px} .r{height:20px}", model: model, viewports: [(400, 300)],
            options: new SweepOptions { Focus = false });
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Equal("", f.State);
    }

    /// <summary>Two flags that are each fine alone and wrong together: the pair state is the one
    /// that finds it, and the stamp names both.</summary>
    [Fact]
    public void A_fault_that_needs_two_flags_together_is_found_by_the_pair_and_stamped_with_both()
    {
        var html = "<body><div class='row' data-a='{{A}}' data-b='{{B}}'><div class='x'>x</div><div class='y'>y</div></div></body>";
        var css = "body{margin:0} .row{display:flex} .x,.y{width:100px;height:20px;flex-shrink:0}"
                + " .row[data-a='True'] .x{width:250px} .row[data-b='True'] .y{width:250px}";
        var model = new Model();
        var report = CupriDoctor.Sweep(html, css, model: model, viewports: [(400, 300)],
                                       options: new SweepOptions { Focus = false });
        output.WriteLine(report.ToString());

        var f = Assert.Single(report.Findings, x => x.Code == "CF0072");
        Assert.Contains("A, B", f.State);
        Assert.False(model.A || model.B);
    }

    [Fact]
    public void Pairs_can_be_switched_off_and_the_budget_drops_them_first()
    {
        var html = "<body><p data-a='{{A}}' data-b='{{B}}' data-c='{{Open}}' data-d='{{Dark}}'>p</p></body>";
        var none = CupriDoctor.Sweep(html, "", model: new Model(), viewports: [(400, 300)],
                                     options: new SweepOptions { Focus = false, Pairs = false });
        Assert.Equal(1 + 4 + 1, none.Checks);                                  // loads, each alone, all
        var all = CupriDoctor.Sweep(html, "", model: new Model(), viewports: [(400, 300)],
                                    options: new SweepOptions { Focus = false });
        Assert.Equal(1 + 4 + 1 + 6, all.Checks);                               // …and six pairs
        var capped = CupriDoctor.Sweep(html, "", model: new Model(), viewports: [(400, 300)],
                                       options: new SweepOptions { Focus = false, MaxChecks = 8 });
        Assert.Equal(8, capped.Checks);                                        // the last pairs dropped, singles kept
        var tight = CupriDoctor.Sweep(html, "", model: new Model(), viewports: [(400, 300)],
                                      options: new SweepOptions { Focus = false, MaxChecks = 3 });
        Assert.Equal(3, tight.Checks);                                         // loads, A, B
    }

    [Fact]
    public void A_flag_already_on_is_swept_off_and_said_so()
    {
        var model = new Model { Open = true };
        var report = CupriDoctor.Sweep(Dialog, DialogCss, model: model, viewports: [(412, 400)],
                                       options: new SweepOptions { Focus = false });
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Equal("", f.State);                                             // present as it loads: the dialog is open
        Assert.True(model.Open);
    }

    // ---- focus ---------------------------------------------------------------------------------

    /// <summary>A column of buttons, one per row, where focus widens the focused one: each is right
    /// on its own and the column is ragged only while one has the focus (CF0073), which is a state
    /// the plain check never sees. (One per ROW, because CF0073 reads a column off sibling rows — a
    /// stack of buttons sharing one parent is ragged by nature and stays silent.)</summary>
    [Fact]
    public void Tabbing_through_the_controls_finds_what_only_focus_shows()
    {
        var html = "<body><div class='row'><cupri-button>One</cupri-button><span>a</span></div>"
                 + "<div class='row'><cupri-button>Two</cupri-button><span>b</span></div>"
                 + "<div class='row'><cupri-button>Three</cupri-button><span>c</span></div></body>";
        var css = "body{margin:0;font-family:sans-serif} .row{display:flex;align-items:center;gap:8px;margin-bottom:8px}"
                + " .cupri-button{min-width:120px} .cupri-button[data-focus]{padding-left:40px;padding-right:40px;min-width:220px}";
        var plain = CupriDoctor.Check(html, css, width: 400, height: 300, model: new Model());
        Assert.DoesNotContain(plain.Findings, f => f.Code == "CF0073");

        var report = CupriDoctor.Sweep(html, css, model: new Model(), viewports: [(400, 300)]);
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0073");
        Assert.StartsWith("focus on \"", f.State);
        Assert.Contains("[focus on \"", f.ToString());
        Assert.Equal(1 + 3, report.Checks);                                    // loads, then three Tab stops
    }

    [Fact]
    public void Focus_stops_when_it_comes_back_round()
    {
        var html = "<body><cupri-button>A</cupri-button><cupri-button>B</cupri-button></body>";
        var report = CupriDoctor.Sweep(html, "", model: new Model(), viewports: [(400, 300)]);
        Assert.Equal(1 + 2, report.Checks);
    }

    // ---- with viewports ------------------------------------------------------------------------

    [Fact]
    public void Viewport_and_state_stamps_combine()
    {
        var html = "<body><div class='row'><div class='a'></div><div class='b'></div></div><cupri-dialog open='{{Open}}' title='T'>"
                 + "<div class='panel'><div class='r'>one</div><div class='r'>two</div></div></cupri-dialog></body>";
        var css = "body{margin:0} .row{display:flex} .a,.b{width:300px;height:20px;flex-shrink:0} .panel{height:10px} .r{height:20px}";
        var report = CupriDoctor.Sweep(html, css, model: new Model(), viewports: CupriDoctor.DesktopAndPhone,
                                       options: new SweepOptions { Focus = false });
        output.WriteLine(report.ToString());

        var off = Assert.Single(report.Findings, x => x.Code == "CF0072");
        Assert.Equal("412x915", off.Viewports);
        Assert.Equal("", off.State);                                           // wrong at the phone size however the flags sit
        var over = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Equal("", over.Viewports);                                      // wrong at both sizes…
        Assert.Equal("with Open", over.State);                                      // …only with the dialog open
    }

    /// <summary>The whole Showcase, every flag, both sizes: the shape of the call an app's test
    /// would make, and a check that it stays within its budget and reports nothing it should not.</summary>
    [Fact]
    public void The_shipped_showcase_can_be_swept()
    {
        var app = new Demo.ShowcaseApp("overlays");
        var report = CupriDoctor.Sweep(app.Html, app.Css, app.Components, app.Model, app.Configure,
                                       viewports: [(940, 720)], options: new SweepOptions { Pairs = false, Focus = false });
        output.WriteLine($"{report.Checks} checks");
        output.WriteLine(report.ToString());
        Assert.False(report.HasErrors, report.ToString());
        Assert.True(report.Checks > 10, "the Showcase's overlay flags were not discovered");
        // The sweep's first real catch: the drawer panel was height:100% PLUS padding, 44px taller
        // than the viewport with its bottom off-screen (CF0076) — fixed with box-sizing:border-box.
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0076");
    }
}
