using System.Collections.Generic;
using SkiaSharp;

namespace CupriFace.Style;

public enum LengthUnit { Auto, Px, Percent, Calc }

/// <summary>A CSS length: px, %, auto, or a simple calc(px ± %).</summary>
public readonly struct Length
{
    public readonly LengthUnit Unit;
    public readonly float Value;        // px or % magnitude
    public readonly float PercentPart;  // Calc only: the % term

    public Length(LengthUnit unit, float value, float percentPart = 0f)
    {
        Unit = unit; Value = value; PercentPart = percentPart;
    }

    public static readonly Length Auto = new(LengthUnit.Auto, 0f);
    public static readonly Length Zero = new(LengthUnit.Px, 0f);

    /// <summary>calc() reduced to a px term + a % term (e.g. calc(100% - 40px)).</summary>
    public static Length Calc(float px, float percent) => new(LengthUnit.Calc, px, percent);

    public bool IsAuto => Unit == LengthUnit.Auto;
    public bool IsDefinite => Unit != LengthUnit.Auto;

    /// <summary>Resolve against a basis (used for %). Auto returns <paramref name="autoValue"/>.</summary>
    public float Resolve(float basis, float autoValue = 0f) => Unit switch
    {
        LengthUnit.Px => Value,
        LengthUnit.Percent => Value / 100f * basis,
        LengthUnit.Calc => Value + PercentPart / 100f * basis,
        _ => autoValue,
    };
}

/// <summary>Four-sided set of lengths (margin/padding).</summary>
public struct LengthEdges
{
    public Length Top, Right, Bottom, Left;
    public static LengthEdges Zero => new() { Top = Length.Zero, Right = Length.Zero, Bottom = Length.Zero, Left = Length.Zero };
    public void SetAll(Length v) { Top = Right = Bottom = Left = v; }
}

public enum DisplayType { Block, Flex, Grid, InlineBlock, Inline, None }

public enum TrackKind { Px, Percent, Fraction, Auto }

/// <summary>A CSS grid track size: <c>120px</c>, <c>25%</c>, <c>1fr</c>, <c>auto</c>, or minmax.</summary>
public readonly struct TrackSize
{
    public readonly TrackKind Kind;
    public readonly float Value;
    public readonly float MinPx;  // minmax() floor (0 = none)
    public TrackSize(TrackKind kind, float value, float minPx = 0f) { Kind = kind; Value = value; MinPx = minPx; }
    public static readonly TrackSize Auto = new(TrackKind.Auto, 0);
}

/// <summary>A grid template's <c>repeat(auto-fill|auto-fit, …)</c>: the repeated pattern, the index
/// in the fixed track list where the repetitions slot in, and whether repetitions beyond the item
/// count collapse (auto-fit). The COUNT depends on the container size, so it cannot be expanded at
/// parse time — LayoutGrid materialises the real track list per layout pass.</summary>
public sealed class GridAutoRepeat
{
    public readonly List<TrackSize> Pattern;
    public readonly int InsertAt;
    public readonly bool Fit;
    public GridAutoRepeat(List<TrackSize> pattern, int insertAt, bool fit)
    { Pattern = pattern; InsertAt = insertAt; Fit = fit; }
}

/// <summary>Grid item placement along one axis: an optional 1-based start line and a span, or named
/// grid lines (resolved against the container's template line names at layout time).</summary>
public readonly struct GridPlacement
{
    public readonly int? Start; // 1-based grid line, null = auto-place
    public readonly int Span;
    public readonly string? StartName, EndName; // named lines (override Start/Span when they resolve)
    public GridPlacement(int? start, int span, string? startName = null, string? endName = null)
    { Start = start; Span = Math.Max(1, span); StartName = startName; EndName = endName; }
    public static readonly GridPlacement Auto = new(null, 1);
}
public enum FlexDirection { Row, RowReverse, Column, ColumnReverse }
public enum FlexWrapMode { NoWrap, Wrap }
public enum JustifyContent { FlexStart, Center, FlexEnd, SpaceBetween, SpaceAround, SpaceEvenly }
public enum AlignItems { Stretch, FlexStart, Center, FlexEnd }
public enum TextAlign { Left, Center, Right }
public enum PositionType { Static, Relative, Absolute, Fixed, Sticky }
public enum OverflowMode { Visible, Hidden, Scroll }

/// <summary>CSS <c>white-space</c> (the supported subset). <c>NoWrap</c> lays text out on a single
/// line that overflows instead of wrapping — used by single-line text fields. The preserved modes
/// (#69) treat every <c>\n</c> in the text as a HARD line break: <c>Pre</c> also keeps spaces
/// verbatim and never wraps (code); <c>PreWrap</c> keeps spaces and wraps long lines (chat, logs);
/// <c>PreLine</c> collapses runs of spaces but keeps the newlines.</summary>
public enum WhiteSpaceMode { Normal, NoWrap, Pre, PreWrap, PreLine }

/// <summary>CSS <c>font-style</c>. Selects the face's slant; a family with no italic/oblique face
/// falls back to whatever the platform matches (usually upright), the same as a browser.</summary>
public enum FontSlant { Normal, Italic, Oblique }

/// <summary>CSS <c>text-decoration-line</c> — combinable, e.g. <c>underline line-through</c>.</summary>
[Flags]
public enum TextDecorations { None = 0, Underline = 1, LineThrough = 2, Overline = 4 }

/// <summary>CSS <c>resize</c>: which axes a user can drag the element's size on (via a corner grip).</summary>
public enum ResizeMode { None, Both, Horizontal, Vertical }

/// <summary>CSS <c>cursor</c> (the supported subset). <c>Auto</c> means "unspecified" — it inherits, and
/// where nothing sets it the document infers one from the element (pointer over links/buttons, text over
/// fields, resize arrows over drag boundaries). Hosts map these to their platform cursor (SDL / GLFW / the
/// canvas <c>style.cursor</c>).</summary>
public enum CursorType
{
    Auto, Default, Pointer, Text, Wait, Progress, Help, Crosshair, Move, NotAllowed,
    Grab, Grabbing, EwResize, NsResize, NeswResize, NwseResize, None,
}

/// <summary>CSS <c>border-style</c> (the supported subset). <c>hidden</c> maps to <c>None</c>; other
/// keywords (double/groove/…) fall back to <c>Solid</c>.</summary>
public enum BorderLineStyle { Solid, Dashed, Dotted, None }

/// <summary>A CSS <c>filter</c> function. Colour-matrix ops (brightness…invert) carry their amount in
/// <c>A</c>; <c>Blur</c> carries the radius in <c>A</c>; <c>DropShadow</c> uses A=dx, B=dy, C=blur,
/// plus <c>Color</c>.</summary>
public enum FilterKind { Blur, Brightness, Contrast, Grayscale, Saturate, Sepia, Invert, Opacity, DropShadow }

public readonly record struct FilterOp(FilterKind Kind, float A, float B, float C, SkiaSharp.SKColor Color);

/// <summary>A CSS <c>box-shadow</c> layer: offset (Dx,Dy), Blur radius, Spread, Color, and Inset (an
/// inner shadow rather than a drop shadow).</summary>
public readonly record struct BoxShadow(float Dx, float Dy, float Blur, float Spread, SkiaSharp.SKColor Color, bool Inset);

public enum GradientKind { Linear, Radial }

/// <summary>A gradient colour stop: its <c>Color</c> at <c>Position</c> (0..1), or <c>Position</c> NaN
/// to auto-distribute it evenly.</summary>
public readonly record struct GradientStop(SkiaSharp.SKColor Color, float Position);

/// <summary>A CSS <c>linear-gradient()</c> / <c>radial-gradient()</c> background. <c>AngleDeg</c> is the
/// CSS angle (0 = to top, 90 = to right; ignored for radial).</summary>
public sealed record Gradient(GradientKind Kind, float AngleDeg, IReadOnlyList<GradientStop> Stops);

public static class Colors
{
    private static readonly Dictionary<string, SKColor> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["transparent"] = new SKColor(0, 0, 0, 0),
        ["black"] = SKColors.Black, ["white"] = SKColors.White,
        ["red"] = SKColors.Red, ["green"] = new SKColor(0, 128, 0), ["blue"] = SKColors.Blue,
        ["gray"] = SKColors.Gray, ["grey"] = SKColors.Gray, ["silver"] = new SKColor(0xC0, 0xC0, 0xC0),
        ["orange"] = new SKColor(0xFF, 0xA5, 0x00), ["yellow"] = SKColors.Yellow,
        ["purple"] = new SKColor(0x80, 0, 0x80), ["teal"] = new SKColor(0, 0x80, 0x80),
        ["navy"] = new SKColor(0, 0, 0x80), ["maroon"] = new SKColor(0x80, 0, 0),
        ["lime"] = new SKColor(0, 0xFF, 0), ["aqua"] = new SKColor(0, 0xFF, 0xFF),
        ["cyan"] = new SKColor(0, 0xFF, 0xFF), ["magenta"] = new SKColor(0xFF, 0, 0xFF),
        ["slategray"] = new SKColor(0x70, 0x80, 0x90), ["dimgray"] = new SKColor(0x69, 0x69, 0x69),
        ["lightgray"] = new SKColor(0xD3, 0xD3, 0xD3), ["lightgrey"] = new SKColor(0xD3, 0xD3, 0xD3),
        ["whitesmoke"] = new SKColor(0xF5, 0xF5, 0xF5), ["gainsboro"] = new SKColor(0xDC, 0xDC, 0xDC),
        ["steelblue"] = new SKColor(0x46, 0x82, 0xB4), ["coral"] = new SKColor(0xFF, 0x7F, 0x50),
        ["tomato"] = new SKColor(0xFF, 0x63, 0x47), ["gold"] = new SKColor(0xFF, 0xD7, 0x00),
        ["copper"] = new SKColor(0xB8, 0x73, 0x33),
    };

    public static bool TryParse(string? text, out SKColor color)
    {
        color = SKColors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (text[0] == '#')
        {
            var hex = text[1..];
            if (hex.Length == 3)
            {
                byte r = (byte)(Convert.ToInt32($"{hex[0]}{hex[0]}", 16));
                byte g = (byte)(Convert.ToInt32($"{hex[1]}{hex[1]}", 16));
                byte b = (byte)(Convert.ToInt32($"{hex[2]}{hex[2]}", 16));
                color = new SKColor(r, g, b);
                return true;
            }
            if (hex.Length == 6 || hex.Length == 8)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
                color = new SKColor(r, g, b, a);
                return true;
            }
            return false;
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var inner = text[(text.IndexOf('(') + 1)..text.IndexOf(')')];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                byte a = 255;
                if (parts.Length >= 4 && CssNumber.TryParse(parts[3], out var af)) a = (byte)Math.Clamp(af * 255f, 0, 255);
                color = new SKColor(r, g, b, a);
                return true;
            }
            return false;
        }

        return Named.TryGetValue(text, out color);
    }
}

/// <summary>
/// One radius, as authored: a length in px, or a percentage of the box it will be drawn on.
///
/// <para>A percentage cannot be folded into a float at parse time the way <c>20px</c> can — it is
/// resolved against the BOX, horizontally against its width and vertically against its height, which
/// is what makes <c>border-radius: 50%</c> an ellipse on a rectangle rather than a circle. It used
/// to be parsed with the ordinary px parser, which failed on the <c>%</c> and fell back to zero, so
/// the canonical circular avatar rendered as a square (#162).</para>
/// </summary>
public readonly record struct RadiusLength(float Value, bool IsPercent)
{
    public static readonly RadiusLength Zero = new(0, false);
    public bool IsZero => Value <= 0;
    public float Resolve(float basis) => IsPercent ? Value / 100f * MathF.Max(0, basis) : Value;
}

/// <summary>
/// <c>border-radius</c> as authored: four corners, each with a horizontal and a vertical radius.
///
/// <para>Stored per corner because the shorthand takes one to four values (TL, TR, BR, BL, with the
/// usual mirroring), and per axis because <c>A / B</c> gives the horizontal radii before the slash
/// and the vertical ones after. Both used to be handed whole to a single-number parser, which
/// failed and fell back to zero — so a card asking for a rounded top edge got no rounding at all,
/// which is worse than the value being ignored (#163).</para>
/// </summary>
public readonly record struct BorderRadiusSpec(
    RadiusLength TopLeftX, RadiusLength TopLeftY,
    RadiusLength TopRightX, RadiusLength TopRightY,
    RadiusLength BottomRightX, RadiusLength BottomRightY,
    RadiusLength BottomLeftX, RadiusLength BottomLeftY)
{
    public static readonly BorderRadiusSpec None = default;

    /// <summary>Every corner the same length on both axes — what a single-value shorthand means.</summary>
    public static BorderRadiusSpec Uniform(RadiusLength r) => new(r, r, r, r, r, r, r, r);

    public bool IsZero => TopLeftX.IsZero && TopLeftY.IsZero && TopRightX.IsZero && TopRightY.IsZero
                       && BottomRightX.IsZero && BottomRightY.IsZero && BottomLeftX.IsZero && BottomLeftY.IsZero;

    /// <summary>Resolve against the border box. Percentages need this and lengths pass through.</summary>
    public CornerRadii Resolve(float width, float height) => IsZero
        ? CornerRadii.None
        : new CornerRadii(
            new SKPoint(TopLeftX.Resolve(width), TopLeftY.Resolve(height)),
            new SKPoint(TopRightX.Resolve(width), TopRightY.Resolve(height)),
            new SKPoint(BottomRightX.Resolve(width), BottomRightY.Resolve(height)),
            new SKPoint(BottomLeftX.Resolve(width), BottomLeftY.Resolve(height)));
}

/// <summary>
/// Four corners' radii in px, ready to draw: <c>(rx, ry)</c> each, clockwise from the top left.
///
/// <para>Implicitly convertible from a float, because most boxes are uniformly rounded or not
/// rounded at all and every paint command that carries a radius should stay readable for that
/// case.</para>
/// </summary>
public readonly record struct CornerRadii(SKPoint TopLeft, SKPoint TopRight, SKPoint BottomRight, SKPoint BottomLeft)
{
    public static readonly CornerRadii None = default;

    public static implicit operator CornerRadii(float r)
    {
        var p = new SKPoint(r, r);
        return new CornerRadii(p, p, p, p);
    }

    public bool IsZero => TopLeft.X <= 0 && TopLeft.Y <= 0 && TopRight.X <= 0 && TopRight.Y <= 0
                       && BottomRight.X <= 0 && BottomRight.Y <= 0 && BottomLeft.X <= 0 && BottomLeft.Y <= 0;

    /// <summary>The one radius that describes every corner, or null when they differ — so the common
    /// case can keep taking Skia's cheap two-argument path.</summary>
    public float? Uniform =>
        TopLeft.X == TopLeft.Y && TopLeft == TopRight && TopLeft == BottomRight && TopLeft == BottomLeft
            ? TopLeft.X : null;

    /// <summary>Move every corner inwards by <paramref name="inset"/> — what a border stroke centred
    /// on the edge needs, and what the uniform path already did by subtracting from one float.</summary>
    public CornerRadii Deflate(float inset) => new(
        new SKPoint(MathF.Max(0, TopLeft.X - inset), MathF.Max(0, TopLeft.Y - inset)),
        new SKPoint(MathF.Max(0, TopRight.X - inset), MathF.Max(0, TopRight.Y - inset)),
        new SKPoint(MathF.Max(0, BottomRight.X - inset), MathF.Max(0, BottomRight.Y - inset)),
        new SKPoint(MathF.Max(0, BottomLeft.X - inset), MathF.Max(0, BottomLeft.Y - inset)));

    /// <summary>The Skia shape. <c>SetRectRadii</c> scales every radius down proportionally when the
    /// corners would overlap, which is the CSS rule too — so <c>999px</c> and <c>50%</c> both land on
    /// the same capsule without this code clamping anything itself.</summary>
    public SKRoundRect ToRoundRect(SKRect rect)
    {
        var rr = new SKRoundRect();
        rr.SetRectRadii(rect, [TopLeft, TopRight, BottomRight, BottomLeft]);
        return rr;
    }
}
