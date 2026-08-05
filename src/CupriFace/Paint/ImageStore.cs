using System.Reflection;
using CupriFace.Resources;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// Decodes and caches raster images for <c>&lt;cupri-image&gt;</c>. The <c>src</c> resolves through
/// the <see cref="CupriSource"/> pipeline (embedded / file / URL) or an inline <c>data:</c> URI, so
/// images inherit the same trust model as markup/CSS. Synchronous: an image is decoded on first use
/// and cached — a decode/load failure caches <c>null</c>, so it never retries or throws at paint time.
/// </summary>
public sealed class ImageStore : IDisposable
{
    private readonly Dictionary<string, SKImage?> _cache = new(StringComparer.Ordinal);
    private Assembly? _assembly;

    /// <summary>App assembly used to resolve a bare <c>src</c> (e.g. <c>Assets/logo.png</c>) as embedded.</summary>
    public void SetAssembly(Assembly? assembly) => _assembly = assembly;

    /// <summary>The decoded image for <paramref name="src"/>, or null (missing/undecodable). Cached.</summary>
    public SKImage? Get(string? src)
    {
        if (string.IsNullOrEmpty(src)) return null;
        if (_cache.TryGetValue(src, out var cached)) return cached;
        SKImage? img = null;
        try
        {
            var bytes = LoadBytes(src);
            if (bytes is { Length: > 0 }) img = SKImage.FromEncodedData(bytes);
        }
        catch { img = null; } // unresolved/undecodable → cache null (paint nothing), never throw
        _cache[src] = img;
        return img;
    }

    /// <summary>Intrinsic pixel size of <paramref name="src"/>, for layout; null if it can't load.</summary>
    public (int W, int H)? Size(string? src) => Get(src) is { } img ? (img.Width, img.Height) : null;

    private byte[]? LoadBytes(string src)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = src.IndexOf(',');
            if (comma < 0) return null;
            var payload = src[(comma + 1)..];
            return src[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return CupriSource.Url(new Uri(src)).ReadBytes();
        if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return CupriSource.File(new Uri(src).LocalPath).ReadBytes();

        // A bare/relative name: embedded in the app assembly if one is registered, else a local file.
        if (_assembly is not null)
        {
            try { return CupriSource.Embedded(_assembly, src).ReadBytes(); }
            catch (CupriResourceException) { /* not embedded → fall through to a file */ }
        }
        return CupriSource.File(src).ReadBytes();
    }

    public void Dispose()
    {
        foreach (var img in _cache.Values) img?.Dispose();
        _cache.Clear();
    }
}
