using AngleSharp.Dom;
using CupriFace.Style;

namespace CupriFace.Dom;

/// <summary>
/// A node in the render tree (mirrors the relevant DOM subtree). Carries computed
/// style and, after layout, its border-box geometry relative to the parent's content
/// box. Text nodes have <see cref="Text"/> set and no <see cref="Element"/>.
/// </summary>
public sealed class RenderNode
{
    public string Tag = "";
    public IElement? Element;
    public string? Text;              // non-null for text nodes
    public ComputedStyle Style = new();
    public RenderNode? Parent;
    public readonly List<RenderNode> Children = new();

    // ---- Layout results (border-box, in parent content coordinates) ----
    public float X, Y, Width, Height;

    // Resolved box metrics (px)
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PadTop, PadRight, PadBottom, PadLeft;
    public float BorderTopW, BorderRightW, BorderBottomW, BorderLeftW;

    // For text nodes: laid-out lines (set by the text layout pass in M2).
    public List<TextLine>? Lines;

    // For icon nodes: an SVG path (24×24 viewBox) filled with the computed color.
    public string? IconPath;

    // For image nodes (<cupri-image>): the source to decode + paint (resolved by ImageStore).
    public string? ImageSrc;

    // For chart plots (line chart / sparkline): normalised points "x0,y0 x1,y1 …" in 0..1 (y=0 top),
    // painted as a polyline (+ area fill / dots) scaled into the content box. Set from data-cupri-line.
    public string? ChartLine;

    // Overlays: position:fixed nodes are lifted to the top layer and painted last,
    // with X/Y already in absolute viewport coordinates.
    public bool IsTopLayer;

    // Scrolling (overflow:scroll/auto): full content extent + current offset. ScrollY is
    // interaction state — layout recomputes ScrollContentHeight but preserves ScrollY.
    public float ScrollContentHeight;
    public float ScrollY;
    public float ScrollX; // horizontal caret scroll for a single-line (white-space:nowrap) text field

    public float ContentBoxWidth => Width - HorizontalInsets;

    // User-dragged size (CSS resize) — interaction state, preserved across rebuilds like ScrollY.
    // Null = use the CSS size. Overrides width/height in layout, then clamped to min/max-*.
    public float? ResizeW, ResizeH;
    public float ContentBoxHeight => Height - VerticalInsets;
    public float MaxScrollY => MathF.Max(0, ScrollContentHeight - ContentBoxHeight);
    public bool IsScrollable => MaxScrollY > 0.5f;

    public bool IsText => Text is not null;

    public float ContentLeftInset => BorderLeftW + PadLeft;
    public float ContentTopInset => BorderTopW + PadTop;
    public float HorizontalInsets => BorderLeftW + PadLeft + PadRight + BorderRightW;
    public float VerticalInsets => BorderTopW + PadTop + PadBottom + BorderBottomW;

    public void AddChild(RenderNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }
}

/// <summary>A single laid-out line of text produced by the text layout pass.</summary>
public sealed class TextLine
{
    public required string Text;
    public float X, Y;      // baseline-independent top-left, relative to node content box
    public float Width, Height;
}
