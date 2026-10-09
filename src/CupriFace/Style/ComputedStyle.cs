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

    /// <summary>
    /// The OUTER half of <c>display</c>: this box sits on a line beside its siblings and shrinks to
    /// its content, instead of taking a line of its own and filling the width.
    ///
    /// <para><b>A flag rather than a DisplayType, deliberately.</b> CSS's <c>display</c> is two
    /// decisions wearing one name — an outer role (block or inline) and an inner one (flow, flex,
    /// grid) — and <c>inline-flex</c> changes only the outer. Adding <c>InlineFlex</c> to the enum
    /// would have meant auditing all 34 places that compare against <c>DisplayType.Flex</c> and
    /// getting every one right, where missing one leaves a flex container that silently stops
    /// behaving like one. This way the inner role is untouched and exactly one gate consults the
    /// outer: <c>IsInlineLevel</c>, which decides whether a child joins an inline formatting
    /// context.</para>
    ///
    /// <para>Set by <c>inline-flex</c> and <c>inline-grid</c>. <c>inline-block</c> does not need it —
    /// it is already its own DisplayType, and its inner role is plain flow.</para>
    /// </summary>
    public bool InlineLevel;

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

    // ---- Outline: a border that is NOT part of the box ------------------------------------------
    //
    // The whole reason this property exists in CSS, and the whole reason it is worth having here: it
    // is painted OUTSIDE the border box and occupies no space, so showing and hiding one cannot move
    // anything. A focus ring is the case that matters. Drawn with `border` instead, the element grows
    // by twice the width the moment it is focused and every sibling after it shifts -- which is what
    // a controller-driven UI looks like when the selection moves.
    //
    // Deliberately narrower than CSS: one width, one colour, one offset, no per-side anything. A
    // focus ring that differs per side is not a thing anyone has asked for, and the restraint keeps
    // this out of the layout entirely.
    public float OutlineWidth;
    public SKColor OutlineColor = SKColors.Transparent;
    public float OutlineOffset;
    public BorderLineStyle OutlineStyle = BorderLineStyle.Solid;

    /// <summary>
    /// <c>font-variant-numeric: tabular-nums</c> — every digit takes the width of the widest digit,
    /// so a value that changes does not change the width of the thing showing it.
    ///
    /// <para>In most faces a <c>1</c> is narrower than a <c>0</c>. That is right for prose and wrong
    /// for a clock, a score, a counter or a percentage: as <c>15:00:32</c> ticks over, the text
    /// physically changes width, the box holding it resizes, and everything beside it twitches. The
    /// reported case was a scoreboard whose badges jittered once a second.</para>
    /// </summary>
    public bool TabularNums;

    /// <summary>Worth painting: a width, a visible colour, and not <c>outline-style: none</c>.</summary>
    public bool HasOutline =>
        OutlineWidth > 0 && OutlineColor.Alpha > 0 && OutlineStyle != BorderLineStyle.None;

    /// <summary>The author wrote <c>outline: none</c> — as opposed to not mentioning outline at all.
    ///
    /// <para>Both leave nothing to paint, so <see cref="HasOutline"/> cannot tell them apart, and the
    /// difference is the whole of what <c>:focus { outline: none }</c> means on the web: "I am styling
    /// this myself, stop drawing your ring". Without it the engine's ring was drawn on top of an
    /// author's own focus styling and the universal idiom for suppressing it did nothing.</para>
    /// </summary>
    public bool OutlineSuppressed;


    /// <summary>Per corner and per axis, and unresolved: a percentage is a fraction of the BOX, so it
    /// becomes a number only when there is a box to measure it against. <see cref="BorderRadiusSpec.Resolve"/>.</summary>
    public BorderRadiusSpec BorderRadius;

    // Paint
    public SKColor Background = SKColors.Transparent;
    public float Opacity = 1f;

    /// <summary><c>visibility</c>: false for <c>hidden</c> (and <c>collapse</c>, which is the same
    /// thing outside a table). The box is laid out and takes its space exactly as before — it simply
    /// paints nothing, cannot be clicked, cannot be tabbed to and is absent from the accessibility
    /// tree. That is the whole difference from <c>display: none</c>, which removes the box.
    ///
    /// <para><b>Inherited, and overridable per child</b> — which is why this is a field rather than
    /// a subtree decision. <c>visibility: visible</c> on a descendant of a hidden element makes that
    /// descendant visible again, so nothing may skip a subtree on the strength of it. The cascade
    /// gives this for free: <see cref="InheritFrom"/> runs before the author's rules, so a child's
    /// own declaration simply wins afterwards.</para></summary>
    public bool Visible = true;

    /// <summary><c>content</c>, decoded: the text a <c>::before</c>/<c>::after</c> box holds (often
    /// <c>""</c>, a box with nothing in it), or null for <c>none</c>/<c>normal</c> — no box at all,
    /// which is what a pseudo-element with no <c>content</c> declaration is. Read only on the
    /// element standing in for a pseudo-element (#261); meaningless anywhere else, as in CSS.</summary>
    public string? Content;

    /// <summary><c>content: attr(name)</c> — the owner's attribute, read when the tree is built,
    /// because the cascade has no element in hand.</summary>
    public string? ContentAttr;

    // Filter (CSS filter chain — blur / colour-matrix / drop-shadow). Not inherited.
    public List<FilterOp>? Filter;

    // Backdrop filter: blur (etc.) applied to what's painted BEHIND this element. Only honoured on a
    // full-viewport top-layer element (a modal/drawer/shelf scrim) — it blurs the page behind it.
    public List<FilterOp>? BackdropFilter;

    // Box shadow layers (CSS box-shadow) — outset drop shadows and/or inset inner shadows. Not inherited.
    public List<BoxShadow>? BoxShadow;

    /// <summary>
    /// The background's image layers, gradients and <c>url()</c> images alike, painted over
    /// <see cref="Background"/>. <b>First is topmost</b>, as in CSS. Null or empty = no image layer.
    ///
    /// <para>A single layer until #279: a declaration with several comma-separated images kept only
    /// the first, so a background built from two or three glows over a base colour — the normal way
    /// a designed background is made, and 38 of 165 corpus compositions — lost all but one of its
    /// lights.</para>
    ///
    /// <para>Not inherited.</para>
    /// </summary>
    public List<BackgroundLayer>? BackgroundLayers;

    /// <summary>Where each image layer sits and how it tiles (<c>background-size</c>,
    /// <c>-position</c>, <c>-repeat</c>) (#267). Paired with <see cref="BackgroundLayers"/> by
    /// index, repeating when shorter — which is how CSS pairs the longhands with the layers, and
    /// why they are a list of their own rather than a field on the layer. Not inherited.</summary>
    public List<BackgroundGeometry>? BackgroundGeometries;

    /// <summary>The geometry for layer <paramref name="index"/>: the list repeats to cover the
    /// layers, and an unset list is CSS's initial value throughout.</summary>
    public BackgroundGeometry GeometryFor(int index) =>
        BackgroundGeometries is { Count: > 0 } g ? g[index % g.Count] : BackgroundGeometry.Default;

    /// <summary>True when anything would paint over the background colour — the layout pass's
    /// "does this box draw" test, and the one place that only needs to know whether, not what.</summary>
    public bool HasBackgroundImage => BackgroundLayers is { Count: > 0 };

    /// <summary><c>clip-path</c>: a basic shape the element's paint — children, shadow and all —
    /// is clipped to, in its own border box (#268). Null is <c>none</c>. Not inherited.</summary>
    public ClipShape? ClipPath;

    // Transform (applied around TransformOrigin within the border box at paint time)
    public bool HasTransform;
    public float TranslateX, TranslateY, RotateDeg;
    public float ScaleX = 1f, ScaleY = 1f;

    // ---- The third dimension (#269) ---------------------------------------------------------
    //
    // rotateX/rotateY, translateZ and perspective() are the element's own; `perspective` is the
    // PROPERTY, which an element lends to its children about its perspective-origin; preserve-3d
    // hands the element's 4x4 to its children rather than flattening them into its image; and
    // backface-visibility: hidden culls a face whose front is turned away. None of them is
    // inherited. Transform3D composes them.
    public float RotateXDeg, RotateYDeg, TranslateZ;
    /// <summary>The <c>perspective()</c> FUNCTION inside <c>transform</c>, in px; 0 = none.</summary>
    public float PerspectiveFn;
    /// <summary>The <c>perspective</c> PROPERTY: the viewer's distance for this element's children,
    /// in px; 0 = <c>none</c>.</summary>
    public float Perspective;
    public Length PerspectiveOriginX = new(LengthUnit.Percent, 50f);
    public Length PerspectiveOriginY = new(LengthUnit.Percent, 50f);
    public bool Preserve3D;
    public bool BackfaceHidden;

    /// <summary>The PERCENTAGE part of a translate, kept apart from the px part because it is a
    /// fraction of a box that is only known at paint time: <c>translate(-50%, -50%)</c> means half
    /// of the element's OWN border box, which is what makes it the commonest centring idiom there
    /// is. It used to be parsed as 0px and silently dropped — the element painted exactly where it
    /// would with no transform at all, and nothing reported it (#258). Interpolated in its own unit,
    /// the way a percentage width is, and resolved by <see cref="ResolvedTranslate"/>.</summary>
    public float TranslateXPct, TranslateYPct;

    /// <summary>The translation in px for a border box of the given size: the px part plus the
    /// percentage part of that box. Painting, hit-testing and the damage diff must all move the
    /// element by the SAME amount, so each asks here rather than adding the two up itself.</summary>
    public (float X, float Y) ResolvedTranslate(float width, float height) =>
        (TranslateX + TranslateXPct / 100f * width, TranslateY + TranslateYPct / 100f * height);

    // transform-origin — the transform's fixed point, resolved against the border box. The CSS
    // initial value is `50% 50%`, so the default keeps the centre behaviour every transform had
    // before this was honoured. NOT inherited (deliberately absent from InheritFrom): an origin is
    // a property of one element's own box, and inheriting it would silently re-anchor children.
    public Length TransformOriginX = new(LengthUnit.Percent, 50f);
    public Length TransformOriginY = new(LengthUnit.Percent, 50f);

    /// <summary>The author wrote a <c>transform-origin</c>, as opposed to the default above
    /// standing in. An HTML box cannot tell the two apart and need not; an SVG shape can, because
    /// ITS initial origin is <c>0 0</c> of the viewBox rather than its own centre (#262).</summary>
    public bool TransformOriginSet;

    /// <summary><c>transform-box: fill-box</c> — the transform's reference box is the shape's own
    /// bounds rather than the viewBox, which is what "spin this icon about its centre" is spelled
    /// as. Only an SVG shape reads it; an HTML box's reference is always its border box.</summary>
    public bool TransformBoxFill;

    // ---- SVG paint ------------------------------------------------------------------------------
    //
    // The presentation properties of a shape inside an inline <svg>, so that a stylesheet rule and
    // a @keyframes stop reach a <path> the way they reach a <div> (#262). Null means "nothing in
    // the cascade said" — the drawing's own parsed attributes then stand. Inherited, as they are
    // in SVG: a <g fill="red"> colours every shape under it that does not say otherwise, and the
    // presentation attributes themselves enter the cascade as the lowest-priority declarations
    // (StyleResolver), so attribute, rule and inheritance resolve in the one place.
    public SKColor? SvgFill, SvgStroke;
    public float? SvgStrokeWidth, SvgStrokeDashOffset, SvgFillOpacity, SvgStrokeOpacity;
    /// <summary><c>stroke-dasharray</c>: null when unset, EMPTY for <c>none</c> (a solid stroke).</summary>
    public float[]? SvgStrokeDashArray;

    /// <summary>The transform's fixed point as an offset inside a border box of the given size.
    /// Painting and hit-testing must pivot about the SAME point — an element that paints anchored
    /// to its bottom edge but tests as if anchored to its centre is clickable where it isn't drawn
    /// — so both go through here rather than each computing a pivot of their own.</summary>
    public (float X, float Y) TransformPivot(float width, float height) =>
        (TransformOriginX.Resolve(width, width / 2f), TransformOriginY.Resolve(height, height / 2f));

    /// <summary>
    /// The animations on this element, in the order written — CSS allows a list, and the LAST entry
    /// to touch a property is the one that wins (#284).
    ///
    /// <para>There used to be room for exactly one, in seven scalar fields. A comma-separated
    /// shorthand was tokenised on spaces alone, so the entries collapsed into a single hybrid built
    /// from parts of each: <c>slide 2s linear both, fade 1s linear 1s both</c> ran SLIDE with
    /// FADE's delay, and the even-looking case ran the first animation at its very first frame,
    /// which looks exactly like nothing happening. The longhand list failed differently and just as
    /// quietly — <c>animation-name: slide,fade</c> was stored whole and matched no
    /// <c>@keyframes</c>. Neither said a word.</para>
    /// </summary>
    public List<AnimationSpec>? Animations;

    internal AnimationBase? AnimBase;    // the values a keyframe overrides, captured before its first frame

    /// <summary>True when any entry could actually run — a named animation with a duration.</summary>
    public bool HasAnimation
    {
        get
        {
            if (Animations is not { Count: > 0 } list) return false;
            foreach (var a in list) if (a.Name is not null && a.Duration > 0) return true;
            return false;
        }
    }

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

    /// <summary><c>text-transform</c> — inherited, like every text property, and applied to each
    /// text node as the tree is built (#266).</summary>
    public TextTransform TextTransform = TextTransform.None;

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
        Visible = parent.Visible;          // inherited; a child's own `visibility: visible` wins after this
        FontStyle = parent.FontStyle;
        Decorations = parent.Decorations;
        LetterSpacing = parent.LetterSpacing;
        TextTransform = parent.TextTransform;
        // Inherited, like every other font property here, and for a reason worth stating: the
        // declaration goes on the element, but the thing that gets MEASURED is the text node inside
        // it. Without this line the flag was set on the div, read as false on its text, and
        // tabular-nums did precisely nothing while appearing to be supported.
        TabularNums = parent.TabularNums;
        // SVG paint inherits down the tree the way text colour does — a group's fill is its
        // children's fill until one of them says otherwise.
        SvgFill = parent.SvgFill; SvgStroke = parent.SvgStroke;
        SvgStrokeWidth = parent.SvgStrokeWidth; SvgStrokeDashOffset = parent.SvgStrokeDashOffset;
        SvgFillOpacity = parent.SvgFillOpacity; SvgStrokeOpacity = parent.SvgStrokeOpacity;
        SvgStrokeDashArray = parent.SvgStrokeDashArray;
    }

    public bool IsFlexContainer => Display == DisplayType.Flex;
    public bool IsGridContainer => Display == DisplayType.Grid;
    public bool MainIsHorizontal => FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;
}
