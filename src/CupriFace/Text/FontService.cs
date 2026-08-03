using CupriFace.Style;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CupriFace.Text;

/// <summary>
/// Caches typefaces and fonts by (family, weight, size). DESIGN.md §7.5 — text is
/// shaped/measured once and reused; recreating <see cref="SKFont"/> per frame would
/// allocate and re-rasterise. Not thread-safe (single UI thread for now).
/// </summary>
public sealed class FontService : IDisposable
{
    private readonly Dictionary<(string, int), SKTypeface> _typefaces = new();
    private readonly Dictionary<(string, int, int), SKFont> _fonts = new();
    private readonly Dictionary<(string, int), SKShaper> _shapers = new();

    public SKTypeface GetTypeface(string family, int weight)
    {
        var key = (family.ToLowerInvariant(), weight);
        if (_typefaces.TryGetValue(key, out var tf)) return tf;
        var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        tf = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        _typefaces[key] = tf;
        return tf;
    }

    public SKFont GetFont(ComputedStyle s) => GetFont(s.FontFamily, s.FontWeight, s.FontSize);

    public SKFont GetFont(string family, int weight, float size)
    {
        var key = (family.ToLowerInvariant(), weight, (int)MathF.Round(size * 4)); // 0.25px buckets
        if (_fonts.TryGetValue(key, out var f)) return f;
        f = new SKFont(GetTypeface(family, weight), size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        _fonts[key] = f;
        return f;
    }

    /// <summary>A HarfBuzz shaper for the (family, weight), cached per typeface.</summary>
    public SKShaper GetShaper(string family, int weight)
    {
        var key = (family.ToLowerInvariant(), weight);
        if (_shapers.TryGetValue(key, out var sh)) return sh;
        sh = new SKShaper(GetTypeface(family, weight));
        _shapers[key] = sh;
        return sh;
    }

    /// <summary>
    /// Measure text width using HarfBuzz shaping (correct advances/kerning/ligatures),
    /// falling back to Skia's simple measurement if the native shaper is unavailable.
    /// </summary>
    public float MeasureText(string family, int weight, float size, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var font = GetFont(family, weight, size);
        try { return GetShaper(family, weight).Shape(text, font).Width; }
        catch { return font.MeasureText(text); }
    }

    public float MeasureText(ComputedStyle s, string text) => MeasureText(s.FontFamily, s.FontWeight, s.FontSize, text);

    /// <summary>Line height in px for a style (font-size × line-height multiple).</summary>
    public static float LineHeightPx(ComputedStyle s) => s.FontSize * s.LineHeight;

    public void Dispose()
    {
        foreach (var sh in _shapers.Values) sh.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
        foreach (var t in _typefaces.Values) t.Dispose();
        _shapers.Clear();
        _fonts.Clear();
        _typefaces.Clear();
    }
}
