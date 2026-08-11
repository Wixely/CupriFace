using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-video src="clip.webm" fit="contain|cover|fill|none" poster="p.png" label="…"
/// autoplay muted loop controls&gt;</c> — a video element. The frames arrive through the live
/// surface lane (<c>data-cupri-surface</c>) from whichever <see cref="Media.IVideoBackend"/> the
/// HOST registered (<c>doc.UseVideo</c>): the desktop WebM decoder or the browser's own decoder
/// on the web host. Until the first frame (or with no backend at all) the <c>poster</c> image
/// shows. The `autoplay` attribute is honored only together with `muted` — the web's own rule,
/// applied on every host so one app behaves identically everywhere.
///
/// Size like an image: CSS width/height, aspect preserved when only one is given, intrinsic
/// video size otherwise. Full-window video is just <c>width:100%; height:100%</c> +
/// <c>fit="cover"</c>; the ⛶ control requests OS fullscreen via <c>data-window-command</c>.
/// </summary>
public sealed class VideoComponent : ComponentBase
{
    public override string Tag => "cupri-video";
    public override string DefaultCss => """
        .cupri-video { display:block; position:relative; overflow:hidden; background:#0b0d10; }
        .cupri-video-bar { position:absolute; left:0; right:0; bottom:0; display:flex; gap:6px; align-items:center;
                           padding:6px 10px; background:rgba(10,12,16,0.55); }
        .cupri-video-btn { display:inline-flex; align-items:center; justify-content:center;
                           width:32px; height:32px; border-radius:6px; color:#ffffff; }
        .cupri-video-btn:hover { background:rgba(255,255,255,0.18); }
        .cupri-video-title { color:#e6e9ef; flex:1; }
        """;

    public override void Expand(IElement el)
    {
        var src = Str(el, "src");
        var poster = Str(el, "poster");
        var label = Str(el, "label", src);
        var esc = System.Net.WebUtility.HtmlEncode(label);

        // The element ITSELF is the picture — surface + poster + object-fit live on it, so it
        // sizes exactly like <cupri-image> (CSS width/height, aspect kept, else intrinsic video
        // size). The controls bar overlays its bottom edge; a click anywhere else toggles.
        el.ClassList.Add("cupri-video");
        el.SetAttribute("data-cupri-video", src);
        el.SetAttribute("data-cupri-surface", "video:" + src);
        if (poster.Length > 0) el.SetAttribute("data-cupri-image", poster);
        el.SetAttribute("data-object-fit", Str(el, "fit", "contain"));
        el.SetAttribute("data-video-cmd", "toggle");
        el.SetAttribute("role", "img");
        el.SetAttribute("aria-label", label);
        // Policy flags for the document's video wiring (SyncVideos) — attribute presence only.
        if (Flag(el, "autoplay")) el.SetAttribute("data-video-autoplay", "");
        if (Flag(el, "muted")) el.SetAttribute("data-video-muted", "");
        if (Flag(el, "loop")) el.SetAttribute("data-video-loop", "");

        el.InnerHtml = !Flag(el, "controls") ? "" : $"""
            <div class='cupri-video-bar'>
              <div class='cupri-video-btn' role='button' aria-label='Play' data-video-role='toggle' data-video-cmd='toggle'>{IconMarkup("play", 18)}</div>
              <div class='cupri-video-btn' role='button' aria-label='Mute' data-video-role='mute' data-video-cmd='mute'>{IconMarkup("volume", 18)}</div>
              <span class='cupri-video-title'>{esc}</span>
              <div class='cupri-video-btn' role='button' aria-label='Fullscreen' data-window-command='toggle-fullscreen'>{IconMarkup("fullscreen", 18)}</div>
            </div>
            """;
    }

    /// <summary>Reflect live player state into a freshly rebuilt element's controls (the DOM is
    /// re-parsed every rebuild, so the document calls this for each <c>&lt;cupri-video&gt;</c> it
    /// wired). Glyphs + accessible labels flip together, so Narrator and the pixels agree.</summary>
    internal static void SyncControls(IElement el, Media.IVideoPlayer player)
    {
        if (el.QuerySelector("[data-video-role='toggle']") is { } toggle)
        {
            toggle.InnerHtml = IconMarkup(player.Playing ? "pause" : "play", 18);
            toggle.SetAttribute("aria-label", player.Playing ? "Pause" : "Play");
        }
        if (el.QuerySelector("[data-video-role='mute']") is { } mute)
        {
            mute.InnerHtml = IconMarkup(player.Muted ? "volume-off" : "volume", 18);
            mute.SetAttribute("aria-label", player.Muted ? "Unmute" : "Mute");
        }
    }
}
