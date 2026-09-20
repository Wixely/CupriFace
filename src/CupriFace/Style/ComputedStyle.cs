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

    /// <summary>Tracking, in px: extra advance added after every grapheme CLUSTER, negative to close
    /// letters up. <c>normal</c> is 0. Inherited, like the other text properties.
    ///
    /// <para>Per cluster rather than per glyph or per char, so a ligature is spaced once and a
    /// combining mark is not pushed off the letter it belongs to.</para></summary>
    public float LetterSpacing;

    public Length Width = Length.Auto, Height = Length.Auto;
    public Length MinWidth = Length.Auto, MinHeight = Length.Auto;
    public Length MaxWidth = Length.Auto, MaxHeight = Length.Auto;

    public LengthEdges Margin = LengthEdges.Zero;
    public LengthEdges Padding = LengthEdges.Zero;

    /// <summary><c>box-sizing: border-box</c> — a declared width/height (and min/max) describes the
    /// BORDER box, so padding and borders come out of it rather than being added on top. CSS
    /// defaults to content-box, which this keeps, but almost every real stylesheet flips it
    /// globally: without it a full-bleed <c>width:100%</c> container with padding overflows its
    /// parent by twice that padding, and anything centred inside is silently pushed off-centre
    /// (#76).</summary>
    public bool BorderBox;

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
    // repeat(auto-fill|auto-fit, …): the COUNT depends on the container size, so it cannot expand
    // at parse time — the pattern and its slot in the fixed tracks wait for LayoutGrid.
    public GridAutoRepeat? GridRepeatColumns, GridRepeatRows;
    public Dictionary<string, int>? GridColumnLines, GridRowLines; // [name] → 1-based grid line

    // Grid item
    public GridPlacement GridColumn = GridPlacement.Auto;
    public GridPlacement GridRow = GridPlacement.Auto;

    // Border
    public float BorderTop, BorderRight, BorderBottom, BorderLeft;
    public SKColor BorderTopColor = SKColors.Black, BorderRightColor = SKColors.Black,
                   BorderBottomColor = SKColors.Black, BorderLeftColor = SKColors.Black;

    /// <summary>The border colour as the <c>border-color</c> shorthand means it: reading gives the
    /// top edge's, writing sets all four. Kept so the single-colour callers — the transition on
    /// <c>border-color</c>, and layout's "does this box paint anything" test — say what they mean
    /// without knowing the sides exist.</summary>
    public SKColor BorderColor
    {
        get => BorderTopColor;
        set => BorderTopColor = BorderRightColor = BorderBottomColor = BorderLeftColor = value;
    }

    /// <summary>True when any edge would actually draw: it has a width AND a visible colour. A
    /// one-sided border makes the other three transparent-by-absence rather than zero-width, so a
    /// test against one shared colour would have answered for the wrong edge.</summary>
    public bool AnyBorderVisible =>
        (BorderTop > 0 && BorderTopColor.Alpha > 0) || (BorderRight > 0 && BorderRightColor.Alpha > 0)
        || (BorderBottom > 0 && BorderBottomColor.Alpha > 0) || (BorderLeft > 0 && BorderLeftColor.Alpha > 0);
    public BorderLineStyle BorderStyle = BorderLineStyle.Solid;
    /// <summary>Per corner and per axis, and unresolved: a percentage is a fraction of the BOX, so it
    /// becomes a number only when there is a box to measure it against. <see cref="BorderRadiusSpec.Resolve"/>.</summary>
    public BorderRadiusSpec BorderRadius;

    // Paint
    public SKColor Background = SKColors.Transparent;
    public float Opacity = 1f;

    // Filter (CSS filter chain — blur / colour-matrix / drop-shadow). Not inherited.
    public List<FilterOp>? Filter;

    // Backdrop filter: blur (etc.) applied to what's painted BEHIND this element. Only honoured on a
    // full-viewport top-layer element (a modal/drawer/shelf scrim) — it blurs the page behind it.
    public List<FilterOp>? BackdropFilter;

    // Box shadow layers (CSS box-shadow) — outset drop shadows and/or inset inner shadows. Not inherited.
    public List<BoxShadow>? BoxShadow;

    // Background gradient (CSS linear-gradient()/radial-gradient()); painted over Background. Not inherited.
    public Gradient? BackgroundGradient;

    // Transform (applied around TransformOrigin within the border box at paint time)
    public bool HasTransform;
    public float TranslateX, TranslateY, RotateDeg;
    public float ScaleX = 1f, ScaleY = 1f;

    // transform-origin — the transform's fixed point, resolved against the border box. The CSS
    // initial value is `50% 50%`, so the default keeps the centre behaviour every transform had
    // before this was honoured. NOT inherited (deliberately absent from InheritFrom): an origin is
    // a property of one element's own box, and inheriting it would silently re-anchor children.
    public Length TransformOriginX = new(LengthUnit.Percent, 50f);
    public Length TransformOriginY = new(LengthUnit.Percent, 50f);

    /// <summary>The transform's fixed point as an offset inside a border box of the given size.
    /// Painting and hit-testing must pivot about the SAME point — an element that paints anchored
    /// to its bottom edge but tests as if anchored to its centre is clickable where it isn't drawn
    /// — so both go through here rather than each computing a pivot of their own.</summary>
    public (float X, float Y) TransformPivot(float width, float height) =>
        (TransformOriginX.Resolve(width, width / 2f), TransformOriginY.Resolve(height, height / 2f));

    // Animation
    public string? AnimationName;
    public float AnimationDuration; // seconds
    public float AnimationDelay;    // seconds; negative starts part-way through, as in CSS
    public float AnimationIterations = 1f;

    /// <summary>The animation's timing function. Parsed but THROWN AWAY until now: every keyword was
    /// matched and discarded, so `ease-out` and `linear` produced identical values at every sample
    /// and an animation only ever ran linearly (noticed while isolating #184). The curve machinery
    /// already existed for transitions; animations simply never asked for it.</summary>
    public Easing AnimationEasing = Easing.Linear; // CSS default: once; `infinite` is +∞
    public bool AnimationFillForwards;  // hold the last frame after the run
    public bool AnimationFillBackwards; // show the first frame during the delay
    internal AnimationBase? AnimBase;    // the values a keyframe overrides, captured before its first frame

    // Transitions (NOT inherited — deliberately absent from InheritFrom). Null unless the element
    // declares `transition`. Each entry animates one paint property when its target value changes.
    public List<TransitionSpec>? Transitions;

    // CSS custom properties (design tokens). Inherit by default; resolved by var(). Copy-on-write:
    // InheritFrom SHARES the parent's dictionary (most nodes declare no tokens of their own, and copying
    // the theme's ~15 tokens for every node dominated rebuild allocations); a node that declares one
    // clones first via OwnCustomProps().
    public Dictionary<string, string> CustomProps = new();
    private bool _sharedProps; // CustomProps currently references an ancestor's dictionary

    /// <summary>The node's own (writable) custom-prop dictionary — clones the shared ancestor copy on
    /// first write. All writers must go through this; writing CustomProps directly while shared would
    /// corrupt every node inheriting from the same ancestor.</summary>
    public Dictionary<string, string> OwnCustomProps()
    {
        if (_sharedProps) { CustomProps = new Dictionary<string, string>(CustomProps); _sharedProps = false; }
        return CustomProps;
    }

    // Text (inherited)
    public SKColor Color = SKColors.Black;
    public float FontSize = 16f;
    public int FontWeight = 400;
    public string FontFamily = "sans-serif";
    public float LineHeight = 1.2f; // multiple of font-size

    /// <summary>An ABSOLUTE line box in px, when <c>line-height</c> was given as a length. Null when
    /// it is a ratio, which is the usual case and the initial value.
    ///
    /// <para>A length cannot be folded into <see cref="LineHeight"/>, and folding it was the bug: a
    /// px value was divided by a hardcoded 16 to make a ratio "refined once font-size is known", and
    /// nothing ever refined it. The line box came out font-size/16 times too tall — three times over
    /// at 48px, exactly once at 16px, so it looked like a fixed factor to anyone testing at a single
    /// size (#181).</para>
    ///
    /// <para>Inherited as a length, which is what CSS does with a length.</para></summary>
    public float? LineHeightPx;
    public TextAlign TextAlign = TextAlign.Left;
    public WhiteSpaceMode WhiteSpace = WhiteSpaceMode.Normal; // inherited
    // Mid-token line breaking (inherited, like all text-wrapping behaviour). Two flags because they
    // are two CSS properties that cascade independently: WordBreakAll is `word-break: break-all`
    // (break between ANY two characters once the line is full); OverflowWrapBreak is
    // `overflow-wrap` / legacy `word-wrap` `break-word`/`anywhere` (break inside a token only when
    // it cannot fit on a line of its own — the emergency break for a 62-char address).
    public bool WordBreakAll;      // inherited
    public bool OverflowWrapBreak; // inherited
    public CursorType Cursor = CursorType.Auto; // inherited; Auto = unspecified (document infers one)
    public FontSlant FontStyle = FontSlant.Normal; // inherited
    // Real CSS doesn't inherit text-decoration; it propagates to in-flow descendants, which a browser
    // draws as one line across the ancestor's line boxes. We inherit it instead and let each text run
    // draw its own segment — visually the same for the cases that matter (a link with a <b> inside),
    // and it means `a { text-decoration: underline }` behaves as authors expect.
    public TextDecorations Decorations = TextDecorations.None;

    /// <summary>Copy inherited properties down from a parent as the starting point.</summary>
    public void InheritFrom(ComputedStyle parent)
    {
        CustomProps = parent.CustomProps; _sharedProps = true; // custom props inherit (copy-on-write)
        Color = parent.Color;
        FontSize = parent.FontSize;
        FontWeight = parent.FontWeight;
        FontFamily = parent.FontFamily;
        LineHeight = parent.LineHeight;
        LineHeightPx = parent.LineHeightPx;
        TextAlign = parent.TextAlign;
        WhiteSpace = parent.WhiteSpace;
        WordBreakAll = parent.WordBreakAll;
        OverflowWrapBreak = parent.OverflowWrapBreak;
        Cursor = parent.Cursor;
        FontStyle = parent.FontStyle;
        Decorations = parent.Decorations;
        LetterSpacing = parent.LetterSpacing;
    }

    public bool IsFlexContainer => Display == DisplayType.Flex;
    public bool IsGridContainer => Display == DisplayType.Grid;
    public bool MainIsHorizontal => FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;
}
