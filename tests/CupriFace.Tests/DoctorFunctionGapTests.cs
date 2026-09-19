using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// CF0051 — an unsupported CSS <i>function</i> — reported from the declaration that uses one rather
/// than from anywhere the words appear (#188).
///
/// <para>It was a substring search over the stylesheet and the markup, so it fired on a document
/// that never used a repeating gradient: one whose comment explained that it deliberately avoids
/// them, and even one whose body copy merely displayed the words. The check could not be silenced
/// except by not writing the name down — so a project could not document why it avoided the feature
/// without failing its own lint, and any composition whose subject was CSS became unlintable.</para>
///
/// <para>The report was the perverse case: a file whose header comment said <i>"one gradient with
/// hard stops rather than a repeating-linear-gradient(), because the engine does not paint repeating
/// gradients — CupriDoctor reports that as CF0051"</i>. The warning punished someone for writing
/// down why they had obeyed it.</para>
///
/// <para>CF0050 never behaved this way, because it is raised from the properties the resolver threw
/// away. CF0051 now comes from the declarations the resolver applied, which is the same idea one
/// level down: only a declaration can paint nothing.</para>
/// </summary>
public class DoctorFunctionGapTests(ITestOutputHelper output)
{
    private const string Style = "<style>body,html{font-family:\"Noto Sans\";}";

    private static int Cf0051(string html) =>
        CupriDoctor.Check(html, "", width: 1280, height: 720).Findings.Count(f => f.Code == "CF0051");

    /// <summary>The reported table, verbatim.</summary>
    [Theory]
    // 1. the control: a document that really does use one
    [InlineData("<div class=\"a\">x</div>" + Style
        + ".a{width:200px;height:40px;background:repeating-linear-gradient(45deg,#111 0 10px,#222 10px 20px);}</style>", 1)]
    // 2. named only in an HTML comment
    [InlineData("<!-- one gradient with hard stops rather than a repeating-linear-gradient(), because... -->"
        + "<div class=\"a\">x</div>" + Style + ".a{width:200px;height:40px;background:#d9642a;}</style>", 0)]
    // 3. named only in a CSS comment
    [InlineData("<div class=\"a\">x</div>" + Style
        + " /* not a repeating-linear-gradient() */ .a{width:200px;height:40px;background:#d9642a;}</style>", 0)]
    // 4. named only in ordinary text the document PAINTS
    [InlineData("<div class=\"a\">repeating-linear-gradient()</div>" + Style
        + ".a{width:200px;height:40px;background:#d9642a;}</style>", 0)]
    // 5. the other control: no mention at all
    [InlineData("<div class=\"a\">x</div>" + Style + ".a{width:200px;height:40px;background:#d9642a;}</style>", 0)]
    public void The_reported_table(string html, int expected)
    {
        var got = Cf0051(html);
        output.WriteLine($"expected {expected}, got {got}");
        Assert.Equal(expected, got);
    }

    /// <summary>The composition that started it: a file that avoids the feature and says so. It must
    /// lint clean, or the only way to pass is to delete the explanation.</summary>
    [Fact]
    public void A_document_can_explain_why_it_avoids_a_gap()
    {
        var html = """
            <!-- It is one gradient with hard stops rather than a repeating-linear-gradient(), because
                 the engine does not paint repeating gradients — CupriDoctor reports that as CF0051
                 rather than leaving it to be found in a render. -->
            <div class="bar">x</div>
            <style>
              body,html{font-family:"Noto Sans";}
              .bar{width:200px;height:40px;background:linear-gradient(90deg,#111 0 10px,#222 10px 20px);}
            </style>
            """;
        var report = CupriDoctor.Check(html, "", width: 1280, height: 720);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0051");
    }

    /// <summary>Every gap in the list behaves the same way — the fix must not be about one needle.</summary>
    [Theory]
    [InlineData("repeating-linear-gradient(45deg,#111 0 10px,#222 10px 20px)")]
    [InlineData("repeating-radial-gradient(circle,#111 0 10px,#222 10px 20px)")]
    [InlineData("conic-gradient(#111 0deg,#222 180deg)")]
    [InlineData("image-set(\"a.png\" 1x)")]
    public void Each_gap_is_reported_when_used_and_not_when_only_mentioned(string value)
    {
        var used = $"<div class='a'>x</div><style>.a{{width:50px;height:50px;background:{value};}}</style>";
        var mentioned = $"<div class='a'>{value}</div><style>/* {value} */ .a{{width:50px;height:50px;background:#eee;}}</style>";

        output.WriteLine($"{value,-58} used={Cf0051(used)} mentioned={Cf0051(mentioned)}");
        Assert.Equal(1, Cf0051(used));
        Assert.Equal(0, Cf0051(mentioned));
    }

    /// <summary>An external stylesheet is still checked — the fix moved where findings come from, and
    /// must not have dropped a source of them.</summary>
    [Fact]
    public void An_external_stylesheet_is_still_checked()
    {
        var r = CupriDoctor.Check("<div class='a'>x</div>",
            ".a{width:50px;height:50px;background:conic-gradient(#111 0deg,#222 180deg);}");
        Assert.Single(r.Findings, f => f.Code == "CF0051");
    }

    /// <summary>…and a comment in the EXTERNAL stylesheet is ignored too. The old search read that
    /// file as well, so this was wrong before the inline-style change ever widened it.</summary>
    [Fact]
    public void A_comment_in_the_external_stylesheet_is_ignored()
    {
        var r = CupriDoctor.Check("<div class='a'>x</div>",
            "/* deliberately not a conic-gradient() */ .a{width:50px;height:50px;background:#eee;}");
        Assert.DoesNotContain(r.Findings, f => f.Code == "CF0051");
    }

    /// <summary>One finding per gap, not one per element it applies to. The resolver announces a
    /// declaration once for every element the rule matches, so a repeated row would otherwise report
    /// the same gradient a dozen times.</summary>
    [Fact]
    public void A_gap_used_by_many_elements_is_reported_once()
    {
        var rows = string.Concat(Enumerable.Range(0, 12).Select(_ => "<div class='r'>x</div>"));
        var html = $"<div>{rows}</div><style>.r{{width:50px;height:8px;"
                 + "background:conic-gradient(#111 0deg,#222 180deg);}</style>";
        Assert.Equal(1, Cf0051(html));
    }

    /// <summary>Two different gaps in one document are two findings — deduplicating per gap must not
    /// have collapsed them into one.</summary>
    [Fact]
    public void Two_different_gaps_are_two_findings()
    {
        var html = "<div class='a'>x</div><div class='b'>y</div><style>"
                 + ".a{width:50px;height:50px;background:conic-gradient(#111 0deg,#222 180deg);}"
                 + ".b{width:50px;height:50px;background:repeating-linear-gradient(45deg,#111 0 10px,#222 10px 20px);}"
                 + "</style>";
        Assert.Equal(2, Cf0051(html));
    }

    /// <summary>A gap reached through a custom property is still a real declaration, and the hook
    /// reports the resolved value — so hiding one behind var() does not hide it from the check.</summary>
    [Fact]
    public void A_gap_behind_a_custom_property_is_still_found()
    {
        var html = "<div class='a'>x</div><style>"
                 + ":root{--bg:conic-gradient(#111 0deg,#222 180deg);}"
                 + ".a{width:50px;height:50px;background:var(--bg);}</style>";
        output.WriteLine(CupriDoctor.Check(html, "", width: 400, height: 300).ToString());
        Assert.Equal(1, Cf0051(html));
    }

    /// <summary>The finding still carries a usable line number. It is found by searching the text,
    /// which is fine once the finding itself has been established by a declaration.</summary>
    [Fact]
    public void The_finding_still_points_at_a_line()
    {
        var html = "<div class='a'>x</div>\n<style>\n.a{width:50px;height:50px;"
                 + "background:conic-gradient(#111 0deg,#222 180deg);}\n</style>";
        var f = Assert.Single(CupriDoctor.Check(html, "").Findings, x => x.Code == "CF0051");
        output.WriteLine($"{f.Code} line {f.Line}: {f.Message}");
        Assert.True(f.Line > 0, "a finding with no line at all is a regression in usability");
    }

    /// <summary>The contrast the report drew: CF0050 was always right about this, and still is.</summary>
    [Fact]
    public void The_property_check_still_ignores_a_name_in_a_comment()
    {
        var clean = CupriDoctor.Check(
            "<div class='a'>x</div><style>/* we avoid letter-spacing here */ .a{width:10px;height:10px;}</style>", "");
        Assert.DoesNotContain(clean.Findings, f => f.Code == "CF0050");

        var real = CupriDoctor.Check(
            "<div class='a'>x</div><style>.a{width:10px;height:10px;letter-spacing:2px;}</style>", "");
        Assert.Single(real.Findings, f => f.Code == "CF0050");
    }
}
