using SkiaSharp;

namespace CupriFace.Style;

/// <summary>
/// Resolved (computed) style for one render node. A dense value object rather than a
/// property bag — cheap to copy and cache-friendly (DESIGN.md §7.4). Inheritance is
/// applied by <see cref="InheritFrom"/> before the cascade runs.
/// </summary>
public sealed class ComputedStyle
{
    // Layout / box
    public DisplayType Display = DisplayType.Block;
    public PositionType Position = PositionType.Static;
    public OverflowMode Overflow = OverflowMode.Visible;
    public ResizeMode Resize = ResizeMode.None;
    public int ZIndex;

    public Length Width = Length.Auto, Height = Length.Auto;
    public Length MinWidth = Length.Auto, MinHeight = Length.Auto;
    public Length MaxWidth = Length.Auto, MaxHeight = Length.Auto;

    public LengthEdges Margin = LengthEdges.Zero;
    public LengthEdges Padding = LengthEdges.Zero;

    // Absolute positioning insets
    public Length Top = Length.Auto, Right = Length.Auto, Bottom = Length.Auto, Left = Length.Auto;

    // Flex container
    public FlexDirection FlexDirection = FlexDirection.Row;
    public FlexWrapMode FlexWrap = FlexWrapMode.NoWrap;
    public JustifyContent JustifyContent = JustifyContent.FlexStart;
    public AlignItems AlignItems = AlignItems.Stretch;
    public AlignItems JustifyItems = AlignItems.Stretch; // grid inline-axis item alignment
    public float RowGap, ColumnGap;

    // Flex item
    public float FlexGrow;
    public float FlexShrink = 1f;
    public Length FlexBasis = Length.Auto;

    // Grid container
    public List<TrackSize>? GridTemplateColumns;
    public List<TrackSize>? GridTemplateRows;
    public TrackSize? GridAutoRows;
    public Dictionary<string, int>? GridColumnLines, GridRowLines; // [name] → 1-based grid line

    // Grid item
    public GridPlacement GridColumn = GridPlacement.Auto;
    public GridPlacement GridRow = GridPlacement.Auto;

    // Border
    public float BorderTop, BorderRight, BorderBottom, BorderLeft;
    public SKColor BorderColor = SKColors.Black;
    public BorderLineStyle BorderStyle = BorderLineStyle.Solid;
    public float BorderRadius;

    // Paint
    public SKColor Background = SKColors.Transparent;
    public float Opacity = 1f;

    // Filter (CSS filter chain — blur / colour-matrix / drop-shadow). Not inherited.
    public List<FilterOp>? Filter;

    // Backdrop filter: blur (etc.) applied to what's painted BEHIND this element. Only honoured on a
    // full-viewport top-layer element (a modal/drawer/shelf scrim) — it blurs the page behind it.
    public List<FilterOp>? BackdropFilter;

    // Transform (applied around the border-box centre at paint time)
    public bool HasTransform;
    public float TranslateX, TranslateY, RotateDeg;
    public float ScaleX = 1f, ScaleY = 1f;

    // Animation
    public string? AnimationName;
    public float AnimationDuration; // seconds

    // Transitions (NOT inherited — deliberately absent from InheritFrom). Null unless the element
    // declares `transition`. Each entry animates one paint property when its target value changes.
    public List<TransitionSpec>? Transitions;

    // CSS custom properties (design tokens). Inherit by default; resolved by var().
    public Dictionary<string, string> CustomProps = new();

    // Text (inherited)
    public SKColor Color = SKColors.Black;
    public float FontSize = 16f;
    public int FontWeight = 400;
    public string FontFamily = "sans-serif";
    public float LineHeight = 1.2f; // multiple of font-size
    public TextAlign TextAlign = TextAlign.Left;
    public WhiteSpaceMode WhiteSpace = WhiteSpaceMode.Normal; // inherited

    /// <summary>Copy inherited properties down from a parent as the starting point.</summary>
    public void InheritFrom(ComputedStyle parent)
    {
        CustomProps = new Dictionary<string, string>(parent.CustomProps); // custom props inherit
        Color = parent.Color;
        FontSize = parent.FontSize;
        FontWeight = parent.FontWeight;
        FontFamily = parent.FontFamily;
        LineHeight = parent.LineHeight;
        TextAlign = parent.TextAlign;
        WhiteSpace = parent.WhiteSpace;
    }

    public bool IsFlexContainer => Display == DisplayType.Flex;
    public bool IsGridContainer => Display == DisplayType.Grid;
    public bool MainIsHorizontal => FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;
}
