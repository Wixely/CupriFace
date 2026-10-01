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
        /* STILL inline-block, and that is not a preference -- it is the only shrink-to-fit display
           this engine has. `display:flex` centres an icon against a label properly, which is what a
           button wants, but a flex container here is BLOCK-level: two buttons became 600px wide and
           stacked instead of sitting side by side. `inline-flex` parses and maps to the same
           DisplayType, and `fit-content` maps to auto, so neither is a way out. Centring an icon
           against a label therefore waits for a real inline-flex (#TBD) -- a button that fills the
           row is a worse bug than one whose icon sits a pixel high.

           The BORDER is on the base rule, transparent, so a variant that shows one is the same size
           as a variant that does not. It used to be declared only on .ghost, which made a ghost
           button 4px wider and taller than a primary one and resized any button that switched.

           box-sizing is border-box on this one control, where it is safe: the button declares no
           width or height of its own, so nothing it says changes meaning -- it only makes a width a
           CALLER sets mean the box they can see. */
        .cupri-button { display:inline-block; box-sizing:border-box; padding:10px 18px;
                        border-radius:8px; border:2px transparent;
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
