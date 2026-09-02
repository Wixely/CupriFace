using AngleSharp.Dom;
using CupriFace.Components;

namespace CupriFace.Lottie;

/// <summary>
/// <c>&lt;cupri-lottie src="…" loop autoplay width height&gt;</c> — an After Effects animation.
///
/// <para>The element becomes a live surface, exactly as <c>cupri-video</c> does: it carries
/// <c>data-cupri-surface</c> and the paint path draws whatever frame the player has published. That
/// means <c>object-fit</c>, damage tracking and the render-on-demand cadence all come from the engine
/// rather than being invented here.</para>
///
/// <para>The component only marks the element. Loading the JSON and producing frames is
/// <see cref="LottiePlayer"/>'s job, wired by <c>doc.UseLottie()</c> — components expand markup and
/// have no document to register a source with.</para>
/// </summary>
public sealed class LottieComponent : ComponentBase
{
    public override string Tag => "cupri-lottie";

    /// <summary>The engine key for one animation's surface. Shared by the component and the wiring so
    /// they cannot disagree about which element a player belongs to.</summary>
    internal static string SurfaceKey(string src) => "lottie:" + src;

    public override string DefaultCss => """
        /* No background: a Lottie is drawn over whatever the page put behind it, and a plate would
           show as a rectangle around every animation with a rounded or open design. */
        .cupri-lottie { display:block; }
        """;

    public override void Expand(IElement el)
    {
        var src = Str(el, "src");
        el.ClassList.Add("cupri-lottie");
        el.SetAttribute("role", "img");
        // An animation with no label is decoration as far as a screen reader is concerned, and saying
        // so is better than announcing a filename.
        if (Str(el, "label") is { Length: > 0 } label) el.SetAttribute("aria-label", label);
        else el.SetAttribute("aria-hidden", "true");

        if (src.Length > 0) el.SetAttribute("data-cupri-surface", SurfaceKey(src));

        // Sizing follows the image model: explicit width/height win, otherwise the surface's natural
        // size does. Written as inline style so an app stylesheet still beats it.
        var w = Str(el, "width");
        var h = Str(el, "height");
        if (w.Length > 0 || h.Length > 0)
        {
            var style = el.GetAttribute("style") ?? "";
            if (w.Length > 0) style += $";width:{w}px";
            if (h.Length > 0) style += $";height:{h}px";
            el.SetAttribute("style", style.TrimStart(';'));
        }
    }
}
