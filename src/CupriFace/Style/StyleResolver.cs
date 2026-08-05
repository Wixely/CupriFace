using System.Text.RegularExpressions;
using AngleSharp.Dom;
using CupriFace.Dom;

namespace CupriFace.Style;

/// <summary>
/// Builds the render tree from a DOM and resolves computed styles: user-agent
/// defaults → author rules (cascade by specificity then order) → inline style,
/// with inherited properties flowing down from the parent.
/// </summary>
public sealed class StyleResolver
{
    private static readonly Regex _repeat = new(@"repeat\(\s*(\d+)\s*,\s*([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _var = new(@"var\(\s*(--[\w-]+)\s*(?:,\s*([^)]*))?\)", RegexOptions.Compiled);

    /// <summary>Resolve var(--token[, fallback]) against the cascaded custom properties.</summary>
    private static string ResolveVars(string value, Dictionary<string, string> props)
    {
        if (!value.Contains("var(", StringComparison.Ordinal)) return value;
        for (var pass = 0; pass < 4 && value.Contains("var(", StringComparison.Ordinal); pass++)
            value = _var.Replace(value, m =>
                props.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Groups[2].Value.Trim());
        return value;
    }

    private readonly List<CssRule> _rules;
    private readonly float _viewportWidth;
    private readonly Dictionary<IElement, List<CssRule>> _matched = new();

    public StyleResolver(List<CssRule> rules, float viewportWidth = 1024f)
    {
        _rules = rules;
        _viewportWidth = viewportWidth;
    }

    public RenderNode BuildTree(IDocument document)
    {
        // Pre-match every rule using AngleSharp's real selector engine.
        foreach (var rule in _rules)
        {
            if (rule.Media is { } m && !m.Matches(_viewportWidth)) continue; // @media gate
            IHtmlCollection<IElement> hits;
            try { hits = document.QuerySelectorAll(rule.Selector); }
            catch { continue; } // skip selectors we can't compile (e.g. exotic pseudos)
            foreach (var el in hits)
            {
                if (!_matched.TryGetValue(el, out var list))
                    _matched[el] = list = new List<CssRule>();
                list.Add(rule);
            }
        }

        var body = document.Body ?? throw new InvalidOperationException("Document has no <body>.");
        var root = new RenderNode { Tag = "body", Element = body };
        ResolveStyle(root, parent: null);
        BuildChildren(root, body);
        return root;
    }

    private void BuildChildren(RenderNode parentNode, IElement parentEl)
    {
        foreach (var child in parentEl.ChildNodes)
        {
            switch (child)
            {
                case IElement el:
                    var tag = el.LocalName.ToLowerInvariant();
                    if (tag is "script" or "style" or "head" or "meta" or "link" or "title") continue;
                    var node = new RenderNode { Tag = tag, Element = el };
                    parentNode.AddChild(node);
                    ResolveStyle(node, parentNode.Style);
                    node.IconPath = el.GetAttribute("data-cupri-icon"); // set by icon-bearing components
                    node.ImageSrc = el.GetAttribute("data-cupri-image"); // set by <cupri-image>
                    if (node.Style.Display != DisplayType.None)
                        BuildChildren(node, el);
                    break;

                case IText t:
                    var text = t.Text;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var textNode = new RenderNode { Tag = "#text", Text = CollapseWhitespace(text) };
                    parentNode.AddChild(textNode);
                    // Text inherits the parent's computed style directly.
                    textNode.Style.InheritFrom(parentNode.Style);
                    textNode.Style.Display = DisplayType.Inline;
                    break;
            }
        }
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString().Trim();
    }

    private void ResolveStyle(RenderNode node, ComputedStyle? parent)
    {
        var style = node.Style;
        if (parent is not null) style.InheritFrom(parent);

        ApplyUserAgentDefaults(node);

        // Ordered declaration sets: author rules (specificity, order) then inline (wins).
        var ordered = new List<Dictionary<string, string>>();
        if (node.Element is { } el && _matched.TryGetValue(el, out var rules))
            foreach (var rule in rules.OrderBy(r => r.Specificity).ThenBy(r => r.Order))
                ordered.Add(rule.Declarations);
        var inline = node.Element?.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(inline))
            ordered.Add(CssParser.ParseDeclarations(inline));

        // Pass 1: custom properties (--tokens) cascade + inherit into CustomProps.
        foreach (var decls in ordered)
            foreach (var (k, v) in decls)
                if (k.StartsWith("--", StringComparison.Ordinal))
                    style.CustomProps[k] = ResolveVars(v, style.CustomProps);

        // Pass 2: normal properties, with var() resolved against the final tokens.
        foreach (var decls in ordered)
            Apply(style, decls);
    }

    private static void ApplyUserAgentDefaults(RenderNode node)
    {
        var s = node.Style;
        switch (node.Tag)
        {
            case "div" or "p" or "section" or "header" or "footer" or "main" or "article" or "nav" or "ul" or "ol" or "li":
                s.Display = DisplayType.Block; break;
            case "span" or "a" or "strong" or "b" or "em" or "i" or "small" or "label":
                s.Display = DisplayType.Inline; break;
            case "h1": s.Display = DisplayType.Block; s.FontSize = 32; s.FontWeight = 700; break;
            case "h2": s.Display = DisplayType.Block; s.FontSize = 24; s.FontWeight = 700; break;
            case "h3": s.Display = DisplayType.Block; s.FontSize = 19; s.FontWeight = 700; break;
        }
        if (node.Tag is "strong" or "b") s.FontWeight = 700;
    }

    /// <summary>Apply a declaration block onto a style (used by the animation system).</summary>
    public static void ApplyDeclarations(ComputedStyle s, Dictionary<string, string> decls) => Apply(s, decls);

    private static void Apply(ComputedStyle s, Dictionary<string, string> decls)
    {
        foreach (var (propRaw, valueRaw) in decls)
        {
            var prop = propRaw.ToLowerInvariant();
            if (prop.StartsWith("--", StringComparison.Ordinal)) continue; // custom props: pass 1
            var v = ResolveVars(valueRaw.Trim(), s.CustomProps);
            switch (prop)
            {
                case "display": s.Display = ParseDisplay(v); break;
                case "position": s.Position = v.ToLowerInvariant() switch { "relative" => PositionType.Relative, "absolute" => PositionType.Absolute, "fixed" => PositionType.Fixed, _ => PositionType.Static }; break;
                case "z-index": s.ZIndex = (int)ParseNum(v); break;
                case "overflow": s.Overflow = v.ToLowerInvariant() switch { "hidden" => OverflowMode.Hidden, "scroll" or "auto" => OverflowMode.Scroll, _ => OverflowMode.Visible }; break;

                case "width": s.Width = ParseLen(v); break;
                case "height": s.Height = ParseLen(v); break;
                case "min-width": s.MinWidth = ParseLen(v); break;
                case "min-height": s.MinHeight = ParseLen(v); break;
                case "max-width": s.MaxWidth = ParseLen(v); break;
                case "max-height": s.MaxHeight = ParseLen(v); break;

                case "margin": s.Margin = ParseEdges(v); break;
                case "margin-top": s.Margin.Top = ParseLen(v); break;
                case "margin-right": s.Margin.Right = ParseLen(v); break;
                case "margin-bottom": s.Margin.Bottom = ParseLen(v); break;
                case "margin-left": s.Margin.Left = ParseLen(v); break;

                case "padding": s.Padding = ParseEdges(v); break;
                case "padding-top": s.Padding.Top = ParseLen(v); break;
                case "padding-right": s.Padding.Right = ParseLen(v); break;
                case "padding-bottom": s.Padding.Bottom = ParseLen(v); break;
                case "padding-left": s.Padding.Left = ParseLen(v); break;

                case "top": s.Top = ParseLen(v); break;
                case "right": s.Right = ParseLen(v); break;
                case "bottom": s.Bottom = ParseLen(v); break;
                case "left": s.Left = ParseLen(v); break;

                case "flex-direction": s.FlexDirection = ParseFlexDir(v); break;
                case "flex-wrap": s.FlexWrap = v.Contains("wrap", StringComparison.OrdinalIgnoreCase) && !v.Contains("nowrap", StringComparison.OrdinalIgnoreCase) ? FlexWrapMode.Wrap : FlexWrapMode.NoWrap; break;
                case "justify-content": s.JustifyContent = ParseJustify(v); break;
                case "align-items": s.AlignItems = ParseAlign(v); break;
                case "justify-items": s.JustifyItems = ParseAlign(v); break;
                case "gap": { var g = ParsePx(v); s.RowGap = s.ColumnGap = g; break; }
                case "row-gap": s.RowGap = ParsePx(v); break;
                case "column-gap": s.ColumnGap = ParsePx(v); break;

                case "flex-grow": s.FlexGrow = ParseNum(v); break;
                case "flex-shrink": s.FlexShrink = ParseNum(v); break;
                case "flex-basis": s.FlexBasis = ParseLen(v); break;
                case "flex": ParseFlexShorthand(s, v); break;

                case "grid-template-columns": s.GridTemplateColumns = ParseTracks(v); break;
                case "grid-template-rows": s.GridTemplateRows = ParseTracks(v); break;
                case "grid-auto-rows": s.GridAutoRows = ParseTrack(v); break;
                case "grid-column": s.GridColumn = ParsePlacement(v); break;
                case "grid-row": s.GridRow = ParsePlacement(v); break;

                case "border": ParseBorderShorthand(s, v); break;
                case "border-width": { var w = ParsePx(v); s.BorderTop = s.BorderRight = s.BorderBottom = s.BorderLeft = w; break; }
                case "border-color": if (Colors.TryParse(v, out var bc)) s.BorderColor = bc; break;
                case "border-radius": s.BorderRadius = ParsePx(v); break;

                case "background" or "background-color": if (Colors.TryParse(v, out var bg)) s.Background = bg; break;
                case "opacity": s.Opacity = Math.Clamp(ParseNum(v), 0f, 1f); break;
                case "transform": ParseTransform(s, v); break;
                case "animation": ParseAnimation(s, v); break;
                case "animation-name": s.AnimationName = v; break;
                case "animation-duration": s.AnimationDuration = ParseSeconds(v); break;

                case "color": if (Colors.TryParse(v, out var col)) s.Color = col; break;
                case "font-size": s.FontSize = ParsePx(v, s.FontSize); break;
                case "font-weight": s.FontWeight = ParseWeight(v); break;
                case "font-family": s.FontFamily = v.Split(',')[0].Trim().Trim('"', '\''); break;
                case "line-height": s.LineHeight = ParseLineHeight(v); break;
                case "text-align": s.TextAlign = v.ToLowerInvariant() switch { "center" => TextAlign.Center, "right" => TextAlign.Right, _ => TextAlign.Left }; break;
            }
        }
    }

    // ---- value parsers -------------------------------------------------------
    private static DisplayType ParseDisplay(string v) => v.ToLowerInvariant() switch
    {
        "flex" or "inline-flex" => DisplayType.Flex,
        "grid" or "inline-grid" => DisplayType.Grid,
        "inline-block" => DisplayType.InlineBlock,
        "inline" => DisplayType.Inline,
        "none" => DisplayType.None,
        _ => DisplayType.Block,
    };

    private static FlexDirection ParseFlexDir(string v) => v.ToLowerInvariant() switch
    {
        "row-reverse" => FlexDirection.RowReverse,
        "column" => FlexDirection.Column,
        "column-reverse" => FlexDirection.ColumnReverse,
        _ => FlexDirection.Row,
    };

    private static JustifyContent ParseJustify(string v) => v.ToLowerInvariant() switch
    {
        "center" => JustifyContent.Center,
        "flex-end" or "end" => JustifyContent.FlexEnd,
        "space-between" => JustifyContent.SpaceBetween,
        "space-around" => JustifyContent.SpaceAround,
        "space-evenly" => JustifyContent.SpaceEvenly,
        _ => JustifyContent.FlexStart,
    };

    private static AlignItems ParseAlign(string v) => v.ToLowerInvariant() switch
    {
        "center" => AlignItems.Center,
        "flex-start" or "start" => AlignItems.FlexStart,
        "flex-end" or "end" => AlignItems.FlexEnd,
        _ => AlignItems.Stretch,
    };

    private static Length ParseLen(string v)
    {
        v = v.Trim();
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Length.Auto;
        if (v.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)) return ParseCalc(v);
        if (v.EndsWith('%') && float.TryParse(v[..^1], out var pct)) return new Length(LengthUnit.Percent, pct);
        return new Length(LengthUnit.Px, ParsePx(v));
    }

    // Simple calc(): sum of signed px and % terms, e.g. calc(100% - 40px), calc(50% + 8px).
    private static Length ParseCalc(string v)
    {
        var inner = v[(v.IndexOf('(') + 1)..v.LastIndexOf(')')];
        inner = inner.Replace("+", " + ").Replace("-", " - ");
        var tokens = inner.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        float px = 0, percent = 0, sign = 1;
        foreach (var tok in tokens)
        {
            if (tok == "+") { sign = 1; continue; }
            if (tok == "-") { sign = -1; continue; }
            if (tok.EndsWith('%') && float.TryParse(tok[..^1], out var p)) percent += sign * p;
            else px += sign * ParsePx(tok);
            sign = 1;
        }
        return Length.Calc(px, percent);
    }

    private static LengthEdges ParseEdges(string v)
    {
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Length t, r, b, l;
        switch (parts.Length)
        {
            case 1: t = r = b = l = ParseLen(parts[0]); break;
            case 2: t = b = ParseLen(parts[0]); r = l = ParseLen(parts[1]); break;
            case 3: t = ParseLen(parts[0]); r = l = ParseLen(parts[1]); b = ParseLen(parts[2]); break;
            default: t = ParseLen(parts[0]); r = ParseLen(parts[1]); b = ParseLen(parts[2]); l = ParseLen(parts[3]); break;
        }
        return new LengthEdges { Top = t, Right = r, Bottom = b, Left = l };
    }

    private static float ParsePx(string v, float fallback = 0f)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("px")) v = v[..^2];
        else if (v.EndsWith("rem") || v.EndsWith("em")) { if (float.TryParse(v.TrimEnd('r', 'e', 'm'), out var em)) return em * 16f; }
        return float.TryParse(v, out var px) ? px : fallback;
    }

    private static float ParseNum(string v) => float.TryParse(v.Trim(), out var n) ? n : 0f;

    private static int ParseWeight(string v) => v.ToLowerInvariant() switch
    {
        "bold" or "bolder" => 700,
        "normal" or "lighter" => 400,
        _ => int.TryParse(v, out var w) ? w : 400,
    };

    private static float ParseLineHeight(string v)
    {
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return ParsePx(v) / 16f; // rough; refined once font-size known
        return float.TryParse(v, out var n) ? n : 1.2f;
    }

    // ---- grid parsers --------------------------------------------------------
    private static List<TrackSize> ParseTracks(string v)
    {
        // Expand repeat(n, tracklist) → the tracklist repeated n times.
        v = _repeat.Replace(v, m =>
        {
            var count = int.Parse(m.Groups[1].Value);
            var inner = m.Groups[2].Value.Trim();
            return string.Join(' ', Enumerable.Repeat(inner, count));
        });

        var tracks = new List<TrackSize>();
        foreach (var tok in SplitTopLevel(v))
            tracks.Add(ParseTrack(tok));
        return tracks;
    }

    /// <summary>Split on spaces but keep parenthesised groups (minmax(...)) intact.</summary>
    private static List<string> SplitTopLevel(string v)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var ch in v)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            if (char.IsWhiteSpace(ch) && depth == 0)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static TrackSize ParseTrack(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v == "auto") return TrackSize.Auto;
        if (v.StartsWith("minmax("))
        {
            var inner = v[7..v.LastIndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
            var min = inner.Length > 0 ? ParsePx(inner[0]) : 0f;
            var max = inner.Length > 1 ? ParseTrack(inner[1]) : new TrackSize(TrackKind.Fraction, 1);
            return new TrackSize(max.Kind, max.Value, minPx: min);
        }
        if (v.EndsWith("fr")) return new TrackSize(TrackKind.Fraction, float.TryParse(v[..^2], out var fr) ? fr : 1);
        if (v.EndsWith('%')) return new TrackSize(TrackKind.Percent, float.TryParse(v[..^1], out var p) ? p : 0);
        return new TrackSize(TrackKind.Px, ParsePx(v));
    }

    private static GridPlacement ParsePlacement(string v)
    {
        // Supports: "2", "span 3", "2 / 4", "2 / span 3".
        v = v.Trim().ToLowerInvariant();
        var sides = v.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        int? start = null;
        var span = 1;
        if (sides.Length >= 1 && sides[0].StartsWith("span"))
            span = int.TryParse(sides[0][4..].Trim(), out var sp) ? sp : 1;
        else if (sides.Length >= 1 && int.TryParse(sides[0], out var st))
            start = st;

        if (sides.Length >= 2)
        {
            var end = sides[1];
            if (end.StartsWith("span")) span = int.TryParse(end[4..].Trim(), out var sp2) ? sp2 : 1;
            else if (int.TryParse(end, out var e) && start is { } s0) span = Math.Max(1, e - s0);
        }
        return new GridPlacement(start, span);
    }

    private static void ParseAnimation(ComputedStyle s, string v)
    {
        // animation: <name> <duration> [timing] [delay] [iteration] ...
        foreach (var tok in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.EndsWith("s", StringComparison.OrdinalIgnoreCase) && char.IsDigit(tok[0]))
            {
                if (s.AnimationDuration == 0) s.AnimationDuration = ParseSeconds(tok);
            }
            else if (s.AnimationName is null && tok is not ("linear" or "ease" or "ease-in" or "ease-out"
                     or "ease-in-out" or "infinite" or "alternate" or "normal" or "both" or "forwards"))
            {
                s.AnimationName = tok;
            }
        }
    }

    private static float ParseSeconds(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("ms")) return float.TryParse(v[..^2], out var ms) ? ms / 1000f : 0f;
        if (v.EndsWith('s')) return float.TryParse(v[..^1], out var sec) ? sec : 0f;
        return float.TryParse(v, out var n) ? n : 0f;
    }

    private static readonly Regex _transformFn = new(@"(\w+)\(([^)]*)\)", RegexOptions.Compiled);

    private static void ParseTransform(ComputedStyle s, string v)
    {
        foreach (Match m in _transformFn.Matches(v))
        {
            var fn = m.Groups[1].Value.ToLowerInvariant();
            var args = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            float A(int i) => i < args.Length ? ParsePx(args[i]) : 0f;
            float N(int i, float d) => i < args.Length && float.TryParse(args[i].TrimEnd('d', 'e', 'g'), out var n) ? n : d;
            switch (fn)
            {
                case "translate": s.TranslateX = A(0); s.TranslateY = A(1); s.HasTransform = true; break;
                case "translatex": s.TranslateX = A(0); s.HasTransform = true; break;
                case "translatey": s.TranslateY = A(0); s.HasTransform = true; break;
                case "scale": s.ScaleX = N(0, 1); s.ScaleY = args.Length > 1 ? N(1, 1) : s.ScaleX; s.HasTransform = true; break;
                case "scalex": s.ScaleX = N(0, 1); s.HasTransform = true; break;
                case "scaley": s.ScaleY = N(0, 1); s.HasTransform = true; break;
                case "rotate": s.RotateDeg = N(0, 0); s.HasTransform = true; break;
            }
        }
    }

    private static void ParseFlexShorthand(ComputedStyle s, string v)
    {
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && float.TryParse(parts[0], out var g)) { s.FlexGrow = g; s.FlexShrink = 1; s.FlexBasis = new Length(LengthUnit.Px, 0); return; }
        if (parts.Length >= 1 && float.TryParse(parts[0], out var grow)) s.FlexGrow = grow;
        if (parts.Length >= 2 && float.TryParse(parts[1], out var shrink)) s.FlexShrink = shrink;
        if (parts.Length >= 3) s.FlexBasis = ParseLen(parts[2]);
    }

    private static void ParseBorderShorthand(ComputedStyle s, string v)
    {
        foreach (var token in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase) || float.TryParse(token, out _))
            { var w = ParsePx(token); s.BorderTop = s.BorderRight = s.BorderBottom = s.BorderLeft = w; }
            else if (Colors.TryParse(token, out var c)) s.BorderColor = c;
            // style keyword (solid/dashed) ignored in M1
        }
    }
}
