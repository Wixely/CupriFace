using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

public class TransitionTests
{
    private const string Css = """
        body { background:#ffffff; }
        .box { width:120px; height:60px; background:#000000; opacity:1;
               transition: background 1s linear, opacity 1s linear, transform 1s ease-in; }
        .box:hover { background:#ffffff; opacity:0.4; transform: translateX(100px); }
        .plain { width:80px; height:40px; background:#000000; }
        .plain:hover { background:#ffffff; }
        """;
    private const string Html = "<body><div class='box'>x</div><div class='plain'>y</div></body>";

    [Fact]
    public void Color_opacity_transform_interpolate_over_time()
    {
        using var t = new TestDoc(Html, Css);
        t.HoverClass("box");
        Assert.True(t.Doc.HasActiveTransitions);

        t.Doc.Animate(0.0);
        var s0 = t.FindClass("box").Style;
        Assert.Equal(0, s0.Background.Red);       // t=0 holds the start (black)
        Assert.Equal(1f, s0.Opacity, 0.01);

        t.Doc.Animate(0.5);
        var s = t.FindClass("box").Style;
        Assert.InRange(s.Background.Red, 122, 134); // linear halfway grey
        Assert.Equal(0.7f, s.Opacity, 0.03);        // linear 1 → 0.4
        Assert.InRange(s.TranslateX, 1f, 49f);      // ease-in lags a linear 50px

        t.Doc.Animate(1.0);
        var s1 = t.FindClass("box").Style;
        Assert.Equal(255, s1.Background.Red);
        Assert.Equal(0.4f, s1.Opacity, 0.01);
        Assert.Equal(100f, s1.TranslateX, 0.5);
        Assert.False(t.Doc.HasActiveTransitions);   // settled
    }

    [Fact]
    public void Unhover_reverses_the_transition()
    {
        using var t = new TestDoc(Html, Css);
        t.HoverClass("box");
        t.Doc.Animate(0.0); t.Doc.Animate(1.0);     // settle at the hover target (white)
        Assert.False(t.Doc.HasActiveTransitions);

        t.Move(380, 290);                            // move to an empty corner → unhover
        Assert.True(t.Doc.HasActiveTransitions);
        t.Doc.Animate(2.0); t.Doc.Animate(2.5);      // 0.5 into the reverse
        Assert.InRange(t.FindClass("box").Style.Background.Red, 114, 142); // back ~grey
    }

    [Fact]
    public void Element_without_transition_changes_instantly()
    {
        using var t = new TestDoc(Html, Css);
        t.HoverClass("plain");
        Assert.Equal(255, t.FindClass("plain").Style.Background.Red); // snaps, no animation
    }

    [Fact]
    public void CubicBezier_literal_parses_and_applies_the_custom_curve()
    {
        // The cubic-bezier's inner commas must NOT split the transition list (paren-aware parsing).
        const string css = """
            body { background:#ffffff; }
            .box { width:100px; height:50px; background:#000000; opacity:1;
                   transition: background 1s cubic-bezier(0.1, 0.7, 1.0, 0.1), opacity 1s linear; }
            .box:hover { background:#ffffff; opacity:0; }
            """;
        using var t = new TestDoc("<body><div class='box'>b</div></body>", css);
        t.HoverClass("box");
        t.Doc.Animate(0.0); t.Doc.Animate(0.5);
        var s = t.FindClass("box").Style;

        Assert.Equal(0.5f, s.Opacity, 0.03);            // the second (opacity) transition survived parsing
        Assert.InRange(s.Background.Red, 90, 120);      // custom curve y≈0.42 (not linear 128, not ease ~153)
    }

    [Fact]
    public void Overshoot_cubic_bezier_pushes_past_target_then_settles()
    {
        const string css = """
            .over { width:60px; height:60px; background:#4682B4;
                    transition: transform 1s cubic-bezier(0.68, -0.55, 0.27, 1.55); }
            .over:hover { transform: translateX(100px); }
            """;
        using var t = new TestDoc("<body><div class='over'>o</div></body>", css);
        t.HoverClass("over");
        t.Doc.Animate(1.0);
        var max = 0f;
        for (var u = 1.0f; u <= 2.0f; u += 0.05f) { t.Doc.Animate(u); max = System.MathF.Max(max, t.FindClass("over").Style.TranslateX); }
        t.Doc.Animate(2.0);
        Assert.True(max > 105f, $"overshoot max={max}");
        Assert.Equal(100f, t.FindClass("over").Style.TranslateX, 1.0);
    }

    [Fact]
    public void Filter_transition_interpolates_ops()
    {
        const string css = """
            body { background:#ffffff; }
            .box { width:100px; height:60px; background:#4682B4;
                   filter: blur(0px) grayscale(0); transition: filter 1s linear; }
            .box:hover { filter: blur(6px) grayscale(1); }
            """;
        using var t = new TestDoc("<body><div class='box'>x</div></body>", css);

        float Amount(FilterKind k)
        {
            foreach (var op in t.FindClass("box").Style.Filter ?? []) if (op.Kind == k) return op.A;
            return -1;
        }

        t.HoverClass("box");
        Assert.True(t.Doc.HasActiveTransitions);

        t.Doc.Animate(0.0); t.Doc.Animate(0.5);
        Assert.Equal(3f, Amount(FilterKind.Blur), 0.4);       // halfway to 6px
        Assert.Equal(0.5f, Amount(FilterKind.Grayscale), 0.06);

        t.Doc.Animate(1.0);
        Assert.Equal(6f, Amount(FilterKind.Blur), 0.05);
        Assert.Equal(1f, Amount(FilterKind.Grayscale), 0.06);
    }
}
