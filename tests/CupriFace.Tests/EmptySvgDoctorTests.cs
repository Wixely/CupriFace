using CupriFace.Diagnostics;
using CupriFace.Svg;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The doctor tells an empty <c>&lt;svg&gt;</c> apart from one the SVG package is not there to draw (#270).
///
/// <para>An <c>&lt;svg&gt;</c> was exempt from CF0030 only when the package had stamped it, and the
/// package does not stamp an svg with nothing in it — so a document that had the package and had
/// called <c>UseSvg()</c> was told to add the package and call <c>UseSvg()</c>. 17 of 165 blocks in
/// one corpus carry an empty <c>&lt;svg&gt;</c> that a script was going to fill; the right answer
/// there is "there is no script here", which is a different fix entirely.</para>
/// </summary>
public class EmptySvgDoctorTests(ITestOutputHelper output)
{
    private const string Empty = "<body><svg id='chart' viewBox='0 0 100 100'></svg></body>";
    private const string Drawn = "<body><svg viewBox='0 0 24 24'><rect x='4' y='4' width='16' height='16' fill='#000'/></svg></body>";

    private string Report(string html, bool useSvg)
    {
        var r = CupriDoctor.Check(html, "svg{width:100px;height:100px}", configure: useSvg ? d => d.UseSvg() : null);
        output.WriteLine(r.ToString());
        return r.ToString();
    }

    /// <summary>The issue's case: the package is configured, the svg is simply empty.</summary>
    [Fact]
    public void An_empty_svg_with_the_package_configured_is_told_it_is_empty_not_to_add_the_package()
    {
        var report = Report(Empty, useSvg: true);
        Assert.Contains("no drawable content", report);
        Assert.DoesNotContain("CupriFace.Svg", report);
        Assert.Contains("CF0030", report);
    }

    /// <summary>Without the package an empty svg is still empty for the same reason, and adding
    /// the package would not change that — so the advice is the same.</summary>
    [Fact]
    public void An_empty_svg_without_the_package_gets_the_same_answer()
    {
        var report = Report(Empty, useSvg: false);
        Assert.Contains("no drawable content", report);
        Assert.DoesNotContain("CupriFace.Svg", report);
    }

    /// <summary>An svg with only a defs block or a group is empty too.</summary>
    [Theory]
    [InlineData("<svg viewBox='0 0 10 10'><defs><linearGradient id='g'/></defs></svg>")]
    [InlineData("<svg viewBox='0 0 10 10'><g id='layer'></g></svg>")]
    public void Structure_without_shapes_counts_as_empty(string svg)
    {
        var report = Report("<body>" + svg + "</body>", useSvg: true);
        Assert.Contains("no drawable content", report);
    }

    /// <summary>An svg WITH shapes and no package still gets the package advice — that case is real.</summary>
    [Fact]
    public void A_drawn_svg_without_the_package_is_still_told_to_add_it()
    {
        var report = Report(Drawn, useSvg: false);
        Assert.Contains("CupriFace.Svg", report);
    }

    [Fact]
    public void A_drawn_svg_with_the_package_is_clean()
    {
        var r = CupriDoctor.Check(Drawn, "svg{width:100px;height:100px}", configure: d => d.UseSvg());
        output.WriteLine(r.ToString());
        Assert.DoesNotContain(r.Findings, f => f.Code == "CF0030");
    }

    /// <summary>A shape nested in a group, at any depth, is drawable content.</summary>
    [Fact]
    public void A_shape_inside_a_group_is_content()
    {
        var r = CupriDoctor.Check("<body><svg viewBox='0 0 24 24'><g><g><circle cx='12' cy='12' r='4'/></g></g></svg></body>",
            "svg{width:100px;height:100px}", configure: d => d.UseSvg());
        output.WriteLine(r.ToString());
        Assert.DoesNotContain(r.Findings, f => f.Code == "CF0030");
    }
}
