using SkiaSharp;

namespace CupriFace.Diagnostics;

/// <summary>
/// WCAG 2.1 contrast: the ratio between two colours' relative luminance, and the thresholds text
/// has to clear to be readable.
///
/// <para>Public because it is the one piece of accessibility arithmetic a caller is likely to want
/// for itself — picking a readable foreground for a themed accent, say, or asserting a palette in
/// its own tests. <see cref="CupriDoctor"/> uses it for <c>CF0090</c>.</para>
/// </summary>
public static class Contrast
{
    /// <summary>WCAG AA for ordinary text.</summary>
    public const double AaNormal = 4.5;

    /// <summary>WCAG AA for large text, which needs less because the strokes are thicker.</summary>
    public const double AaLarge = 3.0;

    /// <summary>
    /// Relative luminance (WCAG 2.1): each channel linearised out of sRGB, then weighted for the
    /// eye's sensitivity. 0 is black, 1 is white.
    ///
    /// <para>Alpha is ignored — a colour has to be composited against something before its
    /// luminance means anything, which is <see cref="Over"/>'s job.</para>
    /// </summary>
    public static double RelativeLuminance(SKColor c) =>
        0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// The contrast ratio between two colours, from 1 (identical) to 21 (black against white).
    /// Order does not matter.
    /// </summary>
    public static double Ratio(SKColor a, SKColor b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="over"/> composited onto <paramref name="under"/>, so a
    /// semi-transparent colour can be measured as what the eye actually sees.</summary>
    public static SKColor Over(SKColor over, SKColor under)
    {
        if (over.Alpha == 255) return over;
        var a = over.Alpha / 255f;
        static byte Mix(byte o, byte u, float a) => (byte)Math.Clamp((int)MathF.Round(o * a + u * (1 - a)), 0, 255);
        return new SKColor(Mix(over.Red, under.Red, a), Mix(over.Green, under.Green, a), Mix(over.Blue, under.Blue, a));
    }

    /// <summary>
    /// The ratio text of this size and weight has to clear: 3:1 once it is large, 4.5:1 otherwise.
    ///
    /// <para>WCAG calls text large at 18pt, or 14pt when bold — 24px and 18.66px at the usual 96dpi,
    /// which is what the engine's px sizes are in.</para>
    /// </summary>
    public static double RequiredFor(float fontSizePx, int fontWeight) =>
        fontSizePx >= 24f || (fontWeight >= 700 && fontSizePx >= 18.66f) ? AaLarge : AaNormal;

    /// <summary>Whether text of this size and weight clears WCAG AA against its background.</summary>
    public static bool MeetsAa(SKColor text, SKColor background, float fontSizePx, int fontWeight) =>
        Ratio(text, background) >= RequiredFor(fontSizePx, fontWeight) - 0.005;
}
