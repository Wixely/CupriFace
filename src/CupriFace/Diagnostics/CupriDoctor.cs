using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Style;

namespace CupriFace.Diagnostics;

/// <summary>How much a finding matters.</summary>
public enum Severity
{
    /// <summary>Works, but there is a better way — or a habit that will bite later.</summary>
    Info,

    /// <summary>Probably not what was meant. The document still renders.</summary>
    Warning,

    /// <summary>This will not appear on screen, or will appear wrong.</summary>
    Error,
}

/// <summary>One thing the checker noticed.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Code">A short stable identifier (<c>CF0010</c>) so a finding can be searched for,
/// quoted in a review, or ignored by a caller that disagrees with it.</param>
/// <param name="Message">What is wrong, in one sentence.</param>
/// <param name="Fix">What to do instead — the half that turns a complaint into help. Empty when
/// there is nothing specific to suggest.</param>
/// <param name="Line">1-based source line, or 0 when the finding is not tied to one.</param>
public readonly record struct Finding(
    Severity Severity, string Code, string Message, string Fix = "", int Line = 0)
{
    public override string ToString() =>
        $"{Severity.ToString().ToLowerInvariant()} {Code}" +
        (Line > 0 ? $" (line {Line})" : "") + ": " + Message +
        (Fix.Length > 0 ? "  -> " + Fix : "");
}

/// <summary>What a check found, and whether anything is actually wrong.</summary>
public sealed class DoctorReport
{
    internal DoctorReport(IReadOnlyList<Finding> findings) => Findings = findings;

    /// <summary>Everything noticed, worst first.</summary>
    public IReadOnlyList<Finding> Findings { get; }

    /// <summary>Nothing found at any severity — the one-line answer.</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>Something will not render. Warnings and info may still be present when this is false.</summary>
    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);

    /// <summary>How many findings of one severity.</summary>
    public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>A report you can print to a terminal or put in a test failure message.</summary>
    public override string ToString()
    {
        if (IsClean) return "No problems found.";
        var sb = new StringBuilder();
        sb.Append(Findings.Count).Append(Findings.Count == 1 ? " finding" : " findings")
          .Append(" (").Append(Count(Severity.Error)).Append(" error, ")
          .Append(Count(Severity.Warning)).Append(" warning, ")
          .Append(Count(Severity.Info)).AppendLine(" info)");
        foreach (var f in Findings) sb.Append("  ").AppendLine(f.ToString());
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// A development-time check for a document: hand it your markup (and stylesheet) and it names what
/// will not work, before you go hunting for it on screen.
///
/// <code>
/// var report = CupriDoctor.Check(html, css);
/// if (!report.IsClean) Console.WriteLine(report);
/// </code>
///
/// <para><b>Why this exists.</b> The engine is deliberately forgiving at run time — an unsupported
/// CSS property is ignored, an element it has no primitive for simply does not draw — because a
/// stylesheet written for a browser must not crash an app. The cost is that a mistake looks exactly
/// like a layout you have not finished: nothing throws, nothing logs, the box is just empty. This
/// turns that silence into a list.</para>
///
/// <para><b>Why the checks are derived, not listed.</b> A hand-written table of "supported elements"
/// and "supported properties" would be wrong within a release, and a checker that accuses working
/// markup of being broken gets switched off and stays off. So each check reads from the thing it is
/// checking: unrendered elements come from diffing the real render tree against the DOM, unknown
/// components from the real <see cref="ComponentRegistry"/>, and unsupported CSS from the real
/// <see cref="StyleResolver"/> reporting what it ignored. Add an element or a property to the engine
/// and this stops complaining about it, with no edit here.</para>
///
/// <para>Development-time only. Nothing here runs while an app is running.</para>
/// </summary>
public static partial class CupriDoctor
{
    /// <summary>Check markup and, if you have one, a stylesheet.</summary>
    /// <param name="html">Document markup — the same string you would give <c>CupriApp.Html</c>.</param>
    /// <param name="css">The stylesheet. Without it the CSS checks are skipped rather than guessed.</param>
    /// <param name="components">The component library the app will use. Defaults to the built-ins,
    /// which is what an app that does not override <c>CupriApp.Components</c> gets.</param>
    /// <param name="width">Viewport for the trial layout.</param>
    /// <param name="height">Viewport height for the trial layout.</param>
    /// <param name="model">The binding model, if the document has one. Supplying it unlocks the
    /// checks that cannot be done without it: whether every <c>{{path}}</c> names something real,
    /// and whether the boxes still fit once actual content is in them. Without it those are skipped
    /// rather than guessed.</param>
    public static DoctorReport Check(string html, string? css = null,
                                     ComponentRegistry? components = null,
                                     int width = 1024, int height = 768,
                                     object? model = null)
    {
        var findings = new List<Finding>();
        var registry = components ?? ComponentRegistry.Default();
        var lines = html.Replace("\r\n", "\n").Split('\n');

        MalformedHtml(html, findings);

        // A real document, laid out once. Every check below reads the engine's actual behaviour
        // rather than a description of it.
        CupriDocument? doc = null;
        IDocument? dom = null;
        var unsupportedCss = new List<string>();
        var missingGlyphs = new SortedSet<int>();
        try
        {
            // The CSS hook goes on BEFORE the document is built. Styles resolve during the first
            // build and the result is cached, so a hook attached afterwards hears nothing at all —
            // which is exactly how the first version of this quietly reported no CSS problems ever.
            StyleResolver.UnsupportedProperty = (p, _) => unsupportedCss.Add(p);
            Text.FontService.GlyphMissing = cp => missingGlyphs.Add(cp);
            try
            {
                doc = CupriDocument.Load(html, css ?? "");
                doc.UseComponents(registry);
                // Bound BEFORE the layout below, so the boxes measured are the ones real content
                // produces. Checking an unbound template would measure empty strings and miss
                // precisely the overflow that appears once the data arrives.
                if (model is not null) doc.Bind(model);
                // The expanded DOM, from the document's own rebuild hook. These are the same element
                // instances the render tree points at, which is what makes the "did this draw
                // anything?" comparison exact rather than by-name. Registering the handler is not
                // enough — the first build already happened inside Load — so ask for one more.
                doc.OnRebuilt(d => dom = d);
                doc.Refresh();
                using (doc.RenderToImage(width, height)) { }
            }
            finally { StyleResolver.UnsupportedProperty = null; Text.FontService.GlyphMissing = null; }
        }
        catch (Exception ex)
        {
            findings.Add(new Finding(Severity.Error, "CF0001",
                $"The document could not be built: {ex.GetType().Name}: {ex.Message}",
                "Fix this first — the other checks ran on an incomplete document, or not at all."));
        }

        if (doc is not null)
        {
            UnknownComponents(dom, registry, lines, findings);
            TogglesWithNothingToToggle(dom, lines, findings);
            UnrenderedElements(doc, dom, registry, lines, findings);
            ScriptingHabits(dom, lines, findings);
            BoxesThatDoNotFit(doc, width, lines, findings);
            if (model is not null) UnresolvedBindings(html, model, lines, findings);
            doc.Dispose();
        }

        MissingGlyphs(missingGlyphs, findings);
        UnsupportedCssProperties(unsupportedCss, css, findings);
        UnsupportedCssFunctions(css, findings);

        findings.Sort((a, b) =>
        {
            var bySeverity = b.Severity.CompareTo(a.Severity);
            return bySeverity != 0 ? bySeverity : a.Line.CompareTo(b.Line);
        });
        return new DoctorReport(findings);
    }

    // ---- 1. is the markup even balanced? -------------------------------------------------------

    /// <summary>
    /// Unbalanced tags, found by scanning the source rather than by asking the parser.
    ///
    /// <para>AngleSharp repairs broken markup silently, exactly as a browser does — a missing
    /// <c>&lt;/div&gt;</c> becomes a differently-shaped tree, not an error. That is the right
    /// behaviour, and precisely why the mistake surfaces as a mystifying layout instead of a
    /// message. Scanning the source also means a finding can point at the line the tag was OPENED
    /// on, which is the line you actually need.</para>
    /// </summary>
    private static void MalformedHtml(string html, List<Finding> findings)
    {
        var open = new Stack<(string Tag, int Line)>();
        var line = 1;

        for (var i = 0; i < html.Length; i++)
        {
            if (html[i] == '\n') { line++; continue; }
            if (html[i] != '<') continue;

            if (html.AsSpan(i).StartsWith("<!--"))                 // comment: skip it whole
            {
                var close = html.IndexOf("-->", i, StringComparison.Ordinal);
                var stop = close < 0 ? html.Length : close;
                line += CountNewlines(html, i, stop);
                i = close < 0 ? html.Length : close + 2;
                continue;
            }
            if (i + 1 < html.Length && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                var close = html.IndexOf('>', i);                  // doctype / PI
                i = close < 0 ? html.Length : close;
                continue;
            }

            var isClose = i + 1 < html.Length && html[i + 1] == '/';
            var nameStart = i + (isClose ? 2 : 1);
            var j = nameStart;
            while (j < html.Length && (char.IsLetterOrDigit(html[j]) || html[j] == '-')) j++;
            if (j == nameStart) continue;                          // a bare '<' in ordinary text
            var tag = html[nameStart..j].ToLowerInvariant();

            var gt = html.IndexOf('>', j);
            if (gt < 0) break;                                     // truncated tag; nothing more to say
            var selfClosing = html[gt - 1] == '/';
            var tagLine = line;
            line += CountNewlines(html, i, gt);

            if (VoidElements.Contains(tag) || selfClosing) { i = gt; continue; }
            if (!isClose) { open.Push((tag, tagLine)); i = gt; continue; }

            if (open.Count == 0)
            {
                findings.Add(new Finding(Severity.Warning, "CF0011",
                    $"</{tag}> closes a tag that was never opened.",
                    "Delete it, or add the matching opening tag.", tagLine));
            }
            else if (open.Peek().Tag == tag)
            {
                open.Pop();
            }
            else if (open.Any(o => o.Tag == tag))
            {
                // The tag IS open, just not innermost — so everything between is unclosed, and those
                // are the real findings. Naming them beats naming the close that revealed them.
                while (open.Count > 0 && open.Peek().Tag != tag)
                {
                    var (unclosed, at) = open.Pop();
                    findings.Add(new Finding(Severity.Error, "CF0010",
                        $"<{unclosed}> is never closed — the </{tag}> on line {tagLine} closes its parent first.",
                        $"Add </{unclosed}> before that.", at));
                }
                if (open.Count > 0) open.Pop();
            }
            else
            {
                findings.Add(new Finding(Severity.Warning, "CF0011",
                    $"</{tag}> does not match the open <{open.Peek().Tag}>.",
                    "Check the nesting order.", tagLine));
            }
            i = gt;
        }

        foreach (var (tag, at) in open)
            findings.Add(new Finding(Severity.Error, "CF0010",
                $"<{tag}> is never closed.",
                $"Add </{tag}>. The parser repairs this the way a browser does, so the tree will not "
                + "be the shape you wrote.", at));
    }

    private static int CountNewlines(string s, int from, int toExclusive)
    {
        var n = 0;
        for (var k = from; k < toExclusive && k < s.Length; k++) if (s[k] == '\n') n++;
        return n;
    }

    /// <summary>HTML void elements: no closing tag, never unbalanced.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link",
        "meta", "param", "source", "track", "wbr",
    };

    // ---- 2. a cupri-* tag nobody registered ----------------------------------------------------

    private static void UnknownComponents(IDocument? dom, ComponentRegistry registry,
                                          string[] lines, List<Finding> findings)
    {
        var known = registry.Tags.ToArray();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in All(dom))
        {
            var tag = el.LocalName;
            if (!tag.StartsWith("cupri-", StringComparison.OrdinalIgnoreCase)) continue;
            if (known.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            // <cupri-option> inside <cupri-select>: the parent reads it and builds the list itself.
            // Data for a component, not a component — rendering nothing is its whole job (#145).
            if (InsideRegisteredComponent(el, registry)) continue;
            if (!reported.Add(tag)) continue;

            var near = Nearest(tag, known);
            findings.Add(new Finding(Severity.Error, "CF0020",
                $"<{tag}> is not a registered component, so it renders nothing.",
                near is null
                    ? "Register it with registry.Register(new YourComponent()), or check the spelling."
                    : $"Did you mean <{near}>?",
                LineOf(lines, "<" + tag, Occurrence(dom, el))));
        }
    }

    /// <summary>
    /// A control whose trigger can never open anything.
    ///
    /// <para>Components that open a panel — select, popover, drawer, the pickers — keep their open
    /// state in the MODEL, so <c>&lt;cupri-select value="{{V}}"&gt;</c> with no
    /// <c>open="{{Flag}}"</c> expands, lays out, draws its trigger, and is dead. Clicking it is even
    /// reported as HANDLED, so no return value, log or rendered frame says otherwise. One reached a
    /// shipped Showcase page that way and was found by a person clicking it.</para>
    ///
    /// <para>Checked by walking the path the RUNTIME walks: <c>ToggleNearestOpen</c> starts at the
    /// clicked node and looks up the ancestors for <c>data-bind-open</c>. If that walk would come up
    /// empty, the control cannot open. Reading the runtime's own rule rather than keeping a list of
    /// which components need binding is what keeps this correct as controls are added.</para>
    /// </summary>
    private static void TogglesWithNothingToToggle(IDocument? dom, string[] lines, List<Finding> findings)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in All(dom))
        {
            if (!el.HasAttribute("data-cupri-toggle")) continue;

            // Two spellings, because this runs WITHOUT a model. At runtime the binder compiles
            // open="{{Flag}}" into data-bind-open and ToggleNearestOpen looks for that — but there is
            // no model here to bind against, so data-bind-open does not exist yet and the raw `open`
            // attribute is what survives expansion. Checking only the compiled form reported every
            // working control in the Showcase as broken.
            var bound = false;
            for (var n = el; n is not null; n = n.ParentElement)
                if (n.GetAttribute("data-bind-open") is { Length: > 0 } || n.HasAttribute("open"))
                { bound = true; break; }
            if (bound) continue;

            // Name the component, not the generated trigger inside it that nobody wrote.
            var owner = el;
            for (var n = el; n is not null; n = n.ParentElement)
                if (n.LocalName.StartsWith("cupri-", StringComparison.OrdinalIgnoreCase)) { owner = n; break; }
            if (!reported.Add(owner.LocalName)) continue;

            findings.Add(new Finding(Severity.Error, "CF0021",
                $"<{owner.LocalName}> can never open — its trigger has no open state to toggle.",
                "Add open=\"{{SomeFlag}}\" and a bool on your model. The click is reported as handled "
                + "either way, so nothing else will tell you.",
                LineOf(lines, "<" + owner.LocalName)));
        }
    }

    // ---- 3. an element the engine draws nothing for --------------------------------------------

    /// <summary>
    /// The check that would have caught <c>&lt;img&gt;</c>. Rather than keeping a list of supported
    /// elements — wrong by the next release — it lays the document out and asks which elements
    /// produced no render node. Anything in the markup the renderer never saw is, by definition,
    /// invisible.
    /// </summary>
    private static void UnrenderedElements(CupriDocument doc, IDocument? dom,
                                           ComponentRegistry registry,
                                           string[] lines, List<Finding> findings)
    {
        var rendered = new HashSet<IElement>();
        void Walk(RenderNode n)
        {
            if (n.Element is { } e) rendered.Add(e);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);

        // Elements a display:none render node covers, when the tree keeps such a node. Together
        // with the DOM's own markers this is how "hidden on purpose" is told from "never drawn".
        var hiddenRoots = new HashSet<IElement>();
        void FindHidden(RenderNode n)
        {
            if (n.Element is { } e && n.Style.Display == DisplayType.None) hiddenRoots.Add(e);
            foreach (var c in n.Children) FindHidden(c);
        }
        FindHidden(doc.Root);

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in All(dom))
        {
            var tag = el.LocalName;
            if (NotDrawnOnPurpose.Contains(tag)) continue;
            // A component's own tag is replaced by what it expands into, so it is legitimately
            // absent from the render tree.
            if (registry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            // …and so is anything a component consumes or emits: its option rows, its own internals.
            if (InsideRegisteredComponent(el, registry)) continue;
            // Hidden on purpose is not "never drawn". With a real model most of an app is hidden
            // pages (style="display:{{ConfigDisplay}}"), and every element inside one is absent
            // from the render tree by design (#145: nine of sixteen findings were this).
            if (IsHidden(el, hiddenRoots)) continue;
            // Report the ROOT of a missing subtree only. Its descendants are missing because it is,
            // and listing each of them buries the one line that says why.
            if (el.ParentElement is { } parent && parent.LocalName != "body" && !rendered.Contains(parent)
                && !registry.Tags.Contains(parent.LocalName, StringComparer.OrdinalIgnoreCase)
                && !Replacements.ContainsKey(parent.LocalName)) continue;

            // The browser-habit elements are named outright, because the render-tree test below
            // does NOT catch them: the engine happily builds a box for an <img>, lays it out, and
            // then has no primitive to draw anything into it. It is present, sized, and empty —
            // which is worse than absent, because every automatic check says it is fine.
            if (Replacements.TryGetValue(tag, out var better))
            {
                if (!reported.Add(tag)) continue;
                findings.Add(new Finding(Severity.Error, "CF0030",
                    $"<{tag}> is not something the engine draws — it lays out, and then stays empty.",
                    better, LineOf(lines, "<" + tag, Occurrence(dom, el))));
                continue;
            }

            // The generic catch-all: in the markup, but the renderer never saw it.
            if (rendered.Contains(el)) continue;
            if (!reported.Add(tag)) continue;
            findings.Add(new Finding(Severity.Warning, "CF0031",
                $"<{tag}> produced no render output, so none of it will appear.",
                "If it is meant to be visible, use a div (or a cupri-* component) instead.",
                LineOf(lines, "<" + tag, Occurrence(dom, el))));
        }
    }

    /// <summary>Is this element inside a registered component's subtree — i.e. either consumed by
    /// it (a select's options) or emitted by it (its internals)? Neither has a render node of its
    /// own, and neither is a fault.</summary>
    private static bool InsideRegisteredComponent(IElement el, ComponentRegistry registry)
    {
        for (var a = el.ParentElement; a is not null; a = a.ParentElement)
            if (registry.Tags.Contains(a.LocalName, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Hidden on purpose, by itself or by an ancestor: a display:none render node, an
    /// inline <c>display:none</c> (which is what a bound <c>style="display:{{X}}"</c> becomes), the
    /// <c>hidden</c> attribute, or <c>aria-hidden="true"</c>.</summary>
    private static bool IsHidden(IElement el, HashSet<IElement> hiddenRoots)
    {
        for (var a = el; a is not null; a = a.ParentElement)
        {
            if (hiddenRoots.Contains(a) || a.HasAttribute("hidden")) return true;
            if (a.GetAttribute("aria-hidden") == "true") return true;
            if (a.GetAttribute("style") is { } style
                && Regex.IsMatch(style, @"display\s*:\s*none", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Which same-named element this is, in document order — zero-based, and paired with
    /// <see cref="LineOf(string[], string, int)"/> so a finding names the element it is about
    /// rather than the first one sharing its tag. That is how a hidden page's paragraph came to be
    /// reported as the visible subtitle on line 6 (#145).</summary>
    private static int Occurrence(IDocument? dom, IElement el)
    {
        var n = 0;
        foreach (var e in All(dom))
        {
            if (ReferenceEquals(e, el)) return n;
            if (e.LocalName == el.LocalName) n++;
        }
        return 0;
    }

    /// <summary>Elements that legitimately draw nothing — structure, metadata, and the ones the
    /// engine consumes itself. Absent from the render tree by design, so never a finding.</summary>
    private static readonly HashSet<string> NotDrawnOnPurpose = new(StringComparer.OrdinalIgnoreCase)
    { "html", "head", "meta", "title", "link", "style", "script", "base", "template", "br", "wbr" };

    /// <summary>Elements people reach for out of browser habit, and what to use instead. Each renders
    /// as empty space today with no message, which is exactly why they are worth naming.</summary>
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["img"] = "Use <cupri-image src=\"...\"> — the engine has no raw <img> primitive.",
        ["video"] = "Use <cupri-video src=\"...\"> (desktop WebM needs the CupriFace.Media package).",
        ["audio"] = "Use <cupri-video> without a visible frame, or drive playback from your model.",
        ["canvas"] = "Supply pixels through ISurfaceSource, or use CupriFace.Gl for a GL viewport.",
        ["svg"] = "There is no SVG. Use <cupri-icon> for icons, or draw with CSS boxes and borders.",
        ["iframe"] = "There is no embedded browser; render the content as part of this document.",
        ["object"] = "There is no plugin surface; render the content as part of this document.",
        ["embed"] = "There is no plugin surface; render the content as part of this document.",
    };

    // ---- 4. JavaScript habits ------------------------------------------------------------------

    private static void ScriptingHabits(IDocument? dom, string[] lines, List<Finding> findings)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in All(dom))
        {
            if (el.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase) && reported.Add("<script"))
                findings.Add(new Finding(Severity.Warning, "CF0040",
                    "<script> does nothing — there is no JavaScript engine.",
                    "Put the behaviour in C#: doc.OnClick / OnAction / OnShortcut inside Configure.",
                    LineOf(lines, "<script")));

            foreach (var attr in el.Attributes)
            {
                if (!attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) continue;
                if (attr.Name.Length <= 2 || !reported.Add(attr.Name)) continue;
                findings.Add(new Finding(Severity.Warning, "CF0041",
                    $"{attr.Name}=\"...\" never fires — inline event handlers are JavaScript.",
                    "Use doc.OnClick(selector, ...), or mark the element with data-action and use OnAction.",
                    LineOf(lines, attr.Name + "=")));
            }
        }
    }

    // ---- 5. CSS the resolver threw away --------------------------------------------------------

    private static void UnsupportedCssProperties(List<string> ignored, string? css,
                                                 List<Finding> findings)
    {
        if (css is null) return;
        var lines = css.Replace("\r\n", "\n").Split('\n');
        foreach (var prop in ignored.Distinct(StringComparer.OrdinalIgnoreCase))
            findings.Add(new Finding(Severity.Warning, "CF0050",
                $"CSS property '{prop}' is not supported and was ignored.",
                Suggest(prop), LineOf(lines, prop + ":")));
    }

    private static string Suggest(string prop) => prop switch
    {
        "float" or "clear" => "Use flexbox (display:flex) — there is no float layout.",
        "grid-template-areas" => "Use grid-template-columns/rows with grid-column/row spans.",
        "transition-property" or "transition-duration" or "transition-timing-function"
            => "Use the `transition` shorthand.",
        "animation-direction" => "Not implemented — an animation always plays forwards.",
        _ when prop.StartsWith('-') => "Vendor-prefixed properties are never supported; use the standard name.",
        _ => "Check the spelling, or see TOOLBOX.md for what the engine styles.",
    };

    /// <summary>Value-level gaps the property switch cannot see: the property is supported, the
    /// FUNCTION inside it is not, so the declaration is accepted and then quietly paints nothing.
    /// This is the one list here that IS hand-written, because there is no hook to derive it from —
    /// so it is kept short and specific rather than trying to be complete.</summary>
    private static void UnsupportedCssFunctions(string? css, List<Finding> findings)
    {
        if (css is null) return;
        var lines = css.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            foreach (var (needle, fix) in FunctionGaps)
                if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new Finding(Severity.Warning, "CF0051",
                        $"{needle}) is not supported — the declaration parses but paints nothing.",
                        fix, i + 1));
    }

    private static readonly (string Needle, string Fix)[] FunctionGaps =
    [
        ("repeating-linear-gradient(",
            "Only linear-gradient() and radial-gradient() exist. Repeat it with elements instead."),
        ("repeating-radial-gradient(", "Only linear-gradient() and radial-gradient() exist."),
        ("conic-gradient(", "Not supported; a radial-gradient() or an ISurfaceSource can stand in."),
        ("image-set(", "Not supported — give one source."),
    ];

    // ---- shared helpers ------------------------------------------------------------------------

    private static IEnumerable<IElement> All(IDocument? dom) => dom is null ? [] : dom.All;

    // ---- 5b. characters no font on this machine can draw ---------------------------------------

    /// <summary>
    /// Codepoints that found no face and will paint as .notdef boxes — tofu.
    ///
    /// <para>Falling back to an empty box is correct behaviour: a missing glyph must never take an
    /// app down. But tofu in a screenshot is routinely misread as a font-size, encoding or shaping
    /// problem, and the actual cause — "this machine has no font with that character" — is not
    /// visible from the markup at all. Naming the character and its codepoint turns a mystery box
    /// into a one-line answer.</para>
    ///
    /// <para>Reported as a warning, not an error, and deliberately so: it is a property of the
    /// MACHINE, not the document. The same markup is fine on a box with the right fonts installed,
    /// which is itself the thing worth knowing before shipping a screenshot or a build.</para>
    ///
    /// <para><b>It under-reports on some platforms, and a clean result is not proof of no tofu.</b>
    /// This fires only when the system font manager returns NOTHING for a character. macOS ships a
    /// LastResort face that matches every codepoint and draws a placeholder box for it, so the user
    /// sees tofu while <c>MatchCharacter</c> reports success and nothing is raised. Trust a CF0080
    /// finding; do not trust its absence as coverage. Look at the render.</para>
    /// </summary>
    private static void MissingGlyphs(SortedSet<int> codepoints, List<Finding> findings)
    {
        if (codepoints.Count == 0) return;

        var shown = codepoints.Take(8)
            .Select(cp => $"U+{cp:X4} ({char.ConvertFromUtf32(cp)})");
        var more = codepoints.Count > 8 ? $" and {codepoints.Count - 8} more" : "";

        findings.Add(new Finding(Severity.Warning, "CF0080",
            $"No available font can draw {string.Join(", ", shown)}{more} — "
            + "they render as empty .notdef boxes.",
            "Register a face that covers them with doc.LoadFonts(dir) — emoji, CJK and symbol ranges "
            + "are the usual gaps. This is about the fonts on THIS machine, so the same markup may "
            + "look fine elsewhere, which is the trap."));
    }

    // ---- 6. do the boxes fit what is in them? --------------------------------------------------

    /// <summary>
    /// Content that does not fit the box it was given, and boxes with no area at all.
    ///
    /// <para>This is the check whose absence is felt most, because the SCREENSHOT LIES. A fixed
    /// height smaller than its contents does not clip — <c>overflow: visible</c> is the CSS default —
    /// so the children paint outside it while the next sibling is placed using the declared height.
    /// What you see is two unrelated elements drawn over each other, which reads as a paint or
    /// z-order fault and sends you to the wrong part of the codebase entirely.</para>
    ///
    /// <para>The rule itself lives in <see cref="BoxOverflow"/> so that this and
    /// <c>CupriDocument.DumpTree</c> cannot drift apart on what counts as overflow.</para>
    /// </summary>
    private static void BoxesThatDoNotFit(CupriDocument doc, int viewportWidth, string[] lines, List<Finding> findings)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);

        // A collapsed box collapses everything inside it, so its children are symptoms rather than
        // separate faults. Reporting the outermost one and stopping is the difference between one
        // actionable line and a wall of them.
        //
        // `absX` is where this box starts on screen, and `clipRight` is the first edge past which
        // content is LOST: the viewport, or the nearest ancestor with overflow:hidden. Sideways
        // overflow is judged against that rather than against the parent alone, because the two
        // questions have different answers and only one of them matters — a box 8px wider than a
        // container with room to spare beside it is invisible, while the same 8px past the window
        // edge is content nobody can reach. Measured on the Showcase at its design size, the
        // geometric rule alone reported two such harmless cases; against the clip, none.
        //
        // A SCROLLING ancestor is the opposite of a clip: it is the author saying the content is
        // wider on purpose and can be dragged to. It therefore exempts everything inside it (a
        // sentinel of infinity), which is the same answer the vertical rule's advice already gives
        // — "or set overflow:scroll to keep the size and scroll inside it".
        void Walk(RenderNode n, bool insideCollapsed, bool insideOffScreen, float absX, float clipRight)
        {
            if (insideCollapsed)
            {
                foreach (var c in n.Children) Walk(c, true, insideOffScreen, absX + c.X, clipRight);
                return;
            }
            var collapsed = false;
            if (BoxOverflow.Overshoot(n) is { } over)
            {
                var name = Name(n);
                if (reported.Add("o:" + name))
                    findings.Add(new Finding(Severity.Warning, "CF0070",
                        $"{name} is {n.Height:0}px tall but its contents need {n.Height + over:0}px — "
                        + $"they overflow it by {over:0}px and paint over whatever follows.",
                        "Remove the fixed height and let it grow, or set overflow:scroll to keep the "
                        + "size and scroll inside it. It does not clip on its own.",
                        LineOf(lines, ClassNeedle(n))));
            }
            else if (BoxOverflow.IsEmptyBoxWithContent(n))
            {
                collapsed = true;
                var name = Name(n);
                if (reported.Add("e:" + name))
                    findings.Add(new Finding(Severity.Warning, "CF0071",
                        $"{name} laid out {n.Width:0}x{n.Height:0} but has content inside it, so none of it is visible.",
                        "Usually a percentage height with no sized parent, a flex item given no basis, "
                        + "or an image whose size never resolved.",
                        LineOf(lines, ClassNeedle(n))));
            }

            // Sideways, and reported only when it escapes something that cuts it off. This is how a
            // desktop layout fails on a phone: fixed columns that add up to more than the viewport
            // simply run off the side, with nothing on screen to say the missing part exists.
            // …and only the OUTERMOST one. Everything inside a box that is already off the edge is
            // off the edge too, and each nested box that also overflows its own parent would report
            // again: measured on a chrome of three fixed columns holding an over-wide card, one
            // visual failure produced three findings. The first is the one to act on, and fixing it
            // is what decides whether the others were ever real.
            var offScreen = false;
            if (!insideOffScreen && BoxOverflow.OvershootX(n) is { } overX)
            {
                var contentRight = absX + n.Width - n.BorderRightW + overX;
                if (contentRight > clipRight + 1f)
                {
                    offScreen = true;
                    var name = Name(n);
                    if (reported.Add("x:" + name))
                        findings.Add(new Finding(Severity.Warning, "CF0072",
                            $"{name} is {n.Width:0}px wide but its contents need {n.Width + overX:0}px — "
                            + $"they run {contentRight - clipRight:0}px past the "
                            + $"{(clipRight >= viewportWidth - 0.5f ? "right-hand edge of the viewport" : "edge of the box that clips them")}, "
                            + "where nothing on screen says they exist.",
                            "Let the row wrap (flex-wrap:wrap), give the fixed widths a max-width or a "
                            + "@media rule, or set overflow:scroll so it can be dragged sideways. "
                            + "It does not clip or wrap on its own.",
                            LineOf(lines, ClassNeedle(n))));
                }
            }

            // overflow:hidden IS the edge for everything inside it; overflow:scroll removes the edge
            // entirely, because what is outside can be dragged into view.
            var childClip = n.Style.Overflow switch
            {
                OverflowMode.Hidden => MathF.Min(clipRight, absX + n.Width - n.BorderRightW),
                OverflowMode.Scroll => float.PositiveInfinity,
                _ => clipRight,
            };
            foreach (var c in n.Children) Walk(c, collapsed, insideOffScreen || offScreen, absX + c.X, childClip);
        }
        Walk(doc.Root, false, false, doc.Root.X, viewportWidth);

        static string Name(RenderNode n) =>
            n.Element?.GetAttribute("class") is { Length: > 0 } cls
                ? $"<{n.Tag} class='{cls}'>"
                : "<" + (n.Tag.Length > 0 ? n.Tag : "?") + ">";

        static string ClassNeedle(RenderNode n) =>
            n.Element?.GetAttribute("class") is { Length: > 0 } cls
                ? cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                : "<" + n.Tag;
    }

    // ---- 7. does every binding name something real? --------------------------------------------

    /// <summary>
    /// <c>{{Paths}}</c> that resolve to nothing on the model.
    ///
    /// <para>A misspelt binding is the quietest failure in the engine: an unknown property resolves
    /// to null, null formats as the empty string, and the element renders perfectly with nothing in
    /// it. On screen it is indistinguishable from a model that has no data yet, so it survives both
    /// a code review and a screenshot.</para>
    ///
    /// <para><b>Existence, not value.</b> The question asked is whether the PROPERTY EXISTS, by
    /// reflection on the concrete type. A property that exists and is legitimately null or empty is
    /// not a finding and must not be, or this would fire on every loading state and get switched
    /// off.</para>
    ///
    /// <para><b>Repeat scopes.</b> Inside <c>data-repeat="Items"</c> a path is relative to one item,
    /// not the root. Rather than track scopes through the template, a path that fails against the
    /// root is retried against the element type of every repeat collection in the document, and
    /// reported only when it matches nothing anywhere. That trades a few missed typos for never
    /// crying wolf on correct markup — the right way round for a tool someone has to choose to
    /// run.</para>
    /// </summary>
    private static void UnresolvedBindings(string html, object model, string[] lines, List<Finding> findings)
    {
        var scopes = new List<Type> { model.GetType() };
        foreach (Match r in RepeatAttr().Matches(html))
            if (ItemType(model.GetType(), r.Groups[1].Value.Trim()) is { } item && !scopes.Contains(item))
                scopes.Add(item);

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Mustache().Matches(html))
        {
            var path = m.Groups[1].Value.Trim();
            if (path.Length == 0 || path is "this" or ".") continue;
            if (!reported.Add(path)) continue;
            if (scopes.Any(t => PathExists(t, path))) continue;

            var leaf = path.Split('.').Last();
            var near = Nearest(leaf, PropertyNames(scopes));
            findings.Add(new Finding(Severity.Error, "CF0060",
                $"{{{{{path}}}}} does not name anything on {model.GetType().Name}, so it renders as empty text.",
                near is null
                    ? $"Add the property, or check the spelling — nothing in scope is close to '{leaf}'."
                    : $"Did you mean {{{{{near}}}}}?",
                LineOf(lines, "{{" + path)));
        }
    }

    [GeneratedRegex(@"\{\{\s*([^}]+?)\s*\}\}")]
    private static partial Regex Mustache();

    [GeneratedRegex("""data-repeat\s*=\s*["']([^"']+)["']""")]
    private static partial Regex RepeatAttr();

    /// <summary>Walk a dotted path across TYPES rather than values, so an existing property holding
    /// null is not mistaken for a missing one.</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Development-time checker; never runs inside a published app.")]
    private static bool PathExists(Type type, string path)
    {
        var current = type;
        foreach (var segRaw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = segRaw.Trim();
            if (seg is "this" or ".") continue;
            var prop = current.GetProperty(seg, PublicInstance);
            if (prop is null) return false;
            current = prop.PropertyType;
        }
        return true;
    }

    /// <summary>The element type behind a repeat collection, so paths inside the repeat are checked
    /// against an item instead of the root.</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Development-time checker; never runs inside a published app.")]
    private static Type? ItemType(Type modelType, string path)
    {
        var current = modelType;
        foreach (var segRaw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var prop = current.GetProperty(segRaw.Trim(), PublicInstance);
            if (prop is null) return null;
            current = prop.PropertyType;
        }
        if (current.IsArray) return current.GetElementType();
        foreach (var i in current.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Development-time checker; never runs inside a published app.")]
    private static string[] PropertyNames(IEnumerable<Type> types) =>
        [.. types.SelectMany(t => t.GetProperties(PublicInstance)).Select(p => p.Name).Distinct()];

    private const System.Reflection.BindingFlags PublicInstance =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

    /// <summary>First line containing <paramref name="needle"/>, 1-based, or 0. Approximate on
    /// purpose: it points at the right region without a position-tracking parse, and a finding with
    /// a nearly-right line beats one with none.</summary>
    private static int LineOf(string[] lines, string needle)
    {
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    /// <summary>The line of the Nth occurrence of <paramref name="needle"/> (zero-based), counting
    /// several on one line. A tag needle must be followed by a non-name character, so
    /// <c>&lt;p</c> does not count <c>&lt;path</c> or <c>&lt;progress</c>.</summary>
    private static int LineOf(string[] lines, string needle, int occurrence)
    {
        var seen = 0;
        var isTag = needle.StartsWith('<');
        for (var i = 0; i < lines.Length; i++)
        {
            var at = 0;
            while ((at = lines[i].IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var end = at + needle.Length;
                var wholeTag = !isTag || end >= lines[i].Length
                               || !(char.IsLetterOrDigit(lines[i][end]) || lines[i][end] == '-');
                if (wholeTag)
                {
                    if (seen == occurrence) return i + 1;
                    seen++;
                }
                at = end;
            }
        }
        return LineOf(lines, needle);   // fewer occurrences than expected: the first is still a hint
    }

    /// <summary>The closest known tag within a small edit distance, for "did you mean" — or null when
    /// nothing is close enough that suggesting it would help.</summary>
    private static string? Nearest(string tag, IReadOnlyCollection<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            var d = Distance(tag, candidate);
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }
        return bestDistance <= Math.Max(2, tag.Length / 4) ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
