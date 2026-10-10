using CupriFace.Media;
using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-video&gt;</c> + the document's video wiring, against a fake backend: player
/// lifecycle follows the DOM, transport commands ride input dispatch synchronously, and the
/// autoplay policy is browser-safe by default (autoplay only when muted), with an explicit native
/// opt-in for audible autoplay.
/// </summary>
public class VideoComponentTests
{
    private sealed class FakeSurface : ISurfaceSource
    {
        public SKImage? CurrentFrame { get; set; }
        public (int W, int H)? NaturalSize { get; set; } = (160, 90);
        public bool Ticking { get; set; }
    }

    private sealed class FakePlayer : IVideoPlayer, IVideoTrackSelector, IVideoChapterProvider
    {
        public FakeSurface Fake { get; } = new();
        public ISurfaceSource Surface => Fake;
        public bool Playing { get; private set; }
        public bool Muted { get; set; }
        public double Volume { get; set; } = 1;
        public bool Loop { get; set; }
        public double Duration => 10;
        public double Position { get; set; }
        public bool Disposed { get; private set; }
        public event Action? Ended;
        public event Action? TracksChanged;
        public event Action? ChaptersChanged;
        public IReadOnlyList<VideoTrack> AudioTracks { get; private set; } = [];
        public IReadOnlyList<VideoTrack> SubtitleTracks { get; private set; } = [];
        public int SelectedAudioTrack { get; private set; } = -1;
        public int SelectedSubtitleTrack { get; private set; } = -1;
        public IReadOnlyList<VideoChapter> Chapters { get; private set; } = [];
        public int SelectedChapter { get; private set; } = -1;
        public void Play() { Playing = true; Fake.Ticking = true; }
        public void Pause() { Playing = false; Fake.Ticking = false; }
        public void RaiseEnded() { Playing = false; Fake.Ticking = false; Ended?.Invoke(); }
        public void PublishTracks(IReadOnlyList<VideoTrack> audio, IReadOnlyList<VideoTrack> subtitles)
        {
            AudioTracks = audio;
            SubtitleTracks = subtitles;
            SelectedAudioTrack = audio.FirstOrDefault()?.Id ?? -1;
            SelectedSubtitleTrack = subtitles.FirstOrDefault()?.Id ?? -1;
            TracksChanged?.Invoke();
        }
        public bool SelectAudioTrack(int trackId)
        {
            if (!AudioTracks.Any(track => track.Id == trackId)) return false;
            SelectedAudioTrack = trackId;
            return true;
        }
        public bool SelectSubtitleTrack(int trackId)
        {
            if (!SubtitleTracks.Any(track => track.Id == trackId)) return false;
            SelectedSubtitleTrack = trackId;
            return true;
        }
        public void PublishChapters(IReadOnlyList<VideoChapter> chapters)
        {
            Chapters = chapters;
            SelectedChapter = chapters.FirstOrDefault()?.Index ?? -1;
            ChaptersChanged?.Invoke();
        }
        public bool SelectChapter(int chapterIndex)
        {
            var chapter = Chapters.FirstOrDefault(item => item.Index == chapterIndex);
            if (chapter is null) return false;
            SelectedChapter = chapterIndex;
            Position = chapter.StartSeconds;
            ChaptersChanged?.Invoke();
            return true;
        }
        public void Dispose() { Disposed = true; Fake.Ticking = false; }
    }

    private sealed class FakeBackend : IVideoBackend
    {
        public readonly List<string> Opened = new();
        public readonly Dictionary<string, FakePlayer> Players = new();
        public IVideoPlayer Open(VideoSource source)
        {
            Opened.Add(source.Src);
            return Players[source.Src] = new FakePlayer();
        }
    }

    private sealed class Model { public string Section { get; set; } = "media"; }

    private const string Html = "<body><cupri-video src='clip.webm' controls muted autoplay loop label='Trailer' style='width:320px;height:180px'></cupri-video></body>";

    [Fact]
    public void Expansion_produces_surface_poster_lane_and_accessible_controls()
    {
        using var t = new TestDoc("<body><cupri-video src='clip.webm' poster='p.png' controls label='Trailer'></cupri-video></body>",
            "", components: true);

        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "video:clip.webm"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-cupri-image") == "p.png"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-video-cmd") == "mute"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Trailer"));
        // No backend registered: nothing throws, the element still renders (poster lane).
    }

    [Fact]
    public void Without_a_backend_the_transport_controls_read_disabled()
    {
        // A Play button that looks live and does nothing is a lie — no backend dims the
        // transport (and AT announces it); fullscreen stays enabled (it needs no decoder).
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);
        t.Layout();
        var toggle = t.Find(n => n.Element?.GetAttribute("data-video-role") == "toggle")!;
        Assert.Contains("disabled", toggle.Element!.ClassList);
        Assert.Equal("true", toggle.Element!.GetAttribute("aria-disabled"));
        var seek = t.Find(n => n.Element?.GetAttribute("data-video-role") == "seek")!;
        Assert.Equal("true", seek.Element!.GetAttribute("aria-disabled")); // can't seek nothing
        var fullscreen = t.Find(n => n.Element?.GetAttribute("data-video-role") == "fullscreen")!;
        Assert.DoesNotContain("disabled", fullscreen.Element!.ClassList);

        // Registering a backend un-dims them on the next rebuild.
        var backend = new FakeBackend();
        t.Doc.UseVideo(backend);
        t.Layout();
        var live = t.Find(n => n.Element?.GetAttribute("data-video-role") == "toggle")!;
        Assert.DoesNotContain("disabled", live.Element!.ClassList);
    }

    [Fact]
    public void A_player_opens_once_and_survives_the_per_keystroke_rebuild()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(Html, "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();

        t.Doc.Refresh();
        t.Doc.Refresh();
        Assert.Equal(new[] { "clip.webm" }, backend.Opened);   // cached by src, not re-opened
    }

    [Fact]
    public void Selectable_tracks_are_reported_and_changed_through_the_document()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(Html, "", components: true);
        string? changedSource = null;
        t.Doc.VideoTracksChanged += source => changedSource = source;
        t.Doc.UseVideo(backend);
        t.Layout();

        backend.Players["clip.webm"].PublishTracks(
            [new VideoTrack(1, "English"), new VideoTrack(2, "Commentary")],
            [new VideoTrack(-1, "Off"), new VideoTrack(4, "English CC")]);

        Assert.Equal("clip.webm", changedSource);
        Assert.Equal(["English", "Commentary"], t.Doc.GetVideoAudioTracks("clip.webm").Select(track => track.Label));
        Assert.Equal(["Off", "English CC"], t.Doc.GetVideoSubtitleTracks("clip.webm").Select(track => track.Label));
        Assert.True(t.Doc.SelectVideoAudioTrack("clip.webm", 2));
        Assert.True(t.Doc.SelectVideoSubtitleTrack("clip.webm", 4));
        Assert.Equal(2, t.Doc.GetSelectedVideoAudioTrack("clip.webm"));
        Assert.Equal(4, t.Doc.GetSelectedVideoSubtitleTrack("clip.webm"));
        Assert.False(t.Doc.SelectVideoAudioTrack("clip.webm", 99));
    }

    [Fact]
    public void Playback_state_can_be_observed_and_restored_without_backend_coupling()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(Html, "", components: true);
        VideoPlaybackState? observed = null;
        t.Doc.VideoPlaybackStateChanged += state => observed = state;
        t.Doc.UseVideo(backend);
        t.Layout();
        var player = backend.Players["clip.webm"];
        player.PublishTracks(
            [new VideoTrack(1, "English"), new VideoTrack(2, "Commentary")],
            [new VideoTrack(-1, "Off"), new VideoTrack(4, "English CC")]);

        Assert.True(t.Doc.RestoreVideoPlaybackState("clip.webm", new VideoPlaybackState(
            "clip.webm", 6.5, 10, Playing: true, Muted: false, Volume: .4, AudioTrackId: 2, SubtitleTrackId: 4)));

        Assert.Equal(6.5, player.Position);
        Assert.False(player.Muted);
        Assert.Equal(.4, player.Volume);
        Assert.Equal(2, player.SelectedAudioTrack);
        Assert.Equal(4, player.SelectedSubtitleTrack);
        Assert.NotNull(observed);
        Assert.Equal("clip.webm", observed.Source);
        Assert.Equal(6.5, observed.PositionSeconds);
        Assert.Equal(2, observed.AudioTrackId);
        Assert.Equal(4, observed.SubtitleTrackId);
    }

    [Fact]
    public void Track_selection_ui_is_opt_in_and_uses_the_player_capability()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("""
            <body>
              <cupri-video src='clip.webm' controls tracks style='width:320px;height:180px'></cupri-video>
              <cupri-video-tracks src='clip.webm'></cupri-video-tracks>
            </body>
            """, "", components: true, width: 800, height: 600);
        t.Doc.UseVideo(backend);
        t.Layout();
        backend.Players["clip.webm"].PublishTracks(
            [new VideoTrack(1, "English"), new VideoTrack(2, "Example.Movie.2026.2160p.BluRay.REMUX.HEVC.DTS-HD.MA.TrueHD.7.1.Atmos-FGT - [English]")],
            [new VideoTrack(-1, "Off"), new VideoTrack(4, "English CC")]);
        t.Doc.Refresh();
        t.Layout();

        Assert.NotNull(t.Find(n => n.Element?.HasAttribute("data-cupri-ctx-host") == true));
        Assert.Contains("Audio / Video", t.Find(n => n.Element?.HasAttribute("data-video-track-toggle") == true)!.Element!.TextContent);
        t.ClickMatch(n => n.Element?.HasAttribute("data-video-track-toggle") == true);
        t.Layout();
        Assert.Equal("display:flex", t.Find(n => n.Element?.HasAttribute("data-video-track-panel") == true)!.Element!.GetAttribute("style"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Audio tracks"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Subtitle tracks"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("data-video-track-id") == "4"));
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-tracks-name") == true));
        var tableLabel = t.Find(n => n.IsText && n.Text?.Contains("Example.Movie.2026") == true)!;
        Assert.True(tableLabel.Lines!.Count > 1, "the long table label should wrap inside the Track column");
        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-video-track-choice") == true &&
                          n.Element.GetAttribute("data-video-track-kind") == "audio" &&
                          n.Element.GetAttribute("data-video-track-id") == "2");
        Assert.Equal(2, backend.Players["clip.webm"].SelectedAudioTrack);
        t.Layout();
        Assert.Equal("display:flex", t.Find(n => n.Element?.HasAttribute("data-video-track-panel") == true)!.Element!.GetAttribute("style"));

        var video = t.Find(n => n.Element?.HasAttribute("data-cupri-video") == true)!;
        var (videoX, videoY) = TestDoc.Center(video);
        t.Doc.DispatchContextMenu(videoX, videoY);
        t.Layout();
        var audioMenu = t.Find(n => n.Element?.ClassList.Contains("cupri-menu-parent") == true)!;
        var (audioX, audioY) = TestDoc.Center(audioMenu);
        t.Move(audioX, audioY);
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-track-menu-label") == true));
        var flyout = t.Find(n => n.Element?.ClassList.Contains("cupri-submenu") == true)!;
        var flyoutLabel = TestDoc.Find(flyout, n => n.IsText && n.Text?.Contains("Example.Movie.2026") == true)!;
        Assert.True(flyoutLabel.Lines!.Count > 1, "the long context-menu label should wrap inside the fly-out");
    }

    [Fact]
    public void Track_selection_ui_is_absent_by_default()
    {
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);

        Assert.Null(t.Find(n => n.Element?.HasAttribute("data-cupri-ctx-host") == true));
        Assert.Null(t.Find(n => n.Element?.HasAttribute("data-video-track-toggle") == true));
        Assert.Null(t.Find(n => n.Element?.HasAttribute("data-video-chapter-markers") == true));
    }

    [Fact]
    public void Chapter_table_seeks_and_progress_bar_marks_boundaries_when_opted_in()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("""
            <body>
              <cupri-video src='clip.webm' controls chapters style='width:600px;height:240px'></cupri-video>
              <cupri-video-chapters src='clip.webm'></cupri-video-chapters>
            </body>
            """, "", components: true, width: 800, height: 600);
        t.Doc.UseVideo(backend);
        t.Layout();
        backend.Players["clip.webm"].PublishChapters([
            new VideoChapter(0, "Opening credits and the first mysterious encounter", 0, 2.5),
            new VideoChapter(1, "The middle", 2.5, 5),
            new VideoChapter(2, "Finale", 7.5, 2.5),
        ]);
        t.Doc.Refresh();
        t.Layout();

        Assert.Equal(3, t.Doc.GetVideoChapters("clip.webm").Count);
        Assert.Null(t.Find(n => n.Element?.GetAttribute("aria-label") == "Video chapters"));
        Assert.Equal("false", t.Find(n => n.Element?.HasAttribute("data-video-chapter-toggle") == true)!
            .Element!.GetAttribute("aria-expanded"));
        Assert.Equal(2, Count(t.Root, n => n.Element?.ClassList.Contains("cupri-video-chapter-marker") == true));
        Assert.Equal(3, Count(t.Root, n => n.Element?.ClassList.Contains("cupri-video-chapter-segment") == true));
        Assert.Equal("The middle", t.Find(n => n.Element?.GetAttribute("data-video-chapter-title") == "The middle")!
            .Element!.QuerySelector("[role='tooltip']")!.TextContent);

        var openingSegment = t.Find(n => n.Element?.GetAttribute("data-video-chapter-title") ==
            "Opening credits and the first mysterious encounter")!;
        var (openingX, openingY) = TestDoc.Center(openingSegment);
        t.Move(openingX, openingY);
        openingSegment = t.Find(n => n.Element?.GetAttribute("data-video-chapter-title") ==
            "Opening credits and the first mysterious encounter")!;
        var openingTooltip = t.Find(n => n.Element?.ClassList.Contains("cupri-video-chapter-tooltip") == true &&
            n.Element.TextContent == "Opening credits and the first mysterious encounter")!;
        Assert.NotEqual(CupriFace.Style.DisplayType.None, openingTooltip.Style.Display);
        Assert.True(openingTooltip.Width > openingSegment.Width,
            "the full title tooltip must size independently of a narrow chapter segment");

        var middleSegment = t.Find(n => n.Element?.GetAttribute("data-video-chapter-title") == "The middle")!;
        var (segmentX, segmentY) = TestDoc.Center(middleSegment);
        t.Move(segmentX, segmentY);
        Assert.NotEqual(CupriFace.Style.DisplayType.None,
            t.Find(n => n.Element?.ClassList.Contains("cupri-video-chapter-tooltip") == true &&
                        n.Element.TextContent == "The middle")!.Style.Display);
        var visibleTooltip = t.Find(n => n.Element?.ClassList.Contains("cupri-video-chapter-tooltip") == true &&
                                         n.Element.TextContent == "The middle")!;
        Assert.Equal(CupriFace.Style.PositionType.Fixed, visibleTooltip.Style.Position);
        Assert.NotNull(visibleTooltip.Element!.GetAttribute("data-cupri-anchor"));
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-chapter-title") == "The middle");
        Assert.InRange(backend.Players["clip.webm"].Position, 4.5, 5.5);
        Assert.False(backend.Players["clip.webm"].Playing);

        t.ClickMatch(n => n.Element?.HasAttribute("data-video-chapter-toggle") == true);
        Assert.Equal("true", t.Find(n => n.Element?.HasAttribute("data-video-chapter-toggle") == true)!
            .Element!.GetAttribute("aria-expanded"));
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Video chapters"));
        Assert.Equal("display:block", t.Find(n => n.Element?.HasAttribute("data-video-chapter-panel") == true)!
            .Element!.GetAttribute("style"));
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-chapter-id") == "2");
        Assert.Equal(2, backend.Players["clip.webm"].SelectedChapter);
        Assert.Equal(7.5, backend.Players["clip.webm"].Position);
        Assert.Equal("2", t.Find(n => n.Element?.ClassList.Contains("cupri-video-chapter-current") == true)!
            .Element!.GetAttribute("data-video-chapter-id"));
        Assert.Equal("display:block", t.Find(n => n.Element?.HasAttribute("data-video-chapter-panel") == true)!
            .Element!.GetAttribute("style"));

        static int Count(CupriFace.Dom.RenderNode node, Func<CupriFace.Dom.RenderNode, bool> predicate)
        {
            var count = predicate(node) ? 1 : 0;
            foreach (var child in node.Children) count += Count(child, predicate);
            return count;
        }
    }

    [Fact]
    public void Autoplay_is_muted_by_default_but_native_hosts_can_opt_into_audio()
    {
        var backend = new FakeBackend();
        using (var t = new TestDoc(Html, "", components: true))
        {
            t.Doc.UseVideo(backend);
            t.Layout();
            var p = backend.Players["clip.webm"];
            Assert.True(p.Muted);
            Assert.True(p.Loop);
            Assert.True(p.Playing);                            // muted autoplay: allowed
        }

        var backend2 = new FakeBackend();
        using (var t2 = new TestDoc("<body><cupri-video src='clip.webm' autoplay></cupri-video></body>", "", components: true))
        {
            t2.Doc.UseVideo(backend2);
            t2.Layout();
            Assert.False(backend2.Players["clip.webm"].Playing); // unmuted autoplay: stays paused
        }

        var nativeBackend = new FakeBackend();
        using (var native = new TestDoc("<body><cupri-video src='clip.webm' autoplay></cupri-video></body>", "", components: true))
        {
            native.Doc.AllowUnmutedVideoAutoplay = true;
            native.Doc.UseVideo(nativeBackend);
            native.Layout();
            Assert.False(nativeBackend.Players["clip.webm"].Muted);
            Assert.True(nativeBackend.Players["clip.webm"].Playing);
        }
    }

    [Fact]
    public void Clicking_the_frame_toggles_and_the_controls_relabel()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];

        var frame = t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "video:clip.webm")!;
        t.ClickNode(frame);
        Assert.True(p.Playing);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Pause")); // glyph+label flipped

        t.ClickNode(t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "video:clip.webm")!);
        Assert.False(p.Playing);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Play"));
    }

    [Fact]
    public void Mute_toggles_through_the_button()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];

        Assert.False(p.Muted);
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "mute");
        Assert.True(p.Muted);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Unmute"));
    }

    [Fact]
    public void A_video_in_a_hidden_section_is_stopped_and_disposed()
    {
        var backend = new FakeBackend();
        var m = new Model { Section = "block" };
        const string html = """
            <body>
              <div style="display:{{Section}}"><cupri-video src='clip.webm' muted autoplay></cupri-video></div>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];
        Assert.True(p.Playing);

        // Hide the subtree (a section switch): the rebuild re-binds the template, expansion skips
        // display:none → the src vanishes from the DOM → the document retires the player.
        m.Section = "none";
        t.Doc.Refresh();
        Assert.True(p.Disposed);
    }


    [Fact]
    public void Authored_inline_size_wins_over_the_intrinsic_video_size()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' style='width:320px;height:180px'></cupri-video></body>",
            "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();

        var node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(320, node.Width, 1);
        Assert.Equal(180, node.Height, 1);
    }

    [Fact]
    public void The_seek_bar_scrubs_by_pointer_arrows_and_AT_and_the_clocks_follow()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls style='width:400px;height:225px'></cupri-video></body>",
            "", components: true, width: 500, height: 300);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];

        // Pointer: press at ~half the track → half the (10 s) duration; drag right → follows.
        var seek = t.Find(n => n.Element?.GetAttribute("data-video-role") == "seek")!;
        var box = CupriFace.Interaction.HitTesting.ScreenBox(seek);
        var midY = box.Y + box.H / 2;
        t.Doc.DispatchClick(box.X + box.W * 0.5f, midY, 1);
        Assert.InRange(p.Position, 4.0, 6.0);
        t.Doc.DispatchPointerMove(box.X + box.W * 0.9f, midY);
        Assert.InRange(p.Position, 8.0, 10.0);
        t.Doc.DispatchPointerUp(box.X + box.W * 0.9f, midY);

        // The clocks + fill reflect the position after the rebuild the scrub caused.
        t.Layout();
        var time = t.Find(n => n.Element?.GetAttribute("data-video-role") == "time")!;
        Assert.Equal("0:0" + (int)p.Position % 10, time.Element!.TextContent);
        var seekEl = t.Find(n => n.Element?.GetAttribute("data-video-role") == "seek")!.Element!;
        Assert.Equal(p.Position.ToString("0.#"), seekEl.GetAttribute("aria-valuenow"));

        // Keyboard: arrows scrub ±5 s on the focused bar.
        p.Position = 5;
        t.Doc.AccessibilityFocus(FindSeekPath(t));
        Assert.True(t.Doc.DispatchKey(null, CupriFace.Interaction.EditKey.Right));
        Assert.Equal(10, p.Position, 1);                       // clamped at duration
        Assert.True(t.Doc.DispatchKey(null, CupriFace.Interaction.EditKey.Left));
        Assert.Equal(5, p.Position, 1);

        // AT: RangeValue.SetValue in seconds.
        Assert.True(t.Doc.AccessibilitySetValue(FindSeekPath(t), 7.5));
        Assert.Equal(7.5, p.Position, 2);
    }

    private static string FindSeekPath(TestDoc t)
    {
        static CupriFace.Accessibility.AccessibilityNode? Walk(CupriFace.Accessibility.AccessibilityNode n)
        {
            if (n.Role == "slider") return n;
            foreach (var c in n.Children) if (Walk(c) is { } hit) return hit;
            return null;
        }
        return Walk(t.Doc.BuildAccessibilityTree(500, 300))!.Path;
    }

    [Fact]
    public void The_clocks_advance_on_the_host_poll_while_playing()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(Html, "", components: true, width: 500, height: 300);
        t.Doc.UseVideo(backend);
        t.Layout();
        var p = backend.Players["clip.webm"];
        Assert.True(p.Playing);                                 // muted autoplay
        while (t.Doc.ConsumeImageArrived()) { }                 // drain the initial reflections

        p.Position = 3.2;                                       // playback advanced (decode thread)
        Assert.True(t.Doc.ConsumeImageArrived(), "a ~1 s position change must trigger a reflect");
        t.Layout();
        Assert.Equal("0:03", t.Find(n => n.Element?.GetAttribute("data-video-role") == "time")!.Element!.TextContent);

        p.Position = 3.5;                                       // sub-second drift: no rebuild churn
        Assert.False(t.Doc.ConsumeImageArrived());
    }

    [Fact]
    public void Fullscreen_expands_the_ELEMENT_to_the_viewport_and_asks_for_the_window_too()
    {
        // Deliberately no backend and an authored inline size: fullscreen needs no decoder, and
        // the viewport override must beat the author's own style="width:320px;height:180px".
        using var t = new TestDoc(
            "<body><div style='padding:40px'><cupri-video src='clip.webm' controls style='width:320px;height:180px;border-radius:10px'></cupri-video></div></body>",
            "", components: true, width: 800, height: 500);
        var commands = new List<CupriFace.Interaction.WindowCommand>();
        t.Doc.WindowCommandRequested += commands.Add;
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        var node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(new[] { CupriFace.Interaction.WindowCommand.EnterFullscreen }, commands);
        Assert.Contains("cupri-video-fs", node.Element!.ClassList);
        Assert.Equal(800, node.Width, 1);                       // covers the viewport…
        Assert.Equal(500, node.Height, 1);
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Exit fullscreen"));

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(CupriFace.Interaction.WindowCommand.ExitFullscreen, commands[^1]);
        Assert.DoesNotContain("cupri-video-fs", node.Element!.ClassList);
        Assert.Equal(320, node.Width, 1);                       // …and comes back to its place
        Assert.Equal(180, node.Height, 1);
    }

    [Fact]
    public void Fullscreen_SNAPS_even_when_the_author_gave_the_video_a_size_transition()
    {
        // The live regression: the resize demo's `transition:width/height` made ⛶ TWEEN toward
        // fullscreen — and the percent target resolved against the parent card, pinning the video
        // at ~card width forever. Fullscreen must snap to the viewport immediately, like the web.
        using var t = new TestDoc(
            "<body><div style='padding:40px'><cupri-video src='clip.webm' controls " +
            "style='width:320px;height:180px;transition:width 0.25s ease, height 0.25s ease'></cupri-video></div></body>",
            "", components: true, width: 800, height: 500);
        t.Layout();
        t.Doc.Animate(0.0);

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Doc.Animate(0.05);                                    // mid-tween, were it tweening
        t.Layout();
        var node = t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;
        Assert.Equal(800, node.Width, 1);                       // viewport at once — no tween, no card clamp
        Assert.Equal(500, node.Height, 1);
    }

    [Fact]
    public void The_fullscreen_video_is_on_top_it_owns_a_click_anywhere()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc(
            "<body><div style='padding:30px'><cupri-button>Save</cupri-button><cupri-video src='clip.webm' controls style='width:200px;height:120px'></cupri-video></div></body>",
            "", components: true, width: 800, height: 500);
        t.Doc.UseVideo(backend);
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();

        // The Save button's old spot now hits the top-layer video: the click toggles playback
        // instead of pressing the button underneath.
        var p = backend.Players["clip.webm"];
        Assert.False(p.Playing);
        t.Doc.DispatchClick(60, 45, 1);
        Assert.True(p.Playing);
    }

    [Fact]
    public void Escape_exits_video_fullscreen_and_releases_the_window()
    {
        using var t = new TestDoc(Html, "", components: true, width: 800, height: 500);
        var commands = new List<CupriFace.Interaction.WindowCommand>();
        t.Doc.WindowCommandRequested += commands.Add;
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        Assert.True(t.Doc.DispatchKey(null, CupriFace.Interaction.EditKey.Escape)); // the DOC handles it
        t.Layout();

        Assert.Equal(new[] { CupriFace.Interaction.WindowCommand.EnterFullscreen,
                             CupriFace.Interaction.WindowCommand.ExitFullscreen }, commands);
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    [Fact]
    public void Hiding_the_fullscreen_videos_section_releases_window_fullscreen_too()
    {
        var backend = new FakeBackend();
        var m = new Model { Section = "block" };
        const string html = """
            <body>
              <div style="display:{{Section}}"><cupri-video src='clip.webm' controls muted></cupri-video></div>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true, width: 800, height: 500);
        var commands = new List<CupriFace.Interaction.WindowCommand>();
        t.Doc.WindowCommandRequested += commands.Add;
        t.Doc.UseVideo(backend);
        t.Layout();

        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        m.Section = "none";                                     // the app switches the section away
        t.Doc.Refresh();
        Assert.Equal(new[] { CupriFace.Interaction.WindowCommand.EnterFullscreen,
                             CupriFace.Interaction.WindowCommand.ExitFullscreen }, commands);
    }

    [Fact]
    public void The_browsers_own_fullscreen_exit_puts_the_video_back()
    {
        // On the web the Esc key goes to the BROWSER; the engine only hears fullscreenchange.
        using var t = new TestDoc(Html, "", components: true, width: 800, height: 500);
        t.Layout();
        t.ClickMatch(n => n.Element?.GetAttribute("data-video-cmd") == "fullscreen");
        t.Layout();
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));

        t.Doc.NotifyHostFullscreen(false);                      // what the host reports
        t.Layout();
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-video-fs") == true));
    }

    private sealed class SizeModel
    {
        public string VideoMode { get; set; } = "small";
        public string VideoStyle => (VideoMode == "large"
            ? "width:560px;height:315px;" : "width:320px;height:180px;")
            + "transition:width 1s linear, height 1s linear";
    }

    [Fact]
    public void A_width_height_transition_animates_the_box_and_the_FRAME_follows_it()
    {
        // The size-button demo's contract: a model-driven style change tweens the element through
        // `transition:width/height`, and the VIDEO PIXELS track the animated box every frame —
        // not snap at the end. The fake player serves a solid lime frame so pixels are provable.
        var backend = new FakeBackend();
        var m = new SizeModel();
        const string html = """
            <body style="margin:0">
              <div style="padding:10px">
                <cupri-video src='clip.webm' fit='fill' style="{{VideoStyle}}"></cupri-video>
                <cupri-button data-set-path="VideoMode" data-set-value="large">Large</cupri-button>
              </div>
            </body>
            """;
        using var t = new TestDoc(html, "", m, components: true, width: 800, height: 600);
        t.Doc.UseVideo(backend);
        using var lime = MakeFrame(SKColors.Lime);
        backend.Players["clip.webm"].Fake.CurrentFrame = lime;
        t.Doc.Animate(0.0);
        t.Layout();
        Assert.Equal(320, VideoNode(t).Width, 1);

        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "large");
        t.Doc.Animate(2.0);                                     // transition clock starts here
        t.Doc.Animate(2.5);                                     // 0.5 of 1s, linear
        t.Layout();
        var mid = VideoNode(t);
        Assert.Equal(440, mid.Width, 1);                        // half-way between 320 and 560
        Assert.Equal(247.5, mid.Height, 1);
        Assert.Equal(SKColors.Lime, Pixel(t, 400, 100));        // inside the tween box, OUTSIDE the small one
        Assert.NotEqual(SKColors.Lime, Pixel(t, 520, 100));     // the large-only area isn't video YET

        t.Doc.Animate(3.0);                                     // settled
        t.Layout();
        Assert.Equal(560, VideoNode(t).Width, 1);
        Assert.Equal(315, VideoNode(t).Height, 1);
        Assert.Equal(SKColors.Lime, Pixel(t, 520, 100));        // now it is
    }

    private static CupriFace.Dom.RenderNode VideoNode(TestDoc t) =>
        t.Find(n => n.Element?.GetAttribute("data-cupri-video") == "clip.webm")!;

    private static SKImage MakeFrame(SKColor colour)
    {
        using var s = SKSurface.Create(new SKImageInfo(160, 90));
        s.Canvas.Clear(colour);
        return s.Snapshot();
    }

    private static SKColor Pixel(TestDoc t, int x, int y)
    {
        using var bmp = new SKBitmap(new SKImageInfo(800, 600, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        t.Doc.Render(canvas, 800, 600);
        canvas.Flush();
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Ended_flips_the_controls_back_on_the_next_host_poll()
    {
        var backend = new FakeBackend();
        using var t = new TestDoc("<body><cupri-video src='clip.webm' controls muted autoplay></cupri-video></body>", "", components: true);
        t.Doc.UseVideo(backend);
        t.Layout();
        t.Doc.ConsumeImageArrived();                            // drain
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Pause"));

        backend.Players["clip.webm"].RaiseEnded();              // may come from any thread
        Assert.True(t.Doc.ConsumeImageArrived());               // the UI-thread poll reacts…
        t.Layout();
        Assert.NotNull(t.Find(n => n.Element?.GetAttribute("aria-label") == "Play")); // …and relabels
    }
}
