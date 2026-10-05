using CupriFace.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The 3D transform family (#269): <c>rotateX</c>/<c>rotateY</c>, <c>translate3d</c>,
/// <c>perspective</c>, <c>backface-visibility</c> and <c>transform-style: preserve-3d</c>.
///
/// <para>None of it did anything: the 3D functions parsed and painted nothing, the three properties
/// were reported unsupported. A flip card drew both faces on top of each other, a cuboid showed one
/// face for ever, a tilt was a flat card. They are a family — <c>perspective</c> without a rotation
/// does nothing, a rotation without <c>backface-visibility</c> shows a mirrored back, and
/// <c>preserve-3d</c> is what carries a parent's rotation to its faces — so they land together.</para>
/// </summary>
public class Transform3DTests(ITestOutputHelper output)
{
    // A 100×60 orange card at (50,20) in a 200×100 stage.
    private const string Base = "body{margin:0;background:#fff} .stage{width:200px;height:100px}"
        + " .p{width:100px;height:60px;margin:20px 50px;background:#d9642a}";

    private static SKBitmap Render(string css, string inner = "<div class='p'></div>")
    {
        using var t = new TestDoc("<body><div class='stage'>" + inner + "</div></body>", Base + " " + css, width: 200, height: 100);
        return t.Render();
    }

    private static int Painted(SKBitmap bmp, Func<SKColor, bool>? match = null)
    {
        match ??= c => c != SKColors.White;
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (match(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static int ColumnInk(SKBitmap bmp, int x)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++) if (bmp.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    private static bool Orange(SKColor c) => c.Red > 180 && c.Green < 140 && c.Blue < 100;
    private static bool Blue(SKColor c) => c.Blue > 180 && c.Red < 100;

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>The first row: a half-turn-ish about y narrows the card to cos 50° of its width.
    /// Without perspective the projection is orthographic, so the edges stay vertical.</summary>
    [Fact]
    public void RotateY_narrows_the_card()
    {
        using var flat = Render("");
        using var turned = Render(".p{transform:rotateY(50deg)}");
        var (a, b) = (Painted(flat), Painted(turned));
        output.WriteLine($"flat {a} px, rotateY(50deg) {b} px");
        Assert.InRange(a, 5900, 6100);
        Assert.InRange(b, 3700, 4000);                        // 100·cos50° = 64px wide, 60 tall
        Assert.True(Orange(turned.GetPixel(100, 50)));
        Assert.Equal(SKColors.White, turned.GetPixel(55, 50));
        Assert.Equal(SKColors.White, turned.GetPixel(145, 50));
    }

    [Fact]
    public void RotateX_flattens_the_card()
    {
        using var tipped = Render(".p{transform:rotateX(60deg)}");
        var n = Painted(tipped);
        output.WriteLine($"rotateX(60deg) {n} px");
        Assert.InRange(n, 2800, 3200);                        // 60·cos60° = 30px tall, 100 wide
        Assert.True(Orange(tipped.GetPixel(100, 50)));
        Assert.Equal(SKColors.White, tipped.GetPixel(100, 25));
    }

    /// <summary>The second row: a translate3d with a zero z is the 2D translate, pixel for pixel.</summary>
    [Fact]
    public void Translate3d_moves_like_translate()
    {
        using var two = Render(".p{transform:translate(30px, 10px)}");
        using var three = Render(".p{transform:translate3d(30px, 10px, 0)}");
        Assert.True(ImageDiff.Compare(two, three).IsIdentical);
        Assert.True(Orange(three.GetPixel(130, 50)));
    }

    /// <summary>The third row: under a parent's <c>perspective</c> the near edge is taller than
    /// the far one. A positive rotateY turns the right edge away, so the left column carries more
    /// ink than the right, where without perspective the two match.</summary>
    [Fact]
    public void Perspective_makes_the_near_edge_taller()
    {
        using var ortho = Render(".p{transform:rotateY(50deg)}");
        using var persp = Render(".stage{perspective:300px} .p{transform:rotateY(50deg)}");
        var (ol, or) = (ColumnInk(ortho, 72), ColumnInk(ortho, 128));
        var (pl, pr) = (ColumnInk(persp, 72), ColumnInk(persp, 128));
        output.WriteLine($"orthographic: left {ol} right {or}; perspective: left {pl} right {pr}");
        Assert.InRange(or, ol - 2, ol + 2);
        Assert.True(pl > pr + 8, "the near edge should be clearly taller than the far one");
        Assert.True(pl > ol, "the near edge comes toward the viewer and grows");
        Assert.False(ImageDiff.Compare(ortho, persp).IsIdentical);
    }

    /// <summary>The <c>perspective()</c> function on the element itself does the same job.</summary>
    [Fact]
    public void The_perspective_function_projects_the_element_itself()
    {
        using var persp = Render(".p{transform:perspective(300px) rotateY(50deg)}");
        Assert.True(ColumnInk(persp, 72) > ColumnInk(persp, 128) + 8);
    }

    /// <summary>The telling row: a face turned half-way round with its back hidden vanishes.</summary>
    [Theory]
    [InlineData("rotateY(180deg)")]
    [InlineData("rotateX(180deg)")]
    [InlineData("rotateY(120deg)")]
    public void A_hidden_back_face_paints_nothing(string turn)
    {
        using var shown = Render(".p{transform:" + turn + ";backface-visibility:visible}");
        using var hidden = Render(".p{transform:" + turn + ";backface-visibility:hidden}");
        output.WriteLine($"{turn}: visible {Painted(shown)} px, hidden {Painted(hidden)} px");
        Assert.True(Painted(shown) > 1000, "the mirrored back should still paint when visible");
        Assert.Equal(0, Painted(hidden));
    }

    /// <summary>…and a face that is merely tilted, mirrored in 2D, or not transformed at all is a
    /// front face: backface-visibility never hides those.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("rotateY(60deg)")]
    [InlineData("scaleX(-1)")]
    [InlineData("rotate(180deg)")]
    public void A_front_face_is_never_hidden(string transform)
    {
        using var bmp = Render(".p{transform:" + transform + ";backface-visibility:hidden}");
        Assert.True(Painted(bmp) > 1000, transform + " should still paint");
    }

    // ---- the flip card --------------------------------------------------------------------------

    private const string Card = "<div class='card'><div class='face front'></div><div class='face back'></div></div>";
    private const string CardCss = ".stage{perspective:600px}"
        + " .card{width:100px;height:60px;margin:20px 50px;position:relative;transform-style:preserve-3d;background:none}"
        + " .face{position:absolute;inset:0;backface-visibility:hidden}"
        + " .front{background:#d9642a} .back{background:#1f6feb;transform:rotateY(180deg)}";

    /// <summary>The idiom: a container with <c>preserve-3d</c> rotates; the back face is pre-turned
    /// 180° so it is front-facing once the container has turned. Face up shows the front only,
    /// face down the back only — never both, which is what every flip card did before.</summary>
    [Theory]
    [InlineData("0deg", true)]
    [InlineData("180deg", false)]
    [InlineData("150deg", false)]
    [InlineData("30deg", true)]
    public void A_preserve_3d_flip_card_shows_one_face(string turn, bool frontUp)
    {
        using var bmp = Render(CardCss + " .card{transform:rotateY(" + turn + ")}", Card);
        var centre = bmp.GetPixel(100, 50);
        output.WriteLine($"rotateY({turn}): centre {centre}, orange {Painted(bmp, Orange)} px, blue {Painted(bmp, Blue)} px");
        Assert.True(frontUp ? Orange(centre) : Blue(centre), $"centre is {centre}");
        Assert.Equal(0, frontUp ? Painted(bmp, Blue) : Painted(bmp, Orange));
    }

    /// <summary>Without <c>preserve-3d</c> the container flattens: its children are part of its
    /// image, so a half-turn shows the FRONT, mirrored — the classic gotcha, and CSS's answer.</summary>
    [Fact]
    public void A_flat_container_mirrors_its_front_instead()
    {
        using var bmp = Render(CardCss + " .card{transform:rotateY(180deg);transform-style:flat}", Card);
        Assert.True(Orange(bmp.GetPixel(100, 50)));
    }

    // ---- animation and transition --------------------------------------------------------------

    [Fact]
    public void A_keyframed_rotateY_turns_the_card_over_time()
    {
        const string css = "@keyframes flip{from{transform:rotateY(0deg)}to{transform:rotateY(180deg)}} .p{animation:flip 1s linear forwards;backface-visibility:hidden}";
        int At(double t)
        {
            using var doc = new TestDoc("<body><div class='stage'><div class='p'></div></div></body>", Base + " " + css, width: 200, height: 100);
            doc.Doc.Animate(t);
            using var bmp = doc.Render();
            return Painted(bmp);
        }
        var (a, b, c, d) = (At(0.0), At(0.25), At(0.5), At(0.75));
        output.WriteLine($"0: {a}, 45°: {b}, 90°: {c}, 135°: {d} px");
        Assert.InRange(a, 5900, 6100);
        Assert.InRange(b, 4100, 4400);                        // cos45° of the width
        Assert.True(c < 300, "edge-on should be a sliver at most");
        Assert.Equal(0, d);                                   // past 90°: the hidden back
    }

    [Fact]
    public void A_transition_on_rotateY_is_interpolated()
    {
        using var t = new TestDoc("<body><div class='stage'><div class='p'></div></div></body>",
            Base + " .p{transition:transform 1s linear} .p:hover{transform:rotateY(90deg)}", width: 200, height: 100);
        t.HoverClass("p");
        t.Doc.Animate(0.0); t.Doc.Animate(0.5);
        using var bmp = t.Render();
        var n = Painted(bmp);
        output.WriteLine($"half-way to edge-on: {n} px");
        Assert.InRange(n, 4100, 4400);
    }

    // ---- hit-testing ---------------------------------------------------------------------------

    /// <summary>The card is clickable where it is drawn: under rotateY(60deg) it is 50px wide
    /// about its centre, so a point that was inside the layout box but is now beside the drawn
    /// card misses it.</summary>
    [Fact]
    public void Hit_testing_follows_the_projection()
    {
        using var t = new TestDoc("<body><div class='stage'><div class='p'></div></div></body>",
            Base + " .p{transform:rotateY(60deg)}", width: 200, height: 100);
        Assert.Equal("p", t.Doc.HitTest(100, 50)?.Element?.ClassName);
        Assert.NotEqual("p", t.Doc.HitTest(60, 50)?.Element?.ClassName);
        Assert.NotEqual("p", t.Doc.HitTest(140, 50)?.Element?.ClassName);
    }

    [Fact]
    public void A_hidden_back_face_is_not_hit()
    {
        using var t = new TestDoc("<body><div class='stage'><div class='p'></div></div></body>",
            Base + " .p{transform:rotateY(180deg);backface-visibility:hidden}", width: 200, height: 100);
        Assert.NotEqual("p", t.Doc.HitTest(100, 50)?.Element?.ClassName);
    }

    // ---- the doctor ----------------------------------------------------------------------------

    [Fact]
    public void The_doctor_accepts_the_family()
    {
        var report = CupriDoctor.Check("<body><div class='stage'><div class='p'></div></div></body>",
            Base + " .stage{perspective:400px;perspective-origin:center top;transform-style:preserve-3d}"
                 + " .p{transform:rotateX(10deg) rotateY(20deg) rotateZ(5deg) translate3d(1px, 2px, 3px) translateZ(4px) scale3d(1, 1, 1) perspective(500px);backface-visibility:hidden}");
        output.WriteLine(report.ToString());
        Assert.True(report.IsClean, report.ToString());
    }
}
