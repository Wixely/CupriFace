using CupriFace.Style;
using CupriFace.Text;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>font-variant-numeric: tabular-nums</c> — every digit on the widest digit's advance.
///
/// <para>In most faces a <c>1</c> is narrower than a <c>0</c>. That is right for prose and wrong for
/// a clock, a score, a counter or a percentage: as <c>15:00:32</c> ticks over the text physically
/// changes width, the box resizes, and everything beside it twitches once a second.</para>
///
/// <para><b>Measuring and painting share one computation</b> (<c>FontService.TabularShifts</c>) and
/// that is the whole design. A run measured one width and drawn another is text that overflows its
/// box, clips, or sits off-centre, and the two code paths are a hundred lines apart.</para>
/// </summary>
public class TabularNumsTests(ITestOutputHelper output)
{
    /// <summary>A face whose digits are NOT all the same width, or null when this machine has none.
    ///
    /// <para>Needed because the obvious test — "these two strings measure the same" — is vacuously
    /// true on a face that already has tabular figures, and most UI faces do: Noto Sans (which this
    /// repo ships), Arial, Times and the generic families all measure every digit identically here.
    /// Georgia's old-style figures are the common counter-example. Detected by MEASURING rather than
    /// by naming an OS, so the test exercises the behaviour wherever such a face exists and says so
    /// plainly where none does.</para></summary>
    private static string? ProportionalFace()
    {
        var fs = new FontService();
        foreach (var family in new[] { "Georgia", "Palatino Linotype", "Book Antiqua", "Constantia" })
        {
            var widths = new HashSet<float>();
            for (var d = '0'; d <= '9'; d++)
                widths.Add(fs.MeasureText(family, 400, 40f, d.ToString(), FontSlant.Normal));
            if (widths.Count > 1) return family;
        }
        return null;
    }

    private static float Width(string text, string family, bool tabular)
    {
        using var t = new TestDoc($"<body><div class='n'>{text}</div></body>",
            $".n {{ display:inline-block; font-family:{family}; font-size:40px;"
            + (tabular ? " font-variant-numeric: tabular-nums;" : "") + " }",
            width: 800, height: 200);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(800, 200)) { }
        return t.FindClass("n").Width;
    }

    /// <summary>
    /// THE POINT OF THE FEATURE: the same layout measures the same width whatever the digits say.
    /// Without it these four differ by 44px — a badge that jumps every time the clock ticks.
    /// </summary>
    [Fact]
    public void A_changing_number_keeps_a_constant_width()
    {
        if (ProportionalFace() is not { } face)
        {
            output.WriteLine("no proportional-digit face on this machine — nothing to discriminate");
            return;
        }

        string[] values = ["11:11:11", "00:00:00", "88:88:88", "15:00:32"];
        var plain = values.Select(v => Width(v, face, tabular: false)).ToList();
        var tab = values.Select(v => Width(v, face, tabular: true)).ToList();
        output.WriteLine($"{face}: plain {string.Join(", ", plain.Select(w => w.ToString("0.0")))}");
        output.WriteLine($"{face}: tabular {string.Join(", ", tab.Select(w => w.ToString("0.0")))}");

        // The premise: this face really does vary, so the assertion below means something.
        Assert.True(plain.Max() - plain.Min() > 1f,
            $"{face} should have proportional digits, spread was {plain.Max() - plain.Min():0.00}");

        foreach (var w in tab) Assert.Equal(tab[0], w, 0.5);
    }

    /// <summary>Each digit takes the WIDEST digit's advance, not the narrowest or an average — so a
    /// tabular run of 1s is as wide as a plain run of 0s, 0 being the widest digit in these faces.
    /// That pins which way the padding goes; an average would also give a constant width and would
    /// clip.</summary>
    [Fact]
    public void A_digit_takes_the_widest_digits_advance()
    {
        if (ProportionalFace() is not { } face) return;
        var tabularOnes = Width("11111111", face, tabular: true);
        var plainZeros = Width("00000000", face, tabular: false);
        output.WriteLine($"tabular 1s {tabularOnes:0.0}   plain 0s {plainZeros:0.0}");
        Assert.Equal(plainZeros, tabularOnes, 1.0);
    }

    /// <summary>Text with no digits is untouched. The padding is per-digit, so a label beside a
    /// number must not drift, and <c>15:00:32</c> keeps its colons tight.</summary>
    [Fact]
    public void Text_without_digits_is_unchanged()
    {
        var face = ProportionalFace() ?? "sans-serif";
        Assert.Equal(Width("Hello", face, tabular: false), Width("Hello", face, tabular: true), 0.01);
    }

    /// <summary>On a face that already has tabular figures the flag costs nothing and changes
    /// nothing — the shared helper returns null and both paths take their original route. True on
    /// every machine, which is what makes it worth asserting.</summary>
    [Fact]
    public void A_face_that_is_already_tabular_is_left_alone()
    {
        foreach (var v in new[] { "11:11:11", "00:00:00" })
            Assert.Equal(Width(v, "monospace", tabular: false), Width(v, "monospace", tabular: true), 0.01);
    }

    /// <summary>It INHERITS. The declaration goes on an element and the thing that gets measured is
    /// the text node inside it — without inheritance the flag was set on the div, read as false on
    /// its text, and the feature did nothing at all while appearing to be supported. That is the
    /// failure mode this whole line of work exists to remove, so it gets its own test.</summary>
    [Fact]
    public void It_reaches_the_text_node_inside_the_element()
    {
        using var t = new TestDoc("<body><div class='n'>123</div></body>",
            ".n { font-variant-numeric: tabular-nums; font-size:20px; }", width: 400, height: 120);
        t.Doc.Refresh();
        using (t.Doc.RenderToImage(400, 120)) { }
        var text = t.Find(x => x.IsText);
        Assert.NotNull(text);
        Assert.True(text!.Style.TabularNums, "the text node is what gets measured, so it needs the flag");
    }

    /// <summary>Anything else in <c>font-variant-numeric</c> is still reported as unsupported.
    /// Ordinals, slashed zero and diagonal fractions need OpenType features this engine does not
    /// plumb, and silently accepting them would be the exact failure CF0050 exists to report.</summary>
    [Fact]
    public void The_other_values_are_still_reported_as_unsupported()
    {
        var report = Diagnostics.CupriDoctor.Check("<body><div class='n'>1</div></body>",
            ".n { font-variant-numeric: slashed-zero; }", width: 400, height: 200);
        Assert.Contains(report.Findings,
            f => f.Code == "CF0050" && f.Message.Contains("font-variant-numeric"));
    }
}
