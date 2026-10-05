using CupriFace.Components;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Value forms the resolver accepts and then does nothing with (#201).
///
/// <para>A property the engine does not support says so with <c>CF0050</c>. These did not: they were
/// accepted in silence and had no effect on the frame, which is the worst of the three outcomes —
/// a document that reports nothing is indistinguishable from one that worked.</para>
///
/// <para>3D transforms were the sharpest case, because <c>transform</c> is one of the few properties
/// that ANIMATES. A composition could run a <c>rotateY</c> through a whole keyframe sequence with the
/// timing, the easing and the stops all working correctly, and the element simply never turned.</para>
///
/// <para>The bar is "parses, paints, animates" — and only the first was observable from inside the
/// engine. These make the second observable too.</para>
/// </summary>
public class SilentValueGapTests(ITestOutputHelper output)
{
    private static List<Finding> Gaps(string html, string css) =>
        [.. CupriDoctor.Check(html, css, width: 300, height: 200, components: ComponentRegistry.Default())
             .Findings.Where(f => f.Code is "CF0050" or "CF0051")];

    private List<Finding> Rule(string declarations)
    {
        var fs = Gaps("<div class='p'>x</div>", $".p{{width:50px;height:50px;{declarations}}}");
        output.WriteLine($"{declarations,-46} {(fs.Count == 0 ? "quiet" : fs[0].Code + ": " + fs[0].Message)}");
        return fs;
    }

    // ---- the reported table ---------------------------------------------------------------------

    /// <summary>The transform functions the switch has no case for: the matrices and the shears.
    /// All of them parse and do nothing. (The 3D rotations and translations were in this table
    /// until #269 drew them.)</summary>
    [Theory]
    [InlineData("transform:matrix3d(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1)", "matrix3d")]
    [InlineData("transform:matrix(1,0,0,1,10,10)", "matrix")]
    [InlineData("transform:skew(10deg)", "skew")]
    [InlineData("transform:skewX(10deg)", "skewX")]
    public void An_unimplemented_transform_function_is_reported(string decl, string named)
    {
        var fs = Rule(decl);
        var f = Assert.Single(fs, x => x.Code == "CF0051");
        // The author's own spelling, not the lowercased needle it was matched with: someone scanning
        // a report for what they wrote should find it.
        Assert.Contains(named, f.Message, StringComparison.Ordinal);
    }

    /// <summary>A rotate3d about an axis that is not x, y or z alone cannot be kept as the three
    /// rotations the engine interpolates, so it is reported — by the resolver, which is the only
    /// place that can tell the two apart.</summary>
    [Fact]
    public void An_off_axis_rotate3d_is_reported()
    {
        var fs = Rule("transform:rotate3d(1,1,0,45deg)");
        Assert.Contains(fs, f => f.Message.Contains("rotate3d", StringComparison.Ordinal));
        Assert.Empty(Rule("transform:rotate3d(0,1,0,45deg)"));
    }

    /// <summary>The transforms that DO work stay quiet — the 2D ones, and since #269 the 3D
    /// rotations, translations and scale. A check that reported every transform would pass the
    /// theory above and be useless.</summary>
    [Theory]
    [InlineData("transform:rotate(10deg)")]
    [InlineData("transform:translate(10px,20px)")]
    [InlineData("transform:translateX(10px)")]
    [InlineData("transform:scale(2)")]
    [InlineData("transform:scaleY(1.5)")]
    [InlineData("transform:rotateY(50deg)")]
    [InlineData("transform:rotateX(50deg)")]
    [InlineData("transform:translate3d(60px,20px,0)")]
    [InlineData("transform:translateZ(40px)")]
    [InlineData("transform:scale3d(2,2,1)")]
    [InlineData("transform:perspective(400px) rotateY(20deg)")]
    public void A_transform_that_works_is_not_reported(string decl)
        => Assert.Empty(Rule(decl));

    // ---- backdrop-filter, which is neither supported nor unsupported ---------------------------

    /// <summary>
    /// On an ordinary element it parses and paints nothing, so it is reported.
    ///
    /// <para>This is the asymmetry that caught the reporter: <c>filter: blur(6px)</c> works, so the
    /// feature looks present, and only the backdrop variant is inert.</para>
    /// </summary>
    [Fact]
    public void Backdrop_filter_on_an_ordinary_element_is_reported()
    {
        var f = Assert.Single(Rule("backdrop-filter:blur(6px)"), x => x.Code == "CF0051");
        Assert.Contains("TOP-LAYER", f.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>…and on a dialog it is NOT reported, because there it genuinely works.</b> This is why it
    /// could not be a line in the unsupported-function table: the same declaration is correct in one
    /// place and inert in another, so the check has to know where the element is.
    /// </summary>
    [Fact]
    public void Backdrop_filter_on_a_top_layer_element_is_not_reported()
    {
        var fs = Gaps("<cupri-dialog open='true' blur='true'><div>hi</div></cupri-dialog>", "body{margin:0}");
        output.WriteLine(fs.Count == 0 ? "quiet, as it should be" : string.Join("; ", fs.Select(f => f.Message)));
        Assert.DoesNotContain(fs, f => f.Message.Contains("backdrop-filter"));
    }

    /// <summary>The control the reporter used: plain `filter` works and must stay quiet.</summary>
    [Theory]
    [InlineData("filter:blur(6px)")]
    [InlineData("filter:brightness(0.4)")]
    public void Plain_filter_is_not_reported(string decl) => Assert.Empty(Rule(decl));

    // ---- the row of the report that was already fixed ------------------------------------------

    /// <summary>The issue lists `repeating-linear-gradient` as silent. It is not — #188 made CF0051
    /// raise from the parsed declaration, so it fires on a real one. Pinned because the report says
    /// otherwise and someone reading both should find the answer here.</summary>
    [Fact]
    public void A_repeating_gradient_was_already_being_reported()
    {
        var f = Assert.Single(Rule("background:repeating-linear-gradient(45deg,#111 0 10px,#222 10px 20px)"),
                              x => x.Code == "CF0051");
        Assert.Contains("repeating-linear-gradient", f.Message, StringComparison.Ordinal);
    }

    /// <summary>…and still only when it is a declaration, not when the words merely appear — the
    /// property this check gained in #188, which these additions must not cost it.</summary>
    [Fact]
    public void The_gap_check_still_ignores_a_mention_in_a_comment()
    {
        var fs = Gaps("<div class='p'>we avoid rotateY() and conic-gradient() here</div>",
                      "/* no rotateY(), no matrix3d() */ .p{width:50px;height:50px}");
        output.WriteLine(fs.Count == 0 ? "quiet" : string.Join("; ", fs.Select(f => f.Code + " " + f.Message)));
        Assert.Empty(fs);
    }

    /// <summary>One element with two gaps reports both, so a fix list is complete rather than
    /// discovered one build at a time.</summary>
    [Fact]
    public void Two_gaps_on_one_element_are_both_reported()
    {
        var fs = Rule("transform:skew(10deg);backdrop-filter:blur(2px)");
        Assert.Equal(2, fs.Count(f => f.Code == "CF0051"));
    }

    /// <summary>An animated gap is reported too. This is the case that made the silence expensive:
    /// everything about the animation works except the part that moves. (It was a rotateY until
    /// #269 made that move; a matrix3d is what is left.)</summary>
    [Fact]
    public void An_animated_gap_is_reported()
    {
        var fs = Gaps("<div class='p'>x</div>",
            ".p{width:50px;height:50px;animation:spin 2s linear infinite}"
            + "@keyframes spin{from{transform:matrix3d(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1)}to{transform:matrix3d(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1)}}");
        output.WriteLine(string.Join("\n", fs.Select(f => f.Code + ": " + f.Message)));
        Assert.Contains(fs, f => f.Code == "CF0051" && f.Message.Contains("matrix3d"));
    }
}
