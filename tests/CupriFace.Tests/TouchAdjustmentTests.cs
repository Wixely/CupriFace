using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Touch adjustment (<c>CupriDocument.Touch.cs</c>): a finger that lands near, but not on, something
/// interactive is moved onto it. Reported from a Steam Deck as touch being "a bit too accurate".
///
/// <para>Every case here is a rule the snap must obey or it becomes a bug of its own: only fingers
/// snap, only within reach, only to the nearest, never away from something already under the
/// finger, never to a disabled control, and never through whatever is covering the target.</para>
/// </summary>
public class TouchAdjustmentTests(ITestOutputHelper output)
{
    // Two 40x40 buttons with a 40px gutter between them, in a 300x100 viewport:
    //   A occupies x 20..60,  B occupies x 100..140, both y 30..70.
    private const string Html = """
        <body>
          <button class="a">A</button>
          <button class="b">B</button>
        </body>
        """;

    private const string Css = """
        body { margin:0; font-family:sans-serif; position:relative; }
        button { position:absolute; top:30px; width:40px; height:40px; padding:0; border:0; }
        .a { left:20px; }
        .b { left:100px; }
        """;

    private static (CupriDocument Doc, List<string> Clicks) Setup(string html = Html, string css = Css)
    {
        var clicks = new List<string>();
        var doc = CupriDocument.Load(html, css);
        doc.OnClick("button", e => clicks.Add(e.Element?.GetAttribute("class") ?? "?"));
        doc.Refresh();
        using (doc.RenderToImage(300, 100)) { }
        return (doc, clicks);
    }

    /// <summary>The report itself: 8px to the right of A, in the gutter. A mouse there hits
    /// nothing, which is correct for a mouse. A finger there meant A.</summary>
    [Fact]
    public void A_finger_that_lands_just_beside_a_button_presses_it()
    {
        var (doc, clicks) = Setup();
        using var _ = doc;

        doc.DispatchClick(68, 50);            // mouse: exact, misses
        Assert.Empty(clicks);

        doc.DispatchTap(68, 50);              // finger: adjusted, lands on A
        output.WriteLine(string.Join(",", clicks));
        Assert.Equal(["a"], clicks);
    }

    /// <summary>An inline link has no box of its own — it lives in text fragments — and it is about
    /// the most common thing a finger reaches for. A tap landing just under the link's text must
    /// find it; filtering candidates on their (zero) layout box would have excluded every link in
    /// every paragraph.</summary>
    [Fact]
    public void A_tap_just_below_an_inline_link_presses_it()
    {
        // A link press is a NAVIGATION, consumed before any user OnClick handler runs — a mouse
        // click squarely on the link records nothing through OnClick("a"). Navigated is the channel
        // a pressed link reports on, and only for a ROUTE href: a #anchor is an in-page scroll and
        // reports on nothing at all (measured, all three kinds, direct click and adjusted tap).
        var clicks = new List<string>();
        using var doc = CupriDocument.Load(
            "<body><p>Read the <a href=\"/docs\" class=\"docs\">documentation</a> first.</p></body>",
            "body { margin:0; font-family:sans-serif; font-size:16px; } p { margin:0; padding:8px; }");
        doc.Navigated += e => clicks.Add(e.Href);
        doc.Refresh();
        using (doc.RenderToImage(400, 100)) { }

        // Find the link's real on-screen box, then tap 6px below its bottom edge.
        var (x, y, w, h) = Interaction.HitTesting.ScreenBox(Find(doc.Root, "a")!);
        output.WriteLine($"link box {x:0},{y:0} {w:0}x{h:0}");
        Assert.True(w > 1 && h > 1, "the link should have a fragment-derived box");

        doc.DispatchClick(x + w / 2, y + h + 6);       // mouse: on the paragraph, misses
        Assert.Empty(clicks);
        doc.DispatchTap(x + w / 2, y + h + 6);         // finger: adjusted onto the link
        Assert.Equal(["/docs"], clicks);

        static Dom.RenderNode? Find(Dom.RenderNode n, string tag)
        {
            if (string.Equals(n.Element?.LocalName, tag, StringComparison.OrdinalIgnoreCase)) return n;
            foreach (var c in n.Children) if (Find(c, tag) is { } hit) return hit;
            return null;
        }
    }

    /// <summary>Beyond the radius is a miss, for a finger too — the gutter's midpoint is 20px from
    /// either button, and a tap there did not mean either of them.</summary>
    [Fact]
    public void Beyond_the_radius_a_finger_misses_like_a_mouse()
    {
        var (doc, clicks) = Setup();
        using var _ = doc;

        doc.DispatchTap(80, 50);
        Assert.Empty(clicks);
    }

    /// <summary>Two candidates in reach: the nearer one wins. 12px right of A's edge is also 28px
    /// left of B's — both inside a generous radius, and A is the answer.</summary>
    [Fact]
    public void The_nearest_candidate_wins()
    {
        var (doc, clicks) = Setup();
        using var _ = doc;
        doc.TouchAdjustRadius = 30f;

        doc.DispatchTap(72, 50);
        Assert.Equal(["a"], clicks);

        clicks.Clear();
        doc.DispatchTap(94, 50);              // 6px from B, 34px from A
        Assert.Equal(["b"], clicks);
    }

    /// <summary>A tap already ON a control means that control. Snapping must never steal it for a
    /// neighbour whose centre happens to be closer — a press on A's right edge is a press on A.</summary>
    [Fact]
    public void A_tap_already_on_a_control_is_never_moved()
    {
        var (doc, clicks) = Setup();
        using var _ = doc;
        doc.TouchAdjustRadius = 60f;          // B is well within reach; it must not matter

        doc.DispatchTap(59, 50);              // 1px inside A's right edge
        Assert.Equal(["a"], clicks);
    }

    /// <summary>Zero disables it: an app drawing its own precise targets gets taps exactly where
    /// they fall, and a finger behaves like a mouse.</summary>
    [Fact]
    public void A_zero_radius_disables_adjustment()
    {
        var (doc, clicks) = Setup();
        using var _ = doc;
        doc.TouchAdjustRadius = 0f;

        doc.DispatchTap(68, 50);
        Assert.Empty(clicks);
    }

    /// <summary>A disabled control must not pull a tap away from a live one. Here A is disabled and
    /// the finger lands between them, nearer to A: B is the only thing that can act, so B it is —
    /// or nothing, but never the dead button.</summary>
    [Fact]
    public void A_disabled_control_does_not_attract_a_tap()
    {
        var (doc, clicks) = Setup(Html.Replace("class=\"a\"", "class=\"a\" aria-disabled=\"true\""), Css);
        using var _ = doc;
        doc.TouchAdjustRadius = 30f;

        doc.DispatchTap(72, 50);              // 12px from A, 28px from B
        output.WriteLine(string.Join(",", clicks));
        Assert.DoesNotContain("a", clicks);
    }

    /// <summary>A covered control cannot be reached through what covers it. An overlay painted over
    /// A means a finger beside A meets the overlay, exactly as it would have had it landed on A —
    /// the snap is verified by a real hit test at the adjusted point, and the overlay wins it.</summary>
    [Fact]
    public void A_covered_control_does_not_attract_a_tap()
    {
        var (doc, clicks) = Setup(
            Html.Replace("</body>", "<div class=\"veil\"></div></body>"),
            Css + "\n.veil { position:fixed; left:0; top:0; width:80px; height:100px; background:#0008; }");
        using var _ = doc;

        doc.DispatchTap(68, 50);              // beside A, under the veil
        Assert.Empty(clicks);

        doc.DispatchTap(96, 50);              // beside B, not covered: still adjusts
        Assert.Equal(["b"], clicks);
    }
}
