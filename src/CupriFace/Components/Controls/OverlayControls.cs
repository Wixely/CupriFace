using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-dialog open&gt;</c> — a modal dialog: a full-viewport backdrop plus a centred
/// panel, both lifted to the top layer (position:fixed). Clicking the backdrop dismisses it.
/// </summary>
public sealed class DialogComponent : ComponentBase
{
    public override string Tag => "cupri-dialog";
    public override string DefaultCss => """
        .cupri-dialog { display:block; }
        .cupri-backdrop { position:fixed; top:0; left:0; width:100%; height:100%; background:#00000099; }
        .cupri-dialog-panel { position:fixed; width:360px; background:white; border-radius:14px;
                              padding:24px; z-index:10; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-dialog");
        if (!Flag(el, "open")) { el.InnerHtml = ""; el.SetAttribute("style", "display:none"); return; }

        el.SetAttribute("role", "dialog");
        el.SetAttribute("aria-modal", "true");
        var content = el.InnerHtml;
        el.InnerHtml =
            "<div class='cupri-backdrop' data-cupri-dismiss=\"true\"></div>" +
            $"<div class='cupri-dialog-panel' data-focus-scope>{content}</div>";
    }
}

/// <summary>
/// <c>&lt;cupri-toast&gt;</c> — a transient message pinned to the bottom-right (top layer).
/// </summary>
public sealed class ToastComponent : ComponentBase
{
    public override string Tag => "cupri-toast";
    public override string DefaultCss => """
        .cupri-toast { position:fixed; bottom:24px; right:24px; max-width:320px; z-index:20;
                       background:#1e2430; color:white; padding:14px 18px; border-radius:10px; font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "status");
        el.ClassList.Add("cupri-toast");
    }
}
