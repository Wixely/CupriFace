using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Guards Android host seams that the headless engine test harness cannot execute. The Android
/// project build verifies the platform APIs; these checks make accidental removal of the wiring
/// visible in the ordinary test suite.
/// </summary>
public class AndroidPlaybackIntegrationTests
{
    private static string Android(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CupriFace.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", "CupriFace.Android", file));
    }

    [Fact]
    public void Android_video_uses_libvlc_for_tracks_and_chapters()
    {
        var source = Android("AndroidVideo.cs");

        Assert.Contains("IVideoTrackSelector", source, StringComparison.Ordinal);
        Assert.Contains("IVideoChapterProvider", source, StringComparison.Ordinal);
        Assert.Contains("AudioTrackDescription", source, StringComparison.Ordinal);
        Assert.Contains("SpuDescription", source, StringComparison.Ordinal);
        Assert.Contains("FullChapterDescriptions", source, StringComparison.Ordinal);
        Assert.Contains("ESAdded += OnElementaryStreamChanged", source, StringComparison.Ordinal);
        Assert.Contains("TitleChanged += OnTitleChanged", source, StringComparison.Ordinal);
        Assert.Contains("SetAudioTrack(trackId)", source, StringComparison.Ordinal);
        Assert.Contains("SetSpu(trackId)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Android_seek_keeps_the_requested_position_until_libvlc_reaches_it()
    {
        var source = Android("AndroidVideo.cs");

        Assert.Contains("_pendingSeek >= 0", source, StringComparison.Ordinal);
        Assert.Contains("TimeChanged", source, StringComparison.Ordinal);
        Assert.Contains("_player.Time =", source, StringComparison.Ordinal);
        Assert.Contains("IVideoBufferingState", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run", source, StringComparison.Ordinal);
        Assert.Contains(":input-fast-seek", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Android_touch_replays_batched_motion_history_for_fling_velocity()
    {
        var source = Android("CupriHostView.cs");

        Assert.Contains("e.HistorySize + 1", source, StringComparison.Ordinal);
        Assert.Contains("GetHistoricalEventTime", source, StringComparison.Ordinal);
        Assert.Contains("GetHistoricalX", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Android_video_is_connected_to_a_native_media_session()
    {
        var backend = Android("AndroidVideoBackend.cs");
        var session = Android("AndroidMediaSession.cs");

        Assert.Contains("new AndroidMediaSession", backend, StringComparison.Ordinal);
        Assert.Contains("MediaSession", session, StringComparison.Ordinal);
        Assert.Contains("Notification.MediaStyle", session, StringComparison.Ordinal);
        Assert.Contains("ActionSeekTo", session, StringComparison.Ordinal);
    }
}
