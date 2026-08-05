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
            if (r > 0) canvas.DrawRoundRect(rect, r, r, paint);
            else canvas.DrawRect(rect, paint);
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

    private void DrawRun(SKCanvas canvas, SKPaint paint, SKFont font, string family, int weight, string text, float x, float baseline)
    {
        // HarfBuzz-shaped drawing (correct advances/direction); fall back to Skia's simple path.
        try { canvas.DrawShapedText(_fonts.GetShaper(family, weight), text, x, baseline, font, paint); }
        catch { canvas.DrawText(text, x, baseline, font, paint); }
    }

    private static bool Approximately(float a, float b) => MathF.Abs(a - b) < 0.01f;
}
