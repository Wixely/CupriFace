using System.Globalization;
using System.Text;

namespace CupriFace.Accessibility;

/// <summary>
/// Serialises the platform-neutral semantics tree (<see cref="AccessibilityNode"/>) to an ARIA HTML
/// fragment. The web host injects this into an off-screen, screen-reader-visible DOM element that
/// mirrors the canvas — so assistive tech can read the UI the canvas paints opaquely (DESIGN §5).
/// Each node becomes a <c>&lt;div role="…"&gt;</c> carrying <c>aria-label</c> and the relevant states
/// (<c>aria-checked</c>, <c>aria-selected</c>, <c>aria-expanded</c>, <c>aria-valuenow/min/max</c>,
/// <c>aria-disabled</c>, <c>aria-hidden</c> for content scrolled or clipped out of view) plus
/// <c>data-automation-id</c>, so a test can find a node the way FlaUI finds one by AutomationId.
///
/// <para>What it is NOT, yet: interactive. This is a read-only mirror — activating a node in it
/// reaches nothing, and it has no geometry. The native bridges expose both. Making the web host a
/// bridge rather than a mirror is a separate piece of work; this serialiser is where the tree's
/// fields stop being dropped on the way.</para>
///
/// <para>No <c>tabindex</c>, deliberately. The engine handles Tab itself and the page's key handler
/// stops the browser's default, so a tab stop here was unreachable — measured: Tab never left the
/// keyboard textarea — and <c>tabindex="0"</c> on a node an AT can never reach is a claim it cannot
/// honour. Screen readers walk the mirror by role, not by tab order.</para>
/// </summary>
public static class AriaHtml
{
    public static string Serialize(AccessibilityNode root)
    {
        var sb = new StringBuilder();
        Write(sb, root);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, AccessibilityNode n)
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
        foreach (var child in n.Children) Write(sb, child);
        sb.Append("</div>");
    }

    private static bool IsValueRole(string role) =>
        role is "textbox" or "searchbox" or "combobox" or "spinbutton";

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
