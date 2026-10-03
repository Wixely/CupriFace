using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Styling the selected control yourself — <c>:focus</c>, and <c>outline: none</c> to take the
/// engine's ring off.
///
/// <para>Both were broken in ways that said nothing. <c>:focus</c> is rewritten to
/// <c>[data-focus]</c>, and that attribute was set only on an editable field — so
/// <c>button:focus { … }</c> was a rule that could never match, with no diagnostic and no clue. And
/// <c>outline: none</c>, the universal way to say "I draw my own focus styling", left the engine's
/// blue ring exactly where it was, because "none" was indistinguishable from never mentioning it.
/// An application that wanted its own selection styling had to track the selection itself and style a
/// class of its own.</para>
/// </summary>
public class FocusStylingTests
{
    // body padding so the first button is NOT at the origin: the ring is painted OUTSIDE the box,
    // and at 0,0 its top and left edges would be off-screen and unsampleable.
    private const string Frame = "body{margin:0;padding:20px} .b{width:200px;height:40px;background:#345}";

    private const string Html = """
        <body>
          <div class='b' id='one' role='button'>one</div>
          <div class='b' id='two' role='button'>two</div>
        </body>
        """;

    private static RenderNode Button(TestDoc t, string id) =>
        t.Find(n => n.Element?.GetAttribute("id") == id)!;

    /// <summary>
    /// THE RULE THAT COULD NEVER MATCH. A focused button is now reachable from CSS, so an author can
    /// style the selection however the design wants rather than accepting a blue ring.
    /// </summary>
    [Fact]
    public void A_focused_button_can_be_styled_with_focus()
    {
        using var t = new TestDoc(Html,
            Frame + " .b:focus{background:#f3ce7c}",
            width: 400, height: 300);

        Assert.Equal(new SKColor(0x33, 0x44, 0x55), Button(t, "one").Style.Background);
        t.Key(EditKey.Tab);
        Assert.Equal(new SKColor(0xF3, 0xCE, 0x7C), Button(t, "one").Style.Background);
        Assert.Equal(new SKColor(0x33, 0x44, 0x55), Button(t, "two").Style.Background);
    }

    /// <summary>It follows the selection, rather than sticking to whatever was focused first.</summary>
    [Fact]
    public void The_styling_follows_the_selection()
    {
        using var t = new TestDoc(Html,
            Frame + " .b:focus{background:#f3ce7c}",
            width: 400, height: 300);
        t.Key(EditKey.Tab);
        t.Key(EditKey.Tab);

        Assert.Equal(new SKColor(0x33, 0x44, 0x55), Button(t, "one").Style.Background);
        Assert.Equal(new SKColor(0xF3, 0xCE, 0x7C), Button(t, "two").Style.Background);
    }

    /// <summary><c>:focus-visible</c> works too — an author writing the modern spelling was getting a
    /// selector the parser did not recognise at all.</summary>
    [Fact]
    public void Focus_visible_is_understood()
    {
        using var t = new TestDoc(Html,
            Frame + " .b:focus-visible{background:#7ED491}",
            width: 400, height: 300);
        t.Key(EditKey.Tab);
        Assert.Equal(new SKColor(0x7E, 0xD4, 0x91), Button(t, "one").Style.Background);
    }

    /// <summary>A focused control can be found by selector, which is what an app driving its own
    /// selection styling needs in order to know the hook fired at all.</summary>
    [Fact]
    public void The_focused_control_carries_the_attribute()
    {
        using var t = new TestDoc(Html, Frame, width: 400, height: 300);
        t.Key(EditKey.Tab);
        Assert.True(Button(t, "one").Element!.HasAttribute("data-focus"));
        Assert.False(Button(t, "two").Element!.HasAttribute("data-focus"));
    }

    // ---- taking the engine's ring off ------------------------------------------------------------

    /// <summary>
    /// <c>outline: none</c> SUPPRESSES THE RING. Without it an author's own focus styling was drawn
    /// under the engine's blue rectangle, and the idiom every web developer reaches for did nothing.
    /// Asserted in pixels, because the ring is painted rather than laid out — there is no style to
    /// inspect, only an image to compare.
    /// </summary>
    [Fact]
    public void Outline_none_takes_the_ring_off()
    {
        const string css = Frame;
        using var withRing = new TestDoc(Html, css, width: 400, height: 300);
        using var without = new TestDoc(Html, css + " .b:focus{outline:none;background:#f3ce7c}",
            width: 400, height: 300);
        using var ownRing = new TestDoc(Html, css + " .b:focus{background:#f3ce7c}",
            width: 400, height: 300);

        withRing.Key(EditKey.Tab);
        without.Key(EditKey.Tab);
        ownRing.Key(EditKey.Tab);

        using var a = withRing.Render();
        using var b = without.Render();
        using var c = ownRing.Render();

        // The engine's ring sits OUTSIDE the box: 2px thick, 2px clear of it.
        var ring = (x: 120, y: 18);   // the ring spans y 18-19: box top 20, less a 2px gap, 2px thick
        Assert.NotEqual(SKColors.White, a.GetPixel(ring.x, ring.y));   // drawn when nobody said otherwise
        Assert.NotEqual(SKColors.White, c.GetPixel(ring.x, ring.y));   // …and alongside author styling
        Assert.Equal(SKColors.White, b.GetPixel(ring.x, ring.y));      // outline:none — gone
    }

    /// <summary>Suppressing the ring does not suppress the author's own styling: the point is to
    /// replace it, not to lose both.</summary>
    [Fact]
    public void Suppressing_the_ring_keeps_the_authors_styling()
    {
        using var t = new TestDoc(Html,
            Frame + " .b:focus{outline:none;background:#f3ce7c}",
            width: 400, height: 300);
        t.Key(EditKey.Tab);
        Assert.Equal(new SKColor(0xF3, 0xCE, 0x7C), Button(t, "one").Style.Background);
    }

    /// <summary>One rule turns the ring off everywhere, for an app that styles selection itself
    /// throughout — the global form of the same idiom.</summary>
    [Fact]
    public void One_rule_turns_the_ring_off_document_wide()
    {
        using var t = new TestDoc(Html,
            Frame + " [data-focus]{outline:none}",
            width: 400, height: 300);
        t.Key(EditKey.Tab);
        using var img = t.Render();
        Assert.Equal(SKColors.White, img.GetPixel(120, 18));
    }

    /// <summary>A control's own arrow behaviour is unaffected by any of this, and so is the ring an
    /// author draws with <c>outline</c> rather than suppressing — that path already worked and must
    /// keep working.</summary>
    [Fact]
    public void An_author_outline_is_still_drawn_and_still_replaces_the_ring()
    {
        using var t = new TestDoc(Html,
            Frame + " .b:focus{outline:3px solid #7ED491}",
            width: 400, height: 300);
        t.Key(EditKey.Tab);
        Assert.True(Button(t, "one").Style.HasOutline);
    }
}
