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
                            border:1px #e6e9f0; box-shadow:0 10px 28px #00000026; }
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
/// <c>&lt;cupri-tooltip text open&gt;…&lt;/cupri-tooltip&gt;</c> — wraps a trigger and shows an anchored
/// bubble above it. Shows on <b>hover</b> by default (via <c>:hover</c>); <c>open="true"</c> pins it
/// open regardless. The bubble is always in the DOM but hidden until revealed, so no re-expand is
/// needed — a hover just re-resolves styles (like any other <c>:hover</c> rule).
/// </summary>
public sealed class TooltipComponent : ComponentBase
{
    public override string Tag => "cupri-tooltip";
    public override string DefaultCss => """
        .cupri-tooltip { display:inline-block; }
        .cupri-tt-anchor { display:inline-block; }
        .cupri-tt-bubble { position:fixed; background:#1e2430; color:white; padding:6px 10px;
                           border-radius:6px; font-size:12px; z-index:40; display:none; box-shadow:0 4px 14px #00000033; }
        .cupri-tooltip:hover .cupri-tt-bubble { display:inline-block; } /* reveal on hover */
        .cupri-tt-bubble.cupri-tt-open { display:inline-block; }        /* open="true" pins it */
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var text = Str(el, "text");
        var id = NextId();
        var trigger = el.InnerHtml;

        el.ClassList.Add("cupri-tooltip");
        var bubble = text.Length > 0
            ? $"<div class='cupri-tt-bubble{(open ? " cupri-tt-open" : "")}' role='tooltip' " +
              $"data-cupri-anchor='{id}' data-cupri-placement='top'>{text}</div>"
            : "";
        el.InnerHtml = $"<div class='cupri-tt-anchor' id='{id}'>{trigger}</div>" + bubble;
    }
}
