namespace CupriFace.Web;

/// <summary>
/// What the host says when a page reaches for a part its build left out (#325). Off, video and
/// file drops fail quietly by design — a poster that never plays, a drag the browser refuses — and
/// quiet looks exactly like a bug in the page. So the first time it matters, the console names the
/// switch.
///
/// <para>Only the decisions are here, as plain functions of what the document holds: the host's
/// own tests run with every feature on (a feature switch is read once per process), so the "off"
/// path is checked through these. Each fires once per page; the host keeps the flag.</para>
/// </summary>
internal static class WebFeatureNotices
{
    /// <summary>The line for a page holding a <c>&lt;cupri-video&gt;</c> in a build without video,
    /// or null while it holds none. Looks for the expanded control (<c>[data-cupri-video]</c>), so a
    /// video tag the registry left out — CupriDoctor's CF0020 — is not reported twice.</summary>
    public static string? VideoOff(AngleSharp.Dom.IDocument dom) =>
        dom.QuerySelector("[data-cupri-video]") is null ? null :
        "[CupriFace] this page has a <cupri-video>, and this build left video out " +
        "(<CupriWebVideo>false</CupriWebVideo>): it shows its poster and does not play.";

    /// <summary>The line for a file dragged over a document that has a drop handler, in a build
    /// without file drop; null when the document would not have taken it anyway.</summary>
    public static string? FileDropOff(bool documentAcceptsDrops) =>
        !documentAcceptsDrops ? null :
        "[CupriFace] a file was dragged over the page, and its document handles drops, but this build " +
        "left file drop out (<CupriWebFileDrop>false</CupriWebFileDrop>): the browser was told no drop is accepted.";
}
