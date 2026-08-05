using System.Text;
using CupriFace.Dom;
using SkiaSharp;

namespace CupriFace.Style;

/// <summary>The paint-only properties a CSS <c>transition</c> can animate. All are read/written on
/// <see cref="ComputedStyle"/> and none affect layout, so a transition re-paints without re-laying-out
/// (like <c>@keyframes</c>).</summary>
public enum TransProp { Opacity, Background, Color, BorderColor, Transform }

public enum EasingKind { Linear, Bezier }

/// <summary>A CSS timing function. Keywords (<c>ease</c>/<c>ease-in</c>/…) resolve to their standard
/// cubic-bézier control points; <c>linear</c> is a fast path. (<c>cubic-bezier()</c> literals are a
/// follow-up — the comma parsing collides with the transition-list separator.)</summary>
public readonly record struct Easing(EasingKind Kind, float X1, float Y1, float X2, float Y2)
{
    public static readonly Easing Linear = new(EasingKind.Linear, 0, 0, 1, 1);
    public static readonly Easing Ease = new(EasingKind.Bezier, 0.25f, 0.1f, 0.25f, 1f);
    public static readonly Easing EaseIn = new(EasingKind.Bezier, 0.42f, 0f, 1f, 1f);
    public static readonly Easing EaseOut = new(EasingKind.Bezier, 0f, 0f, 0.58f, 1f);
    public static readonly Easing EaseInOut = new(EasingKind.Bezier, 0.42f, 0f, 0.58f, 1f);

    public static Easing? FromKeyword(string k) => k switch
    {
        "linear" => Linear,
        "ease" => Ease,
        "ease-in" => EaseIn,
        "ease-out" => EaseOut,
        "ease-in-out" => EaseInOut,
        _ => null,
    };

    /// <summary>Map animation progress <paramref name="t"/> (0..1 in time) to eased progress
    /// (0..1 in value): solve x(s)=t on the bézier, then return y(s).</summary>
    public float Eval(float t)
    {
        if (Kind == EasingKind.Linear || t <= 0f || t >= 1f) return Math.Clamp(t, 0f, 1f);
        var s = t; // Newton-Raphson on x(s) = t (a few iterations converge for CSS curves)
        for (var i = 0; i < 6; i++)
        {
            var dx = Deriv(s, X1, X2);
            if (MathF.Abs(dx) < 1e-6f) break;
            s = Math.Clamp(s - (Curve(s, X1, X2) - t) / dx, 0f, 1f);
        }
        return Curve(s, Y1, Y2);
    }

    // Cubic bézier with endpoints P0=0, P3=1 and control values p1,p2.
    private static float Curve(float t, float p1, float p2)
    {
        float mt = 1 - t;
        return 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t;
    }
    private static float Deriv(float t, float p1, float p2)
    {
        float mt = 1 - t;
        return 3 * mt * mt * p1 + 6 * mt * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }
}

/// <summary>One parsed <c>transition</c> entry: which property (or <c>all</c>), how long, after what
/// delay, with what easing.</summary>
public readonly record struct TransitionSpec(string Property, float Duration, float Delay, Easing Easing);

/// <summary>
/// Drives CSS <c>transition</c>s: when a transitioned property's target value changes between style
/// resolutions (hover, class/model change, focus), it animates from the currently-displayed value to
/// the new target over the declared duration/easing. State is keyed by structural path (like scroll
/// state) so it survives the per-interaction rebuild that re-parses the DOM.
/// </summary>
public sealed class TransitionEngine
{
    private sealed class St
    {
        public float[] From = [], To = [], Current = [];
        public double Start = double.NaN; // set on the first applied frame (NaN = pending)
        public float Duration, Delay;
        public Easing Easing;
        public bool Active;
        public bool Seen;
    }

    private static readonly TransProp[] AllProps = [TransProp.Opacity, TransProp.Background, TransProp.Color, TransProp.BorderColor, TransProp.Transform];
    private readonly Dictionary<string, St> _states = new();

    /// <summary>True while any transition is mid-flight (drives the host's continuous repaint).</summary>
    public bool Active { get; private set; }

    /// <summary>After a style resolution: diff each transitioned property's target against the last one
    /// and (re)start a transition where it changed. Prunes state for nodes that vanished.</summary>
    public void Detect(RenderNode root)
    {
        foreach (var st in _states.Values) st.Seen = false;
        Walk(root);
        List<string>? drop = null;
        foreach (var (k, st) in _states) if (!st.Seen) (drop ??= []).Add(k);
        if (drop is not null) foreach (var k in drop) _states.Remove(k);
        Recompute();
    }

    private void Walk(RenderNode n)
    {
        if (n.Style.Transitions is { Count: > 0 } specs)
        {
            var path = PathOf(n);
            foreach (var p in AllProps)
            {
                if (Resolve(specs, p) is not { } spec) continue;
                var key = $"{path}|{(int)p}";
                var target = Read(n.Style, p);
                if (_states.TryGetValue(key, out var st))
                {
                    st.Seen = true;
                    st.Duration = spec.Duration; st.Delay = spec.Delay; st.Easing = spec.Easing;
                    if (!Approx(target, st.To)) // target changed → animate from the current displayed value
                    {
                        st.From = st.Current; st.To = target; st.Start = double.NaN;
                        st.Active = spec.Duration > 0;
                        if (!st.Active) st.Current = target;
                    }
                }
                else // first sight of this property → baseline, no animation on initial style
                {
                    _states[key] = new St
                    {
                        From = target, To = target, Current = target, Seen = true,
                        Duration = spec.Duration, Delay = spec.Delay, Easing = spec.Easing,
                    };
                }
            }
        }
        foreach (var c in n.Children) Walk(c);
    }

    /// <summary>Each frame before paint: write the interpolated value of every in-flight transition
    /// onto its node's style. Returns whether anything is still animating.</summary>
    public bool Apply(RenderNode root, double now)
    {
        if (_states.Count > 0) ApplyWalk(root, now);
        Recompute();
        return Active;
    }

    private void ApplyWalk(RenderNode n, double now)
    {
        if (n.Style.Transitions is { Count: > 0 })
        {
            var path = PathOf(n);
            foreach (var p in AllProps)
            {
                if (!_states.TryGetValue($"{path}|{(int)p}", out var st) || !st.Active) continue;
                if (double.IsNaN(st.Start)) st.Start = now;
                var u = st.Duration > 0 ? (float)((now - st.Start - st.Delay) / st.Duration) : 1f;
                if (u <= 0f) { Write(n.Style, p, st.From); continue; }      // delay phase: hold the start value
                if (u >= 1f) { st.Current = st.To; st.Active = false; Write(n.Style, p, st.To); continue; }
                st.Current = Lerp(st.From, st.To, st.Easing.Eval(u));
                Write(n.Style, p, st.Current);
            }
        }
        foreach (var c in n.Children) ApplyWalk(c, now);
    }

    private void Recompute()
    {
        Active = false;
        foreach (var st in _states.Values) if (st.Active) { Active = true; break; }
    }

    // The effective spec for a property: the last matching entry (an explicit name or `all`) wins.
    private static TransitionSpec? Resolve(List<TransitionSpec> specs, TransProp p)
    {
        var name = Name(p);
        TransitionSpec? found = null;
        foreach (var sp in specs) if (sp.Property == name || sp.Property == "all") found = sp;
        return found;
    }

    private static string Name(TransProp p) => p switch
    {
        TransProp.Opacity => "opacity",
        TransProp.Background => "background",
        TransProp.Color => "color",
        TransProp.BorderColor => "border-color",
        _ => "transform",
    };

    private static float[] Read(ComputedStyle s, TransProp p) => p switch
    {
        TransProp.Opacity => [s.Opacity],
        TransProp.Background => Channels(s.Background),
        TransProp.Color => Channels(s.Color),
        TransProp.BorderColor => Channels(s.BorderColor),
        _ => [s.TranslateX, s.TranslateY, s.ScaleX, s.ScaleY, s.RotateDeg],
    };

    private static void Write(ComputedStyle s, TransProp p, float[] v)
    {
        switch (p)
        {
            case TransProp.Opacity: s.Opacity = Math.Clamp(v[0], 0f, 1f); break;
            case TransProp.Background: s.Background = Color(v); break;
            case TransProp.Color: s.Color = Color(v); break;
            case TransProp.BorderColor: s.BorderColor = Color(v); break;
            default:
                s.TranslateX = v[0]; s.TranslateY = v[1]; s.ScaleX = v[2]; s.ScaleY = v[3]; s.RotateDeg = v[4];
                s.HasTransform = v[0] != 0 || v[1] != 0 || v[2] != 1f || v[3] != 1f || v[4] != 0;
                break;
        }
    }

    private static float[] Channels(SKColor c) => [c.Red, c.Green, c.Blue, c.Alpha];
    private static SKColor Color(float[] v) => new(Byte(v[0]), Byte(v[1]), Byte(v[2]), Byte(v[3]));
    private static byte Byte(float f) => (byte)Math.Clamp((int)MathF.Round(f), 0, 255);

    private static float[] Lerp(float[] a, float[] b, float t)
    {
        var r = new float[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * t;
        return r;
    }
    private static bool Approx(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (MathF.Abs(a[i] - b[i]) > 0.001f) return false;
        return true;
    }

    // Structural path from the root (child-index chain) — stable across the DOM re-parse, matching
    // how scroll/resize state is keyed in CupriDocument.
    private static string PathOf(RenderNode n)
    {
        var sb = new StringBuilder();
        for (var cur = n; cur.Parent is { } p; cur = p) sb.Insert(0, "/" + p.Children.IndexOf(cur));
        return sb.ToString();
    }
}
