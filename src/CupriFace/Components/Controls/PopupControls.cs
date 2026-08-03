using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-menu label open&gt;…&lt;cupri-menu-item&gt;…&lt;/cupri-menu&gt;</c> — a dropdown menu.
/// Renders a trigger in flow and, when open, an anchored popup (top layer) below it.
/// </summary>
public sealed class MenuComponent : ComponentBase
{
    public override string Tag => "cupri-menu";
    public override string DefaultCss => """
        .cupri-menu { display:inline-block; }
        .cupri-menu-trigger { display:inline-flex; align-items:center; gap:6px; padding:9px 14px;
                              background:#eef1f5; color:#1e2430; border-radius:8px; font-weight:bold; font-size:14px; }
        .cupri-menu-popup { position:fixed; background:white; border-radius:10px; padding:6px; z-index:30;
                            border:1px #e6e9f0; }
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var label = Str(el, "label", "Menu");
        var id = NextId();
        var items = el.InnerHtml;

        el.ClassList.Add("cupri-menu");
        var trigger = $"<div class='cupri-menu-trigger' id='{id}' data-cupri-toggle=\"{id}\">{label}" +
                      IconMarkup("chevron-down", 16) + "</div>";
        var popup = open
            ? $"<div class='cupri-menu-popup' role='menu' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>{items}</div>"
            : "";
        el.InnerHtml = trigger + popup;
    }
}

/// <summary><c>&lt;cupri-menu-item&gt;</c> — a row inside a menu; keeps its label.</summary>
public sealed class MenuItemComponent : ComponentBase
{
    public override string Tag => "cupri-menu-item";
    public override string DefaultCss => """
        .cupri-menu-item { display:flex; align-items:center; gap:8px; padding:9px 12px; border-radius:6px;
                           color:#1e2430; font-size:14px; }
        .cupri-menu-item:hover { background:#eef1f5; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "menuitem");
        el.ClassList.Add("cupri-menu-item");
        var icon = Str(el, "icon");
        if (icon.Length > 0) el.InnerHtml = IconMarkup(icon, 18) + el.InnerHtml;
    }
}

/// <summary>
/// <c>&lt;cupri-tooltip text open&gt;…&lt;/cupri-tooltip&gt;</c> — wraps a trigger; shows an anchored
/// bubble above it when open. (Hover-to-open arrives with the :hover work.)
/// </summary>
public sealed class TooltipComponent : ComponentBase
{
    public override string Tag => "cupri-tooltip";
    public override string DefaultCss => """
        .cupri-tooltip { display:inline-block; }
        .cupri-tt-anchor { display:inline-block; }
        .cupri-tt-bubble { position:fixed; background:#1e2430; color:white; padding:6px 10px;
                           border-radius:6px; font-size:12px; z-index:40; }
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var text = Str(el, "text");
        var id = NextId();
        var trigger = el.InnerHtml;

        el.ClassList.Add("cupri-tooltip");
        var bubble = open
            ? $"<div class='cupri-tt-bubble' role='tooltip' data-cupri-anchor='{id}' data-cupri-placement='top'>{text}</div>"
            : "";
        el.InnerHtml = $"<div class='cupri-tt-anchor' id='{id}'>{trigger}</div>" + bubble;
    }
}
