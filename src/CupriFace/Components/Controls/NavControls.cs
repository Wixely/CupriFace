using System.Linq;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-tabs value="{{Tab}}"&gt;&lt;cupri-tab id="a" label="A"&gt;…&lt;/cupri-tab&gt;…&lt;/cupri-tabs&gt;</c>
/// — a tab strip plus the active tab's panel. Clicking a header writes the tab's id to the
/// bound value (via the generic <c>data-set-*</c> click), which re-renders the active panel.
/// </summary>
public sealed class TabsComponent : ComponentBase
{
    public override string Tag => "cupri-tabs";
    public override string DefaultCss => """
        .cupri-tabs { display:block; }
        .cupri-tablist { display:flex; gap:4px; border-bottom:2px var(--cupri-border, #e6e9f0); }
        .cupri-tab-h { padding:9px 16px; color:var(--cupri-muted, #667085); font-weight:bold; font-size:14px;
                       border-bottom:2px transparent; margin-bottom:-2px; }
        .cupri-tab-h[data-hover] { color:var(--cupri-text, #1e2430); }
        .cupri-tab-h.active { color:#B87333; border-bottom:2px #B87333; }
        .cupri-tabpanel { padding:16px 2px; color:var(--cupri-text, #1e2430); }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var tabs = el.Children.Where(c => c.LocalName == "cupri-tab").ToList();
        var active = Str(el, "value");
        if (active.Length == 0 && tabs.Count > 0) active = Str(tabs[0], "id");

        var strip = new System.Text.StringBuilder("<div class='cupri-tablist' role='tablist'>");
        var panel = "";
        foreach (var tab in tabs)
        {
            var id = Str(tab, "id");
            var isActive = id == active;
            strip.Append($"<div class='cupri-tab-h{(isActive ? " active" : "")}' role='tab' aria-selected='{(isActive ? "true" : "false")}'")
                 .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{Attr(id)}'" : "")
                 .Append($">{Str(tab, "label", id)}</div>");
            if (isActive) panel = tab.InnerHtml;
        }
        strip.Append("</div>");

        el.ClassList.Add("cupri-tabs");
        el.InnerHtml = strip + $"<div class='cupri-tabpanel' role='tabpanel'>{panel}</div>";
    }

    private static string Attr(string s) => s.Replace("'", "&#39;");
}

/// <summary>
/// <c>&lt;cupri-accordion&gt;</c> — a container for <c>&lt;cupri-accordion-item&gt;</c>s; each item
/// keeps its own bound open state.
/// </summary>
public sealed class AccordionComponent : ComponentBase
{
    public override string Tag => "cupri-accordion";
    public override string DefaultCss => """
        .cupri-accordion { display:block; border:1px var(--cupri-border, #e6e9f0); border-radius:10px; }
        """;

    public override void Expand(IElement el) => el.ClassList.Add("cupri-accordion");
}

/// <summary>
/// <c>&lt;cupri-accordion-item label="…" open="{{Flag}}"&gt;…&lt;/cupri-accordion-item&gt;</c> — a
/// collapsible section. The header toggles the bound open flag (reuses the overlay toggle).
/// </summary>
public sealed class AccordionItemComponent : ComponentBase
{
    public override string Tag => "cupri-accordion-item";
    public override string DefaultCss => """
        .cupri-acc-item { display:block; border-bottom:1px var(--cupri-border, #e6e9f0); }
        .cupri-acc-header { display:flex; align-items:center; justify-content:space-between; cursor:pointer;
                            padding:13px 16px; color:var(--cupri-text, #1e2430); font-weight:bold; font-size:15px; }
        .cupri-acc-header[data-hover] { background:var(--cupri-hover, #f0f2f5); }
        /* Height animates 0 ↔ auto so the panel slides. The panel is always present (only its height
           changes), overflow:hidden clips the sliding content, and the inner wrapper carries the padding
           so a collapsed panel is genuinely 0-height. */
        .cupri-acc-panel { height:0; overflow:hidden; transition:height 0.28s ease; }
        .cupri-acc-panel.open { height:auto; }
        .cupri-acc-inner { padding:2px 16px 15px 16px; color:var(--cupri-muted, #4a5262); font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var body = el.InnerHtml;
        el.ClassList.Add("cupri-acc-item");
        var chevron = IconMarkup(open ? "chevron-up" : "chevron-down", 18);
        el.InnerHtml =
            $"<div class='cupri-acc-header' role='button' aria-expanded='{(open ? "true" : "false")}' data-cupri-toggle=\"1\">" +
                $"<span>{Str(el, "label")}</span>{chevron}</div>" +
            $"<div class='cupri-acc-panel{(open ? " open" : "")}' aria-hidden='{(open ? "false" : "true")}'>" +
                $"<div class='cupri-acc-inner'>{body}</div></div>";
    }
}

/// <summary><c>&lt;cupri-tree&gt;</c> — root container for a hierarchy of tree items.</summary>
public sealed class TreeComponent : ComponentBase
{
    public override string Tag => "cupri-tree";
    public override string DefaultCss => """
        .cupri-tree { display:block; color:var(--cupri-text, #1e2430); font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "tree");
        el.ClassList.Add("cupri-tree");
    }
}

/// <summary>
/// <c>&lt;cupri-tree-item label="…" open="{{Flag}}"&gt;…nested items…&lt;/cupri-tree-item&gt;</c> — a row
/// that toggles its bound open flag; nested items are indented and shown only when open.
/// Leaf items (no children) render without a toggle chevron.
/// </summary>
public sealed class TreeItemComponent : ComponentBase
{
    public override string Tag => "cupri-tree-item";
    public override string DefaultCss => """
        .cupri-tree-item { display:block; }
        .cupri-tree-row { display:flex; align-items:center; gap:6px; padding:5px 8px; border-radius:6px; }
        .cupri-tree-row[data-hover] { background:var(--cupri-hover, #f0f2f5); }
        .cupri-tree-twist { width:18px; height:18px; color:var(--cupri-muted, #667085); }
        .cupri-tree-children { padding-left:18px; }
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var body = el.InnerHtml.Trim();
        var hasChildren = body.Contains("cupri-tree-item", System.StringComparison.OrdinalIgnoreCase);

        el.SetAttribute("role", "treeitem");
        if (hasChildren) el.SetAttribute("aria-expanded", open ? "true" : "false");
        el.ClassList.Add("cupri-tree-item");

        var twist = hasChildren
            ? $"<div class='cupri-tree-twist' data-cupri-toggle=\"1\">{IconMarkup(open ? "chevron-down" : "chevron-right", 18)}</div>"
            : "<div class='cupri-tree-twist'></div>";
        el.InnerHtml =
            $"<div class='cupri-tree-row'>{twist}<span>{Str(el, "label")}</span></div>" +
            (hasChildren && open ? $"<div class='cupri-tree-children' role='group'>{body}</div>" : "");
    }
}
