using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-switch checked&gt;</c> — a themed toggle. role=switch, aria-checked.
/// </summary>
public sealed class SwitchComponent : ComponentBase
{
    public override string Tag => "cupri-switch";

    public override string DefaultCss => """
        .cupri-switch { display:block; position:relative; width:46px; height:26px;
                        background:#cbd2dc; border-radius:13px; }
        .cupri-switch.on { background:#B87333; }
        .cupri-switch-knob { position:absolute; top:3px; width:20px; height:20px;
                             background:white; border-radius:10px; }
        """;

    public override void Expand(IElement el)
    {
        var on = Flag(el, "checked");

        el.SetAttribute("role", "switch");
        el.SetAttribute("aria-checked", on ? "true" : "false");
        el.ClassList.Add("cupri-switch");
        if (on) el.ClassList.Add("on");

        var knobLeft = on ? 23 : 3;
        el.InnerHtml = $"<div class='cupri-switch-knob' style='left:{knobLeft}px'></div>";
    }
}
