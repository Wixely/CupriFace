using AngleSharp.Dom;
using CupriFace.Components;

namespace Leaf.Controls;

/// <summary>
/// <c>&lt;leaf-status state="ok|warn|down"&gt;Label&lt;/leaf-status&gt;</c> — a coloured pill with a
/// dot. Plain CSS and a few lines of markup, and deliberately NOT sealed: it is the control the sample
/// app subclasses, to show that a library control can be extended rather than forked.
/// </summary>
public class StatusComponent : ComponentBase
{
    public override string Tag => "leaf-status";

    public override string DefaultCss => LeafAssets.Text(Assets.Status.Css);

    public override void Expand(IElement el)
    {
        var state = Str(el, "state", "ok").ToLowerInvariant();
        el.ClassList.Add("leaf-status");
        if (state is "warn" or "down") el.ClassList.Add(state);
        el.SetAttribute("role", "status");
        el.InnerHtml = "<span class='leaf-status-dot'></span><span class='leaf-status-label'>" + el.InnerHtml + "</span>";
    }
}
