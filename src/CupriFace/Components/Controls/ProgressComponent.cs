using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-progress value max&gt;</c> — a themed progress bar. role=progressbar.
/// </summary>
public sealed class ProgressComponent : ComponentBase
{
    public override string Tag => "cupri-progress";

    public override string DefaultCss => """
        .cupri-progress { display:block; height:12px; background:#e2e6ec; border-radius:6px; }
        .cupri-progress-fill { height:12px; background:#B87333; border-radius:6px; }
        """;

    public override void Expand(IElement el)
    {
        var max = Num(el, "max", 100);
        var value = Num(el, "value", 0);
        var pct = Percent(value, 0, max);

        el.SetAttribute("role", "progressbar");
        el.SetAttribute("aria-valuemin", "0");
        el.SetAttribute("aria-valuemax", F(max));
        el.SetAttribute("aria-valuenow", F(value));
        el.ClassList.Add("cupri-progress");

        el.InnerHtml = $"<div class='cupri-progress-fill' style='width:{F(pct)}%'></div>";
    }
}
