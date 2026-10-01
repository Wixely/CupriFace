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
        /* A ROW, CENTRED, WITH A GAP -- not an inline-block.
           inline-block aligned a button's contents on the TEXT BASELINE, so an icon beside a label
           sat high or low depending on the two fonts' metrics rather than on anything the author
           wrote. Measured on <cupri-button><cupri-icon/> Shortcuts</cupri-button>: 104x62 against a
           plain button's 42 tall, with the label laid out at zero width. Every app that got this
           looking right did so by writing `display:flex; align-items:center` over the top -- 42 of
           Bantz's 65 flex rules are that one line, which is a default pointing the wrong way.

           The BORDER is on the base rule, transparent, so a variant that shows one is the same size
           as a variant that does not. It used to be declared only on .ghost, which made a ghost
           button 4px wider and taller than a primary one and resized any button that switched. */
        .cupri-button { display:flex; align-items:center; justify-content:center; gap:8px;
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
