using System.Globalization;
using System.Text;

namespace CupriFace.Accessibility;

/// <summary>
/// Serialises the platform-neutral semantics tree (<see cref="AccessibilityNode"/>) to an ARIA HTML
/// fragment. The web host injects this into a transparent DOM overlay positioned over the canvas,
/// so assistive tech can read AND operate the UI the canvas paints opaquely (DESIGN §5 — the
/// Flutter-web "semantics overlay" model).
/// Each node becomes a <c>&lt;div role="…"&gt;</c> carrying <c>aria-label</c> and the relevant states
/// (<c>aria-checked</c>, <c>aria-selected</c>, <c>aria-expanded</c>, <c>aria-valuenow/min/max</c>,
/// <c>aria-disabled</c>, <c>aria-hidden</c> for content scrolled or clipped out of view) plus
/// <c>data-automation-id</c>, so a test can find a node the way FlaUI finds one by AutomationId.
///
/// <para>What makes it a bridge rather than a mirror, and what each part is for:</para>
/// <list type="bullet">
/// <item><b>Geometry.</b> Every node carries an inline <c>style</c> placing it at its
/// <see cref="AccessibilityNode.Bounds"/>, relative to its parent node, in the page's CSS pixels
/// (the engine's coordinates times <paramref name="pixelScale"/>). That is what gives a screen
/// reader hit-testing, a focus ring in the right place and touch exploration on a phone — the
/// same things the desktop bridges answer through <c>BoundingRectangle</c>.</item>
/// <item><b>Identity.</b> <c>data-path</c> is the node's structural path, the handle the four native
/// bridges post actions through. The page forwards a <c>click</c> on a node — which is how a
/// screen reader activates one — to <c>CupriDocument.AccessibilityActivate(path)</c>, and a focus
/// arriving on a node to <c>AccessibilityFocus(path)</c>.</item>
/// <item><b>Focus.</b> <c>tabindex="-1"</c> on every focusable node, so the page can put DOM focus on
/// the node the engine focused and the screen reader announces it — the web's equivalent of the
/// UIA focus-changed event. <c>-1</c>, never <c>0</c>: the engine owns Tab (the page's key handler
/// stops the browser's default), so a real tab stop here would be a claim the mirror cannot
/// honour. <c>data-focused</c> marks the one node that holds focus now.</item>
/// <item><b>Editing.</b> A leaf text field is mirrored as a real <c>&lt;input&gt;</c> or
/// <c>&lt;textarea&gt;</c> rather than a <c>&lt;div&gt;</c>, carrying the field's kind, placeholder,
/// <c>inputmode</c>, <c>enterkeyhint</c> and the author's <c>autocomplete</c>. The page puts it over
/// the painted field, transparent, and while it holds focus the BROWSER owns the text and selection
/// — which is how the web host gets native IME, editing semantics, a mobile keyboard that knows
/// what it is typing into, and a password manager that can find the field at all. A password's
/// value is never written here; see <see cref="WriteEditorAttributes"/>.</item>
/// </list>
/// </summary>
public static class AriaHtml
{
    /// <param name="root">The semantics tree.</param>
    /// <param name="pixelScale">Engine coordinates to page CSS pixels: the document's zoom times the
    /// host's present scale. 1 when the tree is only read, never placed.</param>
    /// <param name="focusedEdit">The focused field's EXACT edit buffer and selection, in UTF-16
    /// units. Both matter to a host driving a real editing element: the selection becomes
    /// <c>data-sel</c>, so the caret can be put back after the engine rewrites a value (a clamp, a
    /// reformat, a picked suggestion), and the buffer is the field's value verbatim — where
    /// <see cref="AccessibilityNode.Value"/> is the RENDERED text, trimmed, which is right for a
    /// screen reader and wrong for an editor. Typing a trailing space into a field whose mirror
    /// reported the trimmed text had that space taken straight back out again.</param>
    public static string Serialize(AccessibilityNode root, float pixelScale = 1f,
                                   (string Value, int Start, int End)? focusedEdit = null)
    {
        var sb = new StringBuilder();
        Write(sb, root, root.Bounds.X, root.Bounds.Y, pixelScale <= 0 ? 1f : pixelScale, focusedEdit);
        return sb.ToString();
    }

    /// <summary>
    /// The tag a node is mirrored as. A leaf editing surface becomes a REAL editing element, so the
    /// browser's own editor, IME, spell-check, password manager and mobile keyboard operate on the
    /// thing they were built for — the page positions it transparently over the painted field.
    ///
    /// <para>Leaf only, and never a <c>combobox</c>: those are CONTAINERS here (a combobox holds the
    /// textbox you type in, and a date picker holds its grid). Turning a container into an input
    /// would drop everything inside it from the tree, which is the mirror's whole job.</para>
    /// </summary>
    private static string TagFor(AccessibilityNode n) =>
        n.Children.Count == 0 && n.Role is "textbox" or "searchbox" or "spinbutton"
            ? n.Multiline ? "textarea" : "input"
            : "div";

    private static void Write(StringBuilder sb, AccessibilityNode n, float parentX, float parentY, float s,
                              (string Value, int Start, int End)? focusedEdit)
    {
        var tag = TagFor(n);
        sb.Append('<').Append(tag).Append(" role=\"").Append(Esc(n.Role)).Append('"');
        if (!string.IsNullOrEmpty(n.Name)) sb.Append(" aria-label=\"").Append(Esc(n.Name!)).Append('"');
        if (n.Disabled) sb.Append(" aria-disabled=\"true\"");
        // Scrolled past, or clipped by an overflow ancestor: the desktop bridge reports IsOffscreen
        // and Narrator skips the control; without this a screen reader read the whole document
        // aloud as if all of it were on screen.
        if (n.Offscreen) sb.Append(" aria-hidden=\"true\"");
        if (!string.IsNullOrEmpty(n.AutomationId)) sb.Append(" data-automation-id=\"").Append(Esc(n.AutomationId!)).Append('"');
        if (n.Checked is { } c) sb.Append(" aria-checked=\"").Append(c ? "true" : "false").Append('"');
        if (n.Selected is { } selected) sb.Append(" aria-selected=\"").Append(selected ? "true" : "false").Append('"');
        if (n.Expanded is { } expanded) sb.Append(" aria-expanded=\"").Append(expanded ? "true" : "false").Append('"');
        if (n.Now is { } now) sb.Append(" aria-valuenow=\"").Append(Num(now)).Append('"');
        if (n.Min is { } min) sb.Append(" aria-valuemin=\"").Append(Num(min)).Append('"');
        if (n.Max is { } max) sb.Append(" aria-valuemax=\"").Append(Num(max)).Append('"');

        // The root is the overlay itself; the page sizes and places that. Everything below it is a
        // box at its bounds, relative to the node it sits in, so nesting keeps the AT's structure
        // (a group contains its buttons) and the numbers still land on the painted control.
        if (n.Parent is not null)
        {
            sb.Append(" data-path=\"").Append(Esc(n.Path)).Append('"');
            if (n.Focusable) sb.Append(" tabindex=\"-1\"");
            if (n.Focused) sb.Append(" data-focused=\"true\"");
            var (x, y, w, h) = n.Bounds;
            sb.Append(" style=\"left:").Append(Num((x - parentX) * s))
              .Append("px;top:").Append(Num((y - parentY) * s))
              .Append("px;width:").Append(Num(w * s))
              .Append("px;height:").Append(Num(h * s)).Append("px\"");
        }

        if (tag != "div") WriteEditorAttributes(sb, n, tag, focusedEdit);
        sb.Append('>');

        // An <input> is void: its value is an attribute, written above, and it has no children.
        if (tag == "input") return;

        // A value-bearing role's accessible VALUE is its text content, so that is the value and only
        // the value - an empty field reads as empty. It used to be the name, so an empty "Search…"
        // box read its own placeholder back as what had been typed. (A <textarea>'s content IS its
        // value, so the same line serves both jobs.)
        if (IsValueRole(n.Role))
        {
            // A <textarea>'s content IS its value, so the focused field's verbatim buffer belongs
            // here for the same reason it does on an <input>.
            var text = tag == "textarea" ? EditorText(n, focusedEdit) : n.Value ?? "";
            if (text.Length > 0 && !n.Masked) sb.Append(Esc(text));
        }
        // Any other leaf reads its accessible name as text; containers recurse into their children.
        else if (n.Children.Count == 0)
        {
            if (!string.IsNullOrEmpty(n.Name)) sb.Append(Esc(n.Name!));
        }
        var (ox, oy, _, _) = n.Bounds;
        foreach (var child in n.Children) Write(sb, child, ox, oy, s, focusedEdit);
        sb.Append("</").Append(tag).Append('>');
    }

    /// <summary>What turns a mirror node into a usable editor: the input's kind, the keyboard it
    /// asks for, the hint a password manager fills by, and the text itself.</summary>
    private static void WriteEditorAttributes(StringBuilder sb, AccessibilityNode n, string tag,
                                              (string Value, int Start, int End)? focusedEdit)
    {
        // A password field is a FILL TARGET, never a publication. Its value is masked everywhere
        // else (the engine renders bullets and every bridge reports those), and it stays out of the
        // DOM here too — so the plaintext a person types is never written into the page. What a
        // password manager FILLS arrives the other way, through the same path autofill uses on any
        // field, and the engine keeps owning the typing (the host leaves DOM focus on its hidden
        // textarea for a masked field). Filling works; saving a newly typed password does not.
        if (tag == "input")
        {
            sb.Append(" type=\"").Append(n.Masked ? "password" : "text").Append('"');
            var value = EditorText(n, focusedEdit);
            if (!n.Masked && value.Length > 0) sb.Append(" value=\"").Append(Esc(value)).Append('"');
        }

        if (!string.IsNullOrEmpty(n.Placeholder)) sb.Append(" placeholder=\"").Append(Esc(n.Placeholder!)).Append('"');
        if (!string.IsNullOrEmpty(n.InputMode)) sb.Append(" inputmode=\"").Append(Esc(n.InputMode)).Append('"');
        if (!string.IsNullOrEmpty(n.EnterKeyHint)) sb.Append(" enterkeyhint=\"").Append(Esc(n.EnterKeyHint)).Append('"');
        // The author's own `autocomplete`, which is the entire point of a real input: a password
        // manager finds fields by it, on page load, and cannot find them in a canvas at all.
        if (!string.IsNullOrEmpty(n.AutofillHint)) sb.Append(" autocomplete=\"").Append(Esc(n.AutofillHint!)).Append('"');
        // Disabled, not dead: readonly keeps the field in the tree and discoverable (aria-disabled
        // is already on it) while refusing the typing the engine would refuse anyway.
        if (n.Disabled) sb.Append(" readonly");
        // The browser's own text services have nothing to correct here — the engine paints the text
        // and the element is transparent, so a squiggle or an autocapitalised first letter would be
        // an edit nobody asked for.
        sb.Append(" spellcheck=\"false\" autocapitalize=\"off\" autocorrect=\"off\"");
        if (n.Focused && focusedEdit is { } sel)
            sb.Append(" data-sel=\"").Append(sel.Start).Append(',').Append(sel.End).Append('"');
    }

    /// <summary>What an editing element is worth, as opposed to what a screen reader reads. The
    /// focused field answers with the engine's edit buffer VERBATIM — <see cref="AccessibilityNode.Value"/>
    /// is the rendered text and therefore trimmed, so a field being typed into would lose the space
    /// at the end of every word as it was typed.</summary>
    private static string EditorText(AccessibilityNode n, (string Value, int Start, int End)? focusedEdit) =>
        n.Focused && focusedEdit is { } f ? f.Value : n.Value ?? "";

    private static bool IsValueRole(string role) =>
        role is "textbox" or "searchbox" or "combobox" or "spinbutton";

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
