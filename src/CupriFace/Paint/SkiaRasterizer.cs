using CupriFace.Style;
using CupriFace.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CupriFace.Paint;

/// <summary>
/// Plays an immutable <see cref="DisplayList"/> onto an <see cref="SKCanvas"/>. This is
/// the "render thread" side of the commit-snapshot seam: it reads only the snapshot and
/// the font cache, never the live render tree.
/// </summary>
public sealed class SkiaRasterizer
{
    private readonly FontService _fonts;
    public SkiaRasterizer(FontService fonts) => _fonts = fonts;

    private static readonly SKSamplingOptions _imageSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // Destination rect for an image inside its box per object-fit (always drawing the whole image;
    // the caller clips to the box, so Cover's overflow is cropped).
    private static SKRect FitRect(DrawImage di)
    {
        float bw = di.W, bh = di.H, iw = di.Image.Width, ih = di.Image.Height;
        if (iw <= 0 || ih <= 0 || di.Fit == ObjectFit.Fill) return new SKRect(di.X, di.Y, di.X + bw, di.Y + bh);
        var scale = di.Fit switch
        {
            ObjectFit.Cover => MathF.Max(bw / iw, bh / ih),
            ObjectFit.None => 1f,
            _ => MathF.Min(bw / iw, bh / ih), // Contain
        };
        float dw = iw * scale, dh = ih * scale;
        float x = di.X + (bw - dw) / 2f, y = di.Y + (bh - dh) / 2f; // centre in the box
        return new SKRect(x, y, x + dw, y + dh);
    }

    public void Paint(SKCanvas canvas, DisplayList list)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var cmd in list.Commands)
        {
            switch (cmd)
            {
                case FillRect r:
                    fill.Color = r.Color;
                    DrawRect(canvas, fill, r.X, r.Y, r.W, r.H, r.Radius);
                    break;

                case BorderRect b:
                    stroke.Color = b.Color;
                    DrawBorder(canvas, stroke, b);
                    break;

                case TextRun t:
                    DrawText(canvas, textPaint, t);
                    break;

                case PushClip c:
                    canvas.Save();
                    canvas.ClipRoundRect(new SKRoundRect(new SKRect(c.X, c.Y, c.X + c.W, c.Y + c.H), c.Radius), antialias: true);
                    break;

                case PopClip:
                    canvas.Restore();
                    break;

                case PushTransform t:
                {
                    canvas.Save();
                    var m = SKMatrix.CreateTranslation(t.CenterX + t.TranslateX, t.CenterY + t.TranslateY);
                    m = m.PreConcat(SKMatrix.CreateRotationDegrees(t.RotateDeg));
                    m = m.PreConcat(SKMatrix.CreateScale(t.ScaleX, t.ScaleY));
                    m = m.PreConcat(SKMatrix.CreateTranslation(-t.CenterX, -t.CenterY));
                    canvas.Concat(in m);
                    break;
                }

                case PopTransform:
                    canvas.Restore();
                    break;

                case PushOpacity o:
                    // SaveLayer with an alpha-only paint composites the whole subtree as a group.
                    using (var layer = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(o.Alpha * 255f)) })
                        canvas.SaveLayer(layer);
                    break;

                case PopOpacity:
                    canvas.Restore();
                    break;

                case PushFilter f:
                    // SaveLayer with an image filter applies the CSS filter chain to the whole subtree
                    // when the layer is composited on the matching PopFilter/Restore.
                    using (var imf = BuildFilter(f.Ops))
                    using (var layer = new SKPaint { ImageFilter = imf })
                        canvas.SaveLayer(layer);
                    break;

                case PopFilter:
                    canvas.Restore();
                    break;

                case Polyline pl when pl.Points.Count >= 4:
                {
                    using var path = new SKPath();
                    path.MoveTo(pl.Points[0], pl.Points[1]);
                    for (var i = 2; i + 1 < pl.Points.Count; i += 2) path.LineTo(pl.Points[i], pl.Points[i + 1]);

                    if (pl.Fill.Alpha > 0) // area under the line, closed down to the baseline
                    {
                        using var area = new SKPath();
                        area.MoveTo(pl.Points[0], pl.BaseY);
                        area.LineTo(pl.Points[0], pl.Points[1]);
                        for (var i = 2; i + 1 < pl.Points.Count; i += 2) area.LineTo(pl.Points[i], pl.Points[i + 1]);
                        area.LineTo(pl.Points[^2], pl.BaseY);
                        area.Close();
                        fill.Color = pl.Fill;
                        canvas.DrawPath(area, fill);
                    }
                    if (pl.Width > 0)
                    {
                        stroke.Style = SKPaintStyle.Stroke;
                        stroke.StrokeWidth = pl.Width;
                        stroke.StrokeJoin = SKStrokeJoin.Round;
                        stroke.StrokeCap = SKStrokeCap.Round;
                        stroke.Color = pl.Stroke;
                        canvas.DrawPath(path, stroke);
                        stroke.Style = SKPaintStyle.Fill; // reset for the next border/grip
                    }
                    break;
                }

                case ResizeGrip g:
                {
                    stroke.Style = SKPaintStyle.Stroke;
                    stroke.StrokeWidth = 1.5f;
                    stroke.StrokeCap = SKStrokeCap.Round;
                    stroke.Color = g.Color;
                    float gx2 = g.X + g.Size, gy2 = g.Y + g.Size; // three ticks parallel to the corner diagonal
                    foreach (var f in stackalloc[] { 0.30f, 0.62f, 0.94f })
                        canvas.DrawLine(gx2 - g.Size * f, gy2, gx2, gy2 - g.Size * f, stroke);
                    stroke.Style = SKPaintStyle.Fill;
                    stroke.StrokeCap = SKStrokeCap.Butt;
                    break;
                }

                case DrawImage di:
                {
                    canvas.Save();
                    var box = new SKRect(di.X, di.Y, di.X + di.W, di.Y + di.H);
                    if (di.Radius > 0) canvas.ClipRoundRect(new SKRoundRect(box, di.Radius), antialias: true);
                    else canvas.ClipRect(box);
                    canvas.DrawImage(di.Image, FitRect(di), _imageSampling);
                    canvas.Restore();
                    break;
                }

                case FillPath p:
                    using (var path = SKPath.ParseSvgPathData(p.PathData))
                    {
                        if (path is null || p.ViewBox <= 0) break;
                        fill.Color = p.Color;
                        canvas.Save();
                        canvas.Translate(p.X, p.Y);
                        canvas.Scale(p.Width / p.ViewBox, p.Height / p.ViewBox);
                        canvas.DrawPath(path, fill);
                        canvas.Restore();
                    }
                    break;
            }
        }
    }

    private static void DrawRect(SKCanvas canvas, SKPaint paint, float x, float y, float w, float h, float radius)
    {
        var rect = new SKRect(x, y, x + w, y + h);
        if (radius > 0) canvas.DrawRoundRect(rect, radius, radius, paint);
        else canvas.DrawRect(rect, paint);
    }

    // Build one SKImageFilter from a CSS filter chain: colour-matrix ops (brightness…invert) fold into
    // a single colour filter; blur and drop-shadow are image filters composed in declaration order.
    private static SKImageFilter? BuildFilter(IReadOnlyList<FilterOp> ops)
    {
        SKImageFilter? img = null;
        SKColorFilter? color = null;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case FilterKind.Blur:
                    if (op.A > 0) img = SKImageFilter.CreateBlur(op.A, op.A, img); // CSS radius ≈ Gaussian σ
                    break;
                case FilterKind.DropShadow:
                    img = SKImageFilter.CreateDropShadow(op.A, op.B, MathF.Max(0, op.C), MathF.Max(0, op.C), op.Color, img);
                    break;
                default:
                    var m = ColorMatrix(op);
                    if (m is not null)
                    {
                        var cf = SKColorFilter.CreateColorMatrix(m);
                        color = color is null ? cf : SKColorFilter.CreateCompose(cf, color);
                    }
                    break;
            }
        }
        if (color is not null) img = SKImageFilter.CreateColorFilter(color, img); // apply colour after blur/shadow
        return img;
    }

    // A 4×5 row-major colour matrix for one colour-adjust filter (offset column in 0..255).
    private static float[]? ColorMatrix(FilterOp op)
    {
        float a = op.A;
        switch (op.Kind)
        {
            case FilterKind.Brightness:
                return [a, 0, 0, 0, 0,  0, a, 0, 0, 0,  0, 0, a, 0, 0,  0, 0, 0, 1, 0];
            case FilterKind.Contrast:
            {
                // Offset column is normalised [0,1] (Skia scales RGBA to [0,1] before the matrix).
                float t = 0.5f - 0.5f * a;
                return [a, 0, 0, 0, t,  0, a, 0, 0, t,  0, 0, a, 0, t,  0, 0, 0, 1, 0];
            }
            case FilterKind.Invert:
            {
                float s = 1f - 2f * a, o = a; // offset in [0,1]
                return [s, 0, 0, 0, o,  0, s, 0, 0, o,  0, 0, s, 0, o,  0, 0, 0, 1, 0];
            }
            case FilterKind.Opacity:
                return [1, 0, 0, 0, 0,  0, 1, 0, 0, 0,  0, 0, 1, 0, 0,  0, 0, 0, a, 0];
            case FilterKind.Grayscale:
            {
                // Lerp identity → luminance by amount a.
                float g = Math.Clamp(a, 0f, 1f);
                float rr = 0.2126f, gg = 0.7152f, bb = 0.0722f;
                return
                [
                    1 - g + g * rr, g * gg, g * bb, 0, 0,
                    g * rr, 1 - g + g * gg, g * bb, 0, 0,
                    g * rr, g * gg, 1 - g + g * bb, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case FilterKind.Sepia:
            {
                float s = Math.Clamp(a, 0f, 1f);
                return
                [
                    1 - s + s * 0.393f, s * 0.769f, s * 0.189f, 0, 0,
                    s * 0.349f, 1 - s + s * 0.686f, s * 0.168f, 0, 0,
                    s * 0.272f, s * 0.534f, 1 - s + s * 0.131f, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case FilterKind.Saturate:
            {
                // Standard saturation matrix (a=1 → identity, 0 → greyscale, >1 → oversaturated).
                float rw = 0.3086f, gw = 0.6094f, bw = 0.0820f, inv = 1f - a;
                return
                [
                    inv * rw + a, inv * gw, inv * bw, 0, 0,
                    inv * rw, inv * gw + a, inv * bw, 0, 0,
                    inv * rw, inv * gw, inv * bw + a, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            default: return null;
        }
    }

    private static void DrawBorder(SKCanvas canvas, SKPaint paint, BorderRect b)
    {
        var uniform = b.Top > 0 && Approximately(b.Top, b.Right) && Approximately(b.Top, b.Bottom) && Approximately(b.Top, b.Left);
        if (uniform)
        {
            // Stroke centred on the border-box edge, inset by half width.
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = b.Top;
            var inset = b.Top / 2f;
            var rect = new SKRect(b.X + inset, b.Y + inset, b.X + b.W - inset, b.Y + b.H - inset);
            var r = MathF.Max(0, b.Radius - inset);

            // dashed/dotted → a dash path effect on the stroke (dotted = round-capped zero-length dashes).
            SKPathEffect? dash = null;
            if (b.Style == BorderLineStyle.Dashed) { paint.StrokeCap = SKStrokeCap.Butt; dash = SKPathEffect.CreateDash([b.Top * 2.5f, b.Top * 2.5f], 0f); }
            else if (b.Style == BorderLineStyle.Dotted) { paint.StrokeCap = SKStrokeCap.Round; dash = SKPathEffect.CreateDash([0.1f, b.Top * 2f], 0f); }
            paint.PathEffect = dash;

            if (r > 0) canvas.DrawRoundRect(rect, r, r, paint);
            else canvas.DrawRect(rect, paint);

            paint.PathEffect = null; dash?.Dispose();
            paint.StrokeCap = SKStrokeCap.Butt;
            paint.Style = SKPaintStyle.Fill;
            return;
        }

        // Non-uniform: draw each present edge as a filled rectangle.
        if (b.Top > 0) canvas.DrawRect(new SKRect(b.X, b.Y, b.X + b.W, b.Y + b.Top), paint);
        if (b.Bottom > 0) canvas.DrawRect(new SKRect(b.X, b.Y + b.H - b.Bottom, b.X + b.W, b.Y + b.H), paint);
        if (b.Left > 0) canvas.DrawRect(new SKRect(b.X, b.Y, b.X + b.Left, b.Y + b.H), paint);
        if (b.Right > 0) canvas.DrawRect(new SKRect(b.X + b.W - b.Right, b.Y, b.X + b.W, b.Y + b.H), paint);
    }

    private void DrawText(SKCanvas canvas, SKPaint paint, TextRun t)
    {
        var font = _fonts.GetFont(t.Family, t.Weight, t.Size);
        paint.Color = t.Color;

        var alignOffset = t.Align switch
        {
            TextAlign.Center => (t.ContainerWidth - t.LineWidth) / 2f,
            TextAlign.Right => t.ContainerWidth - t.LineWidth,
            _ => 0f,
        };

        // Baseline: centre the *cap height* within the line box (reads as centred, since
        // descender space isn't counted). Falls back to ~0.7em when the font omits cap height.
        var metrics = font.Metrics;
        var cap = metrics.CapHeight > 0 ? metrics.CapHeight : t.Size * 0.7f;
        var x = t.X + MathF.Max(0, alignOffset);
        var baseline = t.Y + t.LineHeight / 2f + cap / 2f;

        // Reorder into visual runs (bidi), then shape each run in its own direction.
        var runs = Bidi.Reorder(t.Text);
        if (runs.Count == 1 && !runs[0].Rtl)
        {
            DrawRun(canvas, paint, font, t.Family, t.Weight, t.Text, x, baseline);
            return;
        }
        var cursor = x;
        foreach (var run in runs)
        {
            DrawRun(canvas, paint, font, t.Family, t.Weight, run.Text, cursor, baseline);
            cursor += _fonts.MeasureText(t.Family, t.Weight, t.Size, run.Text);
        }
    }

    private void DrawRun(SKCanvas canvas, SKPaint paint, SKFont primaryFont, string family, int weight, string text, float x, float baseline)
    {
        // Split into fallback-face runs so glyphs the primary lacks (emoji/CJK/symbols) draw in a
        // face that has them instead of tofu. Each sub-run is HarfBuzz-shaped in its own typeface.
        // (The overwhelmingly common case is one run in the primary face — then we shape once, no
        // extra measuring.)
        var runs = _fonts.SplitRuns(text, family, weight);
        for (var i = 0; i < runs.Count; i++)
        {
            var (segment, tf) = runs[i];
            var font = _fonts.GetFont(tf, primaryFont.Size);
            var shaper = _fonts.GetShaper(tf);
            try { canvas.DrawShapedText(shaper, segment, x, baseline, font, paint); }
            catch { canvas.DrawText(segment, x, baseline, font, paint); }
            if (i < runs.Count - 1) // advance to the next run only when one follows
            {
                try { x += shaper.Shape(segment, font).Width; }
                catch { x += font.MeasureText(segment); }
            }
        }
    }

    private static bool Approximately(float a, float b) => MathF.Abs(a - b) < 0.01f;
}
