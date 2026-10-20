using CupriFace.Components;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>CupriDoctor.Check(doc)</c>: the checks run on a document that already exists, in the state it
/// is in — rather than on a fresh one built from strings, which only ever shows the state a fresh
/// document starts in (every menu closed, nothing focused, no handler run).
/// </summary>
public class LiveDoctorTests(ITestOutputHelper output)
{
    private sealed class Flags { public bool Open { get; set; } }

    // A dialog whose panel holds more than its fixed height allows. Closed, the fault does not exist
    // on screen; open, it is CF0070.
    private const string Html =
        "<body><div class='page'>Page</div><cupri-dialog open='{{Open}}' title='T'>"
        + "<div class='panel'><div class='r'>one</div><div class='r'>two</div><div class='r'>three</div></div>"
        + "<cupri-button>OK</cupri-button></cupri-dialog></body>";
    private const string Css = "body{font-family:sans-serif} .panel{height:30px} .r{height:20px}";

    private static CupriDocument Load(Flags model)
    {
        var doc = CupriDocument.Load(Html, Css);
        doc.UseComponents(ComponentRegistry.Default());
        doc.Bind(model);
        doc.Refresh();
        using (doc.RenderToImage(412, 400)) { }
        return doc;
    }

    [Fact]
    public void A_fault_inside_an_overlay_is_seen_only_once_the_overlay_is_open()
    {
        var model = new Flags();
        using var doc = Load(model);

        Assert.DoesNotContain(CupriDoctor.Check(doc).Findings, f => f.Code == "CF0070");

        model.Open = true;                      // what a click on the trigger would do
        doc.Refresh();
        var report = CupriDoctor.Check(doc);
        output.WriteLine(report.ToString());
        var f = Assert.Single(report.Findings, x => x.Code == "CF0070");
        Assert.Contains("<div class='panel'>", f.Message);
    }

    /// <summary>The string-based check and the live check agree on a document in its starting
    /// state, for everything written in the caller's own markup and stylesheet. Measured on the
    /// largest real document in the repo.
    ///
    /// <para>Left out of the comparison: CF0050 with no line, which is a property in a COMPONENT's
    /// own stylesheet. The string check hears those from its first, unbound build — every section
    /// visible, every control present — and the live check only from the controls actually in the
    /// live tree. Both are honest about the document they were given.</para></summary>
    [Theory]
    [InlineData("controls")]
    [InlineData("components")]
    [InlineData("overlays")]
    public void The_live_check_matches_the_string_check_on_a_fresh_document(string section)
    {
        var app = new Demo.ShowcaseApp(section);
        var fromStrings = CupriDoctor.Check(app.Html, app.Css, app.Components, 940, 720, app.Model, app.Configure);

        using var doc = app.CreateDocument();
        doc.Refresh();
        using (doc.RenderToImage(940, 720)) { }
        var live = CupriDoctor.Check(doc);

        output.WriteLine("strings: " + fromStrings);
        output.WriteLine("live:    " + live);
        static IEnumerable<string> Codes(DoctorReport r) =>
            r.Findings.Where(f => f.Code != "CF0050" || f.Line > 0).Select(f => f.Code).OrderBy(c => c);
        Assert.Equal(Codes(fromStrings), Codes(live));
    }

    /// <summary>The CSS checks hear the stylesheet through a rebuild, so the live check is not a
    /// box-only subset: an ignored property is reported from the live document too.</summary>
    [Fact]
    public void The_live_check_hears_the_stylesheet()
    {
        using var doc = CupriDocument.Load("<body><div class='a'>x</div></body>", ".a{color:#000;text-decoration-skip-ink:none}");
        doc.Refresh();
        using (doc.RenderToImage(200, 100)) { }

        var fromStrings = CupriDoctor.Check("<body><div class='a'>x</div></body>", ".a{color:#000;text-decoration-skip-ink:none}", width: 200, height: 100);
        var live = CupriDoctor.Check(doc);
        output.WriteLine(live.ToString());
        Assert.Equal(fromStrings.Findings.Count(f => f.Code == "CF0050"), live.Findings.Count(f => f.Code == "CF0050"));
        Assert.Contains(live.Findings, f => f.Code == "CF0050");
    }

    [Fact]
    public void The_document_is_left_as_it_was_and_still_usable()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='s'><div class='tall'>t</div></div></body>",
            "body{margin:0} .s{height:100px;overflow:scroll} .tall{height:1000px}");
        doc.Refresh();
        using (doc.RenderToImage(200, 200)) { }
        var scroller = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("s") == true)!;
        doc.DispatchWheel(50, 50, 300f);                   // positive scrolls down
        using (doc.RenderToImage(200, 200)) { }
        scroller = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("s") == true)!;
        var before = scroller.ScrollY;
        Assert.True(before > 0);

        var report = CupriDoctor.Check(doc);
        Assert.NotNull(report);

        scroller = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("s") == true)!;
        Assert.Equal(before, scroller.ScrollY, 0.5);
        Assert.Equal(200f, doc.ViewportWidth);              // its own viewport, not the default
        using (doc.RenderToImage(200, 200)) { }             // not disposed by the check
    }

    [Fact]
    public void A_size_can_be_given_to_check_the_live_state_on_a_phone()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='row'><div class='a'></div><div class='b'></div></div></body>",
            "body{margin:0} .row{display:flex} .a,.b{width:300px;height:20px;flex-shrink:0}");
        doc.Refresh();
        using (doc.RenderToImage(1000, 300)) { }

        Assert.DoesNotContain(CupriDoctor.Check(doc).Findings, f => f.Code == "CF0072");
        Assert.Contains(CupriDoctor.Check(doc, 412, 915).Findings, f => f.Code == "CF0072");
        Assert.Equal(412f, doc.ViewportWidth);              // left at the size asked for, as documented
    }
}
