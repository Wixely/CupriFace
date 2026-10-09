using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Style;
using SkiaSharp;

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
    /// <param name="configure">Wires the optional packages an app uses, exactly as
    /// <c>CupriApp.Configure</c> does — <c>doc =&gt; doc.UseSvg()</c>, and the same for Lottie or
    /// video. Without it the checker sees a document with none of them, and reports the markup they
    /// would have drawn as undrawable. Supply it whenever the real app supplies one.</param>
    public static DoctorReport Check(string html, string? css = null,
                                     ComponentRegistry? components = null,
                                     int width = 1024, int height = 768,
                                     object? model = null,
                                     Action<CupriDocument>? configure = null)
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
        var declarations = new List<(string Prop, string Value)>();
        try
        {
            // The CSS hook goes on BEFORE the document is built. Styles resolve during the first
            // build and the result is cached, so a hook attached afterwards hears nothing at all —
            // which is exactly how the first version of this quietly reported no CSS problems ever.
            StyleResolver.UnsupportedProperty = (p, _) => unsupportedCss.Add(p);
            // Value-level gaps need the declarations themselves — see UnsupportedCssFunctions.
            StyleResolver.DeclarationApplied = (p, v) => declarations.Add((p, v));
            Text.FontService.GlyphMissing = cp => missingGlyphs.Add(cp);
            try
            {
                doc = CupriDocument.Load(html, css ?? "");
                doc.UseComponents(registry);
                configure?.Invoke(doc);   // the app's optional packages, before anything is measured
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
            finally
            {
                StyleResolver.UnsupportedProperty = null;
                StyleResolver.DeclarationApplied = null;
                Text.FontService.GlyphMissing = null;
            }
        }
        catch (Exception ex)
        {
            // Name the declaration, when the failure came from the style pass.
            //
            // The raw exception on its own is unactionable — "length ('-5') must be a non-negative
            // value" says nothing about a colour, a border, or even CSS (#196). But the resolver
            // announces every declaration as it applies it, so the LAST one announced is the one it
            // was working on when it threw. The stack is checked before claiming that: an exception
            // from layout or paint has a last declaration too, and it means nothing there.
            var culprit = declarations.Count > 0 && ex.StackTrace is { } st
                          && (st.Contains("StyleResolver") || st.Contains("Colors") || st.Contains("CupriFace.Style"))
                ? declarations[^1]
                : default((string Prop, string Value)?);

            var where = culprit is { } d ? $" while applying `{d.Prop}: {d.Value}`" : "";
            var line = culprit is { } c ? LineOf(lines, c.Prop + ":") : 0;
            var fix = culprit is { } f
                ? $"Look at `{f.Prop}: {f.Value}` — the engine was applying it when the build failed. "
                  + "Fix this first: the other checks ran on an incomplete document, or not at all."
                : "Fix this first — the other checks ran on an incomplete document, or not at all.";

            findings.Add(new Finding(Severity.Error, "CF0001",
                $"The document could not be built{where}: {ex.GetType().Name}: {ex.Message}",
                fix, line));
        }

        if (doc is not null)
        {
            UnknownComponents(dom, registry, lines, findings);
            TogglesWithNothingToToggle(dom, lines, findings);
            UnrenderedElements(doc, dom, registry, lines, findings);
            ScriptingHabits(dom, lines, findings);
            BoxesThatDoNotFit(doc, width, lines, findings);
            PeersThatDoNotLineUp(doc, registry, lines, findings);
            BoxesWithNothingBetweenThem(doc, registry, lines, findings);
            BackdropFilterOutsideTopLayer(doc, lines, findings);
            TextNobodyCanRead(doc, registry, lines, findings);
            if (model is not null) UnresolvedBindings(html, model, lines, findings);
            doc.Dispose();
        }

        MissingGlyphs(missingGlyphs, findings);
        // Rules whose selector never matched during the render above have still never been looked
        // at — see StateRulesAreNeverApplied. Sweep them before reporting.
        SweepRulesThatNeverMatched(css, html, unsupportedCss);
        UnsupportedCssProperties(unsupportedCss, css, html, findings);
        // Keyframe declarations too: they never reach the resolver's loop, so the hook above never
        // sees them — and an animated transform was exactly the case worth catching, since the
        // timing, easing and stops all work while the element never moves.
        if (doc is not null)
            foreach (var frames in doc.ParsedKeyframes.Values)
                foreach (var frame in frames)
                    foreach (var (prop, value) in frame.Declarations)
                        declarations.Add((prop, value));

        UnsupportedCssFunctions(declarations, css, html, findings);
        ImportedStylesheetsAreNeverFetched(css, html, findings);

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

            // Inside a raw-text element there is no markup, so skip to its close tag rather than
            // reading its contents. A stylesheet that writes `--<id>` in a CSS comment is the case
            // that found this (#215) — it was reported as two unclosed tags, an ERROR, on a
            // document that renders perfectly. The same went for anything angle-bracketed in a
            // <textarea>'s text or in a `content: "<x>"` declaration.
            if (!isClose && RawTextElements.Contains(tag))
            {
                var closeGt = IndexOfRawTextClose(html, gt + 1, tag);
                if (closeGt < 0) { open.Push((tag, tagLine)); break; }   // unclosed: the sweep below names it
                line += CountNewlines(html, gt, closeGt);
                i = closeGt;
                continue;
            }

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

    /// <summary>The elements whose children are text and never markup — raw text (<c>style</c>,
    /// <c>script</c>) and escapable raw text (<c>textarea</c>, <c>title</c>). A <c>&lt;</c> in
    /// there is a less-than sign, so a tag scanner has to jump the whole element.</summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "script", "textarea", "title",
    };

    /// <summary>Finds the <c>&gt;</c> of the <c>&lt;/tag&gt;</c> that ends a raw-text element, or
    /// -1 if it never closes. Only that exact close tag ends one — which is why
    /// <c>&lt;/styles&gt;</c> must not match <c>style</c>, and why trailing space inside the tag
    /// must.</summary>
    private static int IndexOfRawTextClose(string html, int from, string tag)
    {
        for (var at = from; at < html.Length; at++)
        {
            at = html.IndexOf("</", at, StringComparison.Ordinal);
            if (at < 0) return -1;

            var name = at + 2;
            if (name + tag.Length > html.Length) return -1;
            if (string.Compare(html, name, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;

            var k = name + tag.Length;
            while (k < html.Length && char.IsWhiteSpace(html[k])) k++;
            if (k < html.Length && html[k] == '>') return k;
        }
        return -1;
    }

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
            // The engine's own stand-in for ::before / ::after — not a component, not the author's.
            if (tag.Equals(PseudoElements.Tag, StringComparison.OrdinalIgnoreCase)) continue;
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
            // An <svg> the SVG package has claimed is drawn, so it is not a gap. The marker is put
            // there by UseSvg during the rebuild, which is why `configure:` matters above.
            if (tag.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                if (el.HasAttribute("data-cupri-vector")) continue;
                // An <svg> with nothing drawable inside it stays empty with or without the
                // package — the package claims a drawing, and there is none to claim. The old
                // message sent the reader to install a package they had and call a method they
                // had called (#270): 17 of 165 blocks in one corpus carried an <svg> that a
                // script was going to fill, and every one was told to add CupriFace.Svg.
                if (!HasDrawableContent(el))
                {
                    if (!reported.Add("svg:empty")) continue;
                    findings.Add(new Finding(Severity.Warning, "CF0030",
                        "<svg> has no drawable content in the markup — it lays out, and then stays empty.",
                        "If a script was going to fill it at runtime, there is no script here: put the "
                        + "shapes in the markup, or bind them. If it is a placeholder, this is expected.",
                        LineOf(lines, "<" + tag, Occurrence(dom, el))));
                    continue;
                }
            }

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

    /// <summary>Whether an <c>&lt;svg&gt;</c> holds anything the SVG package would draw — a shape, a
    /// line, text, an image or a <c>&lt;use&gt;</c> — at any depth. A root with only
    /// <c>&lt;defs&gt;</c>, a <c>&lt;g&gt;</c> or nothing at all does not.</summary>
    private static bool HasDrawableContent(IElement svg)
    {
        foreach (var d in svg.QuerySelectorAll("*"))
            if (SvgDrawables.Contains(d.LocalName)) return true;
        return false;
    }

    private static readonly HashSet<string> SvgDrawables = new(StringComparer.OrdinalIgnoreCase)
    { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "image", "use" };

    /// <summary>Elements that legitimately draw nothing — structure, metadata, and the ones the
    /// engine consumes itself. Absent from the render tree by design, so never a finding.</summary>
    private static readonly HashSet<string> NotDrawnOnPurpose = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "meta", "title", "link", "style", "script", "base", "template", "br", "wbr",
        // A ::before/::after stand-in whose `content` is none is dropped from the tree by design.
        PseudoElements.Tag,
    };

    /// <summary>Elements people reach for out of browser habit, and what to use instead. Each renders
    /// as empty space today with no message, which is exactly why they are worth naming.</summary>
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["img"] = "Use <cupri-image src=\"...\"> — the engine has no raw <img> primitive.",
        ["video"] = "Use <cupri-video src=\"...\"> (desktop WebM needs the CupriFace.Media package).",
        ["audio"] = "Use <cupri-video> without a visible frame, or drive playback from your model.",
        ["canvas"] = "Supply pixels through ISurfaceSource, or use CupriFace.Gl for a GL viewport.",
        ["svg"] = "Inline SVG needs the CupriFace.Svg package: add it and call doc.UseSvg() "
                + "(pass `configure:` to CupriDoctor.Check so this check sees it too). "
                + "Without it, use <cupri-icon> for icons or draw with CSS boxes and borders.",
        ["iframe"] = "There is no embedded browser; render the content as part of this document.",
        ["object"] = "There is no plugin surface; render the content as part of this document.",
        ["embed"] = "There is no plugin surface; render the content as part of this document.",
    };

    // ---- 3b. text that is there, and unreadable -------------------------------------------------

    /// <summary>
    /// Text whose colour is too close to what is behind it to read (WCAG AA).
    ///
    /// <para>The quietest kind of defect there is: the element lays out, the colours are valid CSS,
    /// nothing throws, and the only symptom is that nobody can read the words. White on a lime
    /// accent measures 1.31:1 where ordinary text needs 4.5:1 — it looks deliberate on a designer's
    /// calibrated screen and disappears on a laptop at an angle.</para>
    ///
    /// <para><b>It stays silent whenever it cannot be sure.</b> The background is found by walking
    /// ancestors for the nearest opaque colour, and ANY gradient, background image, or partial
    /// opacity on the way makes the answer a guess rather than a fact — so the check gives up
    /// rather than reporting. A contrast warning on a button that is actually fine is how a check
    /// like this gets switched off wholesale, and <c>CF0030</c> has already taught this repository
    /// what a confident wrong diagnostic costs (#270).</para>
    /// </summary>
    private static void TextNobodyCanRead(CupriDocument doc, ComponentRegistry registry, string[] lines, List<Finding> findings)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Walk(RenderNode n)
        {
            if (n.Style.Display == DisplayType.None) return;
            if (n.IsText && n.Lines is { Count: > 0 }) Check(n);
            foreach (var c in n.Children) Walk(c);
        }

        void Check(RenderNode text)
        {
            var s = text.Style;
            if (s.Color.Alpha == 0) return;                       // invisible on purpose
            if (text.Lines!.All(l => l.Text.Trim().Length == 0)) return;
            // Hidden from assistive tech is usually hidden from sight too, or decorative.
            for (var a = text.Parent; a is not null; a = a.Parent)
                if (a.Element?.GetAttribute("aria-hidden") == "true") return;

            // A control's own insides are swept out, exactly as the component library's CSS is
            // swept out of CF0050: a caller cannot restyle what `<cupri-button>` expands into, so
            // a finding there is noise they cannot act on — and this repository's own bug rather
            // than theirs. (The stock primary button is white on the copper accent, which measures
            // 3.8:1 against the 4.5:1 bar. Recorded in the tests so the number is on file.)
            //
            // The cost is real and worth naming: an author who retunes `--cupri-accent` to
            // something unreadable will not hear about it from here. Their OWN markup, which is
            // where nearly all text lives, is still checked.
            for (var a = text.Parent; a is not null; a = a.Parent)
                if (a.Element is { } ancestor && registry.Tags.Contains(ancestor.LocalName, StringComparer.OrdinalIgnoreCase))
                    return;

            if (BackgroundUnder(text) is not { } bg) return;      // not knowable: say nothing
            var fg = Contrast.Over(s.Color, bg);
            var ratio = Contrast.Ratio(fg, bg);
            var need = Contrast.RequiredFor(s.FontSize, s.FontWeight);
            if (ratio >= need - 0.005) return;

            // One finding per COLOUR PAIR, not per run of text. The pair is the actionable unit —
            // it is fixed once, in one rule — and the same two colours appear on dozens of elements
            // in any real document. Keyed before the sample is taken so the first occurrence is the
            // one quoted, and its line is the one reported. (The shipped Showcase goes from 55
            // findings to 6 under this rule, saying the same thing.)
            if (!reported.Add(Hex(fg) + "|" + Hex(bg))) return;
            var sample = text.Lines.Select(l => l.Text.Trim()).FirstOrDefault(t => t.Length > 0) ?? "";
            if (sample.Length > 28) sample = sample[..27] + "…";

            var big = need == Contrast.AaLarge;
            findings.Add(new Finding(Severity.Warning, "CF0090",
                $"\"{sample}\" is {Hex(fg)} on {Hex(bg)} — a contrast ratio of {ratio:0.0}:1, "
                + $"where {(big ? "large text" : "text this size")} needs {need:0.0}:1 to be readable.",
                $"Darken or lighten one of them. {Hex(Readable(bg))} on {Hex(bg)} would give "
                + $"{Contrast.Ratio(Readable(bg), bg):0.0}:1.",
                LineOf(lines, sample.Length > 0 ? sample : "<")));
        }

        Walk(doc.Root);
    }

    /// <summary>
    /// The opaque colour painted behind this node, or null when that cannot be known.
    ///
    /// <para>Null is the important half. A gradient, a background image or a partially transparent
    /// ancestor all mean the colour behind the text varies or is not this function's to compute,
    /// and a number guessed from the nearest solid underneath it would be confidently wrong
    /// somewhere in the box.</para>
    /// </summary>
    private static SKColor? BackgroundUnder(RenderNode text)
    {
        for (var n = text.Parent; n is not null; n = n.Parent)
        {
            var s = n.Style;
            if (s.Opacity < 1f) return null;                      // the whole subtree is composited
            if (s.HasBackgroundImage) return null;                // a gradient or an image: it varies
            if (s.Background.Alpha == 0) continue;                // see through it to the next one
            if (s.Background.Alpha < 255) return null;            // part of what is under it shows
            return s.Background;
        }
        return null;                                              // nothing opaque anywhere up the chain
    }

    /// <summary>Black or white, whichever reads better on <paramref name="bg"/> — the suggestion
    /// that comes with the finding.</summary>
    private static SKColor Readable(SKColor bg) =>
        Contrast.Ratio(SKColors.Black, bg) >= Contrast.Ratio(SKColors.White, bg) ? SKColors.Black : SKColors.White;

    private static string Hex(SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";

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

    /// <summary>
    /// The properties the resolver threw away.
    ///
    /// <para>A null <paramref name="css"/> means "there is no external stylesheet", NOT "skip the
    /// CSS checks" — which is what it used to mean, and it took the document's own
    /// <c>&lt;style&gt;</c> block with it (#183). A composition that keeps its rules where nearly
    /// every composition keeps them reported clean, and the signature (<c>string? css</c>) invites
    /// exactly that call. A linter that is silent when it has not looked is worse than one that is
    /// absent, because the absent one makes nobody confident.</para>
    ///
    /// <para>Lines are looked for in the stylesheet AND in the markup, since an inline block lives
    /// in the second. A property with no line is still reported: the finding is the point, the line
    /// number is a convenience.</para>
    /// </summary>
    /// <summary>
    /// Every property the author wrote, including the ones behind a state the check never enters.
    ///
    /// <para><b>The render above only hears about declarations that were APPLIED.</b> A rule is
    /// applied when its selector matches an element, so a document checked at rest never examines
    /// <c>:focus</c>, <c>:hover</c>, <c>:active</c> or <c>:checked</c> — nor any rule for a class
    /// that is added later at runtime. That is the single worst place to be blind, because a focus
    /// ring is written in exactly one of those rules and the property it reaches for first
    /// (<c>outline</c>) is not one this engine supports. Measured before this existed:
    /// <c>.btn { outline: … }</c> reported CF0050; the identical <c>.btn:focus { outline: … }</c>
    /// reported nothing at all.</para>
    ///
    /// <para>So every rule is replayed through <see cref="StyleResolver.ApplyDeclarations"/> onto a
    /// throwaway style, with no selector matching involved. Replayed rather than pattern-matched
    /// against a list of known properties: the resolver's own switch decides what is supported, and
    /// a second list here would drift from it the first time a property was added.</para>
    ///
    /// <para>Only the AUTHOR's CSS — the stylesheet plus any inline <c>&lt;style&gt;</c>. The
    /// component library's own rules are swept out deliberately: a caller cannot fix those, and
    /// anything found there is this repository's bug to fix rather than a finding to publish.</para>
    ///
    /// <para>A rule that matches nothing at all is still reported, and that is intended. A property
    /// the engine ignores does nothing wherever it is written, and "this rule is dead" is worth
    /// knowing on its own.</para>
    /// </summary>
    private static void SweepRulesThatNeverMatched(string? css, string html, List<string> into)
    {
        // The stylesheet, plus every inline <style> block, read from the MARKUP rather than from the
        // built DOM. Two reasons: a document that failed to build has no DOM and its CSS is exactly
        // what a caller most wants checked, and this is already how CssLines finds line numbers —
        // one notion of "where CSS can be written", not two that can disagree.
        var rules = CssParser.Parse(css);
        foreach (Match m in InlineStyleBlocks().Matches(html))
            rules.AddRange(CssParser.Parse(m.Groups[1].Value));
        if (rules.Count == 0) return;

        var previous = StyleResolver.UnsupportedProperty;
        StyleResolver.UnsupportedProperty = (prop, _) => into.Add(prop);
        try
        {
            foreach (var rule in rules)
            {
                // Per rule, because one unparseable value must not cost the sweep every rule after
                // it. A declaration that THROWS is already reported by the render pass when its
                // selector matches; when it never matches, nothing was going to paint anyway.
                try { StyleResolver.ApplyDeclarations(new ComputedStyle(), rule.Declarations); }
                catch { /* not a supportedness question */ }
            }
        }
        finally { StyleResolver.UnsupportedProperty = previous; }
    }

    private static void UnsupportedCssProperties(List<string> ignored, string? css, string html,
                                                 List<Finding> findings)
    {
        var lines = CssLines(css, html);
        foreach (var prop in ignored.Distinct(StringComparer.OrdinalIgnoreCase))
            findings.Add(new Finding(Severity.Warning, "CF0050",
                $"CSS property '{prop}' is not supported and was ignored.",
                Suggest(prop), LineOf(lines, prop + ":")));
    }

    /// <summary>Everywhere CSS can be written in what the caller handed us: the stylesheet, then the
    /// markup (for an inline <c>&lt;style&gt;</c>). Concatenated rather than chosen between, because
    /// a document may well have both.</summary>
    [GeneratedRegex(@"<style[^>]*>(.*?)</style\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyleBlocks();

    private static string[] CssLines(string? css, string html) =>
        ((css ?? "") + "\n" + html).Replace("\r\n", "\n").Split('\n');

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

    /// <summary>
    /// Value-level gaps the property switch cannot see: the property is supported, the FUNCTION
    /// inside it is not, so the declaration is accepted and then quietly paints nothing. The list of
    /// gaps is hand-written, because there is no switch to derive it from — so it is kept short and
    /// specific rather than trying to be complete.
    ///
    /// <para><b>Raised from the DECLARATIONS the resolver applied, not from the document's text.</b>
    /// It used to be a substring search over the stylesheet and the markup, which meant it reported
    /// a document that never used a repeating gradient: one whose CSS comment explained that it
    /// deliberately avoids them, or whose body copy merely displayed the words (#188). A project
    /// could not document why it avoided the feature without failing its own lint, and any
    /// composition whose subject was CSS became unlintable. Only a declaration can paint nothing, so
    /// only a declaration is examined — the same place CF0050 is raised from, which is why that one
    /// never had this problem.</para>
    ///
    /// <para>The line number is still found by searching the text, since a declaration does not carry
    /// one. That is a convenience on a finding that has already been established, and it is exactly
    /// what CF0050 does.</para>
    /// </summary>
    private static void UnsupportedCssFunctions(IReadOnlyList<(string Prop, string Value)> declarations,
                                                string? css, string html, List<Finding> findings)
    {
        var lines = CssLines(css, html);
        foreach (var (needle, display, fix) in FunctionGaps)
        {
            // One finding per gap, however many rules use it: the resolver announces a declaration
            // once per element it applies to, so a gradient on a repeated row would otherwise be
            // reported once per row.
            if (!declarations.Any(d => d.Value.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                continue;
            findings.Add(new Finding(Severity.Warning, "CF0051",
                $"{display}() is not supported — the declaration parses but paints nothing.",
                fix, LineOf(lines, needle)));
        }
    }

    private static readonly (string Needle, string Display, string Fix)[] FunctionGaps =
    [
        ("repeating-linear-gradient(", "repeating-linear-gradient",
            "Only linear-gradient() and radial-gradient() exist. Repeat it with elements instead."),
        ("repeating-radial-gradient(", "repeating-radial-gradient", "Only linear-gradient() and radial-gradient() exist."),
        ("conic-gradient(", "conic-gradient", "Not supported; a radial-gradient() or an ISurfaceSource can stand in."),
        ("image-set(", "image-set", "Not supported — give one source."),

        // The transform functions the resolver's switch has no case for. They were the worst kind of
        // silent: `transform` is one of the few properties that ANIMATES, so a composition could run
        // a rotateY through a whole keyframe sequence with the timing, easing and stops all working
        // while the element never moved (#201). This is a 2D engine and these are the 3D ones, plus
        // the 2D forms it never implemented.
        // rotateX/rotateY/rotateZ, rotate3d about an axis, translate3d/translateZ, scale3d and
        // perspective() are drawn since #269 and are no longer listed. scaleZ has no visible
        // effect on a flat projection, in a browser or here.
        ("matrix3d(", "matrix3d", "Not implemented — compose the effect from translate/scale/rotateX/rotateY/rotate."),
        ("matrix(", "matrix", "Not implemented — compose the effect from translate(), scale() and rotate()."),
        ("skew(", "skew", "Not implemented — there is no shear in the transform pipeline."),
        ("skewx(", "skewX", "Not implemented — there is no shear in the transform pipeline."),
        ("skewy(", "skewY", "Not implemented — there is no shear in the transform pipeline."),
    ];

    /// <summary>
    /// <c>@import</c>, which the engine steps over without fetching.
    ///
    /// <para>Since #289 it no longer eats the rule that follows it, which was the loud half. What
    /// remains is quiet and worth saying: a sheet imported for a web font never arrives, so the text
    /// renders in whatever fallback the stack names and nothing explains why the typeface is wrong.
    /// An import is the ordinary way to pull a font in CSS — 18 of 165 corpus compositions open with
    /// one — so this is the common case, not an exotic one.</para>
    ///
    /// <para>Read from the stylesheet and inline <c>&lt;style&gt;</c> blocks with comments stripped,
    /// never from the whole document's text: a CSS comment explaining the absence of imports, or
    /// body copy that merely says the word, must not fail a caller's own lint (#188). One finding per
    /// document, because the fix is the same for every import in it.</para>
    /// </summary>
    private static void ImportedStylesheetsAreNeverFetched(string? css, string html, List<Finding> findings)
    {
        var authored = CssParser.StripComments(css ?? "");
        foreach (Match block in InlineStyleBlocks().Matches(html))
            authored += "\n" + CssParser.StripComments(block.Groups[1].Value);
        if (!ImportStatement().IsMatch(authored)) return;
        findings.Add(new Finding(Severity.Warning, "CF0052",
            "@import is not fetched — the engine steps over it, so the stylesheet it names never loads.",
            "There is no CSS loader here. Register the font with app.LoadFonts(dir) / LoadFont(bytes) "
            + "and name it in font-family, or inline the imported rules into this stylesheet.",
            LineOf(CssLines(css, html), "@import")));
    }

    /// <summary>An <c>@import</c> STATEMENT: the at-keyword, something, and its terminating
    /// semicolon. Deliberately not a bare substring — see the method above.</summary>
    [GeneratedRegex(@"@import\s[^;}]*;", RegexOptions.IgnoreCase)]
    private static partial Regex ImportStatement();

    /// <summary>
    /// <c>backdrop-filter</c> outside the top layer, where it parses and then paints nothing.
    ///
    /// <para>It is NOT unsupported — a dialog or drawer really does frost what is behind it — which
    /// is why it cannot be a line in the table above and why it was silent: the property resolves,
    /// the painter reads it, and the painter only reads it for a top-layer node. On an ordinary
    /// element it is accepted and has no effect, and nothing said so (#201).</para>
    ///
    /// <para>Reported per element rather than per property, because the same declaration is correct
    /// in one place and inert in another.</para>
    /// </summary>
    private static void BackdropFilterOutsideTopLayer(CupriDocument doc, string[] lines, List<Finding> findings)
    {
        var reported = false;
        void Walk(Dom.RenderNode n)
        {
            if (!reported && n.Style.BackdropFilter is { Count: > 0 } && !n.IsTopLayer && n.Element is not null)
            {
                reported = true;   // one line; the cause is the same for every element that does it
                findings.Add(new Finding(Severity.Warning, "CF0051",
                    "backdrop-filter only frosts what is behind a TOP-LAYER element (a dialog, drawer "
                    + "or sheet). On an ordinary element it parses and paints nothing.",
                    "Put the element in the top layer, or fake it with a semi-transparent background.",
                    LineOf(lines, "backdrop-filter")));
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
    }

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

    }

    /// <summary>An element as a reader will recognise it in their own markup: the tag and the class
    /// they wrote. Shared by every box-shaped finding so one element is named one way.</summary>
    private static string Name(RenderNode n) =>
        n.Element?.GetAttribute("class") is { Length: > 0 } cls
            ? $"<{n.Tag} class='{cls}'>"
            : "<" + (n.Tag.Length > 0 ? n.Tag : "?") + ">";

    /// <summary>What to search the source text for to put a line number on it.</summary>
    private static string ClassNeedle(RenderNode n) =>
        n.Element?.GetAttribute("class") is { Length: > 0 } cls
            ? cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
            : "<" + n.Tag;

    // ---- 6b. do the things that look alike line up? --------------------------------------------

    /// <summary>
    /// Repeated controls that do not agree on their size — the odd one breaks the alignment.
    ///
    /// <para><b>Why no other check sees this.</b> Every box here is exactly the size it asked to be.
    /// Nothing overflows, nothing clips, nothing is unreadable, no binding is wrong: each element is
    /// individually correct and the SET is wrong. A screenshot shows it immediately and every
    /// automatic check says the document is fine — which is the signature of the failures this tool
    /// exists for.</para>
    ///
    /// <para><b>Why it happens.</b> A control sized by its content changes size when the content
    /// does, and content that depends on state ("Connect" / "Configure" / "Disconnecting…") differs
    /// from row to row. Each row is laid out correctly for the text it got, so one button juts out
    /// of a column that is otherwise flush. TOOLBOX's "pin anything whose text changes" is the fix
    /// and has been written down for a long time; what was missing was anything that NOTICED.</para>
    ///
    /// <para><b>The CROSS axis is the one that matters</b>, which is what makes the check possible
    /// at all. Along the axis peers are stacked on, size is just content being different lengths: a
    /// toolbar's buttons are as wide as their labels and nobody expects otherwise. Across it, size
    /// carries a line the eye follows. So a COLUMN of peers is checked on its widths and a ROW of
    /// peers on its heights, and neither is checked on the other.</para>
    ///
    /// <para><b>Controls only, and that is the whole design.</b> Bare text of different lengths is
    /// not a defect — a column of status labels reading "Demo mode", "Local TMDB development
    /// snapshot" and "Demo resolver" is ragged by 162px and completely fine, while the three
    /// buttons beside it differing by 12px is the bug. Flagging size disagreement generally would
    /// bury the second finding under the first. So a peer must carry an interactive ROLE (the same
    /// <see cref="Accessibility.AccessibilityTree.RoleOf"/> a screen reader reads) or be a
    /// registered <c>cupri-*</c> control, and only the OUTERMOST such element counts — a caller
    /// cannot restyle what a component expands into.</para>
    ///
    /// <para><b>The structural rule differs per axis, because so does the innocent twin.</b> A
    /// column is only a column anyone expects to line up when each peer sits in its own ROW — a
    /// de-facto table. Peers sharing one parent are a stack of chips, bubbles or nav links, which
    /// is shrink-wrapped by nature and must stay silent. A row is the opposite: one shared parent
    /// is the ordinary shape of a deck of cards, and that is exactly where a taller one spoils the
    /// line.</para>
    ///
    /// <para><b>It under-reports on purpose.</b> A group needs three peers, because the finding is
    /// "this one disagrees with those two" and a pair has no majority to disagree with; three
    /// different sizes say nothing, for want of an honest number to suggest pinning to; and an
    /// element given an explicit size on the axis in question is taken at its word (which is what
    /// spares a hand-rolled bar chart, whose bars are explicitly sized by definition). A div styled
    /// to look like a button but carrying no role is invisible here; trust a finding, not its
    /// absence.</para>
    /// </summary>
    private static void PeersThatDoNotLineUp(CupriDocument doc, ComponentRegistry registry,
                                             string[] lines, List<Finding> findings)
    {
        // Signature: the tag plus its classes, which is as close as markup gets to the author
        // saying "these are the same kind of thing". Position in the tree is deliberately NOT part
        // of it — rows are often generated, and a repeat that differs structurally (an extra badge
        // on one row) is exactly where this defect hides.
        var groups = new Dictionary<string, List<RenderNode>>(StringComparer.Ordinal);

        void Walk(RenderNode n, bool insideControl)
        {
            if (n.Style.Display == DisplayType.None) return;
            var isControl = !insideControl && n.Element is { } el && IsControl(el, n, registry);
            if (isControl && n.Width > 0.5f && n.Height > 0.5f)
            {
                var key = Signature(n);
                (groups.TryGetValue(key, out var list) ? list : groups[key] = []).Add(n);
            }
            foreach (var c in n.Children) Walk(c, insideControl || isControl);
        }
        Walk(doc.Root, false);

        foreach (var (_, peers) in groups)
        {
            if (peers.Count < 3) continue;

            // Where they sit relative to each other decides WHICH question to ask. A column of
            // peers must agree on their widths; a row of peers must agree on their heights. The
            // axis they are stacked along is free — that is just content being different lengths —
            // and the CROSS axis is the one carrying a line the eye follows.
            var byY = peers.Select(p => (Node: p, Box: Interaction.HitTesting.ScreenBox(p)))
                           .OrderBy(p => p.Box.Y).ToList();
            var byX = byY.OrderBy(p => p.Box.X).ToList();

            if (Stacked(byY, vertical: true)) Compare(byY, vertical: true);
            else if (Stacked(byX, vertical: false)) Compare(byX, vertical: false);

            // Each one clear of the last along the stacking axis, and all of them sharing ground on
            // the other — a column, or a row, rather than an incidental scatter.
            static bool Stacked(List<(RenderNode Node, (float X, float Y, float W, float H) Box)> b, bool vertical)
            {
                float lo = vertical ? b[0].Box.X : b[0].Box.Y;
                float hi = lo + (vertical ? b[0].Box.W : b[0].Box.H);
                for (var i = 1; i < b.Count; i++)
                {
                    var (_, prev) = b[i - 1];
                    var (_, cur) = b[i];
                    var clear = vertical ? cur.Y >= prev.Y + prev.H - 0.5f : cur.X >= prev.X + prev.W - 0.5f;
                    if (!clear) return false;
                    lo = MathF.Max(lo, vertical ? cur.X : cur.Y);
                    hi = MathF.Min(hi, vertical ? cur.X + cur.W : cur.Y + cur.H);
                }
                return hi > lo;
            }

            void Compare(List<(RenderNode Node, (float X, float Y, float W, float H) Box)> boxes, bool vertical)
            {
                // The structural rule differs by axis, because so does the innocent twin.
                //
                // A COLUMN of peers is only a column anyone expects to line up when each one sits
                // in its own row — a de-facto table. Sharing one parent makes it a stack of chips,
                // bubbles or nav links, which is shrink-wrapped by nature and must stay silent.
                //
                // A ROW is the other way round: one shared parent is the ordinary, correct shape of
                // a row of cards or buttons, and that is exactly where a taller one spoils the line.
                var parents = boxes.Select(b => b.Node.Parent).ToList();
                if (parents.Any(p => p?.Element is null)) return;
                var oneParent = parents.Distinct().Count() == 1;
                var repeatedRows = parents.Distinct().Count() == boxes.Count
                                   && parents.Select(Signature).Distinct().Count() == 1;
                if (vertical ? !repeatedRows : !(oneParent || repeatedRows)) return;

                float Size((float X, float Y, float W, float H) b) => vertical ? b.W : b.H;

                // The size the group agrees on, and who disagrees with it. A majority is required
                // rather than a spread, so three peers of three different sizes say nothing: there
                // would be no "the others", and no honest number to suggest pinning to.
                var agreed = boxes.GroupBy(b => MathF.Round(Size(b.Box) * 2f) / 2f)
                                  .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
                if (agreed.Count() < 2 || agreed.Count() * 2 < boxes.Count) return;
                // 4px, not 1 or 2. CI settled this rather than taste: a fixture whose labels
                // differed by ONE LETTER ("Connect" / "Connecl") measured 2px apart on the Linux
                // runner's fonts and produced a finding, while the same markup on Windows and
                // macOS measured under a pixel. A difference that small is both invisible and
                // unstable across machines, and it is never what this check is for — a label that
                // changes with state ("Connect" / "Disconnecting…") moves a control by tens of
                // pixels.
                var odd = boxes.Where(b => MathF.Abs(Size(b.Box) - agreed.Key) >= 4f).ToList();

                // Explicitly sized: the author said what they wanted and got it.
                odd = odd.Where(b => (vertical ? b.Node.Style.Width : b.Node.Style.Height).IsAuto).ToList();
                if (odd.Count == 0) return;

                var worst = odd.OrderByDescending(b => MathF.Abs(Size(b.Box) - agreed.Key)).First();
                var others = boxes.Count - odd.Count;
                var gap = MathF.Abs(Size(worst.Box) - agreed.Key);

                // Which edge is ragged, because that is what the eye actually caught. With differing
                // sizes at most one edge can line up, so there is always an answer.
                var near = boxes.Select(b => vertical ? b.Box.X : b.Box.Y).ToList();
                var far = boxes.Select(b => vertical ? b.Box.X + b.Box.W : b.Box.Y + b.Box.H).ToList();
                var (nearName, farName, line) = vertical
                    ? ("left", "right", "column")
                    : ("top", "bottom", "row");
                var edge = near.Max() - near.Min() <= 0.5f
                    ? $"Their {nearName} edges line up, so this one's {farName} edge juts {gap:0}px out of the {line}."
                    : far.Max() - far.Min() <= 0.5f
                        ? $"Their {farName} edges line up, so this one's {nearName} edge juts {gap:0}px out of the {line}."
                        : $"Neither edge lines up: it sits {gap:0}px out of the {line}.";

                var label = FirstText(worst.Node);
                var otherLabel = boxes.FirstOrDefault(b => !odd.Contains(b)) is { Node: { } o } ? FirstText(o) : "";
                var because = label.Length > 0 && otherLabel.Length > 0 && label != otherLabel
                    ? $" — \"{label}\" is {(vertical ? "a longer label than" : "longer text than")} \"{otherLabel}\""
                    : "";

                var fix = vertical
                    ? $"Pin the size so the text cannot move it: min-width: {MathF.Max(Size(worst.Box), agreed.Key):0}px "
                      + "on all of them (or a fixed column for the whole group). A control sized by its "
                      + "label resizes whenever the label changes, which is what state-dependent text does "
                      + "on every render. For digits, font-variant-numeric: tabular-nums does the same job."
                    : $"Let the row size them together — align-items: stretch (the flex default) gives every "
                      + $"item the tallest one's height, and something has overridden it here. Or pin a floor: "
                      + $"min-height: {MathF.Max(Size(worst.Box), agreed.Key):0}px on all of them. Text that wraps "
                      + "to one more line is the usual cause, so the fix has to survive the longest string, "
                      + "not today's.";

                findings.Add(new Finding(Severity.Warning, "CF0073",
                    $"{Name(worst.Node)} is {Size(worst.Box):0}px {(vertical ? "wide" : "tall")} where the "
                    + $"other {others} like it {(others == 1 ? "is" : "are")} {agreed.Key:0}px{because}. {edge}",
                    fix,
                    // The odd one's own line, found by its LABEL — the class needle would land on the
                    // first peer, which is the one that is RIGHT, and send a reader to the wrong row.
                    (label.Length > 0 ? LineOf(lines, label) : 0) is > 0 and var ln
                        ? ln : LineOf(lines, ClassNeedle(worst.Node))));
            }
        }

        // Tag plus classes: as close as markup gets to the author saying "these are the same kind
        // of thing". Used for the peers themselves and for the rows that hold them.
        static string Signature(RenderNode? n) =>
            n?.Tag + "|" + (n?.Element?.GetAttribute("class") ?? "");

        // An interactive element in its own right: the role a screen reader would read, or a
        // registered component. Disabled is deliberately not excluded — a greyed-out button still
        // has to line up with its neighbours.
        static bool IsControl(IElement el, RenderNode n, ComponentRegistry registry) =>
            registry.Tags.Contains(el.LocalName, StringComparer.OrdinalIgnoreCase)
            || Accessibility.AccessibilityTree.RoleOf(n) is "button" or "link" or "switch" or "checkbox"
                   or "radio" or "slider" or "textbox" or "spinbutton" or "combobox" or "tab" or "menuitem";

        // The first line of text inside a control, for naming it in the finding. A label, not a
        // semantic claim — so the first non-empty run in tree order is the right one.
        static string FirstText(RenderNode n)
        {
            if (n.IsText && n.Lines is { Count: > 0 })
                foreach (var line in n.Lines)
                    if (line.Text.Trim() is { Length: > 0 } t) return t.Length > 24 ? t[..23] + "…" : t;
            foreach (var c in n.Children)
                if (FirstText(c) is { Length: > 0 } found) return found;
            return "";
        }
    }

    // ---- 6c. is anything that should stand alone touching? -------------------------------------

    /// <summary>
    /// Two rounded, filled boxes sitting flush against each other, with nothing between them.
    ///
    /// <para>A rounded corner is the author saying "this is a separate object". Two of them meeting
    /// at 0px do not read as two objects: the curves collide, the background shows through the
    /// wedge between them, and the pair reads as one broken shape. It is the signature of a margin
    /// nobody set, and — like everything else in this section — every box involved is exactly the
    /// size and position it asked for, so nothing else can see it.</para>
    ///
    /// <para><b>The corners that MEET are the whole rule.</b> Flush is not evidence of anything on
    /// its own: a card header above a card body is flush BY DESIGN and must stay silent. Measured
    /// side by side, those two cases are identical — a 760x85 box at y=0 and another at y=85, in
    /// both. What separates them is that the header's bottom corners and the body's top corners are
    /// SQUARE, which is how the author said "these two are one surface", while a card and a button
    /// below it are rounded on both sides of the seam. So the check looks only at the corners on
    /// the edge where the two actually touch, and only reports when both sides are rounded
    /// there.</para>
    ///
    /// <para><b>Flush, not overlapping.</b> An overlap is usually deliberate — a stack of round
    /// avatars pulled together with a negative margin is a design, not a defect — so only a gap of
    /// nothing at all is reported. Zero is the number that means "unset"; anything else, including
    /// a single pixel, is a number somebody chose.</para>
    ///
    /// <para><b>Info, not a warning</b>, and the first check in this tool to say so. The layout
    /// works; it reads badly. That is the level's definition, and it keeps a judgement call about
    /// visual polish out of anything gating on warnings — this is the most subjective check here
    /// and it should be the easiest to ignore.</para>
    /// </summary>
    private static void BoxesWithNothingBetweenThem(CupriDocument doc, ComponentRegistry registry,
                                                    string[] lines, List<Finding> findings)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Walk(RenderNode n, bool insideControl)
        {
            if (n.Style.Display == DisplayType.None) return;
            if (!insideControl) CheckChildrenOf(n);
            var inside = insideControl
                || (n.Element is { } el && registry.Tags.Contains(el.LocalName, StringComparer.OrdinalIgnoreCase));
            foreach (var c in n.Children) Walk(c, inside);
        }

        void CheckChildrenOf(RenderNode parent)
        {
            // Visible boxes only: a radius nobody can see cannot collide with anything. In document
            // order, which is the order they were written and the order they lay out in — a pair
            // that is flush and adjacent is a pair someone wrote next to each other.
            var kids = parent.Children
                .Where(c => c.Style.Display != DisplayType.None && !c.IsText
                            && c.Width > 0.5f && c.Height > 0.5f && IsVisibleBox(c))
                .ToList();

            for (var i = 1; i < kids.Count; i++) Pair(kids[i - 1], kids[i]);
        }

        void Pair(RenderNode a, RenderNode b)
        {
            var (ax, ay, aw, ah) = Interaction.HitTesting.ScreenBox(a);
            var (bx, by, bw, bh) = Interaction.HitTesting.ScreenBox(b);
            var ra = a.Style.BorderRadius.Resolve(aw, ah);
            var rb = b.Style.BorderRadius.Resolve(bw, bh);

            // Flush along one axis, genuinely overlapping on the other — touching, not merely
            // diagonal from one another.
            // Neither box may be one whose PAINTED appearance is not its laid-out box. This check
            // reasons about layout, and #296 found two correct documents that are correct because
            // of something that happens after it: four subtitle cues stacked flush and faded in
            // one at a time (`opacity: 0`, so no viewer ever sees two of them), and a 3D card's
            // depth face placed at exactly the card's width and then turned edge-on with
            // `rotateY(90deg)` — where flush is not incidental but the construction, since a
            // depth face that did NOT touch the front face would be a visible crack in the solid.
            //
            // Anything reasoning about adjacency from the layout box alone has this whole class of
            // false positive, so the exclusion is stated generally rather than per-symptom.
            if (NotWhereItWasLaidOut(a) || NotWhereItWasLaidOut(b)) return;

            var below = MathF.Abs(by - (ay + ah)) <= 0.5f
                        && MathF.Min(ax + aw, bx + bw) - MathF.Max(ax, bx) > 0.5f;
            var beside = MathF.Abs(bx - (ax + aw)) <= 0.5f
                         && MathF.Min(ay + ah, by + bh) - MathF.Max(ay, by) > 0.5f;
            if (!below && !beside) return;

            var rounded = below
                ? Round(ra.BottomLeft, ra.BottomRight) && Round(rb.TopLeft, rb.TopRight)
                : Round(ra.TopRight, ra.BottomRight) && Round(rb.TopLeft, rb.BottomLeft);
            if (!rounded) return;

            var key = Name(a) + "|" + Name(b);
            if (!reported.Add(key)) return;

            var where = below ? "directly below" : "directly beside";
            var side = below ? "margin-top" : "margin-left";
            findings.Add(new Finding(Severity.Info, "CF0074",
                $"{Name(b)} sits flush {where} {Name(a)} — nothing at all between them, and both are "
                + "rounded where they meet, so the two curves collide instead of reading as separate things.",
                $"Give them room: {side} on the second, or a gap on the parent if it is a flex row or "
                + "column. Square-edged boxes can sit flush and read as one surface — a card header "
                + "above a card body does exactly that — which is why this is only reported when both "
                + "sides of the seam are curved.",
                LineOf(lines, ClassNeedle(b))));
        }

        /// <summary>Whether this box is painted somewhere other than where it was laid out — or not
        /// painted at all. Either makes its layout-box adjacency meaningless.
        ///
        /// <para>Zero opacity counts from any ancestor, because that is how it reaches the screen:
        /// a faded parent hides a fully opaque child. A TRANSFORM counts only on the box itself —
        /// one on a shared ancestor moves both peers together and leaves the seam between them
        /// exactly as it was.</para>
        ///
        /// <para>Excluding every transform rather than only the out-of-plane rotation that was
        /// reported is deliberate, and it has a cost worth naming: a genuinely colliding pair where
        /// one is nudged by `translateY(-2px)` on hover will no longer be reported. The general
        /// statement is the honest one — a transform means the painted position is not the laid-out
        /// position — and under-reporting is this check's stated posture.</para></summary>
        static bool NotWhereItWasLaidOut(RenderNode n)
        {
            if (n.Style.HasTransform) return true;
            for (var a = n; a is not null; a = a.Parent)
                if (a.Style.Opacity <= 0.001f) return true;
            return false;
        }

        // Rounded on the edge where the two meet. Either corner of that edge is enough: a single
        // curve against a flush neighbour already shows the wedge.
        static bool Round(SkiaSharp.SKPoint c1, SkiaSharp.SKPoint c2) =>
            MathF.Max(c1.X, c1.Y) > 0.5f || MathF.Max(c2.X, c2.Y) > 0.5f;

        // Something you can see the shape of: a fill, an image, or a border. A transparent box's
        // corners are a fact about nothing.
        static bool IsVisibleBox(RenderNode n) =>
            n.Style.Background.Alpha > 0 || n.Style.HasBackgroundImage
            || ((n.BorderTopW > 0 || n.BorderRightW > 0 || n.BorderBottomW > 0 || n.BorderLeftW > 0)
                && n.Style.BorderTopColor.Alpha > 0);

        Walk(doc.Root, false);
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
        // Every repeat's element type, to a FIXED POINT. Resolving each name against the root type
        // alone missed a repeat whose collection lives on an ITEM type — a per-row detail panel, an
        // options group, a thread — so its element type never entered scope and every binding inside
        // it was reported as naming nothing, at Severity.Error, on markup that renders correctly
        // (#160). Bounded by the number of distinct row types: each pass either adds one or stops.
        var scopes = new List<Type> { model.GetType() };
        var repeats = RepeatAttr().Matches(html).Select(r => r.Groups[1].Value.Trim()).Distinct().ToList();
        bool added;
        do
        {
            added = false;
            foreach (var name in repeats)
                foreach (var scope in scopes.ToList())
                    if (ItemType(scope, name) is { } item && !scopes.Contains(item))
                    {
                        scopes.Add(item);
                        added = true;
                    }
        } while (added);

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
