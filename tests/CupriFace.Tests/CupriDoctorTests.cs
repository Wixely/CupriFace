using CupriFace.Demo;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The development-time checker.
///
/// <para>Two halves, and the second matters more. The first is that each check FIRES on the mistake
/// it is for. The second is that it stays quiet on markup that is fine — because a checker with
/// false positives gets switched off, and a switched-off checker finds nothing at all. The shipped
/// Showcase is used as the quiet case: it is the largest real document in the repo, so anything the
/// checker dislikes about it is either a genuine finding or a bug in the checker.</para>
/// </summary>
public class CupriDoctorTests(ITestOutputHelper output)
{
    // ---- it stays quiet when nothing is wrong --------------------------------------------------

    [Fact]
    public void CleanMarkupProducesNothing()
    {
        var report = CupriDoctor.Check(
            "<body><div class='card'><cupri-button>Save</cupri-button></div></body>",
            ".card { display:flex; padding:12px; background:#fff; border-radius:8px }");

        output.WriteLine(report.ToString());
        Assert.True(report.IsClean, report.ToString());
    }

    /// <summary>The real Showcase — 700-odd lines of markup and its stylesheet. Any ERROR here is a
    /// false positive by definition: this document is what the screenshots in the README are.</summary>
    [Fact]
    public void TheShippedShowcaseHasNoErrors()
    {
        var app = new ShowcaseApp();
        var report = CupriDoctor.Check(app.Html, app.Css, app.Components);

        output.WriteLine(report.ToString());
        Assert.False(report.HasErrors, report.ToString());
    }

    /// <summary>Every other shipped app, for the same reason.</summary>
    [Theory]
    [InlineData("settings")]
    [InlineData("controls")]
    [InlineData("mobile")]
    public void ShippedAppsHaveNoErrors(string which)
    {
        CupriApp app = which switch
        {
            "settings" => new SettingsApp(),
            "controls" => new ControlsApp(),
            _ => new MobileApp(),
        };
        var report = CupriDoctor.Check(app.Html, app.Css, app.Components);

        output.WriteLine(report.ToString());
        Assert.False(report.HasErrors, report.ToString());
    }

    // ---- unbalanced tags -------------------------------------------------------------------------

    [Fact]
    public void UnclosedTagIsReportedAtTheLineItOpened()
    {
        var report = CupriDoctor.Check("<body>\n  <div class='a'>\n    <span>text</span>\n</body>");

        var f = Assert.Single(report.Findings, x => x.Code == "CF0010");
        Assert.Equal(Severity.Error, f.Severity);
        Assert.Contains("div", f.Message);
        Assert.Equal(2, f.Line);          // the line it was OPENED on, not where the problem surfaced
    }

    [Fact]
    public void StrayClosingTagIsReported()
    {
        var report = CupriDoctor.Check("<body><div>a</div></span></body>");

        Assert.Contains(report.Findings, f => f.Code == "CF0011" && f.Message.Contains("span"));
    }

    /// <summary>Void elements have no closing tag and must never be reported as unclosed — the
    /// commonest way a naive balance check produces nonsense.</summary>
    [Fact]
    public void VoidElementsAreNotUnbalanced()
    {
        var report = CupriDoctor.Check("<body><div>a<br>b<hr>c<input></div></body>");

        Assert.DoesNotContain(report.Findings, f => f.Code is "CF0010" or "CF0011");
    }

    [Fact]
    public void SelfClosingAndCommentsDoNotConfuseIt()
    {
        var report = CupriDoctor.Check(
            "<body>\n<!-- <div> a commented tag </div> -->\n<cupri-icon name='x'/>\n</body>");

        Assert.DoesNotContain(report.Findings, f => f.Code is "CF0010" or "CF0011");
    }

    // ---- browser habits --------------------------------------------------------------------------

    /// <summary>The one that started this: <c>&lt;img&gt;</c> renders as nothing, silently.</summary>
    [Fact]
    public void ImgIsCaughtAndPointedAtCupriImage()
    {
        var report = CupriDoctor.Check("<body><img src='cat.png'></body>");

        var f = Assert.Single(report.Findings, x => x.Code == "CF0030");
        Assert.Equal(Severity.Error, f.Severity);
        Assert.Contains("cupri-image", f.Fix);
    }

    [Theory]
    [InlineData("<body><video src='v.webm'></video></body>", "cupri-video")]
    [InlineData("<body><canvas></canvas></body>", "ISurfaceSource")]
    [InlineData("<body><svg></svg></body>", "cupri-icon")]
    [InlineData("<body><iframe src='x'></iframe></body>", "embedded browser")]
    public void OtherBrowserElementsAreCaught(string html, string expectedInFix)
    {
        var report = CupriDoctor.Check(html);

        Assert.Contains(report.Findings, f => f.Code == "CF0030" && f.Fix.Contains(expectedInFix));
    }

    [Fact]
    public void InlineHandlersAndScriptAreCaught()
    {
        var report = CupriDoctor.Check(
            "<body><div onclick='go()'>x</div><script>go()</script></body>");

        Assert.Contains(report.Findings, f => f.Code == "CF0041" && f.Message.Contains("onclick"));
        Assert.Contains(report.Findings, f => f.Code == "CF0040");
    }

    // ---- components ------------------------------------------------------------------------------

    [Fact]
    public void UnknownComponentSuggestsTheNearestRealOne()
    {
        var report = CupriDoctor.Check("<body><cupri-slidr></cupri-slidr></body>");

        var f = Assert.Single(report.Findings, x => x.Code == "CF0020");
        Assert.Equal(Severity.Error, f.Severity);
        Assert.Contains("cupri-slider", f.Fix);
    }

    /// <summary>A name nothing is close to gets the generic advice rather than a silly suggestion.</summary>
    [Fact]
    public void UnknownComponentWithNoNeighbourJustSaysRegisterIt()
    {
        var report = CupriDoctor.Check("<body><cupri-zzzzzzzzz></cupri-zzzzzzzzz></body>");

        var f = Assert.Single(report.Findings, x => x.Code == "CF0020");
        Assert.Contains("Register it", f.Fix);
    }

    [Fact]
    public void RegisteredComponentsAreNotReported()
    {
        var report = CupriDoctor.Check(
            "<body><cupri-slider value='5'></cupri-slider><cupri-switch></cupri-switch></body>");

        Assert.DoesNotContain(report.Findings, f => f.Code is "CF0020" or "CF0031");
    }

    // ---- CSS ------------------------------------------------------------------------------------

    /// <summary>Derived from the resolver itself, so this cannot drift: the property is reported
    /// because the real switch had no arm for it.</summary>
    [Fact]
    public void UnsupportedCssPropertyIsReported()
    {
        var report = CupriDoctor.Check("<body><div class='a'>x</div></body>",
                                       ".a { float: left; display: flex }");

        var f = Assert.Single(report.Findings, x => x.Code == "CF0050");
        Assert.Contains("float", f.Message);
        Assert.Contains("flexbox", f.Fix);
    }

    /// <summary>The mirror of the above, and the one that keeps the checker trustworthy: supported
    /// properties must produce nothing.</summary>
    [Fact]
    public void SupportedCssIsSilent()
    {
        var report = CupriDoctor.Check("<body><div class='a'>x</div></body>",
            ".a { display:flex; gap:8px; padding:4px 8px; color:#333; background:#fff; " +
            "border-radius:6px; font-size:14px; font-weight:bold; margin:0 auto; width:50% }");

        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0050");
    }

    /// <summary>The gap I walked into myself: the property is supported, the FUNCTION is not, so it
    /// parses and then paints nothing at all.</summary>
    [Fact]
    public void UnsupportedGradientFunctionIsReported()
    {
        var report = CupriDoctor.Check("<body><div class='a'>x</div></body>",
            ".a { background: repeating-linear-gradient(90deg, #fff 0, #fff 1px, #000 1px, #000 4px) }");

        Assert.Contains(report.Findings, f => f.Code == "CF0051");
    }

    [Fact]
    public void SupportedGradientIsSilent()
    {
        var report = CupriDoctor.Check("<body><div class='a'>x</div></body>",
                                       ".a { background: linear-gradient(90deg, #fff, #000) }");

        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0051");
    }

    // ---- the report itself -----------------------------------------------------------------------

    [Fact]
    public void ReportReadsAsSomethingYouCanPaste()
    {
        var report = CupriDoctor.Check("<body><img src='x.png'><div onclick='y()'>z</div></body>");
        var text = report.ToString();

        output.WriteLine(text);
        Assert.Contains("error CF0030", text);
        Assert.Contains("->", text);                 // every finding carries a suggested fix
        Assert.False(report.IsClean);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void CleanReportSaysSo()
    {
        Assert.Equal("No problems found.", CupriDoctor.Check("<body><div>ok</div></body>").ToString());
    }

    /// <summary>The hook the CSS check installs on the shared resolver must not survive the call —
    /// it is a static, and a leaked one would keep collecting for the rest of the process.</summary>
    [Fact]
    public void TheResolverHookIsAlwaysCleanedUp()
    {
        CupriDoctor.Check("<body><div class='a'>x</div></body>", ".a { float:left }");

        // Reflection rather than InternalsVisibleTo: the hook is internal on purpose, and opening
        // the whole engine's internals to the test assembly for one assertion is the wrong trade.
        var field = typeof(CupriDocument).Assembly
            .GetType("CupriFace.Style.StyleResolver")!
            .GetField("UnsupportedProperty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Null(field.GetValue(null));
    }
}
