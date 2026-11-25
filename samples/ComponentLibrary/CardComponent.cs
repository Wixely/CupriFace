using AngleSharp.Dom;
using CupriFace.Components;

namespace Leaf.Controls;

/// <summary>
/// <c>&lt;leaf-card heading="Booking" subtitle="Table for tonight"&gt;…&lt;/leaf-card&gt;</c> — a
/// titled panel that keeps its children as the body. Markup from <c>Assets/Card.html</c>, styles from
/// <c>Assets/Card.css</c>; no behaviour. The children may be other components (a counter, a built-in
/// button): they expand on the registry's next pass.
/// </summary>
public class CardComponent : ComponentBase
{
    public override string Tag => "leaf-card";

    public override string DefaultCss => LeafAssets.Text(Assets.Card.Css);

    public override void Expand(IElement el)
    {
        el.ClassList.Add("leaf-card");
        el.SetAttribute("role", "group");
        var heading = Str(el, "heading");
        if (heading.Length > 0) el.SetAttribute("aria-label", heading);
        var template = LeafAssets.Fill(LeafAssets.Text(Assets.Card.Html),
            ("heading", heading), ("subtitle", Str(el, "subtitle")));
        el.InnerHtml = LeafAssets.FillRaw(template, "body", el.InnerHtml);
    }
}
