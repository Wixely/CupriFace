using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// Damage tracking for hosts that retain their canvas between frames: given the previous and next
/// <see cref="DisplayList"/> commands, computes the device-space rectangle that actually changed, so
/// the rasteriser can clip to it and the host can present just that region. Commands are records with
/// absolute geometry, so consecutive frames of a mostly-static UI are value-identical outside the
/// changed span. Conservative by construction — any situation whose bounds can't be proven returns
/// the full viewport (a correct, merely unoptimised, repaint).
/// </summary>
public static class DamageDiff
{
    /// <summary>The union bounds of what changed, in device space. <see cref="SKRect.Empty"/> when the
    /// frames are identical (nothing to repaint or present); the full viewport when there is no previous
    /// frame or bounds can't be computed safely (unbalanced scopes, unbounded layers).</summary>
    public static SKRect Compute(IReadOnlyList<PaintCommand>? prev, IReadOnlyList<PaintCommand> next,
        float width, float height)
    {
        var full = SKRect.Create(0, 0, width, height);
        if (prev is null) return full;

        // Trim the equal prefix, then the equal suffix (not crossing the prefix).
        int na = prev.Count, nb = next.Count, min = Math.Min(na, nb);
        var head = 0;
        while (head < min && Eq(prev[head], next[head])) head++;
        if (head == na && head == nb) return SKRect.Empty;              // identical frames

        int ta = na - 1, tb = nb - 1;
        while (ta >= head && tb >= head && Eq(prev[ta], next[tb])) { ta--; tb--; }

        // A shader whose uniforms changed (its clock ticked) differs at its PushShader and at
        // nothing else: the commands inside it and its PopShader all fall in the equal suffix,
        // which leaves a span that opens a scope and never closes it. Give the span its close
        // back, so an animated shader damages its own box instead of the whole viewport.
        ta = CloseShaders(prev, head, ta);
        tb = CloseShaders(next, head, tb);

        // Suffix pairing is positionally valid only if each middle span is push/pop balanced —
        // otherwise value-equal suffix commands would paint under different transform/clip scopes.
        if (!Balanced(prev, head, ta) || !Balanced(next, head, tb)) return full;

        // Transform state at the span start (the prefixes are identical, so one scan serves both).
        // Likewise the shader groups already open there: a change INSIDE one repaints all of it,
        // because a shader may read its input anywhere and move it anywhere within its box.
        var m = SKMatrix.Identity;
        var stack = new List<SKMatrix>();
        var enclosing = SKRect.Empty;
        var shaderDepth = 0;
        for (var i = 0; i < head; i++)
        {
            Track(prev[i], ref m, stack);
            if (prev[i] is PushShader ps)
            {
                if (shaderDepth++ == 0) enclosing = m.MapRect(SKRect.Create(ps.X, ps.Y, ps.W, ps.H));
            }
            else if (prev[i] is PopShader && shaderDepth > 0) shaderDepth--;
        }

        var damage = SKRect.Empty;
        if (!SpanBounds(prev, head, ta, m, ref damage)) return full;
        if (!SpanBounds(next, head, tb, m, ref damage)) return full;
        if (damage.IsEmpty) return SKRect.Empty;
        if (shaderDepth > 0) damage = SKRect.Union(damage, enclosing);

        damage.Inflate(2, 2); // anti-aliasing + rounding slack
        damage.Intersect(full);
        return damage;
    }

    /// <summary>The subset of <paramref name="cmds"/> a repaint clipped to <paramref name="rect"/>
    /// actually needs: every stack op (Push*/Pop* — state must replay in order, and layer pairs
    /// must stay balanced) plus every painting command whose transform-mapped bounds intersect the
    /// rect. Built for the surface fast path: replaying the FULL retained list per video frame
    /// makes Skia clip-reject hundreds of draws — the submission walk itself was the cost.</summary>
    public static IReadOnlyList<PaintCommand> CullTo(IReadOnlyList<PaintCommand> cmds, SKRect rect)
    {
        // Pass 1: keep stack ops + intersecting painting commands.
        var kept = new List<PaintCommand>(64);
        var m = SKMatrix.Identity;
        var stack = new List<SKMatrix>();
        var shaderDepth = 0;
        foreach (var cmd in cmds)
        {
            Track(cmd, ref m, stack);
            // A shader group is kept WHOLE: its output inside the rect may be drawn from content
            // outside it (a ripple, a magnifier), so nothing in it can be culled by position. The
            // push and pop are painting commands as far as pass 2 is concerned, and never elided.
            if (cmd is PushShader) shaderDepth++;
            if (shaderDepth > 0)
            {
                kept.Add(cmd);
                if (cmd is PopShader) shaderDepth--;
                continue;
            }
            if (cmd is PushClip or PushClipShape or PopClip or PushTransform or PopTransform
                    or PushOpacity or PopOpacity or PushFilter or PopFilter)
            {
                kept.Add(cmd);                        // state/layer op — pruned in pass 2 if empty
                continue;
            }
            var b = LocalBounds(cmd);
            if (b is null) { kept.Add(cmd); continue; }   // unboundable: keep, conservative
            // MapRect is a native call — skip it under the identity transform (almost every
            // command on a typical page; this walk runs per fast-path video frame).
            var mapped = m.IsIdentity ? b.Value : m.MapRect(b.Value);
            mapped.Inflate(2, 2);                     // the same AA slack Compute uses
            if (mapped.IntersectsWith(rect)) kept.Add(cmd);
        }

        // Pass 2: elide scope groups left with NOTHING inside — a page's every clip pair would
        // otherwise survive as hundreds of native save/clip/restore calls per video frame.
        var drop = new bool[kept.Count];
        var scopes = new List<(int Index, bool HasPaint)>();
        for (var i = 0; i < kept.Count; i++)
        {
            switch (kept[i])
            {
                case PushClip or PushClipShape or PushTransform or PushOpacity or PushFilter:
                    scopes.Add((i, false));
                    break;
                case PopClip or PopTransform or PopOpacity or PopFilter when scopes.Count > 0:
                    var (open, hasPaint) = scopes[^1];
                    scopes.RemoveAt(scopes.Count - 1);
                    if (!hasPaint) { drop[open] = true; drop[i] = true; }
                    else if (scopes.Count > 0) scopes[^1] = (scopes[^1].Index, true); // parent has content
                    break;
                default:
                    if (scopes.Count > 0) scopes[^1] = (scopes[^1].Index, true);
                    break;
            }
        }
        var outp = new List<PaintCommand>(kept.Count);
        for (var i = 0; i < kept.Count; i++) if (!drop[i]) outp.Add(kept[i]);
        return outp;
    }

    // Value equality, with the two commands whose record equality degrades to reference equality
    // (they hold lists) compared element-wise — otherwise charts/filters would look changed every frame.
    private static bool Eq(PaintCommand a, PaintCommand b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.GetType() != b.GetType()) return false;
        return a switch
        {
            Polyline pa when b is Polyline pb =>
                pa.Width == pb.Width && pa.Stroke == pb.Stroke && pa.Fill == pb.Fill
                && pa.BaseY == pb.BaseY && pa.Curved == pb.Curved && SeqEq(pa.Points, pb.Points),
            PushFilter fa when b is PushFilter fb =>
                fa.X == fb.X && fa.Y == fb.Y && fa.W == fb.W && fa.H == fb.H && OpsEq(fa.Ops, fb.Ops),
            _ => a.Equals(b),
        };
    }

    private static bool SeqEq(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool OpsEq(IReadOnlyList<Style.FilterOp> a, IReadOnlyList<Style.FilterOp> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    // If [from, to] opens shader groups it does not close, the index of the PopShader that closes
    // the last of them; otherwise the end it was given. Only ever moves the end later, into what
    // was the equal suffix, so the commands still paired after it are paired exactly as before.
    private static int CloseShaders(IReadOnlyList<PaintCommand> list, int from, int to)
    {
        var depth = 0;
        for (var i = from; i <= to; i++)
        {
            if (list[i] is PushShader) depth++;
            else if (list[i] is PopShader && depth > 0) depth--;
        }
        while (depth > 0 && to + 1 < list.Count)
        {
            to++;
            if (list[to] is PushShader) depth++;
            else if (list[to] is PopShader) depth--;
        }
        return to;
    }

    // Every scope type must open/close within the span (and never pop below the span's start depth).
    private static bool Balanced(IReadOnlyList<PaintCommand> list, int from, int to)
    {
        int clip = 0, transform = 0, opacity = 0, filter = 0, shader = 0;
        for (var i = from; i <= to; i++)
        {
            switch (list[i])
            {
                case PushClip or PushClipShape: clip++; break;
                case PopClip: if (--clip < 0) return false; break;
                case PushTransform: transform++; break;
                case PopTransform: if (--transform < 0) return false; break;
                case PushOpacity: opacity++; break;
                case PopOpacity: if (--opacity < 0) return false; break;
                case PushFilter: filter++; break;
                case PopFilter: if (--filter < 0) return false; break;
                case PushShader: shader++; break;
                case PopShader: if (--shader < 0) return false; break;
            }
        }
        return clip == 0 && transform == 0 && opacity == 0 && filter == 0 && shader == 0;
    }

    // Maintain the current transform while scanning (mirrors SkiaRasterizer's PushTransform math).
    private static void Track(PaintCommand cmd, ref SKMatrix m, List<SKMatrix> stack)
    {
        switch (cmd)
        {
            case PushTransform t:
                stack.Add(m);
                m = m.PreConcat(t.Matrix);
                break;
            case PopTransform when stack.Count > 0:
                m = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                break;
        }
    }

    // Union the (matrix-mapped) bounds of every painting command in [from, to]. False = a command's
    // effect can't be bounded (an unbounded opacity/filter layer) → caller falls back to full damage.
    private static bool SpanBounds(IReadOnlyList<PaintCommand> list, int from, int to, SKMatrix m, ref SKRect damage)
    {
        var stack = new List<SKMatrix>();
        for (var i = from; i <= to; i++)
        {
            var cmd = list[i];
            Track(cmd, ref m, stack);
            var b = LocalBounds(cmd);
            if (b is null)
            {
                if (cmd is PushOpacity or PushFilter) return false; // unbounded layer (W ≤ 0): whole clip
                continue; // a pure stack op — paints nothing itself
            }
            var mapped = m.MapRect(b.Value);
            damage = damage.IsEmpty ? mapped : SKRect.Union(damage, mapped);
        }
        return true;
    }

    // The rectangle a command can touch, in its own (pre-transform) space; null for stack ops —
    // except unbounded Push layers, which SpanBounds treats as "can't bound".
    private static SKRect? LocalBounds(PaintCommand cmd) => cmd switch
    {
        FillRect c => SKRect.Create(c.X, c.Y, c.W, c.H),
        GradientRect c => SKRect.Create(c.X, c.Y, c.W, c.H),
        ShaderRect c => SKRect.Create(c.X, c.Y, c.W, c.H),
        PushShader c => SKRect.Create(c.X, c.Y, c.W, c.H),
        TiledImage c => SKRect.Create(c.X, c.Y, c.W, c.H),
        BorderRect c => SKRect.Create(c.X, c.Y, c.W, c.H),
        ShadowRect c => Grow(SKRect.Create(c.X, c.Y, c.W, c.H),
            c.Spread + c.Blur * 2 + MathF.Max(MathF.Abs(c.Dx), MathF.Abs(c.Dy))),
        // The exact glyph span (alignment resolved), not the whole container — container-wide
        // bounds pulled every nearby paragraph into surface-fast-path replays and fattened damage.
        VectorPath v => SKRect.Create(v.X, v.Y, v.Width, v.Height),
        TextRun c => SKRect.Create(
            c.Align switch
            {
                Style.TextAlign.Center => c.X + MathF.Max(0, (c.ContainerWidth - c.LineWidth) / 2),
                Style.TextAlign.Right => c.X + MathF.Max(0, c.ContainerWidth - c.LineWidth),
                _ => c.X,
            }, c.Y, c.LineWidth, c.LineHeight),
        FillPath c => SKRect.Create(c.X, c.Y, c.Width, c.Height),
        ResizeGrip c => SKRect.Create(c.X, c.Y, c.Size, c.Size),
        DrawImage c => SKRect.Create(c.X, c.Y, c.W, c.H),
        DrawSurface c => SKRect.Create(c.X, c.Y, c.W, c.H),
        ClearHole c => SKRect.Create(c.X, c.Y, c.W, c.H),
        Polyline c => PolyBounds(c),
        PushOpacity c => c.W > 0 ? SKRect.Create(c.X, c.Y, c.W, c.H) : null, // null ⇒ unbounded
        PushFilter c => c.W > 0 ? SKRect.Create(c.X, c.Y, c.W, c.H) : null,
        _ => null, // PushClip / Pop* — no pixels of their own
    };

    private static SKRect Grow(SKRect r, float by) { r.Inflate(by, by); return r; }

    private static SKRect PolyBounds(Polyline c)
    {
        if (c.Points.Count < 2) return SKRect.Empty;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i + 1 < c.Points.Count; i += 2)
        {
            minX = MathF.Min(minX, c.Points[i]); maxX = MathF.Max(maxX, c.Points[i]);
            minY = MathF.Min(minY, c.Points[i + 1]); maxY = MathF.Max(maxY, c.Points[i + 1]);
        }
        if (c.Fill.Alpha > 0) { minY = MathF.Min(minY, c.BaseY); maxY = MathF.Max(maxY, c.BaseY); }
        var r = new SKRect(minX, minY, maxX, maxY);
        r.Inflate(c.Width + 6, c.Width + 6); // stroke width + Catmull-Rom overshoot slack (Curved)
        return r;
    }
}
