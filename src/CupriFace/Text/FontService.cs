using System.Text;
using CupriFace.Style;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CupriFace.Text;

/// <summary>
/// Caches typefaces and fonts by (family, weight, size). DESIGN.md §7.5 — text is
/// shaped/measured once and reused; recreating <see cref="SKFont"/> per frame would
/// allocate and re-rasterise. Not thread-safe (single UI thread for now).
///
/// <b>Fallback-face selection</b> (DESIGN risk #5): a run whose characters the primary family lacks
/// (emoji, CJK, symbols) is split off and rendered in a fallback face found via
/// <see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/> — otherwise those
/// glyphs render as tofu. See <see cref="SplitRuns"/>.
/// </summary>
public sealed class FontService : IDisposable
{
    private readonly Dictionary<(string, int), SKTypeface> _typefaces = new();
    private readonly Dictionary<(string, int, int), SKFont> _fonts = new();
    private readonly Dictionary<(string, int), SKShaper> _shapers = new();
    private readonly Dictionary<(SKTypeface, int), SKFont> _fontsByTypeface = new();
    private readonly Dictionary<SKTypeface, SKShaper> _shapersByTypeface = new();
    private readonly Dictionary<SKTypeface, SKFont> _probes = new();      // cached font per typeface for glyph checks
    private readonly Dictionary<int, SKTypeface?> _fallbackByCodepoint = new(); // fallback face per missing codepoint

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

    /// <summary>A font for a specific typeface + size (used for fallback runs), cached.</summary>
    public SKFont GetFont(SKTypeface typeface, float size)
    {
        var key = (typeface, (int)MathF.Round(size * 4));
        if (_fontsByTypeface.TryGetValue(key, out var f)) return f;
        f = new SKFont(typeface, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        _fontsByTypeface[key] = f;
        return f;
    }

    /// <summary>A HarfBuzz shaper for a specific typeface (fallback runs), cached.</summary>
    public SKShaper GetShaper(SKTypeface typeface)
    {
        if (_shapersByTypeface.TryGetValue(typeface, out var sh)) return sh;
        sh = new SKShaper(typeface);
        _shapersByTypeface[typeface] = sh;
        return sh;
    }

    /// <summary>Split <paramref name="text"/> into runs by which typeface can render each character:
    /// the primary family, or a fallback face for glyphs it lacks (found once per codepoint and
    /// cached). Runs concatenate back to the original text. Codepoint-aware (surrogate pairs stay
    /// together), so emoji and astral characters route to an emoji/symbol font.</summary>
    public List<(string Text, SKTypeface Typeface)> SplitRuns(string text, string family, int weight)
    {
        var primary = GetTypeface(family, weight);
        var runs = new List<(string, SKTypeface)>();
        if (string.IsNullOrEmpty(text)) return runs;

        var sb = new StringBuilder();
        SKTypeface? current = null;
        for (var i = 0; i < text.Length;)
        {
            var high = char.IsHighSurrogate(text[i]) && i + 1 < text.Length;
            var cp = high ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            var len = high ? 2 : 1;
            var tf = TypefaceForCodepoint(primary, family, weight, cp);
            if (current is null) current = tf;
            else if (!ReferenceEquals(tf, current)) { runs.Add((sb.ToString(), current)); sb.Clear(); current = tf; }
            sb.Append(text, i, len);
            i += len;
        }
        if (sb.Length > 0) runs.Add((sb.ToString(), current!));
        return runs;
    }

    // The face to render one codepoint: the primary if it has the glyph (ASCII fast-pathed), else a
    // system fallback that does (cached per codepoint); the primary if nothing matches (graceful tofu).
    private SKTypeface TypefaceForCodepoint(SKTypeface primary, string family, int weight, int cp)
    {
        if (cp < 0x80 || HasGlyph(primary, cp)) return primary;
        if (_fallbackByCodepoint.TryGetValue(cp, out var cached)) return cached ?? primary;
        SKTypeface? fb = null;
        try { fb = SKFontManager.Default.MatchCharacter(family, new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), null, cp); }
        catch { /* no font manager match → tofu in the primary */ }
        _fallbackByCodepoint[cp] = fb;
        return fb ?? primary;
    }

    private bool HasGlyph(SKTypeface tf, int cp)
    {
        if (!_probes.TryGetValue(tf, out var probe)) { probe = new SKFont(tf, 16f); _probes[tf] = probe; }
        return probe.ContainsGlyph(cp);
    }

    /// <summary>
    /// Measure text width using HarfBuzz shaping (correct advances/kerning/ligatures) across
    /// fallback-face runs, falling back to Skia's simple measurement if the native shaper is
    /// unavailable.
    /// </summary>
    public float MeasureText(string family, int weight, float size, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var total = 0f;
        foreach (var (segment, tf) in SplitRuns(text, family, weight))
        {
            var font = GetFont(tf, size);
            try { total += GetShaper(tf).Shape(segment, font).Width; }
            catch { total += font.MeasureText(segment); }
        }
        return total;
    }

    public float MeasureText(ComputedStyle s, string text) => MeasureText(s.FontFamily, s.FontWeight, s.FontSize, text);

    /// <summary>Line height in px for a style (font-size × line-height multiple).</summary>
    public static float LineHeightPx(ComputedStyle s) => s.FontSize * s.LineHeight;

    public void Dispose()
    {
        foreach (var sh in _shapers.Values) sh.Dispose();
        foreach (var sh in _shapersByTypeface.Values) sh.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
        foreach (var f in _fontsByTypeface.Values) f.Dispose();
        foreach (var p in _probes.Values) p.Dispose();
        foreach (var t in _typefaces.Values) t.Dispose();
        _shapers.Clear(); _shapersByTypeface.Clear();
        _fonts.Clear(); _fontsByTypeface.Clear(); _probes.Clear();
        _typefaces.Clear();
    }
}
