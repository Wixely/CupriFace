using CupriFace.Binding;
using CupriFace.Components;

namespace CupriFace.Diagnostics;

/// <summary>What a <see cref="CupriDoctor.Sweep"/> tries, beyond the document as it loads.</summary>
public sealed class SweepOptions
{
    /// <summary>The model paths to flip — the bools behind <c>open="{{Flag}}"</c>, <c>display:{{Flag}}</c>
    /// and the like. Null discovers them: every <c>{{path}}</c> in the markup that resolves to a
    /// writable bool on the model.</summary>
    public IReadOnlyList<string>? Flags { get; init; }

    /// <summary>Also try every PAIR of flags flipped together, on top of each alone and all at once.
    /// Two overlays open at the same time is where "things get hairy", and it is the cheapest
    /// combination that still finds it: 12 flags is 66 pairs, not 4096 states.</summary>
    public bool Pairs { get; init; } = true;

    /// <summary>Also Tab through every focusable control and check with the focus on each — the
    /// state <c>:focus</c> styling and a selected row's size (CF0073) only exist in.</summary>
    public bool Focus { get; init; } = true;

    /// <summary>The most states to check across all viewports; pairs are dropped first when it
    /// would be exceeded. At roughly a fifth of a second each, 400 is about a minute.</summary>
    public int MaxChecks { get; init; } = 400;
}

public static partial class CupriDoctor
{
    /// <summary>
    /// Check a document in the states it can get INTO, not only the one it starts in.
    ///
    /// <para>The ordinary check sees a fresh document: every menu closed, every drawer shut, nothing
    /// focused. That is the state a layout is least likely to be wrong in, and the one it is most
    /// often checked in. The states that go wrong — the dialog open on a phone, two overlays at
    /// once, the focus ring on the one button that was styled with <c>border</c> — are reached by
    /// flipping a bool on the model or pressing Tab, which is what this does, one state at a
    /// time, running every check in each.</para>
    ///
    /// <para><b>Which states.</b> The flags are found in the markup: every <c>{{path}}</c> that
    /// resolves to a writable bool on the model (<see cref="SweepOptions.Flags"/> to say
    /// otherwise). Each is flipped alone, then all together, then every pair; then the focus is
    /// Tabbed through every control. Every state is checked at every viewport given.</para>
    ///
    /// <para><b>How findings read.</b> A finding present in the document as it loads is reported
    /// once, plain. One that appears only in some states carries them: <c>[with DialogOpen,
    /// DrawerOpen]</c>, <c>[focus on "Save"]</c>. One that appears only at some sizes carries those
    /// too, as the viewport check does. The report is the union, so the ordinary check's findings
    /// are a subset of this one's.</para>
    ///
    /// <para><b>The model is flipped in place and put back.</b> Each flag is restored after its
    /// state, including when a check throws. Pass the app's real model: an anonymous object has
    /// read-only properties, which discover no flags and sweep only focus.</para>
    ///
    /// <para><b>What it cannot reach.</b> A state a click HANDLER produces — app code that sets
    /// three properties and a string — is not a flag in the markup. <see cref="InteractionCrawl"/>
    /// clicks the controls for that.</para>
    /// </summary>
    public static DoctorReport Sweep(string html, string? css,
                                     ComponentRegistry? components = null,
                                     object? model = null,
                                     Action<CupriDocument>? configure = null,
                                     IReadOnlyList<(int Width, int Height)>? viewports = null,
                                     SweepOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new SweepOptions();
        viewports ??= [(1024, 768)];
        if (viewports.Count == 0) throw new ArgumentException("At least one viewport is needed.", nameof(viewports));

        var flags = model is null ? [] : options.Flags ?? DiscoverFlags(html, model);
        var states = FlagStates(flags, model, options.Pairs);

        // Budget: the states are ordered as it loads, each flag alone, all together, then the
        // pairs — so cutting the list at the budget drops pairs first and singles last, because
        // each alone and all together are the states that find the most per check.
        var perViewport = Math.Max(1, options.MaxChecks / viewports.Count);
        if (states.Count > perViewport) states = states.Take(perViewport).ToList();

        var merged = new Merger(viewports.Count);
        var checks = 0;
        foreach (var (w, h) in viewports)
        {
            var at = $"{w}x{h}";
            foreach (var state in states)
            {
                state.Apply();
                try
                {
                    var report = CheckAt(html, css, components, w, h, model, configure);
                    checks++;
                    merged.Add(report, at, state.Label);
                }
                finally { state.Restore(); }
            }
            if (options.Focus && checks < options.MaxChecks)
                checks += FocusStates(html, css, components, model, configure, w, h, options.MaxChecks - checks,
                                      (report, label) => merged.Add(report, at, label));
        }
        return merged.ToReport(checks);
    }

    /// <summary>The flags a <see cref="Sweep"/> would flip: every <c>{{path}}</c> in the markup that
    /// is a writable bool on the model, in the order written. Paths that resolve to nothing (an
    /// item's property inside a repeat) are skipped rather than guessed at. Public so a test can
    /// assert the list before trusting a sweep that reports nothing.</summary>
    public static IReadOnlyList<string> DiscoverFlags(string html, object model)
    {
        var flags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in Mustache().Matches(html))
        {
            var path = m.Groups[1].Value.Trim();
            if (path.Length == 0 || path is "this" or "." || !seen.Add(path)) continue;
            if (BindingEngine.Resolve(model, path) is not bool current) continue;
            // Writable: put the same value back and see whether the set is accepted.
            if (!BindingEngine.TrySet(model, path, current)) continue;
            flags.Add(path);
        }
        return flags;
    }

    private sealed class State(string label, bool isPair, Action apply, Action restore)
    {
        public string Label => label;
        public bool IsPair => isPair;
        public void Apply() => apply();
        public void Restore() => restore();
    }

    private static List<State> FlagStates(IReadOnlyList<string> flags, object? model, bool pairs)
    {
        var states = new List<State> { new("", false, () => { }, () => { }) };   // as it loads
        if (model is null || flags.Count == 0) return states;

        State Flip(IReadOnlyList<string> paths, bool isPair, string? label = null)
        {
            var before = new bool[paths.Count];
            return new State(
                label ?? "with " + string.Join(", ", paths.Select(p => Describe(p, model))),
                isPair,
                () =>
                {
                    for (var i = 0; i < paths.Count; i++)
                    {
                        before[i] = BindingEngine.Resolve(model, paths[i]) is true;
                        BindingEngine.TrySet(model, paths[i], !before[i]);
                    }
                },
                () =>
                {
                    for (var i = 0; i < paths.Count; i++) BindingEngine.TrySet(model, paths[i], before[i]);
                });
        }

        foreach (var f in flags) states.Add(Flip([f], false));
        if (flags.Count > 1) states.Add(Flip(flags, false, $"with all {flags.Count} flags flipped"));
        if (pairs)
            for (var i = 0; i < flags.Count; i++)
                for (var j = i + 1; j < flags.Count; j++)
                    states.Add(Flip([flags[i], flags[j]], true));
        return states;

        // "DialogOpen" when the sweep turns it on, "not Notifications" when it turns one off.
        static string Describe(string path, object model) =>
            BindingEngine.Resolve(model, path) is true ? "not " + path : path;
    }

    /// <summary>Tab through the focusables of a live document at one size, checking with the focus
    /// on each. Stops when the focus comes back round, leaves the document, or the budget is
    /// spent. Returns the number of checks made.</summary>
    private static int FocusStates(string html, string? css, ComponentRegistry? components, object? model,
                                   Action<CupriDocument>? configure, int width, int height, int budget,
                                   Action<DoctorReport, string> add)
    {
        if (budget <= 0) return 0;
        CupriDocument doc;
        try
        {
            doc = CupriDocument.Load(html, css ?? "");
            doc.UseComponents(components ?? ComponentRegistry.Default());
            configure?.Invoke(doc);
            if (model is not null) doc.Bind(model);
            doc.Refresh();
            using (doc.RenderToImage(width, height)) { }
        }
        catch
        {
            return 0;       // the plain check has already reported why it cannot be built
        }

        var checks = 0;
        using (doc)
        {
            // Visited by POSITION in the tree, not by name: two buttons both called "Edit" are two
            // Tab stops, and a stop with no name at all is still a stop. The name is for the label.
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var step = 0; step < budget; step++)
            {
                doc.DispatchKey(null, Interaction.EditKey.Tab);
                using (doc.RenderToImage(width, height)) { }
                var focused = FocusedNode(doc.Root);
                if (focused is null || !visited.Add(TreePath(focused))) break;     // nothing focused, or back round
                var name = FocusedName(doc, width, height) ?? "(unnamed)";
                add(Check(doc), $"focus on \"{name}\"");
                checks++;
            }
        }
        return checks;
    }

    private static Dom.RenderNode? FocusedNode(Dom.RenderNode n)
    {
        if (n.Element?.HasAttribute("data-focus") == true) return n;
        foreach (var c in n.Children) if (FocusedNode(c) is { } f) return f;
        return null;
    }

    private static string TreePath(Dom.RenderNode n)
    {
        var sb = new System.Text.StringBuilder();
        for (var cur = n; cur.Parent is { } p; cur = p) sb.Insert(0, "/" + p.Children.IndexOf(cur));
        return sb.ToString();
    }

    private static string? FocusedName(CupriDocument doc, int width, int height)
    {
        return Walk(doc.BuildAccessibilityTree(width, height));
        static string? Walk(Accessibility.AccessibilityNode n)
        {
            if (n.Focused) return n.Name is { Length: > 0 } s ? s : "(unnamed)";
            foreach (var c in n.Children) if (Walk(c) is { } f) return f;
            return null;
        }
    }

    /// <summary>Findings from many checks, folded by identity, with the viewports and states each
    /// was seen in. A finding seen in the as-it-loads state carries no state stamp; one seen at
    /// every viewport carries no viewport stamp.</summary>
    private sealed class Merger(int viewportCount)
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, (Finding First, List<string> At, List<string> States, bool Baseline)> _seen
            = new(StringComparer.Ordinal);

        public void Add(DoctorReport report, string viewport, string state)
        {
            foreach (var f in report.Findings)
            {
                var key = Identity(f);
                if (!_seen.TryGetValue(key, out var e))
                {
                    e = (f, new List<string>(), new List<string>(), false);
                    _order.Add(key);
                }
                if (!e.At.Contains(viewport)) e.At.Add(viewport);
                if (state.Length == 0) e.Baseline = true;
                else if (!e.States.Contains(state)) e.States.Add(state);
                _seen[key] = e;
            }
        }

        public DoctorReport ToReport(int checks)
        {
            var findings = new List<Finding>(_order.Count);
            foreach (var key in _order)
            {
                var (first, at, states, baseline) = _seen[key];
                var f = first;
                if (at.Count < viewportCount) f = f with { Viewports = string.Join(", ", at) };
                if (!baseline && states.Count > 0)
                    f = f with
                    {
                        State = states.Count <= 3
                            ? string.Join("; ", states)
                            : string.Join("; ", states.Take(3)) + $"; and {states.Count - 3} more",
                    };
                findings.Add(f);
            }
            findings.Sort((a, b) =>
            {
                var bySeverity = b.Severity.CompareTo(a.Severity);
                return bySeverity != 0 ? bySeverity : a.Line.CompareTo(b.Line);
            });
            return new DoctorReport(findings) { Checks = checks };
        }
    }
}
