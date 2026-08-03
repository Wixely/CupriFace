using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-slider min max value&gt;</c> — a themed, accessible slider. Expands to a
/// track/fill/thumb and exposes role=slider with aria-valuemin/max/now.
/// </summary>
public sealed class SliderComponent : ComponentBase
{
    public override string Tag => "cupri-slider";

    public override string DefaultCss => """
        .cupri-slider { display:block; padding:9px 9px; }
        .cupri-slider-track { position:relative; height:6px; background:#d7dbe3; border-radius:3px; }
        .cupri-slider-fill { position:absolute; top:0; left:0; height:6px; background:#B87333; border-radius:3px; }
        .cupri-slider-thumb { position:absolute; top:-7px; width:18px; height:18px; background:white;
                              border:2px #B87333; border-radius:10px; }
        """;

    public override void Expand(IElement el)
    {
        var min = Num(el, "min", 0);
        var max = Num(el, "max", 100);
        var value = Num(el, "value", min);
        var pct = Percent(value, min, max);

        el.SetAttribute("role", "slider");
        el.SetAttribute("aria-valuemin", F(min));
        el.SetAttribute("aria-valuemax", F(max));
        el.SetAttribute("aria-valuenow", F(value));
        el.ClassList.Add("cupri-slider");

        // Thumb is 18px wide → offset by half so its centre lands on the value.
        el.InnerHtml =
            $"<div class='cupri-slider-track'>" +
            $"<div class='cupri-slider-fill' style='width:{F(pct)}%'></div>" +
            $"<div class='cupri-slider-thumb' style='left:calc({F(pct)}% - 9px)'></div>" +
            $"</div>";
    }
}
