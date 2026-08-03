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
    public bool Disabled;
    public bool? Checked;                 // switch / checkbox
    public double? Now, Min, Max;         // slider / progressbar
    public (float X, float Y, float W, float H) Bounds;
    public readonly List<AccessibilityNode> Children = new();
}

/// <summary>Builds the semantics tree from a laid-out render tree.</summary>
public static class AccessibilityTree
{
    public static AccessibilityNode Build(RenderNode root)
    {
        var node = new AccessibilityNode { Role = "document", Bounds = HitTesting.AbsoluteBox(root) };
        foreach (var child in root.Children) Collect(child, node);
        return node;
    }

    private static void Collect(RenderNode render, AccessibilityNode parent)
    {
        if (render.Style.Display == DisplayType.None) return;

        var role = RoleOf(render);
        var target = parent;
        if (role is not null && render.Element is { } el)
        {
            var sem = new AccessibilityNode
            {
                Role = role,
                Name = AccessibleName(render, el),
                Bounds = HitTesting.AbsoluteBox(render),
                Focusable = role is "slider" or "button" or "switch" or "link" or "textbox",
                Disabled = el.HasAttribute("disabled"),
            };
            ApplyValues(sem, el, role);
            parent.Children.Add(sem);
            target = sem;
        }
        foreach (var child in render.Children) Collect(child, target);
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

    private static void ApplyValues(AccessibilityNode sem, IElement el, string role)
    {
        double? Attr(string name) =>
            double.TryParse(el.GetAttribute(name), CultureInfo.InvariantCulture, out var v) ? v : null;

        switch (role)
        {
            case "slider" or "progressbar":
                sem.Now = Attr("aria-valuenow");
                sem.Min = Attr("aria-valuemin");
                sem.Max = Attr("aria-valuemax");
                sem.Value = sem.Now?.ToString(CultureInfo.InvariantCulture);
                break;
            case "switch" or "checkbox":
                sem.Checked = el.GetAttribute("aria-checked") == "true";
                break;
        }
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
        if (node.Now is { } now) parts.Add($"value={now}{(node.Min is { } mn ? $" [{mn}..{node.Max}]" : "")}");
        if (node.Focusable) parts.Add("focusable");
        if (node.Disabled) parts.Add("disabled");
        sb.AppendLine($"{indent}{string.Join(' ', parts)}");
        foreach (var child in node.Children) sb.Append(Dump(child, depth + 1));
        return sb.ToString();
    }
}
