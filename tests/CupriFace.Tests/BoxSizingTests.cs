using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>box-sizing: border-box</c>, and <c>margin:auto</c> as a centring mechanism.
///
/// Reported together (#76) because a centred card needs both: a full-bleed container with padding
/// overflows its parent by twice the padding under content-box, which silently shifts anything
/// centred inside it, and the global <c>* { box-sizing: border-box }</c> everyone writes could not
/// rescue it because the property was not read at all.
/// </summary>
public class BoxSizingTests
{
    private const int W = 400, H = 300;
    private const string Reset = "body{margin:0;padding:0}";

    private static RenderNodeBox Box(string html, string css, string id, int w = W, int h = H)
    {
        using var t = new TestDoc(html, Reset + css, width: w, height: h);
        var n = TestDoc.Find(t.Root, x => x.Element?.GetAttribute("id") == id)!;
        return new RenderNodeBox(n.X, n.Y, n.Width, n.Height);
    }

    private readonly record struct RenderNodeBox(float X, float Y, float W, float H)
    {
        public float CentreX => X + W / 2f;
    }

    // ---- box-sizing ---------------------------------------------------------------------------

    [Fact]
    public void Border_box_makes_width_include_padding()
    {
        var b = Box("<body><div id=\"x\">c</div></body>",
                    "#x{box-sizing:border-box;width:200px;padding:20px}", "x");
        Assert.Equal(200f, b.W, 3);   // content-box would be 200 + 40
    }

    [Fact]
    public void Border_box_makes_width_include_borders_too()
    {
        var b = Box("<body><div id=\"x\">c</div></body>",
                    "#x{box-sizing:border-box;width:200px;padding:20px;border:5px solid #000}", "x");
        Assert.Equal(200f, b.W, 3);   // content-box would be 200 + 40 + 10
    }

    [Fact]
    public void Content_box_is_still_the_default()
    {
        var b = Box("<body><div id=\"x\">c</div></body>", "#x{width:200px;padding:20px}", "x");
        Assert.Equal(240f, b.W, 3);
    }

    [Fact]
    public void Border_box_applies_to_height_as_well()
    {
        var b = Box("<body><div id=\"x\">c</div></body>",
                    "#x{box-sizing:border-box;height:100px;padding:15px}", "x");
        Assert.Equal(100f, b.H, 3);
    }

    /// <summary>The reported shape: a full-bleed padded container no longer overflows its parent,
    /// so what it centres actually lands in the middle.</summary>
    [Fact]
    public void A_full_bleed_padded_container_does_not_overflow_its_parent()
    {
        const string html = "<body><div id=\"outer\"><div id=\"card\">card</div></div></body>";
        const string css = """
            #outer{box-sizing:border-box;width:100%;padding:60px;display:flex;justify-content:center}
            #card{width:120px}
            """;
        var outer = Box(html, css, "outer");
        var card = Box(html, css, "card");
        Assert.Equal(W, outer.W, 3);                 // fills, does not exceed
        Assert.Equal(W / 2f, card.CentreX, 3);       // and its content is genuinely centred
    }

    [Fact]
    public void Border_box_never_shrinks_a_box_below_its_padding_and_borders()
    {
        // A width smaller than the frame cannot make the content box negative; CSS floors it at 0,
        // so the border box is exactly the frame.
        var b = Box("<body><div id=\"x\"></div></body>",
                    "#x{box-sizing:border-box;width:10px;padding:20px}", "x");
        Assert.Equal(40f, b.W, 3);
    }

    // ---- auto margins -------------------------------------------------------------------------

    [Fact]
    public void Auto_side_margins_centre_a_flex_item_on_the_main_axis()
    {
        const string html = "<body><div id=\"row\"><div id=\"card\">c</div></div></body>";
        const string css = "#row{display:flex;width:400px}#card{width:100px;margin-left:auto;margin-right:auto}";
        Assert.Equal(200f, Box(html, css, "card").CentreX, 3);
    }

    [Fact]
    public void A_single_auto_margin_pushes_a_flex_item_to_the_far_side()
    {
        const string html = "<body><div id=\"row\"><div id=\"card\">c</div></div></body>";
        const string css = "#row{display:flex;width:400px}#card{width:100px;margin-left:auto}";
        Assert.Equal(300f, Box(html, css, "card").X, 3);   // flush right
    }

    /// <summary>Auto margins take the free space BEFORE justify-content gets a say — that is what
    /// makes them the way to centre one item while the rest stay put.</summary>
    [Fact]
    public void Auto_margin_on_one_item_wins_over_justify_content()
    {
        const string html = """
            <body><div id="row"><div id="a">a</div><div id="b">b</div></div></body>
            """;
        const string css = """
            #row{display:flex;width:400px;justify-content:flex-start}
            #a{width:50px}
            #b{width:50px;margin-left:auto}
            """;
        Assert.Equal(0f, Box(html, css, "a").X, 3);
        Assert.Equal(350f, Box(html, css, "b").X, 3);   // pushed to the end by its own auto margin
    }

    [Fact]
    public void Auto_margins_centre_a_block_element_horizontally()
    {
        var b = Box("<body><div id=\"x\">c</div></body>",
                    "#x{width:100px;margin-left:auto;margin-right:auto}", "x");
        Assert.Equal(W / 2f, b.CentreX, 3);
    }

    [Fact]
    public void Fixed_margins_are_unaffected()
    {
        var b = Box("<body><div id=\"x\">c</div></body>", "#x{width:100px;margin-left:40px}", "x");
        Assert.Equal(40f, b.X, 3);
    }
}
