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
/// </list>
/// </summary>
public static class AriaHtml
{
    /// <param name="root">The semantics tree.</param>
    /// <param name="pixelScale">Engine coordinates to page CSS pixels: the document's zoom times the
    /// host's present scale. 1 when the tree is only read, never placed.</param>
    public static string Serialize(AccessibilityNode root, float pixelScale = 1f)
    {
        var sb = new StringBuilder();
        Write(sb, root, root.Bounds.X, root.Bounds.Y, pixelScale <= 0 ? 1f : pixelScale);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, AccessibilityNode n, float parentX, float parentY, float s)
    {
        sb.Append("<div role=\"").Append(Esc(n.Role)).Append('"');
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
        sb.Append('>');

        // A value-bearing role's accessible VALUE is its text content, so that is the value and only
        // the value - an empty field reads as empty. It used to be the name, so an empty "Search…"
        // box read its own placeholder back as what had been typed.
        if (IsValueRole(n.Role))
        {
            if (!string.IsNullOrEmpty(n.Value)) sb.Append(Esc(n.Value!));
        }
        // Any other leaf reads its accessible name as text; containers recurse into their children.
        else if (n.Children.Count == 0)
        {
            if (!string.IsNullOrEmpty(n.Name)) sb.Append(Esc(n.Name!));
        }
        var (ox, oy, _, _) = n.Bounds;
        foreach (var child in n.Children) Write(sb, child, ox, oy, s);
        sb.Append("</div>");
    }

    private static bool IsValueRole(string role) =>
        role is "textbox" or "searchbox" or "combobox" or "spinbutton";

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
