using CupriFace.Accessibility;

namespace CupriFace.Diagnostics;

/// <summary>What an <see cref="InteractionCrawl"/> clicks, and how many times.</summary>
public sealed class CrawlOptions
{
    /// <summary>The most controls to click. Each click is a fresh document, a click, a layout and
    /// every check — about a quarter of a second — so 80 is around twenty seconds.</summary>
    public int MaxClicks { get; init; } = 80;

    /// <summary>Only controls whose accessible name matches. Null clicks everything focusable: every
    /// button, link, switch, checkbox, radio, slider and anything an <c>OnClick</c> handler is
    /// registered for.</summary>
    public Func<string, bool>? Where { get; init; }
}

/// <summary>
/// Click every control in a document, one per fresh document, and run every check after each —
/// the states a click HANDLER produces, which no flag in the markup names.
///
/// <para><see cref="CupriDoctor.Sweep"/> reaches the states the markup declares: a bool behind
/// <c>open="{{Flag}}"</c>, the focus on a control. It cannot reach what app code does on a click —
/// a handler that sets three properties and a string, swaps a section, starts an edit. Only
/// clicking does, so this clicks, and it clicks on a document built by the app's own factory so the
/// handlers are the real ones.</para>
///
/// <para><b>One click per fresh document.</b> Clicking the same document twice would check the
/// second click in whatever state the first left — a dialog the first click opened swallowing the
/// second — and the order would decide what was found. A fresh document per click means every
/// click lands on the document as it loads, and each state found is one the user reaches in one
/// click from there. The crawl is one level deep, by design: two levels is the square, and the
/// first level is where the first-time failures are.</para>
///
/// <para><b>What it clicks.</b> Everything the accessibility tree calls focusable and on screen,
/// at the centre of its bounds, through the same <c>DispatchClick</c> a mouse arrives through. A
/// control that moves after a click is found again by its position in the tree, not by its
/// bounds. The stamp on a finding is the control's accessible name: <c>[after clicking "Open
/// drawer"]</c>.</para>
///
/// <code>
/// var app = new MyApp();
/// var report = InteractionCrawl.Click(app.CreateDocument, 412, 915);
/// Assert.False(report.HasErrors, report.ToString());
/// </code>
/// </summary>
public static class InteractionCrawl
{
    /// <summary>Click every focusable control, each on a fresh document from <paramref name="fresh"/>,
    /// and check after each. The report is the union: findings present as the document loads are
    /// plain, those a click produced carry <c>[after clicking "…"]</c>.</summary>
    /// <param name="fresh">Builds a new document with the app's components, model and handlers —
    /// <c>app.CreateDocument</c>. Called once per click, plus once to find the controls. Each is
    /// disposed here.</param>
    /// <param name="width">The viewport to lay out and click at.</param>
    /// <param name="height">The viewport height.</param>
    public static DoctorReport Click(Func<CupriDocument> fresh, int width, int height, CrawlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        options ??= new CrawlOptions();

        var merged = new List<(DoctorReport Report, string State)>();
        List<(string Path, string Name)> targets;
        using (var doc = Build(fresh, width, height))
        {
            merged.Add((CupriDoctor.Check(doc), ""));
            targets = Targets(doc, width, height, options);
        }

        foreach (var (path, name) in targets.Take(options.MaxClicks))
        {
            using var doc = Build(fresh, width, height);
            var node = Find(doc.BuildAccessibilityTree(width, height), path);
            if (node is null) continue;                        // the document did not come back the same
            var (x, y, w, h) = node.Bounds;
            doc.DispatchClick(x + w / 2f, y + h / 2f);
            using (doc.RenderToImage(width, height)) { }
            merged.Add((CupriDoctor.Check(doc), $"after clicking \"{name}\""));
        }

        return Fold(merged);
    }

    private static CupriDocument Build(Func<CupriDocument> fresh, int width, int height)
    {
        var doc = fresh();
        doc.Refresh();
        using (doc.RenderToImage(width, height)) { }
        return doc;
    }

    /// <summary>Every focusable, on-screen, enabled control, in tree order — by its path in the
    /// accessibility tree (stable across fresh documents) and the name to label it with.</summary>
    private static List<(string Path, string Name)> Targets(CupriDocument doc, int width, int height, CrawlOptions options)
    {
        var list = new List<(string, string)>();
        Walk(doc.BuildAccessibilityTree(width, height));
        return list;

        void Walk(AccessibilityNode n)
        {
            if (n.Focusable && !n.Disabled && !n.Offscreen && n.Bounds.W > 0.5f && n.Bounds.H > 0.5f)
            {
                var name = n.Name is { Length: > 0 } s ? s : n.Role;
                if (options.Where is null || options.Where(name)) list.Add((n.Path, name));
            }
            foreach (var c in n.Children) Walk(c);
        }
    }

    private static AccessibilityNode? Find(AccessibilityNode n, string path)
    {
        if (n.Path == path) return n;
        foreach (var c in n.Children) if (Find(c, path) is { } f) return f;
        return null;
    }

    /// <summary>Union by identity; a finding seen as the document loads is plain, otherwise it
    /// carries the clicks that produced it (the first three, and a count of the rest).</summary>
    private static DoctorReport Fold(List<(DoctorReport Report, string State)> reports)
    {
        var order = new List<string>();
        var seen = new Dictionary<string, (Finding First, List<string> States, bool Baseline)>(StringComparer.Ordinal);
        foreach (var (report, state) in reports)
            foreach (var f in report.Findings)
            {
                var key = CupriDoctor.Identity(f);
                if (!seen.TryGetValue(key, out var e)) { e = (f, new List<string>(), false); order.Add(key); }
                if (state.Length == 0) e.Baseline = true;
                else if (!e.States.Contains(state)) e.States.Add(state);
                seen[key] = e;
            }

        var findings = new List<Finding>(order.Count);
        foreach (var key in order)
        {
            var (first, states, baseline) = seen[key];
            findings.Add(baseline || states.Count == 0
                ? first
                : first with
                {
                    State = states.Count <= 3
                        ? string.Join("; ", states)
                        : string.Join("; ", states.Take(3)) + $"; and {states.Count - 3} more",
                });
        }
        findings.Sort((a, b) =>
        {
            var bySeverity = b.Severity.CompareTo(a.Severity);
            return bySeverity != 0 ? bySeverity : a.Line.CompareTo(b.Line);
        });
        return new DoctorReport(findings) { Checks = reports.Count };
    }
}
