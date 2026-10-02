using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-button variant&gt;</c> — a themed button that keeps its child label. role=button.
/// variant="primary" (default) or "ghost".
/// </summary>
public sealed class ButtonComponent : ComponentBase
{
    public override string Tag => "cupri-button";

    public override string DefaultCss => """
        /* inline-flex: a row that shrinks to its content.
           Both halves matter and until the engine had inline-flex you could only have one. As
           inline-block a button shrank correctly but aligned its contents on the TEXT BASELINE, so
           an icon beside a label sat high or low depending on two fonts' metrics -- measured at
           104x62 against a plain button's 42, with the label laid out at zero width. As block-level
           `display:flex` the contents centred but the button filled its row, so two buttons stacked
           instead of sitting side by side, which is a worse bug than the one it fixed.

           The BORDER is on the base rule, transparent, so a variant that shows one is the same size
           as a variant that does not -- it used to be declared only on .ghost, which made a ghost
           button 4px larger in both axes and resized any button that switched variant.

           box-sizing is border-box on this one control, where it is safe: the button declares no
           width or height of its own, so nothing it says changes meaning -- it only makes a width a
           CALLER sets mean the box they can see. */
        .cupri-button { display:inline-flex; align-items:center; justify-content:center; gap:8px;
                        box-sizing:border-box; padding:10px 18px; border-radius:8px;
                        border:2px transparent;
                        background:var(--cupri-accent,#B87333); color:white; font-weight:bold; font-size:15px; }
        .cupri-button.ghost { background:transparent; color:var(--cupri-accent,#B87333);
                              border:2px var(--cupri-accent,#B87333); }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "button");
        el.ClassList.Add("cupri-button");
        if (Str(el, "variant") == "ghost") el.ClassList.Add("ghost");
        // Child label content is preserved as-is.
    }
}
