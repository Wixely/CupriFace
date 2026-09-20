using CupriFace.Diagnostics;
using CupriFace.Dom;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>letter-spacing</c> — tracking (#202).
///
/// <para>Reported and ignored until now, and the single most-used property in one surveyed corpus
/// that the engine did not implement: <b>150 of 187 blocks</b>, ahead of inline SVG and 3D
/// transforms. Unlike most gaps it could not be worked around from outside — <c>word-spacing</c> is
/// the wrong quantity, and splitting text into per-character spans needs measurement the caller does
/// not have and would break shaping.</para>
///
/// <para>Applied per grapheme CLUSTER, not per char and not per glyph: a ligature is spaced once and
/// a combining mark is not pushed off the letter it belongs to. The run is shaped first and then
/// re-positioned, so HarfBuzz's kerning survives.</para>
/// </summary>
public class LetterSpacingTests(ITestOutputHelper output)
{
    private const string Word = "Hamburgefonstiv";     // 15 clusters
    private const int Clusters = 15;

    /// <summary>The advance of the first line — the text's own width, not its block's.</summary>
    private float LineWidth(string declarations, string text = Word)
    {
        using var t = new TestDoc($"<body><div class='t'>{text}</div></body>",
            "body{margin:0;font-family:sans-serif}" + $".t{{font-size:20px;{declarations}}}",
            width: 900, height: 80);
        var n = t.Find(x => x.IsText)!;
        var w = n.Lines is { Count: > 0 } ? n.Lines[0].Width : -1f;
        output.WriteLine($"{declarations,-28} line width {w:0.0}");
        return w;
    }

    // ---- measurement ----------------------------------------------------------------------------

    /// <summary>Tracking adds after every cluster, the last one included — which is what browsers do
    /// and what makes the trailing space part of the box.</summary>
    [Theory]
    [InlineData(6f)]
    [InlineData(1f)]
    [InlineData(12f)]
    [InlineData(-2f)]
    public void The_width_changes_by_the_spacing_times_the_cluster_count(float px)
    {
        var plain = LineWidth("");
        var tracked = LineWidth($"letter-spacing:{px.ToString(System.Globalization.CultureInfo.InvariantCulture)}px");
        Assert.Equal(plain + px * Clusters, tracked, 0.5);
    }

    /// <summary>`normal` is zero, and is the same as not saying anything.</summary>
    [Fact]
    public void Normal_is_the_same_as_unset()
        => Assert.Equal(LineWidth(""), LineWidth("letter-spacing:normal"), 0.01);

    /// <summary>Negative tracking closes letters up, which is what a large display heading wants.</summary>
    [Fact]
    public void Negative_spacing_narrows_the_line()
        => Assert.True(LineWidth("letter-spacing:-2px") < LineWidth(""));

    /// <summary>It inherits, like the other text properties — set on a container, felt by the text
    /// inside it. Nearly every real use sets it on a heading or a label, not on a text node.</summary>
    [Fact]
    public void It_inherits()
    {
        using var t = new TestDoc(
            "<body><div class='outer'><span class='inner'>" + Word + "</span></div></body>",
            "body{margin:0;font-family:sans-serif}.outer{font-size:20px;letter-spacing:6px}",
            width: 900, height: 80);
        var n = t.Find(x => x.IsText)!;
        Assert.Equal(6f, n.Style.LetterSpacing, 0.01);
        Assert.Equal(LineWidth("letter-spacing:6px"), n.Lines![0].Width, 0.5);
    }

    /// <summary>
    /// Per CLUSTER, not per UTF-16 unit. A surrogate pair is one letter, so a string of emoji gains
    /// one gap each — counting chars would double it.
    /// </summary>
    [Fact]
    public void Spacing_counts_clusters_not_chars()
    {
        const string emoji = "😀😀😀";              // 3 clusters, 6 chars
        var plain = LineWidth("", emoji);
        var tracked = LineWidth("letter-spacing:10px", emoji);
        output.WriteLine($"3 emoji: {plain:0.0} -> {tracked:0.0} (3 x 10 = 30, 6 x 10 = 60)");
        Assert.Equal(plain + 30f, tracked, 1.5);
    }

    /// <summary>A combining mark belongs to the letter before it and must not be given a gap of its
    /// own, or the accent drifts off the vowel.</summary>
    [Fact]
    public void A_combining_mark_does_not_get_its_own_gap()
    {
        var precomposed = LineWidth("letter-spacing:8px", "é");   // e + combining acute
        var single = LineWidth("letter-spacing:8px", "é");          // é
        output.WriteLine($"e+U+0301 {precomposed:0.0}   é {single:0.0}");
        Assert.Equal(single, precomposed, 1.5);
    }

    // ---- paint -----------------------------------------------------------------------------------

    private static SKBitmap Render(string declarations)
    {
        using var doc = CupriDocument.Load($"<body><div class='t'>{Word}</div></body>",
            "body{margin:0;font-family:sans-serif;background:#fff}" + $".t{{font-size:22px;{declarations}}}");
        doc.Refresh();
        using (doc.RenderToImage(460, 46)) { }
        using var img = doc.RenderToImage(460, 46, SKColors.White);
        return SKBitmap.FromImage(img);
    }

    /// <summary>It reaches the frame. Measuring differently while painting the same would be the
    /// worst of both — the box would be right and the text would not fill it.</summary>
    [Fact]
    public void Tracking_changes_the_painted_pixels()
    {
        using var plain = Render("");
        using var tracked = Render("letter-spacing:6px");
        var d = ImageDiff.Compare(plain, tracked);
        output.WriteLine(d.ToString());
        Assert.False(d.IsIdentical);
    }

    /// <summary>
    /// <b>The regression guard.</b> Untracked text must paint exactly as it did before — the tracked
    /// path positions glyphs by hand, and it would be easy to route everything through it and shift
    /// every word in every document by a subpixel. Zero and unset take the same path and must agree
    /// exactly, not approximately.
    /// </summary>
    [Fact]
    public void Zero_spacing_is_pixel_identical_to_unset()
    {
        using var unset = Render("");
        using var zero = Render("letter-spacing:0");
        using var normal = Render("letter-spacing:normal");
        Assert.True(ImageDiff.Compare(unset, zero, tolerance: 0).IsIdentical);
        Assert.True(ImageDiff.Compare(unset, normal, tolerance: 0).IsIdentical);
    }

    /// <summary>Kerning survives: the run is shaped once and then re-positioned, rather than drawn a
    /// letter at a time. Drawn per letter, "AV" would lose its kern and come out wider than the
    /// shaped pair plus one gap.</summary>
    [Fact]
    public void Shaping_survives_the_repositioning()
    {
        var kerned = LineWidth("", "AVAVAV");
        var tracked = LineWidth("letter-spacing:4px", "AVAVAV");
        output.WriteLine($"AVAVAV {kerned:0.0} -> {tracked:0.0}");
        Assert.Equal(kerned + 4f * 6, tracked, 0.5);
    }

    // ---- diagnostics ------------------------------------------------------------------------------

    /// <summary>It is implemented, so the doctor stops calling it unsupported.</summary>
    [Fact]
    public void It_is_no_longer_reported_as_unsupported()
    {
        var r = CupriDoctor.Check("<div class='t'>x</div>", ".t{letter-spacing:6px}");
        output.WriteLine(r.ToString());
        Assert.DoesNotContain(r.Findings, f => f.Code == "CF0050" && f.Message.Contains("letter-spacing"));
    }

    /// <summary>A unit the parser cannot read still falls back to no tracking rather than throwing —
    /// the engine is forgiving, and a colour parser that threw took a whole document down once.</summary>
    [Theory]
    [InlineData("letter-spacing:2rem")]
    [InlineData("letter-spacing:wide")]
    [InlineData("letter-spacing:")]
    public void An_unreadable_value_does_not_throw(string decl)
    {
        var ex = Record.Exception(() => LineWidth(decl));
        Assert.Null(ex);
    }

    /// <summary>Tracking widens the box the text sits in, so a tracked label reserves the room it
    /// needs instead of overflowing — which is the layout half of the feature.</summary>
    [Fact]
    public void A_tracked_label_reserves_its_own_width()
    {
        using var t = new TestDoc(
            "<body><div class='row'><span class='label'>LABEL</span></div></body>",
            "body{margin:0;font-family:sans-serif}.row{display:flex}"
            + ".label{font-size:14px;letter-spacing:4px}",
            width: 400, height: 60);
        var label = t.FindClass("label");
        var text = t.Find(x => x.IsText)!;
        output.WriteLine($"label box {label.Width:0.0}, text {text.Lines![0].Width:0.0}");
        Assert.True(label.Width >= text.Lines[0].Width - 0.5f,
            $"the box ({label.Width:0.0}) is narrower than the tracked text ({text.Lines[0].Width:0.0})");
    }
}
