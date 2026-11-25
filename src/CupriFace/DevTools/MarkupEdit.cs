namespace CupriFace.DevTools;

/// <summary>Where one element sits in markup text: <see cref="Start"/> at its <c>&lt;</c>,
/// <see cref="End"/> just past its closing <c>&gt;</c>, and <see cref="InnerEnd"/> at the start of
/// its end tag — where content inserted INSIDE it goes. Null for an element with no end tag.</summary>
internal readonly record struct MarkupSpan(int Start, int End, int? InnerEnd, string Tag);

/// <summary>
/// Edits to markup as TEXT — the template a document is built from — so an edit keeps everything
/// around it exactly as written: indentation, comments, attribute quoting, the author's line breaks.
/// Re-serialising the parsed DOM would reformat the whole file, which is not an edit anyone asked
/// for, and is not something to write back over their source.
/// </summary>
internal static class MarkupEdit
{
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    /// <summary>
    /// The span of the element whose start tag begins at <paramref name="start"/>. Its end is found
    /// by counting same-named start and end tags from there — exact for markup that closes what it
    /// opens, which an app's template does. Null when the end cannot be found (an unclosed tag), so
    /// the caller refuses the edit rather than cutting in the wrong place.
    /// </summary>
    public static MarkupSpan? Find(string html, int start)
    {
        if (start < 0 || start >= html.Length || html[start] != '<') return null;
        var tag = NameAt(html, start + 1);
        if (tag.Length == 0) return null;
        var openEnd = TagEnd(html, start);
        if (openEnd < 0) return null;
        if (Void.Contains(tag) || html[openEnd - 2] == '/') return new MarkupSpan(start, openEnd, null, tag);

        var depth = 1;
        var i = openEnd;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) return null;
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (close < 0) return null;
                i = close + 3;
                continue;
            }
            var closing = lt + 1 < html.Length && html[lt + 1] == '/';
            var name = NameAt(html, lt + (closing ? 2 : 1));
            var end = TagEnd(html, lt);
            if (end < 0) return null;
            if (name.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                if (closing)
                {
                    if (--depth == 0) return new MarkupSpan(start, end, lt, tag);
                }
                else if (html[end - 2] != '/') depth++;
            }
            i = end;
        }
        return null;
    }

    /// <summary>Remove the element — and the line it stood on, when it stood on a line of its own,
    /// so a deletion leaves no blank line behind.</summary>
    public static string Delete(string html, MarkupSpan span)
    {
        var (from, to) = (span.Start, span.End);
        var lineStart = LineStart(html, from);
        var lineEnd = html.IndexOf('\n', to);
        if (lineEnd < 0) lineEnd = html.Length;
        if (IsBlank(html, lineStart, from) && IsBlank(html, to, lineEnd))
        {
            from = lineStart;
            to = Math.Min(html.Length, lineEnd + 1);
        }
        return html.Remove(from, to - from);
    }

    /// <summary>Replace the whole element with <paramref name="text"/>, its later lines at the
    /// element's indentation.</summary>
    public static string Replace(string html, MarkupSpan span, string text)
    {
        var nl = NewLine(html);
        var indent = IndentOf(html, span.Start);
        var lines = text.Trim().Replace("\r\n", "\n").Split('\n');
        for (var i = 1; i < lines.Length; i++) if (lines[i].Length > 0) lines[i] = indent + lines[i];
        return html[..span.Start] + string.Join(nl, lines) + html[span.End..];
    }

    /// <summary>Insert <paramref name="text"/> as the next sibling, on its own line at the element's
    /// indentation.</summary>
    public static string InsertAfter(string html, MarkupSpan span, string text)
    {
        var indent = IndentOf(html, span.Start);
        var nl = NewLine(html);
        return html[..span.End] + nl + Indent(text.Trim(), indent, nl) + html[span.End..];
    }

    /// <summary>Insert <paramref name="text"/> as the element's last child, one level deeper than it.
    /// Null for an element with no end tag to put it before.</summary>
    public static string? InsertInside(string html, MarkupSpan span, string text)
    {
        if (span.InnerEnd is not { } at) return null;
        var indent = IndentOf(html, span.Start);
        var nl = NewLine(html);
        var child = Indent(text.Trim(), indent + "  ", nl);
        // An end tag on a line of its own keeps that line, and the new child goes above it.
        var lineStart = LineStart(html, at);
        if (IsBlank(html, lineStart, at))
            return html[..lineStart] + child + nl + html[lineStart..];
        return html[..at] + nl + child + nl + indent + html[at..];
    }

    /// <summary>The element's own text, reindented so its first line sits at column 0 — what an
    /// "edit as HTML" box should show.</summary>
    public static string Outer(string html, MarkupSpan span)
    {
        var indent = IndentOf(html, span.Start);
        var lines = html[span.Start..span.End].Replace("\r\n", "\n").Split('\n');
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].StartsWith(indent, StringComparison.Ordinal)) lines[i] = lines[i][indent.Length..];
        return string.Join("\n", lines);
    }

    /// <summary>Text to put at <paramref name="indent"/>: the first line and every line after it.</summary>
    private static string Indent(string text, string indent, string nl)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(nl, lines.Select(l => l.Length == 0 ? l : indent + l));
    }

    /// <summary>The file's own line ending, so an edit does not mix CRLF into an LF file or back.</summary>
    private static string NewLine(string html) => html.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string IndentOf(string html, int at)
    {
        var ls = LineStart(html, at);
        var i = ls;
        while (i < at && (html[i] == ' ' || html[i] == '\t')) i++;
        return html[ls..i];
    }

    private static int LineStart(string html, int at) => at == 0 ? 0 : html.LastIndexOf('\n', at - 1) + 1;

    private static bool IsBlank(string html, int from, int to)
    {
        for (var i = from; i < to; i++) if (!char.IsWhiteSpace(html[i])) return false;
        return true;
    }

    private static string NameAt(string html, int i)
    {
        var j = i;
        while (j < html.Length && (char.IsLetterOrDigit(html[j]) || html[j] is '-' or '_' or ':')) j++;
        return html[i..j];
    }

    /// <summary>Just past the <c>&gt;</c> that ends the tag starting at <paramref name="lt"/>,
    /// stepping over quoted attribute values (a <c>&gt;</c> inside one is text). -1 if none.</summary>
    private static int TagEnd(string html, int lt)
    {
        char quote = '\0';
        for (var i = lt + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i + 1;
        }
        return -1;
    }
}
