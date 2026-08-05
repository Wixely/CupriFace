using System.Reflection;
using CupriFace.Resources;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// Decodes and caches raster images for <c>&lt;cupri-image&gt;</c>. The <c>src</c> resolves through
/// the <see cref="CupriSource"/> pipeline (embedded / file / URL) or an inline <c>data:</c> URI, so
/// images inherit the same trust model as markup/CSS.
///
/// Local sources (embedded / file / <c>data:</c>) decode synchronously on first use. <b>Remote</b>
/// (<c>http(s)</c>) sources load <b>asynchronously</b> on a background task so a slow network never
/// blocks the first paint: the image reads as "not ready" until it arrives, then <see cref="TakeArrived"/>
/// flags a repaint. A decode/load failure caches <c>null</c>, so it never retries or throws at paint time.
/// </summary>
public sealed class ImageStore : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SKImage?> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal); // remote loads in flight
    private Assembly? _assembly;
    private volatile bool _arrived;
    private bool _disposed;

    /// <summary>Policy for remote (<c>http(s)</c>) image URLs (https-only, size cap, timeout…). Null =
    /// the strict <see cref="CupriSource.Url(System.Uri, CupriSourceOptions?)"/> defaults.</summary>
    public CupriSourceOptions? UrlOptions { get; set; }

    /// <summary>App assembly used to resolve a bare <c>src</c> (e.g. <c>Assets/logo.png</c>) as embedded.</summary>
    public void SetAssembly(Assembly? assembly) => _assembly = assembly;

    /// <summary>True (once, then reset) if a background image load has completed since the last call —
    /// the host uses it to trigger a repaint so the image appears. Cheap to poll every frame.</summary>
    public bool TakeArrived()
    {
        if (!_arrived) return false;
        _arrived = false;
        return true;
    }

    /// <summary>The decoded image for <paramref name="src"/>, or null (missing / undecodable / a remote
    /// image still loading). Cached; remote loads are kicked off on the first request.</summary>
    public SKImage? Get(string? src)
    {
        if (string.IsNullOrEmpty(src)) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(src, out var cached)) return cached;
            if (IsRemote(src))
            {
                if (_pending.Add(src)) StartRemoteLoad(src); // fetch once, in the background
                return null;                                 // not ready yet — paint nothing this frame
            }
        }

        // Local source (embedded / file / data:) — fast, decode synchronously.
        var img = TryDecode(src);
        lock (_lock) { if (!_disposed) _cache[src] = img; else img?.Dispose(); }
        return img;
    }

    /// <summary>Intrinsic pixel size of <paramref name="src"/>, for layout; null if it can't load yet.</summary>
    public (int W, int H)? Size(string? src) => Get(src) is { } img ? (img.Width, img.Height) : null;

    private static bool IsRemote(string src) =>
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private void StartRemoteLoad(string src) => Task.Run(() =>
    {
        var img = TryDecode(src);
        lock (_lock)
        {
            _pending.Remove(src);
            if (_disposed) { img?.Dispose(); return; }
            _cache[src] = img;
            _arrived = true; // signal the host to repaint
        }
    });

    private SKImage? TryDecode(string src)
    {
        try
        {
            var bytes = LoadBytes(src);
            return bytes is { Length: > 0 } ? SKImage.FromEncodedData(bytes) : null;
        }
        catch { return null; } // unresolved/undecodable → null (paint nothing), never throw
    }

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
        if (IsRemote(src))
            return CupriSource.Url(new Uri(src), UrlOptions).ReadBytes();
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
        lock (_lock)
        {
            _disposed = true;
            foreach (var img in _cache.Values) img?.Dispose();
            _cache.Clear();
        }
    }
}
