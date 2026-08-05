using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-textarea value="{{Notes}}" placeholder="…"&gt;</c> — a multi-line editable text
/// field. role=textbox + aria-multiline; each buffer line renders as its own block so hard
/// newlines (Enter) stack. The document's key dispatch inserts newlines and the caret is
/// positioned per line (see CupriDocument.AppendCaret / the data-multiline buffer patch).
/// </summary>
public sealed class TextAreaComponent : ComponentBase
{
    public override string Tag => "cupri-textarea";
    public override string DefaultCss => """
        .cupri-textarea { display:block; min-width:260px; min-height:78px; overflow:auto;
                          background:var(--cupri-surface, white);
                          border:2px var(--cupri-border, #cbd2dc); border-radius:8px; padding:10px 12px; font-size:15px; }
        .cupri-textarea[data-hover] { border:2px #98a2b3; }
        .cupri-textarea:focus { border:2px #B87333; }
        .cupri-textarea[data-invalid] { border:2px #d92d20; }
        .cupri-ta-body { color:var(--cupri-text, #1e2430); }
        .cupri-ta-line { display:block; }
        .cupri-ta-ph { color:#98a2b3; }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value");
        el.SetAttribute("role", "textbox");
        el.SetAttribute("aria-multiline", "true");
        el.SetAttribute("data-multiline", "");
        // Opt-in "follow the tail": when already scrolled to the bottom, new content keeps it pinned
        // there (logging). The engine reads data-follow-tail on rebuild (see CupriDocument.Rebuild).
        if (Flag(el, "follow-tail")) el.SetAttribute("data-follow-tail", "");
        el.ClassList.Add("cupri-textarea");
        var inner = value.Length > 0
            ? RenderLines(value)
            : $"<div class='cupri-ta-line cupri-ta-ph'>{PlaceholderLine(Str(el, "placeholder"))}</div>";
        el.InnerHtml = $"<div class='cupri-ta-body' data-caret-anchor>{inner}</div>";
    }

    /// <summary>Render each newline-separated line of <paramref name="text"/> as its own block.
    /// Empty lines get a zero-width space so they still reserve a line box (no collapse).</summary>
    private const string Zwsp = "​"; // zero-width space: reserves a line box for an empty line

    public static string RenderLines(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Length > 0 && raw[^1] == '\r' ? raw[..^1] : raw; // tolerate stray CRLF '\r'
            sb.Append("<div class='cupri-ta-line'>")
              .Append(line.Length == 0 ? Zwsp : Escape(line))
              .Append("</div>");
        }
        return sb.ToString();
    }

    private static string PlaceholderLine(string s) => s.Length == 0 ? Zwsp : Escape(s);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
