using SkiaSharp;
using System.Text.RegularExpressions;
using CupriFace.Dom;

namespace CupriFace.Style;

public sealed record Keyframe(float Offset, Dictionary<string, string> Declarations);

/// <summary>The animatable values of a style before any keyframe touched them. An animation that is
/// not applying a frame — waiting out its delay without a backwards fill, or finished without a
/// forwards fill — puts these back, so seeking to any time gives that time's frame and never the
/// previous call's.</summary>
internal sealed record AnimationBase(float Opacity, bool HasTransform, float TranslateX, float TranslateY,
    float RotateDeg, float ScaleX, float ScaleY, Length Width, Length Height,
    float TranslateXPct = 0f, float TranslateYPct = 0f,
    float RotateXDeg = 0f, float RotateYDeg = 0f, float TranslateZ = 0f, float PerspectiveFn = 0f,
    SKColor? SvgFill = null, SKColor? SvgStroke = null, float? SvgStrokeWidth = null,
    float? SvgStrokeDashOffset = null, float? SvgFillOpacity = null, float? SvgStrokeOpacity = null,
    ClipShape? ClipPath = null);

/// <summary>
/// Parses <c>@keyframes</c> blocks and applies time-sampled animation overrides to the
/// render tree. Animatable properties: transform + opacity (paint-only), and width +
/// height — those write a definite length that the frame's layout honours, the same road
/// a <c>transition: height</c> takes. Layout always follows Animate (host order:
/// Animate → BuildFrame), so an animated size reflows the element and its siblings.
/// </summary>
public static partial class Animation
{
    [GeneratedRegex(@"@keyframes\s+([A-Za-z_][\w-]*)\s*\{", RegexOptions.Singleline)]
    private static partial Regex KeyframesHeader();

    /// <summary>Extract all @keyframes rules from a stylesheet.</summary>
    public static Dictionary<string, List<Keyframe>> Parse(string? css)
    {
        var map = new Dictionary<string, List<Keyframe>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(css)) return map;

        // The same stripping the rest of the stylesheet gets. Without it a comment between two
        // stops rides into the next stop's selector, which then parses as no percentage and is
        // dropped — see CssParser.StripComments (#184).
        css = CssParser.StripComments(css);

        foreach (Match header in KeyframesHeader().Matches(css))
        {
            var name = header.Groups[1].Value;
            var bodyStart = header.Index + header.Length;
            var body = ExtractBraced(css, bodyStart);
            if (body is null) continue;

            var frames = new List<Keyframe>();
            foreach (Match step in Regex.Matches(body, @"([^{}]+)\{([^{}]*)\}"))
            {
                var decls = CssParser.ParseDeclarations(step.Groups[2].Value);
                foreach (var sel in step.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var offset = sel.Equals("from", StringComparison.OrdinalIgnoreCase) ? 0f
                        : sel.Equals("to", StringComparison.OrdinalIgnoreCase) ? 1f
                        : CssNumber.TryParse(sel.TrimEnd('%'), out var p) ? p / 100f : -1f;
                    if (offset >= 0) frames.Add(new Keyframe(offset, decls));
                }
            }
            frames.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            if (frames.Count > 0) map[name] = frames;
        }
        return map;
    }

    private static string? ExtractBraced(string s, int open)
    {
        var depth = 1;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return s[open..i];
        }
        return null;
    }

    /// <summary>Apply animation overrides to the tree for the given elapsed time. Returns true while
    /// any animation is still RUNNING — a later frame would differ: waiting out its delay, or inside
    /// its iterations. A finished animation (its count of iterations elapsed) holds its last frame if
    /// it fills forwards, otherwise reverts, and either way contributes nothing to the return.</summary>
    public static bool Apply(RenderNode root, Dictionary<string, List<Keyframe>> keyframes, double timeSeconds)
    {
        if (keyframes.Count == 0) return false;
        return Walk(root, keyframes, timeSeconds);
    }

    private static bool Walk(RenderNode node, Dictionary<string, List<Keyframe>> keyframes, double t)
    {
        var running = false;
        var s = node.Style;
        if (s.Animations is { Count: > 0 } specs)
        {
            // The base is captured ONCE, before any entry writes to the style — otherwise the
            // second animation of a list would record the first one's output as the element's
            // resting state (#284).
            s.AnimBase ??= Capture(s);
            // …and the frame is built from that base rather than on top of the last frame's writes,
            // so an entry that stops applying stops showing.
            Restore(s);

            // In order, because CSS gives the LAST entry to touch a property the final say. Each
            // writes only the properties its own keyframes name, so a slide and a fade compose.
            foreach (var a in specs)
            {
                if (a.Name is not { } name || a.Duration <= 0 || !keyframes.TryGetValue(name, out var frames)) continue;
                // The clock is ABSOLUTE document time, not time since the element appeared: the same
                // t gives the same frame whichever order frames are asked for (CupriCut renders out
                // of order; a UI never notices).
                var local = t - a.Delay;
                if (local < 0)
                {
                    if (a.FillBackwards) ApplyFrame(s, frames, 0f, a);
                    running = true;
                }
                else
                {
                    var cycles = local / a.Duration;
                    if (cycles >= a.Iterations)
                    {
                        if (a.FillForwards) ApplyFrame(s, frames, 1f, a);
                    }
                    else
                    {
                        ApplyFrame(s, frames, (float)(cycles % 1.0), a);
                        running = true;
                    }
                }
            }
        }
        foreach (var c in node.Children) if (Walk(c, keyframes, t)) running = true;
        return running;
    }

    /// <summary>The animatable values as they stand before any keyframe touches them.</summary>
    private static AnimationBase Capture(ComputedStyle s) => new(
        s.Opacity, s.HasTransform, s.TranslateX, s.TranslateY, s.RotateDeg, s.ScaleX, s.ScaleY, s.Width, s.Height,
        s.TranslateXPct, s.TranslateYPct,
        s.RotateXDeg, s.RotateYDeg, s.TranslateZ, s.PerspectiveFn,
        s.SvgFill, s.SvgStroke, s.SvgStrokeWidth, s.SvgStrokeDashOffset, s.SvgFillOpacity, s.SvgStrokeOpacity,
        s.ClipPath);

    private static void Restore(ComputedStyle s)
    {
        if (s.AnimBase is not { } b) return;
        s.Opacity = b.Opacity; s.HasTransform = b.HasTransform;
        s.TranslateX = b.TranslateX; s.TranslateY = b.TranslateY; s.RotateDeg = b.RotateDeg;
        s.TranslateXPct = b.TranslateXPct; s.TranslateYPct = b.TranslateYPct;
        s.ScaleX = b.ScaleX; s.ScaleY = b.ScaleY; s.Width = b.Width; s.Height = b.Height;
        s.RotateXDeg = b.RotateXDeg; s.RotateYDeg = b.RotateYDeg; s.TranslateZ = b.TranslateZ; s.PerspectiveFn = b.PerspectiveFn;
        s.SvgFill = b.SvgFill; s.SvgStroke = b.SvgStroke; s.SvgStrokeWidth = b.SvgStrokeWidth;
        s.SvgStrokeDashOffset = b.SvgStrokeDashOffset; s.SvgFillOpacity = b.SvgFillOpacity; s.SvgStrokeOpacity = b.SvgStrokeOpacity;
        s.ClipPath = b.ClipPath;
    }

    private static void ApplyFrame(ComputedStyle s, List<Keyframe> frames, float progress, AnimationSpec spec)
    {
        s.AnimBase ??= Capture(s);
        // Find bracketing keyframes.
        Keyframe a = frames[0], b = frames[^1];
        for (var i = 0; i < frames.Count - 1; i++)
        {
            if (progress >= frames[i].Offset && progress <= frames[i + 1].Offset)
            {
                a = frames[i]; b = frames[i + 1]; break;
            }
        }
        var span = b.Offset - a.Offset;
        var local = span > 0 ? (progress - a.Offset) / span : 0f;
        // CLAMPED, because nothing outside the stops is ever animated to. When progress falls
        // outside every bracketing pair the loop above leaves a and b as the first and last frames,
        // and an unclamped fraction then EXTRAPOLATES past the value the author wrote — a bar
        // declared to end at 545px settled at 714px and held there. That only happened because a
        // comment had silently dropped the stops in between (#184), but extrapolating past the last
        // keyframe is wrong however it is reached.
        local = Math.Clamp(local, 0f, 1f);
        // The timing function shapes each interval between two stops, which is what CSS says it
        // does. Until now it was parsed and discarded, so every animation ran linearly.
        local = spec.Timing.Eval(local);

        var from = new ComputedStyle();
        var to = new ComputedStyle();
        StyleResolver.ApplyDeclarations(from, a.Declarations);
        StyleResolver.ApplyDeclarations(to, b.Declarations);

        if (from.HasTransform || to.HasTransform)
        {
            s.HasTransform = true;
            s.TranslateX = Lerp(from.TranslateX, to.TranslateX, local);
            s.TranslateY = Lerp(from.TranslateY, to.TranslateY, local);
            // The percentage part interpolates in its own unit, as a width does, and resolves
            // against the box at paint time — a stop written as `translateX(-50%)` used to be
            // read as 0px, so the whole run slid to the wrong place (#258).
            s.TranslateXPct = Lerp(from.TranslateXPct, to.TranslateXPct, local);
            s.TranslateYPct = Lerp(from.TranslateYPct, to.TranslateYPct, local);
            s.RotateDeg = Lerp(from.RotateDeg, to.RotateDeg, local);
            s.ScaleX = Lerp(from.ScaleX, to.ScaleX, local);
            s.ScaleY = Lerp(from.ScaleY, to.ScaleY, local);
            // A flip, a tilt, an orbit (#269).
            s.RotateXDeg = Lerp(from.RotateXDeg, to.RotateXDeg, local);
            s.RotateYDeg = Lerp(from.RotateYDeg, to.RotateYDeg, local);
            s.TranslateZ = Lerp(from.TranslateZ, to.TranslateZ, local);
            s.PerspectiveFn = Lerp(from.PerspectiveFn, to.PerspectiveFn, local);
        }
        if (a.Declarations.ContainsKey("opacity") || b.Declarations.ContainsKey("opacity"))
            s.Opacity = Lerp(from.Opacity, to.Opacity, local);

        // Layout properties: the declarations were already parsed into `from`/`to` (width included) —
        // they were just never read. Write the interpolated value as a definite length and the frame's
        // layout picks it up; this was the gap that held a keyframed bar at its start width for the
        // whole run while the engine reported the animation active (#56).
        if (a.Declarations.ContainsKey("width") || b.Declarations.ContainsKey("width"))
            s.Width = LerpLength(from.Width, to.Width, local);
        if (a.Declarations.ContainsKey("height") || b.Declarations.ContainsKey("height"))
            s.Height = LerpLength(from.Height, to.Height, local);

        // SVG paint on a shape inside an inline <svg> (#262): a heart that fills, a path that
        // draws on through its dash offset. A stop that names only one end of a pair holds the
        // element's own value at the other, the way the opacity above does with the base style.
        if (Declared(a, b, "fill")) s.SvgFill = LerpColor(from.SvgFill ?? s.AnimBase.SvgFill, to.SvgFill ?? s.AnimBase.SvgFill, local);
        if (Declared(a, b, "stroke")) s.SvgStroke = LerpColor(from.SvgStroke ?? s.AnimBase.SvgStroke, to.SvgStroke ?? s.AnimBase.SvgStroke, local);
        if (Declared(a, b, "stroke-width")) s.SvgStrokeWidth = Lerp(from.SvgStrokeWidth ?? s.AnimBase.SvgStrokeWidth ?? 1f, to.SvgStrokeWidth ?? s.AnimBase.SvgStrokeWidth ?? 1f, local);
        if (Declared(a, b, "stroke-dashoffset")) s.SvgStrokeDashOffset = Lerp(from.SvgStrokeDashOffset ?? s.AnimBase.SvgStrokeDashOffset ?? 0f, to.SvgStrokeDashOffset ?? s.AnimBase.SvgStrokeDashOffset ?? 0f, local);
        if (Declared(a, b, "fill-opacity")) s.SvgFillOpacity = Lerp(from.SvgFillOpacity ?? s.AnimBase.SvgFillOpacity ?? 1f, to.SvgFillOpacity ?? s.AnimBase.SvgFillOpacity ?? 1f, local);
        if (Declared(a, b, "stroke-opacity")) s.SvgStrokeOpacity = Lerp(from.SvgStrokeOpacity ?? s.AnimBase.SvgStrokeOpacity ?? 1f, to.SvgStrokeOpacity ?? s.AnimBase.SvgStrokeOpacity ?? 1f, local);

        // A wipe, an iris, a mask reveal (#268): the shape's numbers interpolate between stops of
        // the same kind. A stop that omits it holds the element's own shape, and `none` at either
        // end is not interpolable, so the pair flips at the midpoint.
        if (Declared(a, b, "clip-path"))
        {
            var ca = a.Declarations.ContainsKey("clip-path") ? from.ClipPath : s.AnimBase.ClipPath;
            var cb = b.Declarations.ContainsKey("clip-path") ? to.ClipPath : s.AnimBase.ClipPath;
            s.ClipPath = ca is null || cb is null ? (local < 0.5f ? ca : cb) : ClipShape.Lerp(ca, cb, local);
        }
    }

    private static bool Declared(Keyframe a, Keyframe b, string prop) =>
        a.Declarations.ContainsKey(prop) || b.Declarations.ContainsKey(prop);

    private static SKColor? LerpColor(SKColor? a, SKColor? b, float t)
    {
        if (a is not { } x) return b;
        if (b is not { } y) return a;
        return new SKColor(
            Byte(x.Red + (y.Red - x.Red) * t), Byte(x.Green + (y.Green - x.Green) * t),
            Byte(x.Blue + (y.Blue - x.Blue) * t), Byte(x.Alpha + (y.Alpha - x.Alpha) * t));
    }
    private static byte Byte(float f) => (byte)Math.Clamp((int)MathF.Round(f), 0, 255);

    // Same-unit px or % pairs interpolate; anything else — auto (an endpoint that omitted the
    // property), mixed units — flips at the midpoint, which is CSS's behaviour for a
    // non-interpolable pair. A % stays a % and resolves in layout, where the containing block is
    // actually known.
    private static Length LerpLength(Length a, Length b, float t)
    {
        if (a.Unit == b.Unit && a.Unit is LengthUnit.Px or LengthUnit.Percent)
            return new Length(a.Unit, Lerp(a.Value, b.Value, t));
        return t < 0.5f ? a : b;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
