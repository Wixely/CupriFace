using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-popover label="…" open="{{Flag}}"&gt;…rich content…&lt;/cupri-popover&gt;</c> — a click
/// trigger that reveals an anchored panel (top layer) below it. Like a menu, but the popup
/// holds arbitrary content rather than menu items.
/// </summary>
public sealed class PopoverComponent : ComponentBase
{
    public override string Tag => "cupri-popover";
    public override string DefaultCss => """
        .cupri-popover { display:inline-block; }
        .cupri-pop-trigger { display:inline-flex; align-items:center; gap:6px; padding:9px 14px;
                             background:#eef1f5; color:#1e2430; border-radius:8px; font-weight:bold; font-size:14px; }
        .cupri-pop-panel { position:fixed; background:var(--cupri-surface, white); border-radius:10px; padding:14px;
                           z-index:35; border:1px var(--cupri-border, #e6e9f0); max-width:280px;
                           color:var(--cupri-text, #1e2430); font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var id = NextId();
        var content = el.InnerHtml;
        el.ClassList.Add("cupri-popover");
        var trigger = $"<div class='cupri-pop-trigger' id='{id}' data-cupri-toggle=\"{id}\">{Str(el, "label", "More")}"
                      + IconMarkup("chevron-down", 16) + "</div>";
        var panel = open
            ? $"<div class='cupri-pop-panel' role='dialog' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>{content}</div>"
            : "";
        el.InnerHtml = trigger + panel;
    }
}

/// <summary>
/// <c>&lt;cupri-drawer open="{{Flag}}" side="right"&gt;…&lt;/cupri-drawer&gt;</c> — a panel that slides in
/// from an edge over a dismissing backdrop (top layer). <c>side</c> is left or right.
/// </summary>
public sealed class DrawerComponent : ComponentBase
{
    public override string Tag => "cupri-drawer";
    public override string DefaultCss => """
        .cupri-drawer { display:block; }
        .cupri-drawer-panel { position:fixed; top:0; height:100%; width:300px; background:var(--cupri-surface, white);
                              padding:22px; z-index:15; color:var(--cupri-text, #1e2430); }
        .cupri-drawer-panel.right { right:0; }
        .cupri-drawer-panel.left  { left:0; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-drawer");
        if (!Flag(el, "open")) { el.InnerHtml = ""; el.SetAttribute("style", "display:none"); return; }

        el.SetAttribute("role", "dialog");
        el.SetAttribute("aria-modal", "true");
        // Edge is a `left`/`right` state class (see DefaultCss) so it stays overridable.
        var side = Str(el, "side", "right") == "left" ? "left" : "right";
        var content = el.InnerHtml;
        el.InnerHtml =
            "<div class='cupri-backdrop' data-cupri-dismiss=\"true\"></div>" +
            $"<div class='cupri-drawer-panel {side}' data-focus-scope>{content}</div>";
    }
}

/// <summary>
/// <c>&lt;cupri-select value="{{Size}}" open="{{Open}}"&gt;&lt;cupri-option value="s"&gt;Small&lt;/cupri-option&gt;…</c>
/// — a dropdown bound to a value. The trigger shows the selected option's label; the anchored
/// list writes the picked value (generic <c>data-set-*</c>) and closes itself.
/// </summary>
public sealed class SelectComponent : ComponentBase
{
    public override string Tag => "cupri-select";
    public override string DefaultCss => """
        .cupri-select { display:inline-block; }
        .cupri-select-trigger { display:inline-flex; align-items:center; justify-content:space-between; gap:10px;
                                min-width:180px; padding:9px 12px; background:var(--cupri-surface, white);
                                border:2px var(--cupri-border, #cbd2dc); border-radius:8px;
                                color:var(--cupri-text, #1e2430); font-size:15px; }
        .cupri-select-trigger[data-hover] { border:2px #98a2b3; }
        .cupri-select-list { position:fixed; background:var(--cupri-surface, white); border-radius:10px; padding:6px;
                             z-index:30; border:1px var(--cupri-border, #e6e9f0); min-width:180px; }
        .cupri-option-row { display:flex; align-items:center; justify-content:space-between; gap:8px;
                            padding:9px 12px; border-radius:6px; color:var(--cupri-text, #1e2430); font-size:14px; }
        .cupri-option-row[data-hover] { background:var(--cupri-hover, #eef1f5); }
        .cupri-option-row.selected { color:#B87333; font-weight:bold; }
        .cupri-option-check { width:16px; height:16px; color:#B87333; }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var options = el.Children.Where(c => c.LocalName == "cupri-option")
                        .Select(c => (Value: Str(c, "value"), Label: c.TextContent.Trim())).ToList();
        if (value.Length == 0 && options.Count > 0) value = options[0].Value;
        var selected = options.FirstOrDefault(o => o.Value == value);
        var open = Flag(el, "open");
        var id = NextId();

        var list = new StringBuilder();
        if (open)
        {
            list.Append($"<div class='cupri-select-list' role='listbox' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>");
            foreach (var (ov, ol) in options)
            {
                var isSel = ov == value;
                list.Append($"<div class='cupri-option-row{(isSel ? " selected" : "")}' role='option' aria-selected='{(isSel ? "true" : "false")}'")
                    .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{Attr(ov)}'" : "")
                    .Append($"><span>{ol}</span>{(isSel ? IconMarkup("check", 16, "cupri-option-check") : "")}</div>");
            }
            list.Append("</div>");
        }

        el.SetAttribute("role", "combobox");
        el.SetAttribute("aria-expanded", open ? "true" : "false");
        el.ClassList.Add("cupri-select");
        el.InnerHtml =
            $"<div class='cupri-select-trigger' id='{id}' data-cupri-toggle=\"{id}\">" +
                $"<span>{(selected.Label ?? "Select…")}</span>{IconMarkup("chevron-down", 16)}</div>" +
            list;
    }

    private static string Attr(string s) => s.Replace("'", "&#39;");
}
