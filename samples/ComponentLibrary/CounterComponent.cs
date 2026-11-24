using System.Globalization;
using AngleSharp.Dom;
using CupriFace;
using CupriFace.Binding;
using CupriFace.Components;

namespace Leaf.Controls;

/// <summary>
/// <c>&lt;leaf-counter label="Guests" value="{{Guests}}" min="0" max="12" step="1"&gt;</c> — a labelled
/// number with − and + buttons that writes the bound value itself.
///
/// <para>The point of this control is that it carries its own BEHAVIOUR. Its markup comes from
/// <c>Assets/Counter.html</c>, its styles from <c>Assets/Counter.css</c>, and its click handling from
/// <see cref="Attach"/> — so an app that adds the library writes no wiring at all.</para>
///
/// <para><b>Where the bound path comes from.</b> Expansion runs after binding, so the control sees
/// <c>value="3"</c>, not <c>{{Guests}}</c>. But the binder marks every attribute that was a pure
/// binding with <c>data-bind-&lt;attr&gt;</c>, so <c>data-bind-value="Guests"</c> is there to read —
/// one attribute in the markup, as with the built-in rating.</para>
/// </summary>
public class CounterComponent : ComponentBase, ICupriBehaviour
{
    public override string Tag => "leaf-counter";

    public override string DefaultCss => LeafAssets.Text(Assets.Counter.Css);

    public override void Expand(IElement el)
    {
        var value = Num(el, "value", 0);
        var min = Num(el, "min", double.NegativeInfinity);
        var max = Num(el, "max", double.PositiveInfinity);
        var step = Num(el, "step", 1);
        var label = Str(el, "label", "Count");

        el.ClassList.Add("leaf-counter");
        el.SetAttribute("role", "group");
        el.SetAttribute("aria-label", label);
        // Moved to the library's own attribute: data-bind-value is also how the engine identifies a
        // TEXT field, and this control is not one — leaving it would let a click try to focus it for
        // typing. The path itself is all the behaviour needs.
        if (el.GetAttribute("data-bind-value") is { Length: > 0 } path)
        {
            el.SetAttribute("data-leaf-path", path);
            el.RemoveAttribute("data-bind-value");
        }
        if (!double.IsInfinity(min)) el.SetAttribute("data-leaf-min", F(min));
        if (!double.IsInfinity(max)) el.SetAttribute("data-leaf-max", F(max));
        if (value <= min) el.SetAttribute("data-leaf-at-min", "");
        if (value >= max) el.SetAttribute("data-leaf-at-max", "");

        el.InnerHtml = LeafAssets.Fill(LeafAssets.Text(Assets.Counter.Html),
            ("label", label), ("value", F(value)), ("step", F(step)));
    }

    /// <summary>
    /// One handler per document, keyed on the attribute the markup carries — so it finds the buttons
    /// again in every rebuilt DOM and never needs re-attaching. Virtual, so an app that subclasses the
    /// counter can change what a step does (log it, confirm it) and still call this.
    /// </summary>
    public virtual void Attach(CupriDocument document) =>
        document.OnAction("data-leaf-step", e => Step(e.Element, e.Value, e.Model));

    /// <summary>Apply one step to the bound value. Returns true when it handled the click, which
    /// tells the document to refresh.</summary>
    protected virtual bool Step(IElement button, string stepText, object? model)
    {
        if (button.Closest("[data-leaf-path]") is not { } root) return false;
        var path = root.GetAttribute("data-leaf-path")!;
        if (!double.TryParse(stepText, NumberStyles.Float, CultureInfo.InvariantCulture, out var step)) return false;

        var current = Convert.ToDouble(BindingEngine.Resolve(model, path) ?? 0, CultureInfo.InvariantCulture);
        var next = current + step;
        if (double.TryParse(root.GetAttribute("data-leaf-min"), NumberStyles.Float, CultureInfo.InvariantCulture, out var min))
            next = Math.Max(min, next);
        if (double.TryParse(root.GetAttribute("data-leaf-max"), NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
            next = Math.Min(max, next);
        // Claimed even at a limit: the press was meant for this control, and letting it bubble on
        // would hand it to whatever the counter sits inside.
        if (next == current) return true;
        return BindingEngine.TrySet(model, path, next);
    }
}
