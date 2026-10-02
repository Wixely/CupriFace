using CupriFace.Interaction;
using CupriFace.Paint;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The four ways a CupriFace UI comes out looking wrong for reasons that are the ENGINE's, not the
/// author's. Every one of these was reported from a real integration, and three independent apps
/// (Shade, CursorGoblin, Bantz) had already worked around them by hand — 42 of Bantz's 65 flex rules
/// are <c>align-items:center</c>, which is a default pointing the wrong way rather than 42 choices.
/// </summary>
public class AlignmentDefaultsTests(ITestOutputHelper output)
{
    // ---- an outline is painted, and is not part of the box -------------------------------------

    /// <summary>
    /// AN OUTLINE DOES NOT MOVE ANYTHING. This is the whole reason the property exists, and the
    /// reason a focus ring must be drawn with it rather than with <c>border</c>.
    ///
    /// <para>The reported symptom: a controller moves the selection, the selected item grows by
    /// twice its border, and every sibling after it shifts — so the whole row jitters as the
    /// selection travels. With <c>outline</c> the ring is painted outside the border box and the
    /// layout never hears about it.</para>
    /// </summary>
    [Fact]
    public void An_outline_paints_outside_the_box_and_moves_nothing()
    {
        const string html = "<body><div class='b' id='ring'>focused</div><div class='b'>next</div></body>";
        const string plain = ".b { background:#223; padding:10px; border-radius:8px; }";
        const string ringed = plain + " #ring { outline: 3px solid #8b5cf6; outline-offset: 2px; }";

        static (float RingedY, float PlainY, float Height) SecondRow(string css)
        {
            using var t = new TestDoc(html, css, width: 400, height: 200);
            t.Doc.Refresh();
            using (t.Doc.RenderToImage(400, 200)) { }
            var first = t.FindClass("b");
            var second = t.Find(n => n.Element?.ClassList.Contains("b") == true && n != first)!;
            return (first.Y, second.Y, first.Height);
        }

        var without = SecondRow(plain);
        var with = SecondRow(ringed);

        Assert.Equal(without.Height, with.Height, 0.01);   // the outlined box is the same size
        Assert.Equal(without.PlainY, with.PlainY, 0.01);   // and its sibling has not moved
    }

    /// <summary>The ring is drawn where it was asked for: <c>offset + width</c> outside the border
    /// box on every side. Asserted on the painted command rather than on a screenshot, because the
    /// geometry is the claim.</summary>
    [Fact]
    public void The_outline_sits_offset_and_width_outside_the_border_box()
    {
        using var t = new TestDoc(
            "<body><div class='b'>x</div></body>",
            ".b { background:#223; padding:10px; } .b { outline: 3px solid #8b5cf6; outline-offset: 2px; }",
            width: 400, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(400, 200)) { }

        var box = t.FindClass("b");
        var ring = Assert.Single(t.Doc.BuildDisplayList(400, 200).Commands.OfType<BorderRect>());

        const float spread = 2f + 3f;                       // outline-offset + outline-width
        output.WriteLine($"box {box.Width}x{box.Height}  ring {ring.W}x{ring.H} at {ring.X},{ring.Y}");
        Assert.Equal(-spread, ring.X, 0.01);
        Assert.Equal(box.Width + 2 * spread, ring.W, 0.01);
        Assert.Equal(box.Height + 2 * spread, ring.H, 0.01);
        Assert.Equal(3f, ring.Top, 0.01);
    }

    /// <summary><c>outline</c> is accepted in any order, like <c>border</c> — an author who writes
    /// "solid 2px red" has written the same ring as "2px solid red", and CSS says so.</summary>
    [Theory]
    [InlineData("2px solid #ff0000")]
    [InlineData("solid 2px #ff0000")]
    [InlineData("#ff0000 2px solid")]
    public void The_outline_shorthand_does_not_care_about_order(string value)
    {
        using var t = new TestDoc("<body><div class='b'>x</div></body>",
                                  ".b { padding:4px; outline: " + value + "; }", width: 300, height: 120);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(300, 120)) { }
        var ring = Assert.Single(t.Doc.BuildDisplayList(300, 120).Commands.OfType<BorderRect>());
        Assert.Equal(2f, ring.Top, 0.01);
        Assert.Equal((byte)0xFF, ring.Color.Red);
    }

    /// <summary><c>outline: none</c> paints nothing — the escape hatch for turning a ring off.</summary>
    [Fact]
    public void An_outline_of_none_paints_nothing()
    {
        using var t = new TestDoc("<body><div class='b'>x</div></body>",
                                  ".b { padding:4px; outline: 2px solid red; outline-style: none; }",
                                  width: 300, height: 120);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(300, 120)) { }
        Assert.Empty(t.Doc.BuildDisplayList(300, 120).Commands.OfType<BorderRect>());
    }

    // ---- the button is a row, centred ----------------------------------------------------------

    /// <summary>
    /// A BUTTON SHRINKS TO ITS LABEL, SHARES A ROW, AND CENTRES ITS CONTENTS — all three, which is
    /// the combination that was impossible until <c>inline-flex</c> existed.
    ///
    /// <para>Both failure modes are guarded because each was the cost of fixing the other. As
    /// <c>inline-block</c> a button shrank correctly and aligned its contents on the text baseline,
    /// so an icon beside a label sat off-centre. As block-level <c>display:flex</c> the contents
    /// centred and two buttons became full-width and stacked — the worse bug, and one I shipped
    /// briefly before measuring it.</para>
    /// </summary>
    [Fact]
    public void A_button_shrinks_to_its_label_and_shares_a_row()
    {
        using var t = new TestDoc(
            "<body><cupri-button>Save</cupri-button><cupri-button>Cancel</cupri-button></body>",
            "body { background:#111; }", width: 600, height: 200, components: true);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(600, 200)) { }
        output.WriteLine(t.Doc.DumpTree(maxDepth: 3));

        var first = t.FindClass("cupri-button");
        var second = t.Find(n => n.Element?.ClassList.Contains("cupri-button") == true && n != first)!;

        Assert.True(first.Width < 300, $"a button shrinks to its label, got {first.Width}");
        Assert.True(second.X > first.X, "the second sits after the first, not under it");
    }

    /// <summary>AN ICON AND A LABEL SHARE A CENTRE LINE. The original report: the hamburger in a
    /// "Shortcuts" button sat visibly off the label's centre. Asserted on centres rather than edges,
    /// because the icon is shorter than the line box and equal gaps are the claim.</summary>
    [Fact]
    public void An_icon_and_a_label_in_a_button_are_centred_on_each_other()
    {
        using var t = new TestDoc(
            "<body><cupri-button><cupri-icon name='menu'></cupri-icon> Shortcuts</cupri-button></body>",
            "body { background:#111; }", width: 500, height: 200, components: true);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(500, 200)) { }
        output.WriteLine(t.Doc.DumpTree(maxDepth: 3));

        var button = t.FindClass("cupri-button");
        var icon = t.FindClass("cupri-icon");
        Assert.Equal(button.Y + button.Height / 2f, icon.Y + icon.Height / 2f, 1.0);
    }

    /// <summary>A GHOST BUTTON IS THE SAME SIZE AS A PRIMARY ONE. The border lived only on
    /// <c>.ghost</c>, so switching variant — which an app does to show state — resized the control by
    /// 4px in both axes and nudged everything beside it.</summary>
    [Fact]
    public void Both_button_variants_are_the_same_size()
    {
        static (float W, float H) Measure(string markup)
        {
            using var t = new TestDoc("<body>" + markup + "</body>", "body { background:#111; }",
                                      width: 500, height: 200, components: true);
            t.Doc.Refresh();
            using (t.Doc.RenderToImage(500, 200)) { }
            var b = t.FindClass("cupri-button");
            return (b.Width, b.Height);
        }

        var primary = Measure("<cupri-button>Save</cupri-button>");
        var ghost = Measure("<cupri-button variant='ghost'>Save</cupri-button>");
        output.WriteLine($"primary {primary.W}x{primary.H}   ghost {ghost.W}x{ghost.H}");
        Assert.Equal(primary.W, ghost.W, 0.01);
        Assert.Equal(primary.H, ghost.H, 0.01);
    }

    // ---- the built-in focus ring -----------------------------------------------------------------

    /// <summary>The engine's own focus ring takes <c>--cupri-focus</c>. It was a hard-coded blue,
    /// which is wrong in most palettes — and an author who could not recolour it drew their own with
    /// <c>border</c> instead, which is the layout-shifting bug at the top of this file. Recolouring
    /// it is now the easy path.</summary>
    [Fact]
    public void The_built_in_focus_ring_can_be_recoloured()
    {
        using var t = new TestDoc(
            "<body><cupri-button>Save</cupri-button></body>",
            ":root { --cupri-focus: #8b5cf6; } body { background:#111; }",
            width: 400, height: 200, components: true);
        t.Doc.Refresh();
        t.Doc.DispatchKey("", EditKey.Tab, KeyMods.None);     // focus-visible: the ring only shows after Tab
        using (t.Doc.RenderToImage(400, 200)) { }

        // BuildFrame, not BuildDisplayList: the ring is appended by the FRAME path alongside the
        // caret and selection, because it is chrome the engine draws over the document rather than
        // part of it. Asserting on the snapshot would have asserted on a list that never has one.
        var fills = t.Doc.BuildFrame(400, 200).Commands.OfType<FillRect>()
                     .Where(f => f.Color.Red == 0x8B && f.Color.Green == 0x5C && f.Color.Blue == 0xF6).ToList();
        Assert.True(fills.Count >= 4, $"the ring is four edges in the themed colour, saw {fills.Count}");
    }
}
