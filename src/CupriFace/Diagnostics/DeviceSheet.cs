using SkiaSharp;

namespace CupriFace.Diagnostics;

/// <summary>
/// The same document at several viewport sizes, side by side in ONE image — a desktop, a tablet
/// and a phone on a single contact sheet, each box outlined, so one look settles all of them.
///
/// <para><b>Why one image.</b> A responsive layout fails at the size you did not render. The
/// habit this exists to replace is rendering the desktop, looking, and calling it done; a sheet
/// makes the phone render as cheap to look at as the desktop one, because it is in the same file
/// — and when an agent is the one looking, a single image is a single read.</para>
///
/// <para><b>Why the outlines.</b> With <see cref="CupriDocument.DebugOverlay"/> on, every box is
/// drawn as a thin rectangle, and an overlap is visible as two rectangles crossing — which is
/// exactly the thing a plain render hides, because overlapping content looks like content.
/// <c>CupriDoctor</c>'s <c>CF0075</c> reports the same overlap as a number; this is for the eye.</para>
///
/// <code>
/// using var sheet = DeviceSheet.Render(doc, CupriDoctor.DesktopAndPhone);
/// using var data = sheet.Encode(SKEncodedImageFormat.Png, 90);
/// using (var f = File.Create("devices.png")) data.SaveTo(f);
/// </code>
///
/// <para>Each render lays the document out at that size, exactly as a window of that size would,
/// and the document is left laid out at the LAST size in the list. Headless, like everything
/// else here: no window, no GPU.</para>
/// </summary>
public static class DeviceSheet
{
    /// <summary>Render <paramref name="doc"/> at each of <paramref name="viewports"/> and lay the
    /// results out left to right, each under a label naming its size.</summary>
    /// <param name="doc">A document, bound and refreshed — the same one you would hand to
    /// <c>RenderToImage</c>.</param>
    /// <param name="viewports">The sizes, in the order to show them. <c>CupriDoctor.DesktopAndPhone</c>
    /// is the usual pair.</param>
    /// <param name="background">What the document paints over; the page background when null.</param>
    /// <param name="outlines">Draw every element's box as a thin outline (the engine's debug
    /// overlay). On by default, because an overlap is what the sheet is for finding.</param>
    /// <param name="gutter">Pixels between renders, and around the sheet.</param>
    public static SKImage Render(CupriDocument doc, IReadOnlyList<(int Width, int Height)> viewports,
                                 SKColor? background = null, bool outlines = true, int gutter = 24)
    {
        if (viewports.Count == 0) throw new ArgumentException("At least one viewport is needed.", nameof(viewports));

        const int labelH = 22;
        var sheetW = gutter;
        var sheetH = 0;
        foreach (var (w, h) in viewports)
        {
            sheetW += w + gutter;
            sheetH = Math.Max(sheetH, h);
        }
        sheetH += labelH + 2 * gutter;

        var was = doc.DebugOverlay;
        doc.DebugOverlay = outlines;
        try
        {
            var info = new SKImageInfo(sheetW, sheetH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0x2a, 0x2d, 0x33));

            using var font = new SKFont(SKTypeface.Default, 13);
            using var text = new SKPaint { Color = new SKColor(0xe6, 0xe9, 0xf0), IsAntialias = true };
            using var frame = new SKPaint { Color = new SKColor(0x8b, 0x93, 0xa7), IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

            var x = gutter;
            foreach (var (w, h) in viewports)
            {
                // Two renders: the first warms layout and any image that loads on first use, so the
                // second is what a window at this size would actually have shown.
                using (doc.RenderToImage(w, h)) { }
                using var img = doc.RenderToImage(w, h, background);

                canvas.DrawText($"{w} x {h}", x, gutter + 14, SKTextAlign.Left, font, text);
                var top = gutter + labelH;
                canvas.DrawImage(img, x, top);
                canvas.DrawRect(x - 0.5f, top - 0.5f, w + 1, h + 1, frame);
                x += w + gutter;
            }
            return surface.Snapshot();
        }
        finally
        {
            doc.DebugOverlay = was;
        }
    }
}
