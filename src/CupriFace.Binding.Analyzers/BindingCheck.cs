using Microsoft.CodeAnalysis;

namespace CupriFace.Binding.Analyzers;

/// <summary>One finding: where in the markup, and the words for it.</summary>
internal readonly struct BindingFinding(int start, int length, string subject, string where, string advice)
{
    public int Start { get; } = start;
    public int Length { get; } = length;
    /// <summary>"{{Cuont}}" or "data-repeat=\"Itmes\"".</summary>
    public string Subject { get; } = subject;
    /// <summary>"CounterModel", or "TaskItem (an item of Tasks) or on AppModel".</summary>
    public string Where { get; } = where;
    public string Advice { get; } = advice;
}

/// <summary>
/// CupriDoctor's CF0060, asked of symbols instead of a live object. The semantics are the doctor's
/// on purpose — a build warning and a doctor finding that disagree about the same line would teach
/// people to ignore one of them — and both are the engine's: existence, not value; a repeat's items
/// found to a fixed point; a path that fails against the model retried against every item type in
/// the document before it is reported.
///
/// <para>That last rule is the doctor's trade, kept here: rather than resolve each binding against
/// exactly the scopes around it, it is resolved against every scope the document can produce. A
/// name that only exists on some OTHER list's items is therefore missed. In return the check never
/// depends on reading the nesting right, and markup the engine renders correctly is never reported —
/// the right way round for a warning that people can only keep on if it is never wrong.</para>
///
/// <para>Where the build CAN see more than the doctor, it says so: a <c>data-repeat</c> that names
/// nothing renders no rows at all, which is the same silent failure one level up, so it is reported
/// under the same code — and the bindings inside it are not, since their scope is unknowable and
/// reporting each of them would bury the one line that is actually wrong.</para>
/// </summary>
internal static class BindingCheck
{
    // Beyond this many distinct item types a document is not one anybody wrote by hand; stop
    // widening rather than spin. Each fixed-point pass either adds a type or ends.
    private const int MaxScopes = 64;

    public static List<BindingFinding> Run(MarkupScan scan, ITypeSymbol model, ModelShapes shapes)
    {
        var findings = new List<BindingFinding>();
        if (shapes.IsOpaque(model)) return findings;

        var modelName = model.Name;
        var scopes = new List<ITypeSymbol> { model };
        // Per repeat PATH: the item types it produces, and whether any scope made it unknowable.
        var itemTypes = new Dictionary<string, List<ITypeSymbol>>(StringComparer.Ordinal);
        var opaque = new HashSet<string>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var paths = scan.Repeats.Select(r => r.Path).Distinct(StringComparer.Ordinal).ToList();

        bool added;
        do
        {
            added = false;
            foreach (var path in paths)
            {
                var segs = Segments(path);
                if (segs.Count == 0) { opaque.Add(path); named.Add(path); continue; }
                foreach (var scope in scopes.ToList())
                {
                    var r = shapes.Walk(scope, segs);
                    if (r.Outcome == PathOutcome.Missing) continue;
                    named.Add(path);
                    if (r.Outcome == PathOutcome.Unknown) { opaque.Add(path); continue; }
                    foreach (var end in r.Types)
                    {
                        if (shapes.ItemType(end) is not { } item || shapes.IsOpaque(item)) { opaque.Add(path); continue; }
                        if (!itemTypes.TryGetValue(path, out var list)) itemTypes[path] = list = new List<ITypeSymbol>();
                        if (!list.Contains(item, SymbolEqualityComparer.Default)) list.Add(item);
                        if (!scopes.Contains(item, SymbolEqualityComparer.Default) && scopes.Count < MaxScopes)
                        {
                            scopes.Add(item);
                            added = true;
                        }
                    }
                }
            }
        } while (added);

        // A repeat whose items cannot be known — it names nothing, or a type that says nothing.
        bool Unknowable(RepeatSite r) => opaque.Contains(r.Path) || !named.Contains(r.Path);

        // Nesting is only trusted to say "this is inside a list we cannot see into". If the tags did
        // not balance, that cannot be trusted either, so any such list silences the whole document.
        if (!scan.Balanced && scan.Repeats.Any(Unknowable)) return findings;

        foreach (var r in scan.Repeats)
        {
            if (named.Contains(r.Path) || r.Enclosing.Any(Unknowable)) continue;
            findings.Add(new BindingFinding(r.Start, r.Length, $"data-repeat=\"{r.Path}\"",
                Where(r.Enclosing, itemTypes, modelName),
                Suggest(Segments(r.Path), scopes, shapes, "so the list renders no rows")));
        }

        foreach (var b in scan.Bindings)
        {
            var segs = Segments(b.Path);
            if (segs.Count == 0) continue;                      // {{.}} / {{this}}: the context itself
            if (b.Enclosing.Any(Unknowable)) continue;
            if (scopes.Any(s => shapes.Walk(s, segs).Outcome != PathOutcome.Missing)) continue;

            findings.Add(new BindingFinding(b.Start, b.Length, "{{" + b.Path + "}}",
                Where(b.Enclosing, itemTypes, modelName),
                Suggest(segs, scopes, shapes, "so it renders as empty text")));
        }
        return findings;
    }

    /// <summary>The engine's own split: dots, empty segments dropped, each trimmed, and
    /// <c>this</c> meaning the context itself.</summary>
    private static List<string> Segments(string path) =>
        path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0 && s != "this").ToList();

    private static string Where(IReadOnlyList<RepeatSite> enclosing, Dictionary<string, List<ITypeSymbol>> itemTypes,
                                string modelName)
    {
        if (enclosing.Count == 0) return modelName;
        var inner = enclosing[enclosing.Count - 1];
        return itemTypes.TryGetValue(inner.Path, out var items) && items.Count > 0
            ? $"{items[0].Name} (an item of {inner.Path}) or on {modelName}"
            : modelName;
    }

    /// <summary>"Did you mean…" from the segment that failed, looked up on the types the path had
    /// reached by then — so <c>{{Tasks.Cuont}}</c> suggests <c>{{Tasks.Count}}</c>, not a root
    /// property that happens to be spelt similarly.</summary>
    private static string Suggest(List<string> segs, List<ITypeSymbol> scopes, ModelShapes shapes, string consequence)
    {
        var deepest = -1;
        var on = new List<ITypeSymbol>();
        foreach (var s in scopes)
        {
            var r = shapes.Walk(s, segs);
            if (r.Outcome != PathOutcome.Missing) continue;
            if (r.FailedAt > deepest) { deepest = r.FailedAt; on.Clear(); }
            if (r.FailedAt == deepest) on.AddRange(r.Types);
        }
        if (deepest < 0) return consequence + ".";

        var leaf = segs[deepest];
        var near = Nearest(leaf, shapes.PropertyNames(on).ToList());
        if (near is null)
            return $"{consequence}. Nothing in scope is close to '{leaf}' — add the property, or check the spelling.";
        var fixedPath = string.Join(".", segs.Take(deepest).Concat(new[] { near }));
        return $"{consequence}. Did you mean {{{{{fixedPath}}}}}?";
    }

    /// <summary>The doctor's rule exactly: the closest name within max(2, length/4) edits, case
    /// ignored, or nothing when nothing is close enough to help.</summary>
    internal static string? Nearest(string name, IReadOnlyCollection<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            var d = Distance(name, candidate);
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }
        return bestDistance <= Math.Max(2, name.Length / 4) ? best : null;
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
