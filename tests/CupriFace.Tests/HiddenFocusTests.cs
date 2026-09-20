using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// A control nobody can see is not a Tab stop (#195).
///
/// <para>The focus walk collected focusable nodes without asking whether any of them could be seen,
/// on the strength of a comment saying "display:none subtrees are already absent from the render
/// tree". They are not — <c>CupriDoctor</c> depends on exactly the opposite, walking the tree for
/// <c>display:none</c> nodes to tell "hidden on purpose" from "never drawn". One half of the engine
/// assumed what the other half relies on being false.</para>
///
/// <para>The result: a hidden control took a Tab stop and ran its click handler on Enter. Quiet in
/// the worst way — nothing is drawn, so a person tabbing through a form sees the focus ring vanish
/// for one press and come back, with no way to know what holds it.</para>
///
/// <para><c>visibility: hidden</c> is deliberately NOT here. That property is unimplemented (CF0050
/// reports it and it is ignored), so such an element is fully painted; skipping it would make a
/// control that is plainly on screen unreachable by keyboard — a worse bug than this one. See
/// <see cref="Visibility_hidden_is_still_a_stop_because_it_is_still_painted"/>.</para>
/// </summary>
public class HiddenFocusTests(ITestOutputHelper output)
{
    private const string Css = ".hidden{display:none}.scan,.btn{width:120px;height:24px}";

    /// <summary>Which control the first Tab lands on, identified by pressing Enter on it — the same
    /// way the report identified it, and the way a person would find out.</summary>
    private static string FirstStop(string scanAttrs, string scanClass = "scan")
    {
        // The class is a PARAMETER rather than something the caller appends: writing
        // `class='scan' class='scan hidden'` gives a duplicate attribute, the parser keeps the
        // first, and the test then measures the parser instead of the focus walk.
        var html = $"<body><div class='{scanClass}' {scanAttrs} data-scan='1'>Scan a code</div>"
                 + "<div class='btn' data-first='1'>First</div></body>";
        using var doc = CupriDocument.Load(html, Css);
        var trail = "";
        doc.OnClick(".scan", _ => trail += "scan");
        doc.OnClick("[data-first]", _ => trail += "first");
        doc.Refresh();
        using (doc.RenderToImage(400, 300)) { }
        doc.DispatchKey(null, EditKey.Tab);
        doc.DispatchKey(null, EditKey.Enter);
        return trail;
    }

    /// <summary>The control: a visible element really is the first stop, so the rest of the table
    /// is measuring something.</summary>
    [Fact]
    public void A_visible_control_is_the_first_stop() => Assert.Equal("scan", FirstStop(""));

    /// <summary>The reported table. Each of these used to report "scan" — the hidden element took
    /// the stop, and Enter operated it.</summary>
    [Theory]
    [InlineData("", "scan hidden")]              // display:none from a class
    [InlineData("style='display:none'", "scan")] // …and inline
    [InlineData("hidden", "scan")]               // the HTML attribute
    [InlineData("aria-hidden='true'", "scan")]   // announced as absent, yet operable
    public void A_hidden_control_is_not_the_first_stop(string attrs, string cls)
    {
        var got = FirstStop(attrs, cls);
        output.WriteLine($"class='{cls}' {attrs,-22} -> {(got == "" ? "(nothing)" : got)}");
        Assert.Equal("first", got);
    }

    /// <summary>Not specific to click handlers: a focusable ROLE behaves the same, which is the
    /// reporter's last case.</summary>
    [Fact]
    public void A_hidden_button_role_is_not_a_stop()
    {
        var html = "<body><div class='btn hidden' role='button' data-zero='1'>Hidden</div>"
                 + "<div class='btn' role='button' data-first='1'>First</div></body>";
        using var doc = CupriDocument.Load(html, Css);
        var trail = "";
        doc.OnClick("[data-zero]", _ => trail += "zero");
        doc.OnClick("[data-first]", _ => trail += "first");
        doc.Refresh();
        using (doc.RenderToImage(400, 300)) { }
        doc.DispatchKey(null, EditKey.Tab);
        doc.DispatchKey(null, EditKey.Enter);
        Assert.Equal("first", trail);
    }

    /// <summary>The whole subtree goes, not just the hidden node itself — a hidden panel full of
    /// buttons must not donate any of them to the Tab order.</summary>
    [Fact]
    public void Everything_inside_a_hidden_container_goes_with_it()
    {
        var html = "<body><div class='hidden'>"
                 + "<div class='btn' data-a='1'>A</div><div class='btn' data-b='1'>B</div></div>"
                 + "<div class='btn' data-first='1'>First</div></body>";
        using var doc = CupriDocument.Load(html, Css);
        var trail = "";
        doc.OnClick("[data-a]", _ => trail += "a");
        doc.OnClick("[data-b]", _ => trail += "b");
        doc.OnClick("[data-first]", _ => trail += "first");
        doc.Refresh();
        using (doc.RenderToImage(400, 300)) { }

        doc.DispatchKey(null, EditKey.Tab);
        doc.DispatchKey(null, EditKey.Enter);
        doc.DispatchKey(null, EditKey.Tab);
        doc.DispatchKey(null, EditKey.Enter);

        output.WriteLine($"two tabs visited: {trail}");
        Assert.Equal("firstfirst", trail);   // one stop only; the second Tab wraps back to it
    }

    /// <summary>
    /// Banter's case, which is why this was reported rather than worked around a third time: a
    /// control only one platform has, hidden on the others, sitting between two fields. It ate a Tab
    /// and broke three sign-in tests on the desktop head.
    /// </summary>
    [Fact]
    public void A_platform_only_control_does_not_eat_a_tab_stop()
    {
        var html = """
            <body>
              <div class='btn' data-server='1'>Server</div>
              <div class='btn hidden' data-scan='1'>Scan a code</div>
              <div class='btn' data-connect='1'>Connect</div>
            </body>
            """;
        using var doc = CupriDocument.Load(html, Css);
        var trail = "";
        doc.OnClick("[data-server]", _ => trail += "server ");
        doc.OnClick("[data-scan]", _ => trail += "scan ");
        doc.OnClick("[data-connect]", _ => trail += "connect ");
        doc.Refresh();
        using (doc.RenderToImage(400, 300)) { }

        for (var i = 0; i < 2; i++) { doc.DispatchKey(null, EditKey.Tab); doc.DispatchKey(null, EditKey.Enter); }

        output.WriteLine($"tab order: {trail}");
        Assert.Equal("server connect ", trail);
    }

    /// <summary>A control hidden and then shown again rejoins the Tab order — the walk reads the
    /// live tree, so this is really asking that nothing is cached across a rebuild.</summary>
    [Fact]
    public void Showing_a_control_again_returns_it_to_the_order()
    {
        var html = "<body><div class='scan' data-scan='1'>Scan</div>"
                 + "<div class='btn' data-first='1'>First</div></body>";

        using var hiddenDoc = CupriDocument.Load(
            html.Replace("class='scan'", "class='scan hidden'"), Css);
        using var shownDoc = CupriDocument.Load(html, Css);

        static string Walk(CupriDocument d)
        {
            var trail = "";
            d.OnClick("[data-scan]", _ => trail += "scan");
            d.OnClick("[data-first]", _ => trail += "first");
            d.Refresh();
            using (d.RenderToImage(400, 300)) { }
            d.DispatchKey(null, EditKey.Tab);
            d.DispatchKey(null, EditKey.Enter);
            return trail;
        }

        Assert.Equal("first", Walk(hiddenDoc));
        Assert.Equal("scan", Walk(shownDoc));
    }

    /// <summary>
    /// <c>visibility: hidden</c> is STILL a Tab stop, and that is correct today rather than an
    /// oversight: the property is unimplemented, so the element is fully painted. A control on
    /// screen that the keyboard cannot reach would be a worse bug than the one this fixes.
    ///
    /// <para>Pinned so the reasoning is recorded rather than rediscovered: if <c>visibility</c> is
    /// ever implemented, this test should fail and be changed in the same commit that makes the
    /// element stop painting.</para>
    /// </summary>
    [Fact]
    public void Visibility_hidden_is_still_a_stop_because_it_is_still_painted()
    {
        Assert.Equal("scan", FirstStop("style='visibility:hidden'", "scan"));

        var report = Diagnostics.CupriDoctor.Check(
            "<div class='p' style='visibility:hidden'>x</div><style>.p{width:10px;height:10px}</style>", "");
        output.WriteLine(report.ToString());
        Assert.Contains(report.Findings,
            f => f.Code == "CF0050" && f.Message.Contains("visibility"));
    }
}
