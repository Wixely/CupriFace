using SkiaSharp;

namespace CupriFace.Diagnostics;

/// <summary>What changed between two renders, as a number and as a picture.</summary>
/// <param name="ChangedPixels">Pixels differing by more than the tolerance.</param>
/// <param name="TotalPixels">Pixels compared. Zero when the two images differ in size.</param>
/// <param name="SizeChanged">The images are not the same dimensions, so nothing was compared
/// pixel-wise — usually the more interesting finding on its own.</param>
/// <param name="Bounds">The rectangle enclosing every changed pixel, or null when nothing changed.
/// Narrows "something moved" to "something moved HERE".</param>
public readonly record struct DiffResult(
    int ChangedPixels, int TotalPixels, bool SizeChanged, SKRectI? Bounds)
{
    /// <summary>Changed proportion, 0..1. Zero when the sizes differ — check
    /// <see cref="SizeChanged"/> rather than reading this as "identical".</summary>
    public double ChangedFraction => TotalPixels == 0 ? 0 : (double)ChangedPixels / TotalPixels;

    /// <summary>Nothing moved: same size, no pixel over tolerance.</summary>
    public bool IsIdentical => !SizeChanged && ChangedPixels == 0;

    public override string ToString() => SizeChanged
        ? "size changed — not comparable pixel-for-pixel"
        : ChangedPixels == 0
            ? "identical"
            : $"{ChangedPixels:N0}/{TotalPixels:N0} px changed ({ChangedFraction:P2})"
              + (Bounds is { } b ? $" within {b.Left},{b.Top} {b.Width}x{b.Height}" : "");
}

/// <summary>
/// Compare two renders of the same document.
///
/// <para><b>What it is for.</b> "Did my change touch anything it should not have?" is the question
/// that makes a broad edit — a variable rename in the cascade, a padding tweak on a shared class —
/// safe or terrifying, and it is not answerable by looking at two screenshots in turn. Human eyes
/// are poor at spotting that one row moved three pixels; subtraction is perfect at it.</para>
///
/// <para>Render before, render after, diff. A non-zero fraction where you expected zero is the
/// whole signal, and <see cref="DiffResult.Bounds"/> says where to look. <see cref="Visualise"/>
/// gives a picture to open when the number alone is not enough.</para>
/// </summary>
public static class ImageDiff
{
    /// <summary>
    /// Compare two images.
    /// </summary>
    /// <param name="tolerance">Per-channel 0..255 slack. Not zero by default: text rendering is
    /// not bit-identical across runs once subpixel positioning and hinting are involved, and a
    /// comparison that reports every antialiased edge as a change is one nobody keeps using. Pass 0
    /// when you genuinely need exactness.</param>
    public static DiffResult Compare(SKBitmap before, SKBitmap after, int tolerance = 8)
    {
        if (before.Width != after.Width || before.Height != after.Height)
            return new DiffResult(0, 0, SizeChanged: true, null);

        int changed = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < before.Height; y++)
        for (var x = 0; x < before.Width; x++)
        {
            if (!Differs(before.GetPixel(x, y), after.GetPixel(x, y), tolerance)) continue;
            changed++;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        return new DiffResult(changed, before.Width * before.Height, false,
            maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1));
    }

    /// <summary>Convenience for the images <c>RenderToImage</c> hands back.</summary>
    public static DiffResult Compare(SKImage before, SKImage after, int tolerance = 8)
    {
        using var a = SKBitmap.FromImage(before);
        using var b = SKBitmap.FromImage(after);
        return Compare(a, b, tolerance);
    }

    /// <summary>
    /// The "after" image with every changed pixel marked, so a diff can be looked at rather than
    /// only counted. Unchanged pixels are dimmed towards grey and changed ones painted a flat
    /// magenta — a colour chosen because almost no real UI contains it, so the eye finds it
    /// instantly and no legend is needed.
    /// </summary>
    public static SKBitmap Visualise(SKBitmap before, SKBitmap after, int tolerance = 8)
    {
        var w = Math.Min(before.Width, after.Width);
        var h = Math.Min(before.Height, after.Height);
        var outMap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var b = before.GetPixel(x, y);
            var a = after.GetPixel(x, y);
            outMap.SetPixel(x, y, Differs(b, a, tolerance)
                ? new SKColor(0xFF, 0x00, 0xAA)
                : Dim(a));
        }
        return outMap;
    }

    private static bool Differs(SKColor a, SKColor b, int tolerance) =>
        Math.Abs(a.Red - b.Red) > tolerance || Math.Abs(a.Green - b.Green) > tolerance ||
        Math.Abs(a.Blue - b.Blue) > tolerance || Math.Abs(a.Alpha - b.Alpha) > tolerance;

    /// <summary>Enough of the original to recognise the layout, faint enough that the marks are
    /// unmissable against it.</summary>
    private static SKColor Dim(SKColor c)
    {
        var grey = (byte)((c.Red * 0.30 + c.Green * 0.59 + c.Blue * 0.11) * 0.45 + 90);
        return new SKColor(grey, grey, grey, c.Alpha);
    }
}
