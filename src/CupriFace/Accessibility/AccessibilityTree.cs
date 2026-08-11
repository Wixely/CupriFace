using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;

namespace CupriFace.Accessibility;

/// <summary>
/// A platform-neutral semantics node (DESIGN.md §5). The render tree paints to a flat
/// canvas, so this parallel tree is what assistive tech consumes — bridged to UIA /
/// AT-SPI / NSAccessibility on desktop and a hidden DOM overlay on web.
/// </summary>
public sealed class AccessibilityNode
{
    public required string Role;
    public string? Name;
    public string? Value;
    public bool Focusable;
    public bool Focused;                  // has keyboard/edit focus right now
    public bool Disabled;
    public bool? Checked;                 // switch / checkbox / radio
    public bool? Selected;                // tab / option / treeitem (aria-selected)
    public bool? Expanded;                // aria-expanded, where the role carries it
    public double? Now, Min, Max;         // slider / progressbar / spinbutton
    public (float X, float Y, float W, float H) Bounds;   // on-screen CSS px (scroll applied)
    public AccessibilityNode? Parent;
    public readonly List<AccessibilityNode> Children = new();

    /// <summary>Structural path of the backing render node (child-index chain from the root) — the
    /// identity that survives the per-keystroke rebuild, same scheme scroll restoration uses. Feed it
    /// to <see cref="CupriDocument.NodeAtPath"/> / the accessibility action methods.</summary>
    public string Path = "";

    /// <summary>Stable author-provided handle for AT clients: the element <c>id</c>, falling back to
    /// the binding path (<c>data-bind-value</c>/<c>data-bind-checked</c>). Null when anonymous.</summary>
    public string? AutomationId;
}

/// <summary>Builds the semantics tree from a laid-out render tree.</summary>
public static class AccessibilityTree
{
    public static AccessibilityNode Build(RenderNode root) => Build(root, null, null);

    /// <summary>
    /// Build with the document's own oracles: <paramref name="isFocusable"/> is the SAME predicate Tab
    /// order uses (so "focusable" here never disagrees with where Tab actually stops), and
    /// <paramref name="focused"/> is the render node that currently holds keyboard/edit focus.
    /// </summary>
    public static AccessibilityNode Build(RenderNode root, Func<IElement, bool>? isFocusable, RenderNode? focused)
    {
        var node = new AccessibilityNode { Role = "document", Bounds = (root.X, root.Y, root.Width, root.Height) };
        for (var i = 0; i < root.Children.Count; i++)
            Collect(root.Children[i], root.X, ChildOriginY(root, root.Y), "/" + i, node, isFocusable, focused);
        return node;
    }

    // Children of a scrolled element are shifted up by the clamped scroll offset — the same
    // correction HitTesting.Hit applies, so a click synthesized at an a11y node's centre lands
    // on that node even inside a scrolled container.
    private static float ChildOriginY(RenderNode n, float ay) =>
        ay - (n.IsScrollable ? Math.Clamp(n.ScrollY, 0, n.MaxScrollY) : 0f);

    private static void Collect(RenderNode render, float originX, float originY, string path,
        AccessibilityNode parent, Func<IElement, bool>? isFocusable, RenderNode? focused)
    {
        if (render.Style.Display == DisplayType.None) return;

        // Top-layer nodes (overlays, position:fixed) already hold absolute viewport coordinates.
        var ax = (render.IsTopLayer ? 0 : originX) + render.X;
        var ay = (render.IsTopLayer ? 0 : originY) + render.Y;

        var role = RoleOf(render);
        var target = parent;
        if (role is not null && render.Element is { } el)
        {
            var sem = new AccessibilityNode
            {
                Role = role,
                Name = AccessibleName(render, el),
                Path = path,
                Parent = parent,
                Bounds = (ax, ay, render.Width, render.Height),
                Focusable = isFocusable?.Invoke(el)
                            ?? role is "slider" or "button" or "switch" or "checkbox" or "radio"
                                    or "link" or "textbox" or "spinbutton",
                Focused = focused is not null && ReferenceEquals(render, focused),
                Disabled = IsDisabled(el),
                AutomationId = FirstAttr(el, "id", "data-bind-value", "data-bind-checked"),
            };
            ApplyValues(sem, render, el, role);
            parent.Children.Add(sem);
            target = sem;
        }
        var childOy = ChildOriginY(render, ay);
        for (var i = 0; i < render.Children.Count; i++)
            Collect(render.Children[i], ax, childOy, path + "/" + i, target, isFocusable, focused);
    }

    /// <summary>The engine-wide definition of "disabled": the <c>disabled</c> attribute, the
    /// <c>disabled</c> class the components emit (e.g. a pagination arrow on the first page), or
    /// <c>aria-disabled</c>. The cursor logic and this tree share it so they can never disagree.</summary>
    public static bool IsDisabled(IElement el) =>
        el.ClassList.Contains("disabled")
        || el.HasAttribute("disabled")
        || el.GetAttribute("aria-disabled") is "true";

    private static string? FirstAttr(IElement el, params string[] names)
    {
        foreach (var name in names)
            if (el.GetAttribute(name) is { Length: > 0 } v) return v;
        return null;
    }

    private static string? RoleOf(RenderNode n)
    {
        var explicitRole = n.Element?.GetAttribute("role");
        if (explicitRole is { Length: > 0 }) return explicitRole;
        return n.Tag switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "heading",
            "a" => "link",
            "button" => "button",
            "img" => "image",
            _ => null,
        };
    }

    private static void ApplyValues(AccessibilityNode sem, RenderNode render, IElement el, string role)
    {
        double? Attr(string name) =>
            double.TryParse(el.GetAttribute(name), CultureInfo.InvariantCulture, out var v) ? v : null;

        switch (role)
        {
            case "slider" or "progressbar" or "spinbutton":
                sem.Now = Attr("aria-valuenow");
                sem.Min = Attr("aria-valuemin");
                sem.Max = Attr("aria-valuemax");
                sem.Value = sem.Now?.ToString(CultureInfo.InvariantCulture);
                break;
            case "switch" or "checkbox" or "radio":
                sem.Checked = el.GetAttribute("aria-checked") == "true";
                break;
            case "textbox" or "combobox":
                // The rendered text IS the live value (the per-keystroke rebuild writes the edit
                // buffer into the DOM, masked for passwords — so this never leaks one).
                var text = CollectText(render).Trim();
                if (text.Length > 0) sem.Value = text;
                break;
        }

        if (el.GetAttribute("aria-selected") is ("true" or "false") and var sel) sem.Selected = sel == "true";
        if (el.GetAttribute("aria-expanded") is ("true" or "false") and var exp) sem.Expanded = exp == "true";
    }

    private static string? AccessibleName(RenderNode render, IElement el)
    {
        var label = el.GetAttribute("aria-label");
        if (label is { Length: > 0 }) return label;
        var text = CollectText(render).Trim();
        return text.Length > 0 ? text : null;
    }

    private static string CollectText(RenderNode n)
    {
        if (n.IsText) return (n.Text ?? "") + " ";
        var sb = new StringBuilder();
        foreach (var c in n.Children) sb.Append(CollectText(c));
        return sb.ToString();
    }

    /// <summary>Human-readable dump for verification (mirrors what a screen reader sees).</summary>
    public static string Dump(AccessibilityNode node, int depth = 0)
    {
        var sb = new StringBuilder();
        var indent = new string(' ', depth * 2);
        var parts = new List<string> { node.Role };
        if (node.Name is { Length: > 0 }) parts.Add($"\"{node.Name}\"");
        if (node.Checked is { } isChecked) parts.Add($"checked={isChecked}");
        if (node.Selected is { } isSelected) parts.Add($"selected={isSelected}");
        if (node.Expanded is { } isExpanded) parts.Add($"expanded={isExpanded}");
        if (node.Now is { } now) parts.Add($"value={now}{(node.Min is { } mn ? $" [{mn}..{node.Max}]" : "")}");
        if (node.Focusable) parts.Add("focusable");
        if (node.Focused) parts.Add("FOCUSED");
        if (node.Disabled) parts.Add("disabled");
        sb.AppendLine($"{indent}{string.Join(' ', parts)}");
        foreach (var child in node.Children) sb.Append(Dump(child, depth + 1));
        return sb.ToString();
    }
}
