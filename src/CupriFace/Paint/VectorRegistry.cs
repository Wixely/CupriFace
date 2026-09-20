using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// One shape of a vector drawing: a path in the drawing's own coordinates, with how to paint it.
///
/// <para>Path DATA rather than an <c>SKPath</c>, for the same reason <c>FillPath</c> uses it: the
/// display list is a value type that gets diffed between frames, and two strings compare in a way
/// two native path handles do not. Skia parses the <c>d</c> grammar itself — arcs, smooth curves and
/// all — so nothing here re-implements it.</para>
/// </summary>
/// <param name="PathData">SVG path data, in the drawing's viewBox coordinates.</param>
/// <param name="Fill">Fill colour; transparent means do not fill.</param>
/// <param name="Stroke">Stroke colour; transparent means do not stroke.</param>
/// <param name="StrokeWidth">Stroke width in viewBox units.</param>
/// <param name="Opacity">Multiplied into both colours, 0..1.</param>
/// <param name="EvenOdd">Even-odd fill rule instead of the default non-zero.</param>
/// <param name="DashArray">Dash pattern in viewBox units, or null for a solid stroke.</param>
/// <param name="DashOffset">Where the dash pattern starts — what a progress ring animates.</param>
/// <param name="Cap">Stroke end cap.</param>
/// <param name="Join">Stroke corner join.</param>
/// <param name="Transform">The shape's own transform, already composed with its ancestors'.</param>
public readonly record struct VectorShape(
    string PathData,
    SKColor Fill,
    SKColor Stroke,
    float StrokeWidth,
    float Opacity,
    bool EvenOdd,
    float[]? DashArray,
    float DashOffset,
    SKStrokeCap Cap,
    SKStrokeJoin Join,
    SKMatrix Transform);

/// <summary>
/// A resolution-independent drawing an optional package has prepared for one element — what
/// <c>CupriFace.Svg</c> turns an inline <c>&lt;svg&gt;</c> into.
///
/// <para>Deliberately NOT an <see cref="ISurfaceSource"/>. A surface hands over pixels, which is
/// right for a video or a Lottie frame and wrong for a logo: rasterising at layout size means it
/// blurs when the window is zoomed or the element is scaled, and it costs memory proportional to the
/// box rather than to the artwork. These shapes go into the display list as paths and are rasterised
/// at whatever resolution the frame is actually drawn at, composing with the engine's own transform,
/// clip and opacity stack for free.</para>
/// </summary>
public interface IVectorDrawing
{
    /// <summary>The viewBox: the coordinate space <see cref="Shapes"/> are authored in.</summary>
    SKRect ViewBox { get; }

    /// <summary>Intrinsic size in px for layout, or null to take the box the CSS gives it. Comes
    /// from the drawing's own <c>width</c>/<c>height</c> when it states them.</summary>
    (float W, float H)? NaturalSize { get; }

    /// <summary>The shapes, in paint order.</summary>
    IReadOnlyList<VectorShape> Shapes { get; }
}

/// <summary>
/// Vector drawings by key, the way <see cref="SurfaceRegistry"/> holds live surfaces.
///
/// <para>The seam that keeps SVG OUT of the engine: the core knows how to draw a list of paths and
/// nothing about the <c>&lt;svg&gt;</c> language. An element carries
/// <c>data-cupri-vector="&lt;key&gt;"</c>, the optional package registers a drawing under that key,
/// and an app that never draws one links none of it.</para>
/// </summary>
public sealed class VectorRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, IVectorDrawing> _drawings = new(StringComparer.Ordinal);

    public void Register(string key, IVectorDrawing drawing)
    {
        lock (_lock) _drawings[key] = drawing;
    }

    public void Unregister(string key)
    {
        lock (_lock) _drawings.Remove(key);
    }

    public IVectorDrawing? Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_lock) return _drawings.TryGetValue(key, out var d) ? d : null;
    }

    /// <summary>Whether anything is registered — the doctor asks, so it can tell "no SVG support
    /// installed" from "this particular drawing failed".</summary>
    public bool Any { get { lock (_lock) return _drawings.Count > 0; } }
}
