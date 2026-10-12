using CupriFace;
using CupriFace.Media;
using CupriFace.Media.Windows;
using CupriFace.Shell;
using SkiaSharp;

namespace CupriFace.Samples.MediaWindows;

/// <summary>
/// CupriFace.Media.Windows in a real window, on a real GPU — the half of the Windows media path
/// that tests/MediaWindowsGate says it cannot reach.
///
/// <para>Two registrations and that is the setup: construct the backend and hand it to the
/// document. The host HWND it needs to parent its video child window to is already registered by
/// <c>DesktopHost</c> as a <c>Win32HostWindow</c> feature, so nothing here mentions a window
/// handle — the adapter asks the host for the one feature it understands.</para>
///
/// <para>Verified on this path: the backend constructs (native LibVLC resolved), playback advances,
/// <c>VideoEnded</c> fires at the natural end, and the audio/subtitle tracks are enumerated —
/// <c>audio=1 [Track 1 - [English]]</c> from the bundled fixture. In a session with no audio DEVICE
/// (an RDP desktop, a CI runner) LibVLC logs <c>mmdevice audio output error: cannot get default
/// device</c> on repeat: the track is still selected and reported, only its playback fails, and the
/// noise is the environment rather than this adapter.</para>
///
/// <para>What to LOOK at, because the console cannot tell you: the picture appears inside the
/// element's box and moves with it, the engine-drawn control bar and the track menu paint ON TOP
/// of the picture rather than behind it (that is <c>data-surface-overlay</c> and
/// <c>data-surface-occluder</c> doing their jobs over a native surface), and resizing the window
/// keeps the picture and its hole aligned to the pixel.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("usage: MediaWindows [<media-file>]   (default: the repo's demo-av.webm beside the binary)");
            return 0;
        }

        // A path if given, else the fixture the csproj copies next to the binary. Absolute, because
        // a relative one reaches `new Uri` as something it cannot classify — the same normalisation
        // the gate does, for the same reason.
        var requested = args.FirstOrDefault(a => !a.StartsWith('-'));
        var media = Path.GetFullPath(requested ?? Path.Combine(AppContext.BaseDirectory, "demo-av.webm"));
        if (!File.Exists(media))
        {
            Console.WriteLine($"::error:: no such media file: {media}");
            return 1;
        }

        Console.WriteLine($"media   : {media} ({new FileInfo(media).Length:N0} bytes)");
        if (!OperatingSystem.IsWindows())
            Console.WriteLine("note    : not Windows — the host offers no Win32HostWindow, so the adapter will " +
                              "decline and the element keeps its poster. That is the feature registry, not a fault.");

        // Constructed BEFORE the host so a native-discovery failure (#303) is reported here rather
        // than as a dead element inside a window that otherwise looks fine.
        LibVlcVideoBackend backend;
        try
        {
            backend = new LibVlcVideoBackend();
            Console.WriteLine("backend : LibVlcVideoBackend constructed (native LibVLC resolved)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"::error:: the backend would not construct: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        using (backend)
        {
            // Run blocks until the window closes, so the backend outlives every player — which is
            // what the package asks of a host.
            DesktopHost.Run(new MediaApp(media), doc =>
            {
                doc.UseVideo(backend);
                Report(doc);
            });
        }
        Console.WriteLine("closed  : backend disposed");
        return 0;
    }

    /// <summary>
    /// Print what the document discovers, so the run leaves a readable trace and not only a window.
    /// These are the same public getters an application would use, which is the point: the
    /// enumeration is reported through the engine's API rather than read off the adapter.
    /// </summary>
    private static void Report(CupriDocument doc)
    {
        doc.VideoTracksChanged += src =>
        {
            var audio = doc.GetVideoAudioTracks(src);
            var subs = doc.GetVideoSubtitleTracks(src);
            Console.WriteLine($"tracks  : audio={Describe(audio)}  subtitles={Describe(subs)}");
        };
        doc.VideoChaptersChanged += src =>
        {
            var chapters = doc.GetVideoChapters(src);
            Console.WriteLine($"chapters: {chapters.Count}" +
                              (chapters.Count == 0 ? " (none embedded — try an MKV with chapters)"
                                                   : $" — {string.Join(", ", chapters.Take(4).Select(c => c.Label))}"));
        };
        doc.VideoEnded += _ => Console.WriteLine("ended   : reached its natural end");
        doc.VideoPlaybackStateChanged += s =>
            Console.WriteLine($"state   : {(s.Playing ? "playing" : "paused")} {s.PositionSeconds:0.0}/{s.DurationSeconds:0.0}s " +
                              $"vol={s.Volume:0.00}{(s.Muted ? " muted" : "")} audio=#{s.AudioTrackId?.ToString() ?? "-"} subs=#{s.SubtitleTrackId?.ToString() ?? "-"}");
    }

    private static string Describe(IReadOnlyList<VideoTrack> tracks) =>
        tracks.Count == 0 ? "none" : $"{tracks.Count} [{string.Join(" | ", tracks.Select(t => t.Label))}]";
}

/// <summary>The app itself: one video with every opt-in capability turned on, so the control bar,
/// the track menu and the chapter table are all on screen over a native surface.</summary>
internal sealed class MediaApp(string mediaPath) : CupriApp
{
    // file:// rather than a bare Windows path: a backslash-bearing path in an HTML attribute is a
    // different question from a URI, and the engine resolves either — so pick the unambiguous one.
    private readonly string _src = new Uri(mediaPath).AbsoluteUri;
    private readonly string _name = Path.GetFileName(mediaPath);

    public override string Title => $"CupriFace — Windows media ({Path.GetFileName(mediaPath)})";
    public override int Width => 980;
    public override int Height => 680;
    public override SKColor Background => new(0x14, 0x17, 0x1d);

    // Audible autoplay is off by default because browsers reject it; this sample is native-only and
    // the whole point is to hear whether the audio track switched, so it opts in. (Documented in
    // RELEASE-NOTES under AllowUnmutedVideoAutoplay.)
    public override bool AllowUnmutedVideoAutoplay => true;

    public override string Html => $$"""
        <body>
          <div class="wrap">
            <div class="title">CupriFace.Media.Windows — live window</div>
            <p class="sub">A native video child window, parented to this window's HWND and composited
              under the GL surface. The engine punches a transparent hole at the element's box; the
              bar and menus below paint on top of the picture, which is what a host-composited
              surface needs marking for.</p>

            <cupri-video src="{{_src}}" controls tracks chapters autoplay
                         label="{{_name}}" class="player"></cupri-video>

            <div class="row">
              <cupri-button class="pick">Open a different file…</cupri-button>
              <span class="hint">Right-click the picture for the audio/subtitle menu. Pass a path on
                the command line to play a real MKV — the bundled fixture has one track and no
                chapters, so it cannot show either off.</span>
            </div>

            <ul class="check">
              <li>Picture sits inside the element's box, and stays there while the window resizes.</li>
              <li>Control bar and track menu paint OVER the picture, not behind it.</li>
              <li>Seek jumps without freezing the UI — the adapter seeks off the UI thread.</li>
              <li>Fullscreen (⛶) goes to THIS monitor and comes back to the same bounds.</li>
            </ul>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; background:#14171d; color:#e8ecf3; }
        .wrap { padding:22px 26px; display:flex; flex-direction:column; gap:14px; }
        .title { font-size:19px; font-weight:bold; }
        .sub { color:#9aa6b8; font-size:13px; max-width:760px; margin:0; }
        .player { width:100%; height:380px; }
        .row { display:flex; align-items:center; gap:14px; }
        .hint { color:#7d8798; font-size:12px; }
        .check { color:#9aa6b8; font-size:12.5px; }
        .check li { padding:2px 0; }
        """;

    public override void Configure(CupriDocument doc) =>
        // Nothing to open from here — the file comes from the command line. Kept as the obvious
        // place to add a picker, and so the button is not a dead control.
        doc.OnClick(".pick", _ => doc.Toast("Pass a media path on the command line: MediaWindows <file>"));
}
