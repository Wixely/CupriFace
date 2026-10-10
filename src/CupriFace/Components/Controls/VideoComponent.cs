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
/// <c>fit="cover"</c>. The ⛶ control fullscreens the VIDEO, the way the web does it: the
/// element expands to cover the whole viewport in the top layer (letterboxed on black, controls
/// still overlaid) AND the window goes OS-fullscreen via <c>WindowCommandRequested</c> — so the
/// video fills the screen, not just the window. Escape (or ⛶ again) undoes both.
/// </summary>
public sealed class VideoComponent : ComponentBase
{
    private static long _nextChapterAnchorId;

    public override string Tag => "cupri-video";
    public override string DefaultCss => """
        .cupri-video { display:block; position:relative; overflow:hidden; background:#0b0d10; }
        .cupri-video-bar { position:absolute; left:0; right:0; bottom:0; display:flex; gap:6px; align-items:center;
                           padding:6px 10px; background:rgba(10,12,16,0.55); }
        .cupri-video-btn { display:inline-flex; align-items:center; justify-content:center;
                           width:32px; height:32px; border-radius:6px; color:#ffffff; }
        .cupri-video-btn:hover { background:rgba(255,255,255,0.18); }
        .cupri-video-btn.disabled { opacity:0.35; }
        .cupri-video-btn.disabled:hover { background:transparent; }
        .cupri-video-time { color:#e6e9ef; font-size:12px; flex:none; }
        .cupri-video-seek { flex:1; padding:8px 2px; cursor:pointer; }
        .cupri-video-seek.disabled { opacity:0.35; cursor:not-allowed; }
        .cupri-video-seek-track { position:relative; height:4px; background:rgba(255,255,255,0.28); border-radius:2px; }
        .cupri-video-seek-fill { position:absolute; top:0; left:0; height:4px; background:var(--cupri-accent,#B87333); border-radius:2px; }
        .cupri-video-chapter-markers { position:absolute; left:0; right:0; top:0; bottom:0; }
        .cupri-video-chapter-segment { position:absolute; top:-8px; height:20px; }
        .cupri-video-chapter-marker { position:absolute; top:-2px; width:2px; height:8px; background:#0b0d10; }
        .cupri-video-chapter-tooltip { display:none; position:fixed; z-index:40;
                                       width:max-content; max-width:70vw; padding:6px 10px;
                                       border-radius:6px; background:#161b24; color:#f4f6f9; font-size:12px;
                                       line-height:1.3; white-space:normal; overflow-wrap:anywhere; pointer-events:none;
                                       box-shadow:0 3px 12px #00000080; }
        .cupri-video-chapter-segment:hover .cupri-video-chapter-tooltip { display:block; }
        .cupri-video-seek-thumb { position:absolute; top:-4px; width:12px; height:12px; background:white; border-radius:6px;
                                  box-shadow:0 1px 4px #00000059; }
        .cupri-video-fs { z-index:90; background:#000; }
        .cupri-video .cupri-ctx-menu, .cupri-video .cupri-submenu { background:#161b24; color:#f4f6f9; }
        .cupri-video .cupri-submenu { width:360px; max-width:70vw; }
        .cupri-video .cupri-menu-item { color:#f4f6f9; }
        .cupri-video .cupri-menu-item:hover { background:#273244; color:#ffffff; }
        .cupri-video .cupri-video-track-menu-label { overflow-wrap:anywhere; }
        .cupri-video .cupri-video-track-selected { color:var(--cupri-accent,#B87333); }
        """;

    public override void Expand(IElement el)
    {
        var src = Str(el, "src");
        var poster = Str(el, "poster");
        var label = Str(el, "label", src);

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
        var tracks = Flag(el, "tracks");
        if (tracks)
        {
            el.SetAttribute("data-video-tracks-enabled", "");
            el.SetAttribute("data-cupri-ctx-host", "");
        }
        var chapters = Flag(el, "chapters");
        if (chapters) el.SetAttribute("data-video-chapters-enabled", "");

        // The bar: transport, current time, the seek slider (a real role=slider — pointer scrub,
        // arrow keys, AT SetValue all route to the player), duration, fullscreen. The clip's label
        // lives on the ELEMENT's aria-label — the bar has no room for a title once a seek bar
        // exists, which is also every real player's layout.
        var controls = !Flag(el, "controls") ? "" : $"""
            <div class='cupri-video-bar' data-surface-overlay>
              <div class='cupri-video-btn' role='button' aria-label='Play' data-video-role='toggle' data-video-cmd='toggle'>{IconMarkup("play", 18)}</div>
              <div class='cupri-video-btn' role='button' aria-label='Mute' data-video-role='mute' data-video-cmd='mute'>{IconMarkup("volume", 18)}</div>
              <span class='cupri-video-time' data-video-role='time'>0:00</span>
              <div class='cupri-video-seek' role='slider' tabindex='0' aria-label='Seek' data-video-role='seek'
                   aria-valuemin='0' aria-valuemax='0' aria-valuenow='0'>
                <div class='cupri-video-seek-track'>
                  <div class='cupri-video-seek-fill' style='width:0%'></div>
                  {(chapters ? "<div class='cupri-video-chapter-markers' data-video-chapter-markers aria-hidden='true'></div>" : "")}
                  <div class='cupri-video-seek-thumb' style='left:0%'></div>
                </div>
              </div>
              <span class='cupri-video-time' data-video-role='duration'>0:00</span>
              <div class='cupri-video-btn' role='button' aria-label='Fullscreen' data-video-role='fullscreen' data-video-cmd='fullscreen'>{IconMarkup("fullscreen", 18)}</div>
            </div>
            """;
        el.InnerHtml = controls + (tracks ? TrackContextMenuMarkup() : "");
    }

    internal static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>No backend on this host (or the source failed to open): the transport controls
    /// get the standard disabled treatment — dimmed, not-allowed cursor, announced by AT —
    /// instead of looking clickable and doing nothing. Fullscreen stays live: it needs no
    /// decoder, only the window.</summary>
    internal static void MarkInert(IElement el)
    {
        foreach (var role in new[] { "toggle", "mute", "seek" })
            if (el.QuerySelector($"[data-video-role='{role}']") is { } control)
            {
                control.ClassList.Add("disabled");
                control.SetAttribute("aria-disabled", "true");
            }
    }

    /// <summary>Make this element THE fullscreen video (the document calls it during each rebuild
    /// while its src is the fullscreen one — the fresh DOM starts in the normal state). The
    /// geometry goes on the INLINE style so it beats the author's own inline width/height/radius
    /// (`style="width:320px"` would defeat a class); appended last, so it wins within the
    /// attribute too. position:fixed puts it in the top layer — painted above everything,
    /// hit-tested first — and the class brings z-index + the black letterbox background.</summary>
    internal static void ApplyFullscreenState(IElement el)
    {
        el.ClassList.Add("cupri-video-fs");
        // transition:none — entering fullscreen SNAPS, like the browser. An author's own
        // width/height transition (the resize demo has one) must not tween into fullscreen.
        el.SetAttribute("style", (el.GetAttribute("style") ?? "")
            + ";position:fixed;left:0;top:0;width:100%;height:100%;border-radius:0;transition:none");
        if (el.QuerySelector("[data-video-role='fullscreen']") is { } button)
        {
            button.InnerHtml = IconMarkup("fullscreen-exit", 18);
            button.SetAttribute("aria-label", "Exit fullscreen");
        }
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

        // Seek bar + clocks: position/duration as text, fill/thumb as inline percentages, and the
        // slider's ARIA range so AT reads and SETS the same values the pixels show.
        var duration = player.Duration;
        var position = Math.Clamp(player.Position, 0, duration > 0 ? duration : double.MaxValue);
        if (el.QuerySelector("[data-video-role='time']") is { } time) time.TextContent = FormatTime(position);
        if (el.QuerySelector("[data-video-role='duration']") is { } total) total.TextContent = FormatTime(duration);
        if (el.QuerySelector("[data-video-role='seek']") is { } seek)
        {
            seek.SetAttribute("aria-valuemin", "0");
            seek.SetAttribute("aria-valuemax", Math.Round(duration, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            seek.SetAttribute("aria-valuenow", Math.Round(position, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            seek.SetAttribute("aria-valuetext", $"{FormatTime(position)} of {FormatTime(duration)}");
            var pct = duration > 0 ? Math.Clamp(position / duration * 100, 0, 100) : 0;
            var pctText = pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (seek.QuerySelector(".cupri-video-seek-fill") is { } fill) fill.SetAttribute("style", $"width:{pctText}%");
            if (seek.QuerySelector(".cupri-video-seek-thumb") is { } thumb)
                thumb.SetAttribute("style", $"left:{pctText}%;margin-left:-6px"); // centre the 12px thumb
        }
    }

    internal static void SyncTracks(IElement el, string source, Media.IVideoTrackSelector? tracks)
    {
        if (!el.HasAttribute("data-video-tracks-enabled")) return;
        FillTrackList(el.QuerySelector("[data-video-track-list='audio']"), source, "audio",
            tracks?.AudioTracks ?? [], tracks?.SelectedAudioTrack);
        FillTrackList(el.QuerySelector("[data-video-track-list='subtitle']"), source, "subtitle",
            tracks?.SubtitleTracks ?? [], tracks?.SelectedSubtitleTrack);
    }

    internal static void SyncChapters(IElement el, Media.IVideoChapterProvider? provider, double duration)
    {
        if (!el.HasAttribute("data-video-chapters-enabled") ||
            el.QuerySelector("[data-video-chapter-markers]") is not { } markers)
            return;
        if (provider is null || provider.Chapters.Count == 0 || duration <= 0)
        {
            markers.InnerHtml = "";
            return;
        }

        var chapters = provider.Chapters
            .Where(chapter => chapter.StartSeconds >= 0 && chapter.StartSeconds < duration)
            .OrderBy(chapter => chapter.StartSeconds)
            .ToArray();
        var anchorSet = System.Threading.Interlocked.Increment(ref _nextChapterAnchorId);
        markers.InnerHtml = string.Join("", chapters.Select((chapter, index) =>
        {
            var start = Math.Clamp(chapter.StartSeconds / duration * 100, 0, 100);
            var nextStart = index + 1 < chapters.Length
                ? Math.Clamp(chapters[index + 1].StartSeconds / duration * 100, start, 100)
                : 100;
            var left = start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var width = Math.Max(0, nextStart - start)
                .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var marker = index == 0
                ? ""
                : $"<span class='cupri-video-chapter-marker' style='left:{left}%'></span>";
            var anchorId = $"cupri-video-chapter-{anchorSet}-{index}";
            return $"<span class='cupri-video-chapter-segment' id='{anchorId}' style='left:{left}%;width:{width}%' " +
                   $"data-video-chapter-title='{Escape(chapter.Label)}'>" +
                   $"<span class='cupri-video-chapter-tooltip' role='tooltip' data-surface-occluder data-cupri-anchor='{anchorId}' " +
                   $"data-cupri-placement='top'>{Escape(chapter.Label)}</span></span>{marker}";
        }));
    }

    private static void FillTrackList(
        IElement? list,
        string source,
        string kind,
        IReadOnlyList<Media.VideoTrack> tracks,
        int? selected)
    {
        if (list is null) return;
        if (tracks.Count == 0)
        {
            list.InnerHtml = "<div class='cupri-menu-item disabled' role='menuitem' aria-disabled='true' data-video-track-disabled>No selectable tracks</div>";
            return;
        }

        list.InnerHtml = string.Join("", tracks.Select(track =>
        {
            var chosen = track.Id == selected;
            var css = chosen ? "cupri-menu-item cupri-video-track-selected" : "cupri-menu-item";
            var check = chosen ? IconMarkup("check", 16) : "";
            return $"<div class='{css}' role='menuitem' data-video-track-source='{Escape(source)}' " +
                   $"data-video-track-kind='{kind}' data-video-track-id='{track.Id}'><span class='cupri-menu-label cupri-video-track-menu-label'>{Escape(track.Label)}</span>{check}</div>";
        }));
    }

    private static string TrackContextMenuMarkup() => $"""
        <div class='cupri-ctx-menu' role='menu' data-cupri-ctx-menu data-focus-scope data-surface-occluder>
          <div class='cupri-menu-item cupri-menu-parent' role='menuitem' aria-haspopup='menu'>
            <span class='cupri-menu-label'>Audio</span>{IconMarkup("chevron-right", 16)}
            <div class='cupri-submenu' role='menu' data-surface-occluder data-video-track-list='audio'></div>
          </div>
          <div class='cupri-menu-item cupri-menu-parent' role='menuitem' aria-haspopup='menu'>
            <span class='cupri-menu-label'>Subtitles</span>{IconMarkup("chevron-right", 16)}
            <div class='cupri-submenu' role='menu' data-surface-occluder data-video-track-list='subtitle'></div>
          </div>
        </div>
        """;

    internal static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}

/// <summary>
/// Opt-in companion for <c>&lt;cupri-video tracks&gt;</c>. It renders the same audio and subtitle
/// choices as an accessible, collapsible form below a player. Omit this element (and the video's
/// <c>tracks</c> attribute) when an application must not expose track switching to users.
/// </summary>
public sealed class VideoTracksComponent : ComponentBase
{
    public override string Tag => "cupri-video-tracks";
    public override string DefaultCss => """
        .cupri-video-tracks { display:block; }
        .cupri-video-tracks-toggle { display:inline-flex; align-items:center; gap:8px; padding:9px 14px;
                                     border:1px var(--cupri-border,#d8dde6); border-radius:8px;
                                     color:var(--cupri-text,#1e2430); font-weight:bold; }
        .cupri-video-tracks-status { display:block; margin-top:8px; color:var(--cupri-muted,#687184); font-size:12px; }
        .cupri-video-tracks-panel { display:none; margin-top:10px; padding:14px; gap:18px;
                                    background:var(--cupri-surface,#ffffff); border:1px var(--cupri-border,#d8dde6); border-radius:10px; }
        .cupri-video-tracks-column { flex:1; min-width:0; }
        .cupri-video-tracks-column > strong { display:block; margin-bottom:8px; color:var(--cupri-muted,#687184);
                                              font-size:11px; letter-spacing:1px; }
        .cupri-video-tracks-table { display:flex; flex-direction:column; width:100%; overflow:hidden;
                                    border:1px var(--cupri-border,#d8dde6); border-radius:8px; }
        .cupri-video-tracks-row { display:flex; align-items:center; width:100%; border-bottom:1px var(--cupri-border,#d8dde6); }
        .cupri-video-tracks-row:last-child { border-bottom:0; }
        .cupri-video-tracks-header { color:var(--cupri-muted,#687184); font-size:10px; font-weight:bold; letter-spacing:1px; }
        .cupri-video-tracks-cell { flex:1; min-width:0; padding:9px 12px; overflow:hidden; }
        .cupri-video-tracks-name { overflow-wrap:anywhere; }
        .cupri-video-tracks-state { flex:none; width:82px; text-align:center; }
        .cupri-video-track-choice { color:var(--cupri-text,#1e2430); }
        .cupri-video-track-choice:hover, .cupri-video-track-choice.cupri-video-track-selected { background:var(--cupri-hover,#eef1f5); }
        .cupri-video-track-choice.cupri-video-track-selected { color:var(--cupri-accent,#B87333); }
        @media (max-width: 720px) {
          .cupri-video-tracks-panel { flex-direction:column; }
          .cupri-video-tracks-column { width:100%; }
        }
        """;

    public override void Expand(IElement el)
    {
        var source = Str(el, "src");
        var label = Str(el, "label", "Audio / Video");
        el.ClassList.Add("cupri-video-tracks");
        el.SetAttribute("data-video-track-controls", source);
        el.InnerHtml = $"""
            <div class='cupri-video-tracks-toggle' role='button' tabindex='0' data-video-track-toggle='{VideoComponent.Escape(source)}'>
              {IconMarkup("settings", 18)}<span>{VideoComponent.Escape(label)}</span>
            </div>
            <span class='cupri-video-tracks-status' data-video-track-status>Audio and subtitle tracks appear after playback starts.</span>
            <div class='cupri-video-tracks-panel' data-video-track-panel>
              <div class='cupri-video-tracks-column'><strong>AUDIO</strong><div class='cupri-video-tracks-table' role='table' aria-label='Audio tracks' data-video-track-form-list='audio'></div></div>
              <div class='cupri-video-tracks-column'><strong>SUBTITLES</strong><div class='cupri-video-tracks-table' role='table' aria-label='Subtitle tracks' data-video-track-form-list='subtitle'></div></div>
            </div>
            """;
    }

    internal static void Sync(IElement el, string source, Media.IVideoTrackSelector? tracks, bool open)
    {
        if (el.QuerySelector("[data-video-track-panel]") is { } panel)
            panel.SetAttribute("style", open ? "display:flex" : "display:none");

        var audio = tracks?.AudioTracks ?? [];
        var subtitles = tracks?.SubtitleTracks ?? [];
        FillChoices(el.QuerySelector("[data-video-track-form-list='audio']"), source, "audio", audio, tracks?.SelectedAudioTrack);
        FillChoices(el.QuerySelector("[data-video-track-form-list='subtitle']"), source, "subtitle", subtitles, tracks?.SelectedSubtitleTrack);

        if (el.QuerySelector("[data-video-track-status]") is not { } status) return;
        if (tracks is null || (audio.Count == 0 && subtitles.Count == 0))
        {
            status.TextContent = "No selectable tracks.";
            return;
        }
        var audioLabel = audio.FirstOrDefault(track => track.Id == tracks.SelectedAudioTrack)?.Label ?? "Default";
        var subtitleLabel = subtitles.FirstOrDefault(track => track.Id == tracks.SelectedSubtitleTrack)?.Label ?? "Off";
        status.TextContent = $"Audio: {audioLabel} · Subtitles: {subtitleLabel}";
    }

    private static void FillChoices(IElement? list, string source, string kind, IReadOnlyList<Media.VideoTrack> tracks, int? selected)
    {
        if (list is null) return;
        if (tracks.Count == 0)
        {
            list.InnerHtml = "<div class='cupri-video-tracks-row' role='row'><span class='cupri-video-tracks-cell' role='cell'>None</span></div>";
            return;
        }
        var heading = "<div class='cupri-video-tracks-row cupri-video-tracks-header' role='row'>" +
                      "<span class='cupri-video-tracks-cell cupri-video-tracks-name' role='columnheader'>TRACK</span>" +
                      "<span class='cupri-video-tracks-cell cupri-video-tracks-state' role='columnheader'>ACTIVE</span></div>";
        list.InnerHtml = heading + string.Join("", tracks.Select(track =>
        {
            var chosen = track.Id == selected;
            var css = chosen
                ? "cupri-video-tracks-row cupri-video-track-choice cupri-video-track-selected"
                : "cupri-video-tracks-row cupri-video-track-choice";
            var check = chosen ? IconMarkup("check", 16) : "";
            return $"<div class='{css}' role='row' tabindex='0' data-video-track-source='{VideoComponent.Escape(source)}' " +
                   $"data-video-track-kind='{kind}' data-video-track-id='{track.Id}'>" +
                   $"<span class='cupri-video-tracks-cell cupri-video-tracks-name' role='cell'>{VideoComponent.Escape(track.Label)}</span>" +
                   $"<span class='cupri-video-tracks-cell cupri-video-tracks-state' role='cell'>{check}</span></div>";
        }));
    }
}

/// <summary>
/// <c>&lt;cupri-video-chapters src="…"&gt;</c> is an opt-in companion table for a chapter-capable
/// player. Selecting a row seeks to that chapter; omit it when an application should not expose
/// chapter navigation.
/// </summary>
public sealed class VideoChaptersComponent : ComponentBase
{
    public override string Tag => "cupri-video-chapters";
    public override string DefaultCss => """
        .cupri-video-chapters { display:block; margin-top:10px; }
        .cupri-video-chapters-toggle { display:inline-flex; align-items:center; gap:8px; padding:9px 14px;
                                       border:1px var(--cupri-border,#d8dde6); border-radius:8px;
                                       color:var(--cupri-text,#1e2430); font-weight:bold; }
        .cupri-video-chapters-panel { display:none; margin-top:10px; }
        .cupri-video-chapters-panel > strong { display:block; margin-bottom:8px; color:var(--cupri-muted,#687184);
                                               font-size:11px; letter-spacing:1px; }
        .cupri-video-chapters-table { display:flex; flex-direction:column; width:100%; overflow:hidden;
                                      border:1px var(--cupri-border,#d8dde6); border-radius:8px; }
        .cupri-video-chapter-row { display:flex; align-items:center; width:100%; border-bottom:1px var(--cupri-border,#d8dde6); }
        .cupri-video-chapter-row:last-child { border-bottom:0; }
        .cupri-video-chapter-header { color:var(--cupri-muted,#687184); font-size:10px; font-weight:bold; letter-spacing:1px; }
        .cupri-video-chapter-cell { flex:1; min-width:0; padding:9px 12px; overflow:hidden; }
        .cupri-video-chapter-number { flex:none; width:56px; text-align:center; }
        .cupri-video-chapter-time { flex:none; width:94px; text-align:right; }
        .cupri-video-chapter-name { overflow-wrap:anywhere; }
        .cupri-video-chapter-choice { color:var(--cupri-text,#1e2430); }
        .cupri-video-chapter-choice:hover, .cupri-video-chapter-choice.cupri-video-chapter-current { background:var(--cupri-hover,#eef1f5); }
        .cupri-video-chapter-choice.cupri-video-chapter-current { color:var(--cupri-accent,#B87333); }
        """;

    public override void Expand(IElement el)
    {
        var source = Str(el, "src");
        var label = Str(el, "label", "CHAPTERS");
        el.ClassList.Add("cupri-video-chapters");
        el.SetAttribute("data-video-chapter-controls", source);
        el.InnerHtml = $"""
            <div class='cupri-video-chapters-toggle' role='button' tabindex='0' aria-expanded='false'
                 data-video-chapter-toggle='{VideoComponent.Escape(source)}'>
              {IconMarkup("menu", 18)}<span>{VideoComponent.Escape(label)}</span>
            </div>
            <div class='cupri-video-chapters-panel' data-video-chapter-panel>
              <strong>{VideoComponent.Escape(label)}</strong>
              <div class='cupri-video-chapters-table' role='table' aria-label='Video chapters' data-video-chapter-list></div>
            </div>
            """;
    }

    internal static void Sync(IElement el, string source, Media.IVideoChapterProvider? provider, bool open)
    {
        if (el.QuerySelector("[data-video-chapter-panel]") is { } panel)
            panel.SetAttribute("style", open ? "display:block" : "display:none");
        if (el.QuerySelector("[data-video-chapter-toggle]") is { } toggle)
            toggle.SetAttribute("aria-expanded", open ? "true" : "false");
        if (el.QuerySelector("[data-video-chapter-list]") is not { } list) return;
        var chapters = provider?.Chapters ?? [];
        if (chapters.Count == 0)
        {
            list.InnerHtml = "<div class='cupri-video-chapter-row' role='row'><span class='cupri-video-chapter-cell' role='cell'>No chapter information</span></div>";
            return;
        }

        const string heading = "<div class='cupri-video-chapter-row cupri-video-chapter-header' role='row'>" +
                               "<span class='cupri-video-chapter-cell cupri-video-chapter-number' role='columnheader'>#</span>" +
                               "<span class='cupri-video-chapter-cell' role='columnheader'>CHAPTER</span>" +
                               "<span class='cupri-video-chapter-cell cupri-video-chapter-time' role='columnheader'>START</span>" +
                               "<span class='cupri-video-chapter-cell cupri-video-chapter-time' role='columnheader'>DURATION</span></div>";
        list.InnerHtml = heading + string.Join("", chapters.Select((chapter, displayIndex) =>
        {
            var css = chapter.Index == provider!.SelectedChapter
                ? "cupri-video-chapter-row cupri-video-chapter-choice cupri-video-chapter-current"
                : "cupri-video-chapter-row cupri-video-chapter-choice";
            return $"<div class='{css}' role='row' tabindex='0' data-video-chapter-source='{VideoComponent.Escape(source)}' " +
                   $"data-video-chapter-id='{chapter.Index}'>" +
                   $"<span class='cupri-video-chapter-cell cupri-video-chapter-number' role='cell'>{displayIndex + 1}</span>" +
                   $"<span class='cupri-video-chapter-cell cupri-video-chapter-name' role='cell'>{VideoComponent.Escape(chapter.Label)}</span>" +
                   $"<span class='cupri-video-chapter-cell cupri-video-chapter-time' role='cell'>{VideoComponent.FormatTime(chapter.StartSeconds)}</span>" +
                   $"<span class='cupri-video-chapter-cell cupri-video-chapter-time' role='cell'>{VideoComponent.FormatTime(chapter.DurationSeconds)}</span></div>";
        }));
    }
}
