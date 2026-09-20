using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Svg;

/// <summary>
/// A parsed <c>&lt;svg&gt;</c>, ready for the engine to paint.
///
/// <para>Immutable and free of Skia handles: it is a viewBox and a list of path strings, so it can
/// be built once at rebuild time, held in the registry, and read from the paint thread without a
/// lifetime question. The rasterising happens in the engine, at whatever resolution the frame is
/// actually drawn at.</para>
/// </summary>
public sealed class SvgDrawing(SKRect viewBox, (float W, float H)? naturalSize,
                               IReadOnlyList<VectorShape> shapes) : IVectorDrawing
{
    public SKRect ViewBox { get; } = viewBox;

    /// <summary>The drawing's own <c>width</c>/<c>height</c> when it states them in absolute units,
    /// so an inline icon sizes itself the way an image does. Null when it gives only a viewBox, or
    /// gives percentages — then the CSS box decides, which is the more common case in a layout.</summary>
    public (float W, float H)? NaturalSize { get; } = naturalSize;

    public IReadOnlyList<VectorShape> Shapes { get; } = shapes;
}
