using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The rubber band's sideways half. Reported from a device: "vertical rubber banding appears to
/// work, horizontal does NOT" — and it didn't, because overscroll was a single-axis feature
/// (<c>OverscrollY</c> and nothing else). A row that scrolls sideways deserves the same "you have
/// arrived" signal at its end that a page gets at its bottom.
/// </summary>
public class HorizontalOverscrollTests
{
    // A strip wider than its box, inside a page that does NOT scroll vertically — so anything that
    // moves here moved on the horizontal axis, with no vertical path to accidentally take credit.
    private const string Html = "<body><div class='strip'><div class='wide'>w</div></div></body>";
    private const string Css = "body{margin:0} .strip{width:200px;height:80px;overflow:scroll} " +
                               ".wide{width:900px;height:40px}";

    private static CupriDocument Strip()
    {
        var doc = CupriDocument.Load(Html, Css);
        doc.BuildFrame(300, 300);
        return doc;
    }

    private static Dom.RenderNode Node(CupriDocument doc) =>
        TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("strip") == true)!;

    [Fact]
    public void Dragging_past_the_left_end_stretches_sideways()
    {
        using var doc = Strip();
        var touch = new TouchInput(doc);

        touch.Down(100, 40, 0);
        for (var i = 1; i <= 10; i++) touch.Move(100 + i * 12, 40, i * 0.01);   // pull RIGHT at the left end

        var strip = Node(doc);
        Assert.Equal(0f, strip.ScrollX, 1);                       // still at the start…
        Assert.True(strip.OverscrollX < -5, $"nothing stretched sideways ({strip.OverscrollX:F1})");
        Assert.True(doc.OverscrollActive, "the host would never animate the spring-back");
    }

    [Fact]
    public void The_sideways_band_is_bounded_and_springs_back()
    {
        using var doc = Strip();
        var touch = new TouchInput(doc);

        touch.Down(100, 40, 0);
        for (var i = 1; i <= 60; i++) touch.Move(100 + i * 20, 40, i * 0.01);   // pull far past the end

        var strip = Node(doc);
        Assert.True(MathF.Abs(strip.OverscrollX) <= 90.5f,
            $"stretched to {strip.OverscrollX:F0}px — the band should be bounded");
        Assert.True(MathF.Abs(strip.OverscrollX) > 30, "…but it should still visibly give");

        touch.Up(100 + 60 * 20, 40, 0.7);
        for (var f = 1; f <= 80 && doc.OverscrollActive; f++) doc.Animate(0.7 + f * 0.016);
        Assert.Equal(0f, Node(doc).OverscrollX, 1);
    }

    [Fact]
    public void Paint_and_hit_testing_agree_while_the_band_is_stretched()
    {
        // The vertical band's contract, held to on this axis too: what you see and what you can
        // touch must not drift apart mid-stretch.
        using var doc = Strip();
        var strip = Node(doc);
        strip.OverscrollX = 40;

        Assert.Equal(strip.ClampedScrollX + 40, strip.EffectiveScrollX, 2);
    }

    [Fact]
    public void A_text_field_never_acquires_a_band()
    {
        // ScrollX doubles as a single-line field's caret-follow shift. A field is not a scroller,
        // and EffectiveScrollX must pass it through untouched or every caret would sit wrong.
        using var doc = CupriDocument.Load(
            "<body><input class='f' value='x'></body>", "body{margin:0} .f{width:100px}");
        doc.BuildFrame(300, 300);
        var field = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("f") == true)!;
        field.ScrollX = 17;
        field.OverscrollX = 40;                       // even if something set one

        Assert.False(field.IsScrollableX);
        Assert.Equal(17f, field.EffectiveScrollX, 2);
    }

    [Fact]
    public void Moving_to_another_scroller_releases_the_first_band()
    {
        // Only one scroller is stepped at a time, so a band left behind on another would freeze at
        // its stretched offset and shift that content permanently.
        using var doc = CupriDocument.Load(
            "<body><div class='a'><div class='wide'>a</div></div>" +
            "<div class='b'><div class='wide'>b</div></div></body>",
            "body{margin:0} .a,.b{width:200px;height:80px;overflow:scroll} .wide{width:900px;height:40px}");
        doc.BuildFrame(300, 300);

        var touch = new TouchInput(doc);
        // Re-found after each gesture: the finger-down restyle rebuilds the tree.
        Dom.RenderNode A() => TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("a") == true)!;
        Dom.RenderNode B() => TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("b") == true)!;

        // Stretch the first strip, then start a fresh gesture on the second WITHOUT animating in
        // between — so the first band is still stretched at the moment the second one claims it.
        touch.Down(100, 40, 0);
        for (var i = 1; i <= 10; i++) touch.Move(100 + i * 12, 40, i * 0.01);
        Assert.True(MathF.Abs(A().OverscrollX) > 5, "the first strip never stretched");
        touch.Up(220, 40, 0.11);

        touch.Down(100, 120, 0.2);
        for (var i = 1; i <= 10; i++) touch.Move(100 + i * 12, 120, 0.2 + i * 0.01);

        Assert.True(MathF.Abs(B().OverscrollX) > 5, "the second strip never stretched");
        Assert.Equal(0f, A().OverscrollX, 2);
    }
}
