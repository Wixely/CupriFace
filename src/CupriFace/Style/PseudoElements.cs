using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Style;

/// <summary>
/// <c>::before</c> and <c>::after</c> (#261).
///
/// <para>The engine builds its render tree from the DOM, one node per element, so a box that no
/// element produces is a box it cannot have. Rather than teach every layer about a second kind of
/// child, a pseudo-element is given a REAL element: on each rebuild, every element matched by a
/// rule that names <c>::before</c> or <c>::after</c> gets a <c>&lt;cupri-pseudo&gt;</c> child at
/// that end, and the rule's selector is rewritten to match it (<see cref="CssParser"/>). From
/// there the cascade, layout, paint and hit-testing treat it as the element it is. What makes it
/// a pseudo-element is the one rule CSS has for them: it is a box only when <c>content</c> says
/// so, which <see cref="StyleResolver"/> applies when it builds the tree.</para>
///
/// <para>Injected rather than generated at style time because the hover restyle re-resolves the
/// EXISTING DOM: a <c>.btn:hover::after</c> that only gained a box on hover would have nothing to
/// gain it on. So the element exists whenever its owner matches the rule with the interaction
/// state ignored, and <c>content</c> decides, frame by frame, whether it is a box.</para>
/// </summary>
public static class PseudoElements
{
    /// <summary>The tag of the element standing in for a pseudo-element. Registered nowhere: the
    /// doctor knows it, and everything else treats it as the inline element it is.</summary>
    public const string Tag = "cupri-pseudo";

    /// <summary>The attribute naming which end: <c>before</c> or <c>after</c>.</summary>
    public const string SideAttribute = "data-pseudo";

    /// <summary>Give every element a rule names a <c>::before</c>/<c>::after</c> child. Idempotent
    /// per element and side, so a rebuild on an already-injected DOM adds nothing.</summary>
    public static void Inject(IDocument dom, List<CssRule> rules)
    {
        List<CssRule>? pseudo = null;
        foreach (var r in rules)
            if (r.Pseudo is not null && r.PseudoOwner is not null) (pseudo ??= []).Add(r);
        if (pseudo is null) return;

        // Snapshot: the injection appends children, and the live `All` collection would walk into
        // them. The pseudo elements themselves never own one (no `::before::before`).
        foreach (var el in dom.All.ToArray())
        {
            if (el.LocalName == Tag) continue;
            if (el.NamespaceUri is { } ns && ns.EndsWith("svg", StringComparison.Ordinal)) continue;
            if (el.LocalName is "html" or "head" or "body" or "script" or "style") continue;
            foreach (var r in pseudo)
            {
                if (!r.PseudoOwner!.Match(el, null)) continue;
                if (HasSide(el, r.Pseudo!)) continue;
                var child = dom.CreateElement(Tag);
                child.SetAttribute(SideAttribute, r.Pseudo!);
                if (r.Pseudo == "before") el.Prepend(child); else el.Append(child);
            }
        }
    }

    private static bool HasSide(IElement el, string side)
    {
        foreach (var c in el.Children)
            if (c.LocalName == Tag && c.GetAttribute(SideAttribute) == side) return true;
        return false;
    }

    /// <summary>The text a pseudo-element's box holds, or null when it generates no box at all
    /// (<c>content: none</c>, or no <c>content</c> declaration reached it — which is the CSS
    /// initial value, <c>normal</c>, and means the same thing on a pseudo-element).</summary>
    public static string? ContentOf(ComputedStyle s, IElement owner)
    {
        if (s.ContentAttr is { } attr) return owner.GetAttribute(attr) ?? "";
        return s.Content;
    }

    /// <summary>
    /// Parse a <c>content</c> value: one or more quoted strings (concatenated, escapes decoded),
    /// <c>none</c>/<c>normal</c> (no box), <c>attr(name)</c> (the owner's attribute, read when the
    /// tree is built), or the quote keywords. Anything else — <c>counter()</c>, <c>url()</c> — is
    /// reported as unsupported and yields an EMPTY box rather than none: a decorative box whose
    /// text the engine cannot produce is still closer to the design than no box at all.
    /// </summary>
    internal static void Parse(ComputedStyle s, string v, Action<string, string>? unsupported)
    {
        v = v.Trim();
        var lower = v.ToLowerInvariant();
        s.Content = null; s.ContentAttr = null;
        if (lower is "none" or "normal") return;
        if (lower.StartsWith("attr(", StringComparison.Ordinal))
        {
            var close = v.IndexOf(')');
            var name = (close > 5 ? v[5..close] : v[5..]).Trim().Trim('"', '\'');
            s.ContentAttr = name;
            return;
        }

        var sb = new StringBuilder();
        var any = false;
        var i = 0;
        while (i < v.Length)
        {
            var c = v[i];
            if (c is '"' or '\'')
            {
                any = true;
                i = ReadQuoted(v, i, sb);
                continue;
            }
            if (char.IsLetter(c))
            {
                var start = i;
                while (i < v.Length && (char.IsLetterOrDigit(v[i]) || v[i] is '-' or '_')) i++;
                var word = v[start..i].ToLowerInvariant();
                switch (word)
                {
                    case "open-quote": sb.Append('“'); any = true; break;
                    case "close-quote": sb.Append('”'); any = true; break;
                    case "no-open-quote" or "no-close-quote": any = true; break;
                    default:
                        // counter(), counters(), url(), image-set(), element()… — skip the call.
                        if (i < v.Length && v[i] == '(') { var close = v.IndexOf(')', i); i = close < 0 ? v.Length : close + 1; }
                        unsupported?.Invoke("content", v);
                        any = true;
                        break;
                }
                continue;
            }
            i++;
        }
        s.Content = any ? sb.ToString() : "";
    }

    // One quoted string, with CSS escapes: `\"`, `\\`, and `\XXXX` hex (up to six digits, an
    // optional single space swallowed after it — `"\2014 "` is an em dash, not an em dash and a space).
    private static int ReadQuoted(string v, int open, StringBuilder sb)
    {
        var quote = v[open];
        var i = open + 1;
        while (i < v.Length && v[i] != quote)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                var n = v[i + 1];
                if (Uri.IsHexDigit(n))
                {
                    var start = i + 1;
                    var end = start;
                    while (end < v.Length && end - start < 6 && Uri.IsHexDigit(v[end])) end++;
                    if (int.TryParse(v.AsSpan(start, end - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
                        && cp > 0 && cp <= 0x10FFFF)
                        sb.Append(char.ConvertFromUtf32(cp));
                    i = end;
                    if (i < v.Length && v[i] == ' ') i++;
                    continue;
                }
                if (n == '\n') { i += 2; continue; }   // a line continuation
                sb.Append(n);
                i += 2;
                continue;
            }
            sb.Append(v[i]);
            i++;
        }
        return i < v.Length ? i + 1 : i;   // past the closing quote
    }

    /// <summary>Split a trailing <c>::before</c>/<c>::after</c> (or the legacy single-colon
    /// spelling) off a selector. Returns the selector without it and the side, or null when the
    /// selector names no pseudo-element at its end.</summary>
    internal static (string Base, string Side)? Split(string selector)
    {
        var s = selector.TrimEnd();
        foreach (var (suffix, side) in Suffixes)
        {
            if (!s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var b = s[..^suffix.Length].TrimEnd();
            // `::before` alone, or `> ::before`: the owner is anything.
            if (b.Length == 0 || b[^1] is '>' or '+' or '~') b += " *";
            return (b, side);
        }
        return null;
    }

    private static readonly (string Suffix, string Side)[] Suffixes =
    [
        ("::before", "before"), ("::after", "after"), (":before", "before"), (":after", "after"),
    ];

    /// <summary>The selector that matches the generated element itself: the owner's selector, then
    /// the child at that side.</summary>
    internal static string SelectorFor(string ownerSelector, string side) =>
        $"{ownerSelector} > {Tag}[{SideAttribute}=\"{side}\"]";

    /// <summary>The owner's selector with the interaction state taken out, for deciding which
    /// elements get a generated child: the child must exist before the hover that reveals it.</summary>
    internal static string OwnerQuery(string ownerSelector)
    {
        var q = ownerSelector.Replace("[data-hover]", "").Replace("[data-active]", "")
                             .Replace("[data-focus]", "").Replace("[data-drop-over]", "");
        // A compound that was only its state (`[data-hover]::after`) is now empty: anything.
        q = string.Join(' ', q.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (q.Length == 0 || q[^1] is '>' or '+' or '~') q += " *";
        return q;
    }
}
