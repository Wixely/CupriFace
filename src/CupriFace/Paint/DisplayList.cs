using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// An immutable list of absolute-positioned paint commands — the commit-snapshot
/// boundary from DESIGN.md §7.2. The UI thread *builds* a DisplayList from the laid-out
/// render tree; a render thread *rasterises* it, never touching live tree state. For M0
/// the two run on one thread, but the seam is the immutable snapshot below.
/// </summary>
public sealed class DisplayList
{
    private readonly List<PaintCommand> _commands = new();
    public IReadOnlyList<PaintCommand> Commands => _commands;
    internal void Add(PaintCommand cmd) => _commands.Add(cmd);
    public int Count => _commands.Count;
}

public abstract record PaintCommand;

public sealed record FillRect(float X, float Y, float W, float H, float Radius, SKColor Color) : PaintCommand;

public sealed record BorderRect(
    float X, float Y, float W, float H, float Radius,
    float Top, float Right, float Bottom, float Left, SKColor Color,
    BorderLineStyle Style = BorderLineStyle.Solid) : PaintCommand;

public sealed record TextRun(
    float X, float Y, float ContainerWidth, float LineWidth, float LineHeight,
    string Text, string Family, int Weight, float Size, SKColor Color, TextAlign Align) : PaintCommand;

public sealed record PushClip(float X, float Y, float W, float H, float Radius) : PaintCommand;

public sealed record PopClip : PaintCommand;

/// <summary>Push a 2D transform applied around (CenterX, CenterY).</summary>
public sealed record PushTransform(
    float CenterX, float CenterY, float TranslateX, float TranslateY,
    float ScaleX, float ScaleY, float RotateDeg) : PaintCommand;

public sealed record PopTransform : PaintCommand;

/// <summary>Composite the wrapped subtree as a group at <paramref name="Alpha"/> (0..1).</summary>
public sealed record PushOpacity(float Alpha) : PaintCommand;

public sealed record PopOpacity : PaintCommand;

/// <summary>Fill an SVG path (authored in a <paramref name="ViewBox"/>-square) scaled into the box.</summary>
public sealed record FillPath(
    float X, float Y, float Width, float Height, float ViewBox, string PathData, SKColor Color) : PaintCommand;

/// <summary>How an image is fitted into its box (CSS <c>object-fit</c>).</summary>
public enum ObjectFit { Contain, Cover, Fill, None }

/// <summary>A resize grip (corner grab handle) in a control's bottom-right corner (CSS <c>resize</c>).</summary>
public sealed record ResizeGrip(float X, float Y, float Size, SKColor Color) : PaintCommand;

/// <summary>Draw a decoded raster image into the box, fitted per <paramref name="Fit"/>, clipped to
/// the (optionally rounded) box.</summary>
public sealed record DrawImage(
    float X, float Y, float W, float H, SKImage Image, ObjectFit Fit, float Radius) : PaintCommand;
