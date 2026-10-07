using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The HTML <c>hidden</c> attribute is <c>display: none</c>, as it is in every browser's UA
/// stylesheet (#280).
///
/// <para>It was not honoured, so a hidden element rendered — and what a hidden element usually
/// holds is data. One corpus composition keeps its message list in
/// <c>&lt;div hidden data-hf-primitive-data&gt;{…}&lt;/div&gt;</c> and the raw JSON was painted in a
/// line across the top of the frame.</para>
///
/// <para>The engine already believed the attribute everywhere else: keyboard focus skips a hidden
/// element and CupriDoctor counts one as hidden on purpose. So the painted pixels were
/// simultaneously unreachable by Tab and exempt from the check on elements that produce no
/// output, which is why nothing reported them.</para>
/// </summary>
public class HiddenAttributeTests(ITestOutputHelper output)
{
    private const string Css = "body{margin:0;background:#fff;color:#000;font-size:20px}";

    private int Painted(string html, string css = "")
    {
        using var t = new TestDoc("<body>" + html + "</body>", Css + " " + css, width: 600, height: 200);
        using var bmp = t.Render();
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.White) n++;
        output.WriteLine($"{html,-62} -> {n} px");
        return n;
    }

    // ---- the reported table --------------------------------------------------------------------

    [Theory]
    [InlineData("<div hidden>SHOULD NOT APPEAR</div>")]
    [InlineData("<div hidden=\"hidden\">SHOULD NOT APPEAR</div>")]
    [InlineData("<div hidden=\"\">SHOULD NOT APPEAR</div>")]
    [InlineData("<span hidden>SHOULD NOT APPEAR</span>")]
    [InlineData("<div hidden data-hf-primitive-data>{\"messages\":[1,2,3]}</div>")]
    public void A_hidden_element_paints_nothing(string html) => Assert.Equal(0, Painted(html));

    [Fact]
    public void A_visible_control_still_paints()
    {
        Assert.True(Painted("<div>SHOULD APPEAR</div>") > 300, "the control did not draw");
    }

    /// <summary>Its children go with it, however deep — the point is that the subtree is data.</summary>
    [Fact]
    public void The_whole_subtree_goes_with_it()
    {
        Assert.Equal(0, Painted("<div hidden><div><span>a</span><div>b</div></div></div>"));
    }

    /// <summary>A hidden element takes no space either: the block after it sits at the top.</summary>
    [Fact]
    public void It_takes_no_space_in_flow()
    {
        using var t = new TestDoc("<body><div hidden>data</div><div class='after'>x</div></body>",
            Css, width: 600, height: 200);
        var after = TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains("after") == true)!;
        output.WriteLine(t.Doc.DumpTree(maxDepth: 2));
        Assert.Equal(0f, after.Y, 0.5);
    }

    // ---- UA origin: an author rule still wins --------------------------------------------------

    /// <summary>The rule is at UA origin, so an author who deliberately shows a hidden element
    /// gets what they asked for, exactly as in a browser.</summary>
    [Theory]
    [InlineData("[hidden]{display:block}")]
    [InlineData(".shown{display:block}")]
    [InlineData("div{display:block}")]
    public void An_author_rule_can_still_show_it(string css)
    {
        Assert.True(Painted("<div hidden class='shown'>SHOWN ON PURPOSE</div>", css) > 300,
            "an author display rule should beat the UA default");
    }

    [Fact]
    public void An_inline_style_can_still_show_it()
    {
        Assert.True(Painted("<div hidden style='display:block'>SHOWN ON PURPOSE</div>") > 300);
    }

    /// <summary>…and the author's own `[hidden] { display: none }` — the workaround people were
    /// writing — keeps working rather than colliding with the new default.</summary>
    [Fact]
    public void The_author_workaround_still_works()
    {
        Assert.Equal(0, Painted("<div hidden>SHOULD NOT APPEAR</div>", "[hidden]{display:none}"));
    }

    // ---- the engine already agreed everywhere else ----------------------------------------------

    /// <summary>Focus skipped a hidden element before this change, and still does — the two now
    /// say the same thing rather than disagreeing.</summary>
    [Fact]
    public void A_hidden_control_is_not_focusable()
    {
        using var t = new TestDoc("<body><button hidden>no</button><button class='yes'>yes</button></body>",
            Css, width: 600, height: 200, components: true);
        t.Key(EditKey.Tab);
        var focused = t.FocusedName();
        output.WriteLine($"focus landed on: {focused}");
        Assert.DoesNotContain("no", focused ?? "");
    }

    [Fact]
    public void The_style_resolves_to_display_none()
    {
        using var t = new TestDoc("<body><div hidden class='h'>x</div></body>", Css, width: 600, height: 200);
        var node = TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains("h") == true)!;
        Assert.Equal(DisplayType.None, node.Style.Display);
    }

    /// <summary>A script-type data island was already invisible; it stays so. The two ways of
    /// writing a data island now behave the same.</summary>
    [Fact]
    public void A_json_script_island_is_still_invisible()
    {
        Assert.Equal(0, Painted("<script type='application/json'>{\"a\":1}</script>"));
    }
}
