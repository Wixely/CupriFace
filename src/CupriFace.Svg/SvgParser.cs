using System.Globalization;
using AngleSharp.Dom;
using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Svg;

/// <summary>
/// An inline <c>&lt;svg&gt;</c> subtree, read into the flat list of shapes the engine paints.
///
/// <para><b>Scope, deliberately.</b> The shapes, groups and transforms that logos, icons, progress
/// rings and diagram lines are made of — which is what a survey of 187 designed compositions found
/// inline SVG being used for (#203). Gradients, filters, masks, patterns, text and SMIL are out;
/// see SVG-SUPPORT.md for what that costs and what it would take to add.</para>
///
/// <para>Nothing here parses path data or the markup: AngleSharp has already produced a namespaced
/// DOM, and Skia parses the <c>d</c> grammar. What is left is the part neither of them does —
/// turning shape elements into paths, resolving inherited presentation attributes, and composing
/// transforms down the tree.</para>
/// </summary>
public static class SvgParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Read an <c>&lt;svg&gt;</c> element, or null when it has no usable geometry.</summary>
    public static SvgDrawing? Parse(IElement svg)
    {
        ArgumentNullException.ThrowIfNull(svg);

        var viewBox = ReadViewBox(svg);
        var natural = ReadNaturalSize(svg);

        // No viewBox: the width/height become the coordinate space, which is what a browser does.
        viewBox ??= natural is { } n && n.W > 0 && n.H > 0
            ? SKRect.Create(0, 0, n.W, n.H)
            : null;
        if (viewBox is not { Width: > 0, Height: > 0 } box) return null;

        var shapes = new List<VectorShape>();
        var root = new Inherited(SKColors.Black, SKColors.Transparent, 1f, 1f, 1f,
                                 SKStrokeCap.Butt, SKStrokeJoin.Miter, null, 0f, false);
        foreach (var child in svg.Children) Walk(child, root, SKMatrix.Identity, shapes);

        return shapes.Count == 0 ? null : new SvgDrawing(box, natural, shapes);
    }

    // What flows down the tree. SVG's presentation attributes inherit, so a <g fill="red"> colours
    // every shape under it that does not say otherwise — and getting that wrong shows up as a logo
    // drawn entirely in black.
    private readonly record struct Inherited(
        SKColor Fill, SKColor Stroke, float StrokeWidth, float Opacity, float FillOpacity,
        SKStrokeCap Cap, SKStrokeJoin Join, float[]? Dash, float DashOffset, bool EvenOdd);

    private static void Walk(IElement el, Inherited inherited, SKMatrix parentTransform,
                             List<VectorShape> outp)
    {
        if (el.NamespaceUri is not null && !el.NamespaceUri.EndsWith("svg", StringComparison.Ordinal)) return;

        var state = Resolve(el, inherited);
        var transform = parentTransform;
        if (ParseTransform(el.GetAttribute("transform")) is { } own)
            transform = parentTransform.PreConcat(own);

        // display:none and visibility:hidden hide a subtree here the same way they do in CSS.
        var display = Attr(el, "display");
        if (string.Equals(display, "none", StringComparison.OrdinalIgnoreCase)) return;

        switch (el.LocalName.ToLowerInvariant())
        {
            case "g" or "svg":
                foreach (var child in el.Children) Walk(child, state, transform, outp);
                return;

            // Ignored on purpose rather than by omission: these carry no geometry of their own, and
            // silently walking into them would draw a <defs> twice.
            case "defs" or "title" or "desc" or "metadata" or "style" or "clippath" or "mask":
                return;

            default:
                if (PathDataFor(el) is { Length: > 0 } d) outp.Add(Shape(d, state, transform));
                return;
        }
    }

    private static VectorShape Shape(string pathData, Inherited s, SKMatrix transform) =>
        new(pathData,
            Fill: s.Fill.WithAlpha((byte)Math.Clamp(s.Fill.Alpha * s.FillOpacity, 0, 255)),
            Stroke: s.Stroke,
            StrokeWidth: s.StrokeWidth,
            Opacity: s.Opacity,
            EvenOdd: s.EvenOdd,
            DashArray: s.Dash,
            DashOffset: s.DashOffset,
            Cap: s.Cap,
            Join: s.Join,
            Transform: transform);

    // ---- shapes become paths --------------------------------------------------------------------

    /// <summary>Every supported shape as path data, so one command type draws them all. Skia already
    /// parses this grammar, which is why converting to it is cheaper than drawing each shape.</summary>
    private static string? PathDataFor(IElement el)
    {
        switch (el.LocalName.ToLowerInvariant())
        {
            case "path":
                return el.GetAttribute("d");

            case "rect":
            {
                float x = Num(el, "x"), y = Num(el, "y"), w = Num(el, "width"), h = Num(el, "height");
                if (w <= 0 || h <= 0) return null;
                var rx = el.HasAttribute("rx") ? Num(el, "rx") : Num(el, "ry");
                var ry = el.HasAttribute("ry") ? Num(el, "ry") : rx;
                rx = MathF.Min(rx, w / 2f); ry = MathF.Min(ry, h / 2f);
                if (rx <= 0 || ry <= 0)
                    return F($"M{x} {y}H{x + w}V{y + h}H{x}Z");
                // Rounded: four arcs, the corners CSS would draw.
                return F($"M{x + rx} {y}H{x + w - rx}A{rx} {ry} 0 0 1 {x + w} {y + ry}V{y + h - ry}A{rx} {ry} 0 0 1 {x + w - rx} {y + h}H{x + rx}A{rx} {ry} 0 0 1 {x} {y + h - ry}V{y + ry}A{rx} {ry} 0 0 1 {x + rx} {y}Z");
            }

            case "circle":
            {
                float cx = Num(el, "cx"), cy = Num(el, "cy"), r = Num(el, "r");
                return r <= 0 ? null : Ellipse(cx, cy, r, r);
            }

            case "ellipse":
            {
                float cx = Num(el, "cx"), cy = Num(el, "cy"), rx = Num(el, "rx"), ry = Num(el, "ry");
                return rx <= 0 || ry <= 0 ? null : Ellipse(cx, cy, rx, ry);
            }

            case "line":
                return F($"M{Num(el, "x1")} {Num(el, "y1")}L{Num(el, "x2")} {Num(el, "y2")}");

            case "polyline":
            case "polygon":
            {
                var pts = Points(el.GetAttribute("points"));
                if (pts.Count < 2) return null;
                var d = "M" + string.Join("L", pts.Select(p => F($"{p.X} {p.Y}")));
                return el.LocalName.Equals("polygon", StringComparison.OrdinalIgnoreCase) ? d + "Z" : d;
            }

            default:
                return null;   // an element with no geometry, or one this slice does not read
        }
    }

    // Two arcs rather than four: an ellipse is a closed shape and Skia's arc handling is exact, so
    // the half-turns meet without the seam four quarter-arcs can leave.
    private static string Ellipse(float cx, float cy, float rx, float ry) =>
        F($"M{cx - rx} {cy}A{rx} {ry} 0 1 0 {cx + rx} {cy}A{rx} {ry} 0 1 0 {cx - rx} {cy}Z");

    private static List<SKPoint> Points(string? raw)
    {
        var outp = new List<SKPoint>();
        if (string.IsNullOrWhiteSpace(raw)) return outp;
        var nums = raw.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < nums.Length; i += 2)
            if (float.TryParse(nums[i], NumberStyles.Float, Inv, out var x)
                && float.TryParse(nums[i + 1], NumberStyles.Float, Inv, out var y))
                outp.Add(new SKPoint(x, y));
        return outp;
    }

    // ---- presentation attributes -----------------------------------------------------------------

    private static Inherited Resolve(IElement el, Inherited p)
    {
        var fill = Attr(el, "fill") is { } f ? ParseColor(f, p.Fill) : p.Fill;
        var stroke = Attr(el, "stroke") is { } st ? ParseColor(st, p.Stroke) : p.Stroke;
        var width = Attr(el, "stroke-width") is { } sw && TryNum(sw, out var w) ? w : p.StrokeWidth;
        var opacity = Attr(el, "opacity") is { } o && TryNum(o, out var ov) ? p.Opacity * Math.Clamp(ov, 0f, 1f) : p.Opacity;
        var fillOpacity = Attr(el, "fill-opacity") is { } fo && TryNum(fo, out var fov) ? Math.Clamp(fov, 0f, 1f) : p.FillOpacity;
        var cap = Attr(el, "stroke-linecap") switch
        {
            "round" => SKStrokeCap.Round,
            "square" => SKStrokeCap.Square,
            "butt" => SKStrokeCap.Butt,
            _ => p.Cap,
        };
        var join = Attr(el, "stroke-linejoin") switch
        {
            "round" => SKStrokeJoin.Round,
            "bevel" => SKStrokeJoin.Bevel,
            "miter" => SKStrokeJoin.Miter,
            _ => p.Join,
        };
        var dash = Attr(el, "stroke-dasharray") is { } da ? ParseDash(da) : p.Dash;
        var dashOffset = Attr(el, "stroke-dashoffset") is { } doff && TryNum(doff, out var dv) ? dv : p.DashOffset;
        var evenOdd = Attr(el, "fill-rule") switch
        {
            "evenodd" => true,
            "nonzero" => false,
            _ => p.EvenOdd,
        };
        if (string.Equals(Attr(el, "visibility"), "hidden", StringComparison.OrdinalIgnoreCase))
            opacity = 0f;

        return new Inherited(fill, stroke, width, opacity, fillOpacity, cap, join, dash, dashOffset, evenOdd);
    }

    /// <summary>A presentation attribute, or the same name out of an inline <c>style</c> — authors
    /// and export tools use both, often in the same file.</summary>
    private static string? Attr(IElement el, string name)
    {
        if (el.GetAttribute("style") is { Length: > 0 } style)
        {
            foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                if (decl.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return decl[(colon + 1)..].Trim();
            }
        }
        var v = el.GetAttribute(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static SKColor ParseColor(string raw, SKColor inherited)
    {
        raw = raw.Trim();
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return SKColors.Transparent;
        if (raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return inherited;
        // url(#gradient) and the rest of paint-server land: out of scope, and drawing it in black
        // would be worse than not drawing it.
        if (raw.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return SKColors.Transparent;
        return Style.Colors.TryParse(raw, out var c) ? c : inherited;
    }

    private static float[]? ParseDash(string raw)
    {
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<float>();
        foreach (var part in parts)
            if (float.TryParse(part, NumberStyles.Float, Inv, out var n) && n >= 0) outp.Add(n);
        if (outp.Count == 0) return null;
        // Skia needs an even count; SVG repeats an odd list to make it even, as CSS does.
        if (outp.Count % 2 == 1) outp.AddRange(outp.ToArray());
        return [.. outp];
    }

    // ---- transforms --------------------------------------------------------------------------------

    /// <summary>The <c>transform</c> attribute's function list, composed left to right.</summary>
    internal static SKMatrix? ParseTransform(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = SKMatrix.Identity;
        var any = false;

        var i = 0;
        while (i < raw.Length)
        {
            var open = raw.IndexOf('(', i);
            if (open < 0) break;
            var close = raw.IndexOf(')', open);
            if (close < 0) break;

            var fn = raw[i..open].Trim(' ', ',', '\t', '\n', '\r').ToLowerInvariant();
            var args = raw[(open + 1)..close]
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(a => float.TryParse(a, NumberStyles.Float, Inv, out var n) ? n : 0f)
                .ToArray();
            i = close + 1;

            var step = fn switch
            {
                "translate" when args.Length >= 1 =>
                    SKMatrix.CreateTranslation(args[0], args.Length > 1 ? args[1] : 0f),
                "scale" when args.Length >= 1 =>
                    SKMatrix.CreateScale(args[0], args.Length > 1 ? args[1] : args[0]),
                "rotate" when args.Length >= 3 => SKMatrix.CreateRotationDegrees(args[0], args[1], args[2]),
                "rotate" when args.Length >= 1 => SKMatrix.CreateRotationDegrees(args[0]),
                "skewx" when args.Length >= 1 => SKMatrix.CreateSkew(MathF.Tan(args[0] * MathF.PI / 180f), 0f),
                "skewy" when args.Length >= 1 => SKMatrix.CreateSkew(0f, MathF.Tan(args[0] * MathF.PI / 180f)),
                "matrix" when args.Length >= 6 =>
                    new SKMatrix { ScaleX = args[0], SkewY = args[1], SkewX = args[2],
                                   ScaleY = args[3], TransX = args[4], TransY = args[5], Persp2 = 1f },
                _ => (SKMatrix?)null,
            };
            if (step is { } sm) { m = m.PreConcat(sm); any = true; }
        }
        return any ? m : null;
    }

    // ---- the root element's own geometry ------------------------------------------------------------

    private static SKRect? ReadViewBox(IElement svg)
    {
        var raw = svg.GetAttribute("viewBox") ?? svg.GetAttribute("viewbox");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var n = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (n.Length < 4) return null;
        var v = new float[4];
        for (var i = 0; i < 4; i++)
            if (!float.TryParse(n[i], NumberStyles.Float, Inv, out v[i])) return null;
        return v[2] <= 0 || v[3] <= 0 ? null : SKRect.Create(v[0], v[1], v[2], v[3]);
    }

    private static (float W, float H)? ReadNaturalSize(IElement svg)
    {
        var w = Length(svg.GetAttribute("width"));
        var h = Length(svg.GetAttribute("height"));
        return w is > 0 && h is > 0 ? (w.Value, h.Value) : null;

        static float? Length(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            if (raw.EndsWith('%')) return null;                 // relative to a box we do not have here
            if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
            return float.TryParse(raw, NumberStyles.Float, Inv, out var n) ? n : null;
        }
    }

    // ---- tiny helpers --------------------------------------------------------------------------------

    private static float Num(IElement el, string name) =>
        TryNum(el.GetAttribute(name), out var n) ? n : 0f;

    private static bool TryNum(string? raw, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
        return float.TryParse(raw, NumberStyles.Float, Inv, out value);
    }

    /// <summary>Path data built with the invariant culture. A comma decimal separator would turn
    /// <c>M1.5 2</c> into <c>M1,5 2</c>, which parses as different coordinates entirely — the kind of
    /// bug that only appears on someone else's machine.</summary>
    private static string F(FormattableString s) => s.ToString(Inv);
}
