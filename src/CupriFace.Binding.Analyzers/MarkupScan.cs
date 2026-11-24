using System.Text.RegularExpressions;

namespace CupriFace.Binding.Analyzers;

/// <summary>
/// What a piece of markup asks of its model: every <c>{{path}}</c> and every <c>data-repeat</c>,
/// with the character offsets a diagnostic needs, and which repeats each binding sits inside.
///
/// <para>Not an HTML parser, and deliberately so. It only has to know three things — where the
/// comments are (the engine binds text and attributes, never a comment), where each repeat's
/// element begins and ends, and whether the tags balanced. Getting nesting wrong can only ever
/// make the check MORE lenient, because the scopes a path is resolved against are the whole
/// document's (see <see cref="BindingCheck"/>); the one place nesting is trusted is "this binding
/// is inside a list whose items cannot be known", and when the tags do not balance that question
/// is answered "yes" for every binding rather than guessed.</para>
/// </summary>
internal sealed class MarkupScan
{
    // The engine's own pattern (BindingEngine.Interp), so a binding is exactly what the runtime
    // would substitute — no more, no less.
    private static readonly Regex Mustache = new(@"\{\{\s*([^}]+?)\s*\}\}", RegexOptions.CultureInvariant);

    // Elements with no closing tag. Pushing one would hold its region open to the end of the file.
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source",
        "track", "wbr",
    };

    // Elements whose content is text, not tags: a '<' inside a <style> is not an element.
    private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    public List<BindingSite> Bindings { get; } = new();
    public List<RepeatSite> Repeats { get; } = new();

    /// <summary>False when a close tag matched nothing open, closed something other than the
    /// innermost element, or an element was still open at the end.</summary>
    public bool Balanced { get; private set; } = true;

    public static MarkupScan Scan(string text)
    {
        var scan = new MarkupScan();
        var comments = new List<(int Start, int End)>();
        var open = new List<(string Name, int Start, RepeatSite? Repeat)>();

        var i = 0;
        while (i < text.Length)
        {
            var lt = text.IndexOf('<', i);
            if (lt < 0) break;

            if (lt + 4 <= text.Length && string.CompareOrdinal(text, lt, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 3;
                comments.Add((lt, end));
                i = end;
                continue;
            }

            if (lt + 1 < text.Length && (text[lt + 1] == '!' || text[lt + 1] == '?'))
            {
                var end = text.IndexOf('>', lt + 1);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (lt + 1 < text.Length && text[lt + 1] == '/')
            {
                var nameEnd = NameEnd(text, lt + 2);
                var name = text.Substring(lt + 2, nameEnd - (lt + 2));
                var gt = text.IndexOf('>', nameEnd);
                var closeEnd = gt < 0 ? text.Length : gt + 1;
                var at = open.FindLastIndex(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) scan.Balanced = false;
                else
                {
                    if (at != open.Count - 1) scan.Balanced = false;
                    for (var k = open.Count - 1; k >= at; k--)
                        open[k].Repeat?.Close(closeEnd);
                    open.RemoveRange(at, open.Count - at);
                }
                i = closeEnd;
                continue;
            }

            if (lt + 1 < text.Length && char.IsLetter(text[lt + 1]))
            {
                var nameEnd = NameEnd(text, lt + 1);
                var name = text.Substring(lt + 1, nameEnd - (lt + 1));
                var (tagEnd, selfClosing, repeat) = Attributes(text, nameEnd, lt);
                if (repeat is not null) scan.Repeats.Add(repeat);

                if (Void.Contains(name) || selfClosing) repeat?.Close(tagEnd);
                else if (RawText.Contains(name))
                {
                    // The content runs to the matching close tag; nothing inside is a tag.
                    var close = text.IndexOf("</" + name, tagEnd, StringComparison.OrdinalIgnoreCase);
                    var gt = close < 0 ? -1 : text.IndexOf('>', close);
                    var end = gt < 0 ? text.Length : gt + 1;
                    if (close < 0) scan.Balanced = false;
                    repeat?.Close(end);
                    i = end;
                    continue;
                }
                else open.Add((name, lt, repeat));

                i = tagEnd;
                continue;
            }

            i = lt + 1;   // a stray '<' in text
        }

        if (open.Count > 0) scan.Balanced = false;
        foreach (var o in open) o.Repeat?.Close(text.Length);

        foreach (Match m in Mustache.Matches(text))
        {
            if (comments.Any(c => m.Index >= c.Start && m.Index < c.End)) continue;
            var g = m.Groups[1];
            var enclosing = scan.Repeats.Where(r => m.Index >= r.RegionStart && m.Index < r.RegionEnd)
                                        .OrderBy(r => r.RegionStart).ToList();
            scan.Bindings.Add(new BindingSite(g.Value, g.Index, g.Length, enclosing));
        }

        // A repeat nested inside another needs to know it, for the same reason a binding does.
        foreach (var r in scan.Repeats)
            r.Enclosing = scan.Repeats.Where(o => o != r && r.RegionStart > o.RegionStart && r.RegionStart < o.RegionEnd)
                                      .OrderBy(o => o.RegionStart).ToList();
        return scan;
    }

    private static int NameEnd(string text, int from)
    {
        var j = from;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '-' or '_' or ':' or '.')) j++;
        return j;
    }

    /// <summary>Walk a start tag's attributes to its '&gt;', honouring quotes (a '&gt;' inside a
    /// quoted value does not end the tag), and pick out <c>data-repeat</c>.</summary>
    private static (int End, bool SelfClosing, RepeatSite? Repeat) Attributes(string text, int j, int tagStart)
    {
        RepeatSite? repeat = null;
        while (j < text.Length)
        {
            var c = text[j];
            if (c == '>') return (j + 1, j > 0 && text[j - 1] == '/', repeat);
            if (char.IsWhiteSpace(c) || c == '/') { j++; continue; }

            var nameStart = j;
            while (j < text.Length && !char.IsWhiteSpace(text[j]) && text[j] is not '=' and not '>' and not '/') j++;
            var attr = text.Substring(nameStart, j - nameStart);
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j >= text.Length || text[j] != '=') continue;
            j++;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j >= text.Length) break;

            int valueStart, valueEnd;
            if (text[j] is '"' or '\'')
            {
                var q = text[j];
                valueStart = j + 1;
                var close = text.IndexOf(q, valueStart);
                valueEnd = close < 0 ? text.Length : close;
                j = close < 0 ? text.Length : close + 1;
            }
            else
            {
                valueStart = j;
                while (j < text.Length && !char.IsWhiteSpace(text[j]) && text[j] != '>') j++;
                valueEnd = j;
            }

            if (attr.Equals("data-repeat", StringComparison.OrdinalIgnoreCase))
            {
                // Trimmed the way the engine trims each segment, but located at the text itself.
                var s = valueStart;
                var e = valueEnd;
                while (s < e && char.IsWhiteSpace(text[s])) s++;
                while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
                if (e > s) repeat = new RepeatSite(text.Substring(s, e - s), s, e - s, tagStart);
            }
        }
        return (text.Length, false, repeat);
    }
}

/// <summary>One <c>{{path}}</c>: the path as written, where it is, and the repeats around it,
/// outermost first.</summary>
internal sealed class BindingSite(string path, int start, int length, IReadOnlyList<RepeatSite> enclosing)
{
    public string Path { get; } = path;
    public int Start { get; } = start;
    public int Length { get; } = length;
    public IReadOnlyList<RepeatSite> Enclosing { get; } = enclosing;
}

/// <summary>One <c>data-repeat="path"</c>. Its region starts at the repeat element's own '&lt;',
/// because the engine binds that element's other attributes against the item too.</summary>
internal sealed class RepeatSite(string path, int start, int length, int regionStart)
{
    public string Path { get; } = path;
    public int Start { get; } = start;
    public int Length { get; } = length;
    public int RegionStart { get; } = regionStart;
    public int RegionEnd { get; private set; } = int.MaxValue;
    public IReadOnlyList<RepeatSite> Enclosing { get; set; } = Array.Empty<RepeatSite>();

    public void Close(int end) => RegionEnd = end;
}
