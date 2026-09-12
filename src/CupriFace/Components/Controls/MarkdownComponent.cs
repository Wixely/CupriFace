using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-markdown text="{{Doc}}"&gt;</c> (or raw markdown as the element's text) — renders a small
/// Markdown subset: <c>#</c>/<c>##</c>/<c>###</c> headings, <c>**bold**</c>, <c>*italic*</c>/<c>_italic_</c>,
/// inline <c>`code`</c> and fenced <c>```</c> code blocks, <c>- </c>/<c>* </c> bullet lists, <c>[text](url)</c>
/// links, and blank-line-separated paragraphs. It parses to the toolkit's own elements (which then lay out
/// + paint like any other markup), so no HTML/DOM is injected raw.
/// </summary>
public sealed partial class MarkdownComponent : ComponentBase
{
    public override string Tag => "cupri-markdown";
    public override string DefaultCss => """
        .cupri-md { display:block; color:var(--cupri-text,#1e2430); font-size:15px; line-height:1.7; }
        .cupri-md h1 { font-size:25px; font-weight:bold; margin:2px 0 12px; }
        .cupri-md h2 { font-size:19px; font-weight:bold; margin:18px 0 8px; }
        .cupri-md h3 { font-size:16px; font-weight:bold; margin:14px 0 6px; }
        .cupri-md p { margin:0 0 11px; }
        .cupri-md ul { display:block; margin:0 0 11px; padding-left:8px; }
        .cupri-md li { display:block; margin:4px 0; }
        .cupri-md-bull { display:inline-block; width:16px; color:var(--cupri-muted,#98a2b3); }
        .cupri-md a { color:var(--cupri-accent,#B87333); }
        .cupri-md strong { font-weight:bold; }
        .cupri-md code { font-family:monospace; font-size:0.9em; background:var(--cupri-hover,#eef1f5);
                         border:1px solid var(--cupri-border,#e6e9f0); border-radius:5px; padding:1px 5px; }
        .cupri-md pre { background:var(--cupri-hover,#eef1f5); border:1px solid var(--cupri-border,#e6e9f0);
                        border-radius:8px; padding:12px 14px; margin:0 0 11px; font-family:monospace;
                        font-size:13px; line-height:1.5; overflow:auto; }
        .cupri-md ol { display:block; margin:0 0 11px; padding-left:8px; }
        .cupri-md-num { display:inline-block; width:22px; color:var(--cupri-muted,#98a2b3); }
        .cupri-md blockquote { display:block; margin:0 0 11px; padding:6px 12px;
                               border-left:3px solid var(--cupri-border,#e6e9f0);
                               color:var(--cupri-muted,#667085); }
        .cupri-md hr { display:block; height:1px; margin:16px 0; background:var(--cupri-border,#e6e9f0); }
        .cupri-md cupri-image { display:block; max-width:100%; margin:0 0 11px; border-radius:6px; }
        .cupri-md s { color:var(--cupri-muted,#98a2b3); }
        .cupri-md h4 { font-size:15px; font-weight:bold; margin:12px 0 6px; }
        .cupri-md h5 { font-size:14px; font-weight:bold; margin:12px 0 6px; }
        .cupri-md h6 { font-size:13px; font-weight:bold; margin:12px 0 6px;
                       color:var(--cupri-muted,#667085); }
        .cupri-md-code { position:relative; }
        /* An explicit width, because an absolutely-positioned box here takes its container's width
           rather than shrinking to its text — which put `right:8px` at 400-400-8 = -8, i.e. a
           full-width bar hanging off the left edge instead of a button in the corner. */
        .cupri-md-copy { position:absolute; top:8px; right:8px; z-index:1; width:52px; text-align:center;
                         padding:3px 0; border-radius:6px; font-size:12px;
                         background:var(--cupri-surface,#fff); border:1px var(--cupri-border,#e6e9f0);
                         color:var(--cupri-muted,#667085); }
        .cupri-md-copy[data-hover] { color:var(--cupri-text,#1e2430); border-color:#98a2b3; }
        .cupri-md-cl { display:block; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-md");
        var src = Str(el, "text");
        if (src.Length == 0) src = Dedent(el.TextContent);
        el.InnerHtml = Render(src);
    }

    private static string Render(string md)
    {
        var sb = new StringBuilder();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("```"))                              // fenced code block (verbatim)
            {
                i++;
                // The raw lines are kept alongside the rendered ones: the <pre> is a stack of divs
                // with non-breaking spaces standing in for indentation, so its TextContent would come
                // back as one run with the indentation mangled and the line breaks gone. The copy
                // button carries what the author actually wrote.
                var raw = new StringBuilder();
                var body = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) // whitespace + honour no
                {
                    if (raw.Length > 0) raw.Append('\n');
                    raw.Append(lines[i]);
                    body.Append("<div class='cupri-md-cl'>").Append(CodeLine(lines[i])).Append("</div>"); // white-space:pre
                    i++;
                }
                i++;
                // The button comes AFTER the <pre>: absolute positioning puts it on top visually
                // either way, but hit testing walks document order, so a button written first loses
                // every click to the code block it sits over.
                sb.Append("<div class='cupri-md-code'><pre>").Append(body).Append("</pre>")
                  .Append("<div class='cupri-md-copy' role='button' aria-label='Copy code' data-cupri-copy=\"")
                  .Append(Attr(raw.ToString())).Append("\">Copy</div></div>");
            }
            else if (Heading(t) is var (level, text) && level > 0)  // # .. ###### (ATX, space required)
            { sb.Append("<h").Append(level).Append('>').Append(Inline(text))
                .Append("</h").Append(level).Append('>'); i++; }
            else if (IsBullet(t))                                 // bullet list (consecutive - / * lines)
            {
                sb.Append("<ul>");
                while (i < lines.Length && IsBullet(lines[i].TrimStart()))
                { sb.Append("<li><span class='cupri-md-bull'>&#8226;</span>").Append(Inline(lines[i].TrimStart()[2..])).Append("</li>"); i++; }
                sb.Append("</ul>");
            }
            else if (Ordered(t) > 0)                              // ordered list (consecutive `1. ` lines)
            {
                sb.Append("<ol>");
                var n = 1;
                while (i < lines.Length && Ordered(lines[i].TrimStart()) is var w && w > 0)
                {
                    sb.Append("<li><span class='cupri-md-num'>").Append(n++).Append(".</span>")
                      .Append(Inline(lines[i].TrimStart()[w..])).Append("</li>");
                    i++;
                }
                sb.Append("</ol>");
            }
            else if (t.StartsWith("> "))                          // blockquote (consecutive `> ` lines)
            {
                sb.Append("<blockquote>");
                var para = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("> "))
                { if (para.Length > 0) para.Append(' '); para.Append(lines[i].TrimStart()[2..].Trim()); i++; }
                sb.Append(Inline(para.ToString())).Append("</blockquote>");
            }
            else if (IsRule(t)) { sb.Append("<hr>"); i++; }       // --- / *** / ___
            else if (t.Length == 0) i++;                          // blank line
            else                                                  // paragraph (join consecutive plain lines)
            {
                var para = new StringBuilder();
                // ALWAYS consume the line that got us here, THEN join any plain continuations. The
                // guard below rejects lines that start a different block, and a `####` heading is one
                // of those — so a version that only consumed inside the loop consumed nothing at all
                // for it and spun forever on the outer loop. Any line reaching this branch is a
                // paragraph by definition; forward progress is a property of the branch, not of the
                // guard, so a future block type cannot reintroduce the hang.
                para.Append(lines[i].Trim());
                i++;
                while (i < lines.Length && lines[i].TrimStart() is { Length: > 0 } pl && !StartsBlock(pl))
                { para.Append(' ').Append(lines[i].Trim()); i++; }
                sb.Append("<p>").Append(Inline(para.ToString())).Append("</p>");
            }
        }
        return sb.ToString();
    }

    private static bool IsBullet(string t) => t.StartsWith("- ") || t.StartsWith("* ");

    /// <summary>Escape for an ATTRIBUTE value. The ampersand goes first or it would double-escape the
    /// entities the others introduce; newlines are legal inside an attribute and are what make the
    /// copied text keep its lines.</summary>
    private static string Attr(string s) => s
        .Replace("&", "&amp;").Replace("\"", "&quot;")
        .Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>A line that begins some OTHER block, and so must not be swallowed into a paragraph.
    /// Kept in one place so the paragraph guard and the block branches cannot drift apart.</summary>
    private static bool StartsBlock(string t) =>
        Heading(t).Level > 0 || IsBullet(t) || Ordered(t) > 0 || t.StartsWith("> ")
        || t.StartsWith("```") || IsRule(t);

    /// <summary>ATX heading level 1-6, or 0 when this is not one. CommonMark requires the space, so
    /// `#hashtag` is deliberately NOT a heading — it is ordinary paragraph text.</summary>
    private static (int Level, string Text) Heading(string t)
    {
        var h = 0;
        while (h < t.Length && h < 6 && t[h] == '#') h++;
        return h > 0 && h < t.Length && t[h] == ' ' ? (h, t[(h + 1)..]) : (0, t);
    }

    /// <summary>Width of an ordered-list marker (`1. `, `12) `), or 0 when this is not one.</summary>
    private static int Ordered(string t)
    {
        var d = 0;
        while (d < t.Length && char.IsAsciiDigit(t[d])) d++;
        return d > 0 && d + 1 < t.Length && (t[d] == '.' || t[d] == ')') && t[d + 1] == ' ' ? d + 2 : 0;
    }

    /// <summary>A thematic break: three or more of - * _ alone on the line.</summary>
    private static bool IsRule(string t)
    {
        var c = t.Length > 0 ? t[0] : ' ';
        if (c is not ('-' or '*' or '_')) return false;
        var body = t.Replace(" ", "");
        return body.Length >= 3 && body.All(ch => ch == c);
    }

    // Inline spans. Escape first, then code (its content stays literal), then bold, italic, and links.
    private static string Inline(string s)
    {
        s = Esc(s);
        s = CodeRx().Replace(s, m => "<code>" + m.Groups[1].Value + "</code>");
        s = BoldRx().Replace(s, "<strong>$1</strong>");
        s = StrikeRx().Replace(s, "<s>$1</s>");
        s = ItalicRx().Replace(s, m => "<em>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</em>");
        // Images BEFORE links, and the link pattern then refuses a leading `!`. Run the other way
        // round (or without the guard) and `![alt](src)` matches the LINK rule from index 1, leaving
        // a literal "!" sitting in front of an anchor — which is what it used to render.
        // <cupri-image>, not <img>: the engine has no raw img primitive, and this component's whole
        // contract is that it expands to the TOOLKIT's elements. Emitting <img> produced an element
        // nothing renders — a silently empty box.
        s = ImageRx().Replace(s, "<cupri-image src=\"$2\" alt=\"$1\"></cupri-image>");
        s = LinkRx().Replace(s, "<a href=\"$2\">$1</a>");
        return s;
    }

    // Strip the common leading indentation (for markdown written inline in the template).
    private static string Dedent(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l)).Trim('\n');
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // One code line: escape, keep leading indentation (spaces collapse otherwise), keep blank lines tall.
    private static string CodeLine(string s)
    {
        var lead = s.Length - s.TrimStart(' ').Length;
        var body = Esc(s.TrimStart(' '));
        return body.Length == 0 ? "&#160;" : string.Concat(Enumerable.Repeat("&#160;", lead)) + body;
    }

    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodeRx();
    [GeneratedRegex(@"\*\*([^*]+)\*\*")] private static partial Regex BoldRx();
    [GeneratedRegex(@"(?<!\*)\*(?!\*)([^*]+)\*(?!\*)|_([^_]+)_")] private static partial Regex ItalicRx();
    [GeneratedRegex(@"(?<!!)\[([^\]]+)\]\(([^)]+)\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")] private static partial Regex ImageRx();
    [GeneratedRegex(@"~~([^~]+)~~")] private static partial Regex StrikeRx();
}
