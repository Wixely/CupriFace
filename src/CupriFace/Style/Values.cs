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

public enum ClipShapeKind { Inset, Circle, Ellipse, Polygon }

/// <summary>
/// A <c>clip-path</c> basic shape (#268): <c>inset()</c> with an optional <c>round</c>,
/// <c>circle()</c>, <c>ellipse()</c> or <c>polygon()</c>, with every length still unresolved — a
/// percentage is of the element's border box, which exists only at paint time.
///
/// <para>A wipe is an <c>inset()</c> animated from one edge; an iris is a <c>circle()</c> growing
/// from a point; a diagonal cut is a <c>polygon()</c>. 40 of 165 designed compositions in one
/// corpus use one, and with the property ignored every transition showed its final state from the
/// first frame. Interpolable between two shapes of the same kind (and, for a polygon, the same
/// number of points), which is what makes the transitions move rather than flip.</para>
/// </summary>
public sealed class ClipShape : IEquatable<ClipShape>
{
    public ClipShapeKind Kind;

    // inset(): how far each edge is pulled in, and the corners of what is left.
    public Length Top = Length.Zero, Right = Length.Zero, Bottom = Length.Zero, Left = Length.Zero;
    public BorderRadiusSpec Round;

    // circle()/ellipse(): radii (Auto = closest-side, or farthest-side when flagged) and centre.
    public Length RX = Length.Auto, RY = Length.Auto;
    public bool FarthestSide;
    public Length CX = new(LengthUnit.Percent, 50f), CY = new(LengthUnit.Percent, 50f);

    // polygon(): x0 y0 x1 y1 …, and its fill rule.
    public Length[] Points = [];
    public bool EvenOdd;

    /// <summary>The shape as a path in absolute coordinates, for a border box at (x, y) of w × h.</summary>
    public SKPath ToPath(float x, float y, float w, float h)
    {
        var path = new SKPath();
        switch (Kind)
        {
            case ClipShapeKind.Inset:
            {
                var l = x + Left.Resolve(w); var t = y + Top.Resolve(h);
                var r = x + w - Right.Resolve(w); var b = y + h - Bottom.Resolve(h);
                var rect = new SKRect(l, t, MathF.Max(l, r), MathF.Max(t, b));
                if (Round.IsZero) path.AddRect(rect);
                else
                {
                    using var rr = Round.Resolve(rect.Width, rect.Height).ToRoundRect(rect);
                    path.AddRoundRect(rr);
                }
                break;
            }
            case ClipShapeKind.Circle:
            {
                var cx = x + CX.Resolve(w); var cy = y + CY.Resolve(h);
                float r;
                if (RX.IsAuto) r = SideDistance(cx - x, x + w - cx, cy - y, y + h - cy);
                // A percentage radius on a circle is of the box's diagonal over √2 — CSS's reference
                // for the one length that has to serve both axes.
                else if (RX.Unit == LengthUnit.Percent) r = RX.Value / 100f * MathF.Sqrt(w * w + h * h) / MathF.Sqrt(2f);
                else r = RX.Resolve(w);
                path.AddCircle(cx, cy, MathF.Max(0f, r));
                break;
            }
            case ClipShapeKind.Ellipse:
            {
                var cx = x + CX.Resolve(w); var cy = y + CY.Resolve(h);
                var rx = RX.IsAuto ? SideDistance(cx - x, x + w - cx) : RX.Resolve(w);
                var ry = RY.IsAuto ? SideDistance(cy - y, y + h - cy) : RY.Resolve(h);
                path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
                break;
            }
            case ClipShapeKind.Polygon:
            {
                var pts = new SKPoint[Points.Length / 2];
                for (var i = 0; i < pts.Length; i++)
                    pts[i] = new SKPoint(x + Points[2 * i].Resolve(w), y + Points[2 * i + 1].Resolve(h));
                if (pts.Length >= 3) path.AddPoly(pts, close: true);
                if (EvenOdd) path.FillType = SKPathFillType.EvenOdd;
                break;
            }
        }
        return path;
    }

    private float SideDistance(params float[] distances)
    {
        var d = distances[0];
        foreach (var v in distances) d = FarthestSide ? MathF.Max(d, v) : MathF.Min(d, v);
        return MathF.Max(0f, d);
    }

    /// <summary>The shape part-way between two: each length interpolated when the two shapes are of
    /// the same kind with the same number of points, which is what CSS animates; otherwise the pair
    /// is not interpolable and flips at the midpoint, as CSS does too.</summary>
    public static ClipShape Lerp(ClipShape a, ClipShape b, float t)
    {
        if (a.Kind != b.Kind || a.Points.Length != b.Points.Length || a.FarthestSide != b.FarthestSide)
            return t < 0.5f ? a : b;
        var r = new ClipShape
        {
            Kind = a.Kind, FarthestSide = a.FarthestSide, EvenOdd = t < 0.5f ? a.EvenOdd : b.EvenOdd,
            Top = L(a.Top, b.Top, t), Right = L(a.Right, b.Right, t), Bottom = L(a.Bottom, b.Bottom, t), Left = L(a.Left, b.Left, t),
            Round = new BorderRadiusSpec(
                R(a.Round.TopLeftX, b.Round.TopLeftX, t), R(a.Round.TopLeftY, b.Round.TopLeftY, t),
                R(a.Round.TopRightX, b.Round.TopRightX, t), R(a.Round.TopRightY, b.Round.TopRightY, t),
                R(a.Round.BottomRightX, b.Round.BottomRightX, t), R(a.Round.BottomRightY, b.Round.BottomRightY, t),
                R(a.Round.BottomLeftX, b.Round.BottomLeftX, t), R(a.Round.BottomLeftY, b.Round.BottomLeftY, t)),
            RX = L(a.RX, b.RX, t), RY = L(a.RY, b.RY, t), CX = L(a.CX, b.CX, t), CY = L(a.CY, b.CY, t),
            Points = new Length[a.Points.Length],
        };
        for (var i = 0; i < r.Points.Length; i++) r.Points[i] = L(a.Points[i], b.Points[i], t);
        return r;

        static Length L(Length x, Length y, float t)
        {
            if (x.Unit == y.Unit && x.Unit is LengthUnit.Px or LengthUnit.Percent) return new Length(x.Unit, x.Value + (y.Value - x.Value) * t);
            if (x.Unit == y.Unit && x.Unit == LengthUnit.Calc) return Length.Calc(x.Value + (y.Value - x.Value) * t, x.PercentPart + (y.PercentPart - x.PercentPart) * t);
            // 0 is unitless in CSS and interpolates with anything.
            if (x.Unit == LengthUnit.Px && x.Value == 0 && y.Unit == LengthUnit.Percent) return new Length(y.Unit, y.Value * t);
            if (y.Unit == LengthUnit.Px && y.Value == 0 && x.Unit == LengthUnit.Percent) return new Length(x.Unit, x.Value * (1 - t));
            return t < 0.5f ? x : y;
        }
        static RadiusLength R(RadiusLength x, RadiusLength y, float t) =>
            x.IsPercent == y.IsPercent ? new RadiusLength(x.Value + (y.Value - x.Value) * t, x.IsPercent) : t < 0.5f ? x : y;
    }

    public bool Equals(ClipShape? o)
    {
        if (o is null) return false;
        if (Kind != o.Kind || FarthestSide != o.FarthestSide || EvenOdd != o.EvenOdd) return false;
        if (!Same(Top, o.Top) || !Same(Right, o.Right) || !Same(Bottom, o.Bottom) || !Same(Left, o.Left)) return false;
        if (!Round.Equals(o.Round)) return false;
        if (!Same(RX, o.RX) || !Same(RY, o.RY) || !Same(CX, o.CX) || !Same(CY, o.CY)) return false;
        if (Points.Length != o.Points.Length) return false;
        for (var i = 0; i < Points.Length; i++) if (!Same(Points[i], o.Points[i])) return false;
        return true;

        static bool Same(Length a, Length b) => a.Unit == b.Unit && a.Value == b.Value && a.PercentPart == b.PercentPart;
    }
    public override bool Equals(object? obj) => obj is ClipShape c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(Kind, Points.Length, Top.Value, Right.Value, RX.Value, CX.Value);
}

/// <summary>CSS <c>text-transform</c>: the case the text is DRAWN in, whatever case it was typed in.
/// Applied when the render tree is built, after the cascade has resolved, so the markup keeps the
/// author's text and a renderer that does it differently is handed the same document (#266).</summary>
public enum TextTransform { None, Uppercase, Lowercase, Capitalize }

public static class TextCase
{
    /// <summary><paramref name="text"/> in the case <paramref name="transform"/> asks for.
    /// <c>capitalize</c> upper-cases the first letter of each word, a word being what follows
    /// whitespace, as CSS defines it; the rest of the word is left as typed.</summary>
    public static string Apply(string text, TextTransform transform)
    {
        switch (transform)
        {
            case TextTransform.Uppercase: return text.ToUpperInvariant();
            case TextTransform.Lowercase: return text.ToLowerInvariant();
            case TextTransform.Capitalize:
            {
                var chars = text.ToCharArray();
                var atWordStart = true;
                for (var i = 0; i < chars.Length; i++)
                {
                    var c = chars[i];
                    if (char.IsWhiteSpace(c)) { atWordStart = true; continue; }
                    if (atWordStart && char.IsLetter(c)) chars[i] = char.ToUpperInvariant(c);
                    atWordStart = false;
                }
                return new string(chars);
            }
            default: return text;
        }
    }
}

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

/// <summary>A gradient colour stop: its <c>Color</c> at <c>Position</c> (0..1), or at
/// <c>PositionPx</c> px along the gradient line (a radial gradient's radius), or NaN for both to
/// sit evenly between its positioned neighbours.
///
/// <para>A px position is kept as px, because what it is a fraction OF is only known when the
/// gradient is built: the line length over the box it fills — which, under <c>background-size</c>,
/// is the tile. It used to be dropped at parse time, so <c>#000 1.5px, transparent 2px</c> read as
/// two unpositioned stops and a dot grid became a field of blobs (#273).</para></summary>
public readonly record struct GradientStop(SkiaSharp.SKColor Color, float Position, float PositionPx = float.NaN);

/// <summary>A CSS <c>linear-gradient()</c> / <c>radial-gradient()</c> background. <c>AngleDeg</c> is the
/// CSS angle (0 = to top, 90 = to right; ignored for radial).</summary>
public sealed record Gradient(GradientKind Kind, float AngleDeg, IReadOnlyList<GradientStop> Stops);

/// <summary>How <c>background-size</c> sizes the image layer's tile.</summary>
public enum BackgroundSizeKind
{
    /// <summary>A gradient fills the box; a raster image keeps its own pixel size.</summary>
    Auto,
    /// <summary>Explicit <c>Width</c> × <c>Height</c>; an <c>auto</c> on one axis keeps a raster
    /// image's aspect ratio (a gradient, having none, takes the box on that axis).</summary>
    Length,
    /// <summary>Scaled, keeping its ratio, to the smallest size that covers the whole box.</summary>
    Cover,
    /// <summary>Scaled, keeping its ratio, to the largest size that fits inside the box.</summary>
    Contain,
}

/// <summary>
/// Where the image layer of a background sits and how it tiles: <c>background-size</c>,
/// <c>background-position</c> and <c>background-repeat</c> together (#267).
///
/// <para>Everything here was accepted and ignored before: the image or gradient filled the whole box
/// whatever size it was given, so a gradient sized to a fraction of its box as a progress bar, a
/// <c>cover</c> on a photo, and a small repeating tile all rendered as one stretched layer.</para>
/// </summary>
/// <param name="Size">The tile's size.</param>
/// <param name="PosX">Where the tile sits. A percentage is of the SPARE room, as in CSS: <c>50%</c>
/// centres the tile, and <c>100%</c> puts its far edge on the box's far edge.</param>
/// <param name="RepeatX">Tile along the axis; otherwise the single tile is drawn once.</param>
public readonly record struct BackgroundGeometry(
    BackgroundSizeKind Size, Length Width, Length Height, Length PosX, Length PosY, bool RepeatX, bool RepeatY)
{
    /// <summary>CSS's initial values: <c>auto</c>, <c>0% 0%</c>, <c>repeat</c> — the box, filled.</summary>
    public static readonly BackgroundGeometry Default = new(BackgroundSizeKind.Auto, Length.Auto, Length.Auto,
        new Length(LengthUnit.Percent, 0f), new Length(LengthUnit.Percent, 0f), true, true);

    /// <summary>A tile that is exactly the box, however it repeats — the pre-#267 result, and the
    /// fast path.</summary>
    public bool FillsBox(float intrinsicW, float intrinsicH, float boxW, float boxH)
    {
        var (x, y, w, h) = Tile(intrinsicW, intrinsicH, boxW, boxH);
        return MathF.Abs(x) < 0.01f && MathF.Abs(y) < 0.01f
            && MathF.Abs(w - boxW) < 0.01f && MathF.Abs(h - boxH) < 0.01f;
    }

    /// <summary>The tile's rectangle, relative to the box's top-left. <paramref name="intrinsicW"/>
    /// and <paramref name="intrinsicH"/> are the image's own size; pass 0 for a gradient, which has
    /// none and takes the box wherever a size is <c>auto</c>.</summary>
    public (float X, float Y, float W, float H) Tile(float intrinsicW, float intrinsicH, float boxW, float boxH)
    {
        var hasIntrinsic = intrinsicW > 0 && intrinsicH > 0;
        float w, h;
        switch (Size)
        {
            case BackgroundSizeKind.Cover or BackgroundSizeKind.Contain when hasIntrinsic:
            {
                var sx = boxW / intrinsicW; var sy = boxH / intrinsicH;
                var scale = Size == BackgroundSizeKind.Cover ? MathF.Max(sx, sy) : MathF.Min(sx, sy);
                w = intrinsicW * scale; h = intrinsicH * scale;
                break;
            }
            case BackgroundSizeKind.Length:
            {
                var wAuto = Width.IsAuto; var hAuto = Height.IsAuto;
                w = wAuto ? 0 : Width.Resolve(boxW);
                h = hAuto ? 0 : Height.Resolve(boxH);
                if (wAuto && hAuto) { w = hasIntrinsic ? intrinsicW : boxW; h = hasIntrinsic ? intrinsicH : boxH; }
                else if (wAuto) w = hasIntrinsic ? h * intrinsicW / intrinsicH : boxW;
                else if (hAuto) h = hasIntrinsic ? w * intrinsicH / intrinsicW : boxH;
                break;
            }
            default:
                w = hasIntrinsic ? intrinsicW : boxW;
                h = hasIntrinsic ? intrinsicH : boxH;
                break;
        }
        w = MathF.Max(0f, w); h = MathF.Max(0f, h);
        // A percentage position is of the room left over, so 50% centres and 100% right-aligns —
        // which is the only reading under which `center` and `right` mean what they say.
        var x = PosX.Unit == LengthUnit.Percent ? (boxW - w) * PosX.Value / 100f : PosX.Resolve(boxW - w);
        var y = PosY.Unit == LengthUnit.Percent ? (boxH - h) * PosY.Value / 100f : PosY.Resolve(boxH - h);
        return (x, y, w, h);
    }
}

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

    /// <summary>
    /// A CSS colour, or <c>false</c>.
    ///
    /// <para><b>It never throws.</b> That is the contract of a <c>TryParse</c> and it was not being
    /// kept: <c>rgb(1,</c> reached <c>text[(IndexOf('(') + 1)..IndexOf(')')]</c> with no close paren,
    /// so the range was <c>4..-1</c> and the substring length came out at −5
    /// (<c>ArgumentOutOfRangeException</c>); <c>#gggggg</c> reached <c>Convert.ToByte</c> and raised
    /// <c>FormatException</c>. Neither is reachable through well-formed CSS, but both were reachable
    /// through a caller that split a value badly — and one was: the <c>border</c> shorthand split on
    /// spaces, handed this <c>rgb(1,</c>, and the whole DOCUMENT failed to build (#196). A parser
    /// that throws on malformed input turns a dropped declaration into a blank window.</para>
    /// </summary>
    public static bool TryParse(string? text, out SKColor color)
    {
        color = SKColors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (text[0] == '#') return TryParseHex(text[1..], out color);

        // Functional notation. Both parens are required: an unterminated call is not a colour, and
        // deciding that here is what keeps the arithmetic below safe.
        var open = text.IndexOf('(');
        if (open > 0 && text[^1] == ')')
        {
            var fn = text[..open].TrimEnd();
            var inner = text[(open + 1)..^1];
            if (fn.Equals("rgb", StringComparison.OrdinalIgnoreCase)
                || fn.Equals("rgba", StringComparison.OrdinalIgnoreCase))
                return TryParseRgb(inner, out color);
            if (fn.Equals("hsl", StringComparison.OrdinalIgnoreCase)
                || fn.Equals("hsla", StringComparison.OrdinalIgnoreCase))
                return TryParseHsl(inner, out color);
            return false;
        }

        return Named.TryGetValue(text, out color);
    }

    /// <summary>The digits after a <c>#</c>. Validated before conversion rather than after: the old
    /// code handed anything six characters long to <c>Convert.ToByte</c>, which throws on the ones
    /// that are not hex.</summary>
    private static bool TryParseHex(string hex, out SKColor color)
    {
        color = SKColors.Transparent;
        if (hex.Length is not (3 or 6 or 8)) return false;
        foreach (var ch in hex) if (!Uri.IsHexDigit(ch)) return false;

        if (hex.Length == 3)
        {
            color = new SKColor(Nyb(hex[0]), Nyb(hex[1]), Nyb(hex[2]));
            return true;
        }
        color = new SKColor(Hex(hex, 0), Hex(hex, 2), Hex(hex, 4),
                            hex.Length == 8 ? Hex(hex, 6) : (byte)255);
        return true;

        // #abc means #aabbcc: the digit is doubled, not shifted.
        static byte Nyb(char c) => (byte)(Convert.ToInt32(c.ToString(), 16) * 17);
        static byte Hex(string h, int i) => Convert.ToByte(h.Substring(i, 2), 16);
    }

    /// <summary>
    /// The inside of <c>rgb()</c>/<c>rgba()</c>.
    ///
    /// <para>Commas and spaces both separate, and <c>/</c> introduces alpha, because CSS accepts all
    /// three spellings and authors write all three: <c>rgb(1, 2, 3)</c>, <c>rgb(1 2 3)</c> and
    /// <c>rgb(1 2 3 / 0.5)</c>. Only the comma form used to parse, so the modern one was silently
    /// dropped — which in this engine means an element that renders perfectly with no colour.</para>
    /// </summary>
    private static bool TryParseRgb(string inner, out SKColor color)
    {
        color = SKColors.Transparent;
        var parts = Components(inner);
        if (parts.Length < 3
            || !byte.TryParse(parts[0], out var r)
            || !byte.TryParse(parts[1], out var g)
            || !byte.TryParse(parts[2], out var b)) return false;

        byte a = 255;
        if (parts.Length >= 4 && CssNumber.TryParse(parts[3], out var af))
            a = (byte)Math.Clamp(af * 255f, 0, 255);
        color = new SKColor(r, g, b, a);
        return true;
    }

    /// <summary>
    /// The inside of <c>hsl()</c>/<c>hsla()</c>.
    ///
    /// <para>New, and worth having because it was reported as the WORKAROUND for #196 — every form
    /// of it returned false, so an author who took that advice got a border with no colour rather
    /// than a crash, which is the quieter of the two failures and the harder to find.</para>
    /// </summary>
    private static bool TryParseHsl(string inner, out SKColor color)
    {
        color = SKColors.Transparent;
        var parts = Components(inner);
        if (parts.Length < 3
            || !CssNumber.TryParse(parts[0].TrimEnd("deg".ToCharArray()), out var h)
            || !TryPercent(parts[1], out var sat)
            || !TryPercent(parts[2], out var light)) return false;

        var a = 255f;
        if (parts.Length >= 4 && CssNumber.TryParse(parts[3], out var af)) a = Math.Clamp(af, 0f, 1f) * 255f;

        // Hue wraps; saturation and lightness clamp.
        h = ((h % 360f) + 360f) % 360f;
        sat = Math.Clamp(sat, 0f, 1f);
        light = Math.Clamp(light, 0f, 1f);

        var c = (1f - Math.Abs(2f * light - 1f)) * sat;
        var x = c * (1f - Math.Abs(h / 60f % 2f - 1f));
        var m = light - c / 2f;
        var (rp, gp, bp) = h switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        color = new SKColor(Chan(rp + m), Chan(gp + m), Chan(bp + m), (byte)Math.Clamp(a, 0f, 255f));
        return true;

        static byte Chan(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
    }

    /// <summary>A percentage as 0..1, or a bare number treated the same way CSS does inside
    /// <c>hsl()</c> — where the unit is required, so a missing one is a refusal.</summary>
    private static bool TryPercent(string token, out float value)
    {
        value = 0f;
        if (!token.EndsWith('%')) return false;
        if (!CssNumber.TryParse(token[..^1], out var n)) return false;
        value = n / 100f;
        return true;
    }

    /// <summary>The arguments of a colour function. Commas, spaces and the alpha <c>/</c> all
    /// separate, so every spelling CSS allows lands as the same list.</summary>
    private static string[] Components(string inner) =>
        inner.Replace('/', ' ').Split([',', ' '],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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
    /// <summary>The same corners, grown by a uniform ring drawn outside the box.
    ///
    /// <para>A concentric ring round a rounded box has the LARGER radius -- grow the box by n and its
    /// corners grow by n too -- so an outline round a pill stays a pill instead of becoming a rounded
    /// rectangle with a square frame hanging off it. A square corner (0) stays square, which is what
    /// concentricity gives as well.</para>
    /// </summary>
    public CornerRadii Grow(float by)
    {
        if (by <= 0) return this;
        static SKPoint G(SKPoint c, float by) =>
            new(c.X > 0 ? c.X + by : 0, c.Y > 0 ? c.Y + by : 0);
        return new CornerRadii(G(TopLeft, by), G(TopRight, by), G(BottomRight, by), G(BottomLeft, by));
    }

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
