using CupriFace.Diagnostics;
using CupriFace.Style;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>rgb()</c> inside a <c>border</c> shorthand (#196), and the colour parser's promise not to throw.
///
/// <para><c>border: 2px solid rgb(1, 2, 3)</c> — what every CSS formatter emits — meant the whole
/// DOCUMENT failed to build. Two separate defects stacked: the shorthand split on spaces, tearing
/// <c>rgb(1, 2, 3)</c> into <c>rgb(1,</c> + <c>2,</c> + <c>3)</c>; and the colour parser met that
/// unterminated paren, computed <c>text[4..-1]</c>, and threw
/// <c>ArgumentOutOfRangeException</c>.</para>
///
/// <para>Both are fixed, and both matter on their own. The split alone would leave a caller that
/// mis-splits able to crash the engine; the parser alone would downgrade the crash to a border
/// silently drawn with no colour, which is this engine's worst failure mode rather than its
/// best.</para>
/// </summary>
public class BorderColorFunctionTests(ITestOutputHelper output)
{
    private static readonly SKColor Expected = new(1, 2, 3);

    private static RenderNodeStyle Border(string declaration)
    {
        using var t = new TestDoc(
            "<body><div class='p'>x</div></body>",
            "body{margin:0;font-family:sans-serif}"
            + $".p{{width:80px;height:40px;{declaration};}}",
            width: 200, height: 120);
        var n = t.FindClass("p");
        return new RenderNodeStyle(n.Style.BorderTopColor, n.Style.BorderLeftColor,
                                   n.Style.BorderTop, n.Style.BorderLeft);
    }

    private readonly record struct RenderNodeStyle(SKColor Top, SKColor Left, float TopW, float LeftW);

    // ---- the reported table --------------------------------------------------------------------

    /// <summary>Every row the issue tabulated as throwing. None of them may throw, and each must
    /// produce the colour that was asked for — "does not crash" is not the bar.</summary>
    [Theory]
    [InlineData("border: 2px solid rgb(1, 2, 3)")]
    [InlineData("border: 2px solid rgb(1 2 3)")]
    [InlineData("border: solid rgb(1, 2, 3) 2px")]
    [InlineData("border-top: 2px solid rgb(1, 2, 3)")]
    [InlineData("border-left: 2px solid rgb(1, 2, 3)")]
    public void The_reported_declarations_build_and_carry_their_colour(string declaration)
    {
        var s = Border(declaration);
        var got = declaration.Contains("border-left") ? s.Left : s.Top;
        output.WriteLine($"{declaration,-38} -> #{got.Red:X2}{got.Green:X2}{got.Blue:X2} a={got.Alpha}");
        Assert.Equal(Expected, got);
    }

    /// <summary>The alpha form too, which the issue listed separately.</summary>
    [Fact]
    public void Rgba_in_a_border_shorthand_keeps_its_alpha()
    {
        var s = Border("border: 2px solid rgba(1, 2, 3, 0.8)");
        Assert.Equal(new SKColor(1, 2, 3, 204), s.Top);
    }

    /// <summary>The rows the issue listed as already working must still work — the fix changed how
    /// every border value is split, so this is the half that says nothing else moved.</summary>
    [Theory]
    [InlineData("border: 2px solid red", 255, 0, 0)]
    [InlineData("border: 2px solid #abc", 0xAA, 0xBB, 0xCC)]
    [InlineData("border: 2px solid var(--c, #abc)", 0xAA, 0xBB, 0xCC)]
    public void The_forms_that_already_worked_still_work(string declaration, int r, int g, int b)
        => Assert.Equal(new SKColor((byte)r, (byte)g, (byte)b), Border(declaration).Top);

    /// <summary>The width still parses when a colour function sits beside it — the tokens have to
    /// stay in their lanes now that they are split differently.</summary>
    [Fact]
    public void The_width_survives_beside_a_colour_function()
    {
        var s = Border("border: 3px solid rgb(1, 2, 3)");
        Assert.Equal(3f, s.TopW, 0.01);
        Assert.Equal(Expected, s.Top);
    }

    /// <summary>A per-side shorthand sets only its own side, which is the thing #170 added and the
    /// split rewrite could plausibly have broken.</summary>
    [Fact]
    public void A_per_side_shorthand_still_touches_only_that_side()
    {
        using var t = new TestDoc(
            "<body><div class='p'>x</div></body>",
            "body{margin:0}.p{width:80px;height:40px;border-left:4px solid rgb(1, 2, 3)}",
            width: 200, height: 120);
        var n = t.FindClass("p");
        Assert.Equal(4f, n.Style.BorderLeft, 0.01);
        Assert.Equal(0f, n.Style.BorderTop, 0.01);
        Assert.Equal(Expected, n.Style.BorderLeftColor);
    }

    /// <summary>The whole document builds and paints. The issue's repro is a render, not a parse,
    /// because the failure took the document with it.</summary>
    [Fact]
    public void The_reported_document_renders()
    {
        var html = """
            <div class="p">x</div>
            <style>body,html{font-family:"Noto Sans";} .p{width:80px;height:40px;border:2px solid rgb(1, 2, 3);}</style>
            """;
        using var doc = CupriDocument.Load(html, null);
        doc.Refresh();
        using var img = doc.RenderToImage(200, 120);     // threw before
        Assert.NotNull(img);
    }

    // ---- the parser's contract -----------------------------------------------------------------

    /// <summary>
    /// A <c>TryParse</c> returns false. It does not throw, for any input at all — that is the whole
    /// point of the name, and two inputs broke it: an unterminated <c>rgb(</c> and a <c>#</c> whose
    /// digits are not hex. Neither is reachable from well-formed CSS; both were reachable from a
    /// caller that split a value badly, and one such caller shipped.
    /// </summary>
    [Theory]
    [InlineData("rgb(1,")]
    [InlineData("rgba(1,")]
    [InlineData("rgb(")]
    [InlineData("rgb")]
    [InlineData("3)")]
    [InlineData("#gggggg")]
    [InlineData("#zz")]
    [InlineData("#")]
    [InlineData("hsl(20,")]
    [InlineData("(((")]
    [InlineData(")")]
    [InlineData("rgb(1, 2, 3")]
    public void A_malformed_colour_is_false_never_an_exception(string text)
    {
        var ex = Record.Exception(() => Colors.TryParse(text, out _));
        Assert.Null(ex);
        Assert.False(Colors.TryParse(text, out _));
    }

    /// <summary>Every spelling of rgb() CSS allows, including the space-separated form the issue
    /// listed and the slash-alpha one beside it. Only the comma form used to parse.</summary>
    [Theory]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3, 255)]
    [InlineData("rgb(1 2 3)", 1, 2, 3, 255)]
    [InlineData("rgba(1, 2, 3, 0.8)", 1, 2, 3, 204)]
    [InlineData("rgb(1 2 3 / 0.5)", 1, 2, 3, 127)]
    [InlineData("RGB(1,2,3)", 1, 2, 3, 255)]
    public void Every_rgb_spelling_parses(string text, int r, int g, int b, int a)
    {
        Assert.True(Colors.TryParse(text, out var c), text);
        output.WriteLine($"{text,-22} -> {c.Red},{c.Green},{c.Blue} a={c.Alpha}");
        Assert.Equal(new SKColor((byte)r, (byte)g, (byte)b, (byte)a), c);
    }

    /// <summary>
    /// <c>hsl()</c>, which the issue offered as the WORKAROUND — and which returned false in every
    /// form, so an author who took that advice got a border with no colour instead of a crash. The
    /// quieter failure, and the harder one to find.
    /// </summary>
    [Theory]
    [InlineData("hsl(0, 100%, 50%)", 255, 0, 0)]
    [InlineData("hsl(120, 100%, 50%)", 0, 255, 0)]
    [InlineData("hsl(240, 100%, 50%)", 0, 0, 255)]
    [InlineData("hsl(0, 0%, 100%)", 255, 255, 255)]
    [InlineData("hsl(0, 0%, 0%)", 0, 0, 0)]
    [InlineData("hsl(20 50% 40%)", 153, 85, 51)]
    public void Hsl_parses_to_the_colour_it_names(string text, int r, int g, int b)
    {
        Assert.True(Colors.TryParse(text, out var c), text);
        output.WriteLine($"{text,-22} -> {c.Red},{c.Green},{c.Blue}");
        // Within one channel step: the conversion rounds, and a reference value written by hand is
        // allowed to disagree in the last bit rather than the test being wrong about the colour.
        Assert.InRange(Math.Abs(c.Red - r), 0, 1);
        Assert.InRange(Math.Abs(c.Green - g), 0, 1);
        Assert.InRange(Math.Abs(c.Blue - b), 0, 1);
    }

    /// <summary>…and in a border, which is where it was recommended.</summary>
    [Fact]
    public void Hsl_works_in_a_border_shorthand()
    {
        var c = Border("border: 2px solid hsl(0, 100%, 50%)").Top;
        Assert.Equal(new SKColor(255, 0, 0), c);
    }

    /// <summary>Alpha on the hsla form.</summary>
    [Fact]
    public void Hsla_carries_its_alpha()
    {
        Assert.True(Colors.TryParse("hsla(0, 100%, 50%, 0.5)", out var c));
        Assert.InRange(c.Alpha, 126, 128);
    }

    /// <summary>Hue wraps rather than failing, the way CSS defines it.</summary>
    [Fact]
    public void A_hue_past_360_wraps()
    {
        Assert.True(Colors.TryParse("hsl(480, 100%, 50%)", out var wrapped));
        Assert.True(Colors.TryParse("hsl(120, 100%, 50%)", out var plain));
        Assert.Equal(plain, wrapped);
    }

    /// <summary>hsl() needs its units: a bare number where a percentage belongs is a refusal, not a
    /// guess. Guessing is how a wrong colour ships looking deliberate.</summary>
    [Fact]
    public void Hsl_without_percentages_is_refused()
        => Assert.False(Colors.TryParse("hsl(20, 50, 40)", out _));

    // ---- what the doctor says when a build does fail --------------------------------------------

    /// <summary>
    /// <b>A missing bracket never takes the document down.</b> The reported colour case turned out
    /// to be one of three places computing <c>v[(IndexOf('(') + 1)..LastIndexOf(')')]</c>, which on
    /// an unclosed call is a negative length. The other two — <c>calc()</c> and <c>minmax()</c> —
    /// are reachable by simply forgetting a bracket in hand-written CSS, which is a good deal more
    /// likely than the shape that was reported.
    ///
    /// <para>One malformed declaration should cost that declaration, never the whole composition:
    /// when the document does not build, nothing renders and nothing else about it can be
    /// inspected.</para>
    /// </summary>
    [Theory]
    [InlineData(".p{width:10px;height:10px;border:2px solid rgb(1, 2, 3}")]   // the reported family
    [InlineData(".p{width:10px;height:10px;border:2px solid rgb(}")]
    [InlineData(".p{height:10px;width:calc(100% - 40px}")]                     // unclosed calc
    [InlineData(".p{height:10px;width:calc(}")]
    [InlineData(".p{display:grid;grid-template-columns:minmax(10px, 1fr}")]    // unclosed minmax
    [InlineData(".p{display:grid;grid-template-columns:minmax(}")]
    public void An_unclosed_function_costs_the_declaration_not_the_document(string css)
    {
        using var doc = CupriDocument.Load("<div class='p'>x</div>", css);
        doc.Refresh();
        using var img = doc.RenderToImage(200, 120);
        Assert.NotNull(img);
    }

    /// <summary>A well-formed calc() still resolves — the guard must not have changed what a
    /// correct value means.</summary>
    [Fact]
    public void A_well_formed_calc_still_resolves()
    {
        using var t = new TestDoc(
            "<body><div class='p'>x</div></body>",
            "body{margin:0}.p{height:10px;width:calc(100% - 40px)}",
            width: 200, height: 120);
        output.WriteLine($"width {t.FindClass("p").Width:0.0} of a 200px viewport");
        Assert.Equal(160f, t.FindClass("p").Width, 0.5);
    }

    /// <summary>The reported document is clean now — no CF0001, and nothing else raised in its
    /// place.</summary>
    [Fact]
    public void The_reported_document_reports_no_build_failure()
    {
        var html = """
            <div class="p">x</div>
            <style>body,html{font-family:"Noto Sans";} .p{width:80px;height:40px;border:2px solid rgb(1, 2, 3);}</style>
            """;
        var report = CupriDoctor.Check(html, "", width: 200, height: 120);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0001");
    }
}
