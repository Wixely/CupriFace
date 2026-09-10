using System.Text;
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
public static class CupriDoctor
{
    /// <summary>Check markup and, if you have one, a stylesheet.</summary>
    /// <param name="html">Document markup — the same string you would give <c>CupriApp.Html</c>.</param>
    /// <param name="css">The stylesheet. Without it the CSS checks are skipped rather than guessed.</param>
    /// <param name="components">The component library the app will use. Defaults to the built-ins,
    /// which is what an app that does not override <c>CupriApp.Components</c> gets.</param>
    /// <param name="width">Viewport for the trial layout.</param>
    /// <param name="height">Viewport height for the trial layout.</param>
    public static DoctorReport Check(string html, string? css = null,
                                     ComponentRegistry? components = null,
                                     int width = 1024, int height = 768)
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
        try
        {
            // The CSS hook goes on BEFORE the document is built. Styles resolve during the first
            // build and the result is cached, so a hook attached afterwards hears nothing at all —
            // which is exactly how the first version of this quietly reported no CSS problems ever.
            StyleResolver.UnsupportedProperty = (p, _) => unsupportedCss.Add(p);
            try
            {
                doc = CupriDocument.Load(html, css ?? "");
                doc.UseComponents(registry);
                // The expanded DOM, from the document's own rebuild hook. These are the same element
                // instances the render tree points at, which is what makes the "did this draw
                // anything?" comparison exact rather than by-name. Registering the handler is not
                // enough — the first build already happened inside Load — so ask for one more.
                doc.OnRebuilt(d => dom = d);
                doc.Refresh();
                using (doc.RenderToImage(width, height)) { }
            }
            finally { StyleResolver.UnsupportedProperty = null; }
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
            doc.Dispose();
        }

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
            if (!reported.Add(tag)) continue;

            var near = Nearest(tag, known);
            findings.Add(new Finding(Severity.Error, "CF0020",
                $"<{tag}> is not a registered component, so it renders nothing.",
                near is null
                    ? "Register it with registry.Register(new YourComponent()), or check the spelling."
                    : $"Did you mean <{near}>?",
                LineOf(lines, "<" + tag)));
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

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in All(dom))
        {
            var tag = el.LocalName;
            if (NotDrawnOnPurpose.Contains(tag)) continue;
            // A component's own tag is replaced by what it expands into, so it is legitimately
            // absent from the render tree.
            if (registry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;

            // The browser-habit elements are named outright, because the render-tree test below
            // does NOT catch them: the engine happily builds a box for an <img>, lays it out, and
            // then has no primitive to draw anything into it. It is present, sized, and empty —
            // which is worse than absent, because every automatic check says it is fine.
            if (Replacements.TryGetValue(tag, out var better))
            {
                if (!reported.Add(tag)) continue;
                findings.Add(new Finding(Severity.Error, "CF0030",
                    $"<{tag}> is not something the engine draws — it lays out, and then stays empty.",
                    better, LineOf(lines, "<" + tag)));
                continue;
            }

            // The generic catch-all: in the markup, but the renderer never saw it.
            if (rendered.Contains(el)) continue;
            if (!reported.Add(tag)) continue;
            findings.Add(new Finding(Severity.Warning, "CF0031",
                $"<{tag}> produced no render output, so none of it will appear.",
                "If it is meant to be visible, use a div (or a cupri-* component) instead.",
                LineOf(lines, "<" + tag)));
        }
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

    /// <summary>First line containing <paramref name="needle"/>, 1-based, or 0. Approximate on
    /// purpose: it points at the right region without a position-tracking parse, and a finding with
    /// a nearly-right line beats one with none.</summary>
    private static int LineOf(string[] lines, string needle)
    {
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
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
