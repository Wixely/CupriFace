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

    /// <summary>The OPTIONAL media package — the decoder lives apart from the host.</summary>
    private static string Media(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CupriFace.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", "CupriFace.Media.Android", file));
    }

    [Fact]
    public void Android_video_uses_libvlc_for_tracks_and_chapters()
    {
        var source = Media("VlcPlayer.cs");

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
        var source = Media("VlcPlayer.cs");

        Assert.Contains("PendingSeekLive()", source, StringComparison.Ordinal);
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
        var backend = Media("VlcVideoBackend.cs");
        var session = Media("AndroidMediaSession.cs");

        Assert.Contains("new AndroidMediaSession", backend, StringComparison.Ordinal);
        Assert.Contains("MediaSession", session, StringComparison.Ordinal);
        Assert.Contains("Notification.MediaStyle", session, StringComparison.Ordinal);
        Assert.Contains("ActionSeekTo", session, StringComparison.Ordinal);
        // The notification's small icon must fall back when the app declares NO icon (Icon == 0),
        // and a rejected notification must be logged, not thrown: Android kills the process for
        // either, on the main thread, the moment playback starts. The sample app has no icon, so
        // the emulator gate is the real check -- this only pins the two lines it depends on.
        Assert.Contains("appIcon != 0 ? appIcon", session, StringComparison.Ordinal);
        Assert.Contains("try { _notifications?.Notify(", session, StringComparison.Ordinal);
    }

    /// <summary>
    /// The HOST package must carry no decoder. This is the whole point of the split: VideoLAN's
    /// native libraries are tens of megabytes across the ABIs and are LGPL, so an app that shows no
    /// &lt;cupri-video&gt; must not acquire either by merely referencing the Android host. A
    /// PackageReference added back here would be invisible in every other way — it builds, it runs,
    /// and it silently taxes every consumer.
    /// </summary>
    [Fact]
    public void The_android_host_package_does_not_depend_on_a_decoder()
    {
        var host = Project("CupriFace.Android", "CupriFace.Android.csproj");

        Assert.DoesNotContain("LibVLC", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VideoLAN", host, StringComparison.OrdinalIgnoreCase);

        // …and the decoder's own package is where the libraries and their licence live.
        var media = Project("CupriFace.Media.Android", "CupriFace.Media.Android.csproj");
        Assert.Contains("VideoLAN.LibVLC.Android", media, StringComparison.Ordinal);
        Assert.Contains("LibVLC-LGPL-2.1-or-later.txt", media, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.md", media, StringComparison.Ordinal);
    }

    /// <summary>Video is opt-in through a seam the host owns, so the host never names the decoder.
    /// If CupriActivity constructed a backend itself, the reference would come straight back.</summary>
    [Fact]
    public void Video_is_opt_in_through_a_seam_the_host_does_not_implement()
    {
        Assert.Contains("public interface IAndroidVideoBackend", Android("AndroidVideoSeam.cs"), StringComparison.Ordinal);

        var activity = Android("CupriActivity.cs");
        Assert.Contains("protected virtual IAndroidVideoBackend? CreateVideoBackend", activity, StringComparison.Ordinal);
        // The host must not REFERENCE the decoder's package. (Its doc comment may well name the
        // backend — that is how an author discovers the opt-in.)
        Assert.DoesNotContain("CupriFace.Media.Android",
                              Project("CupriFace.Android", "CupriFace.Android.csproj"), StringComparison.Ordinal);

        // The host takes whatever it is handed, rather than newing one up.
        Assert.Contains("public void UseVideo(IAndroidVideoBackend backend)", Android("AndroidHost.cs"), StringComparison.Ordinal);
        Assert.Contains("IAndroidVideoBackend", Media("VlcVideoBackend.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A requested seek is believed only for a bounded time. Position reports the TARGET while a
    /// seek is outstanding — that is what stops a drag snapping backwards — and the only thing that
    /// clears it is a TimeChanged near the target. A seek past the seekable range, or a source that
    /// stops reporting time, never produces one, and the scrubber would then read the target for
    /// ever while playback continued elsewhere.
    /// </summary>
    [Fact]
    public void An_outstanding_seek_expires_rather_than_pinning_the_position()
    {
        var source = Media("VlcPlayer.cs");

        Assert.Contains("SeekBeliefMs", source, StringComparison.Ordinal);
        Assert.Contains("private bool PendingSeekLive()", source, StringComparison.Ordinal);

        // Every reader goes through the expiry, or one of them is left believing a dead seek.
        // (The single bare assignment left is the clear inside the expiry helper itself.)
        Assert.DoesNotContain("_pendingSeek >= 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingSeek < 0 ||", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Disposal waits for workers already inside a LibVLC call. The seek and metadata workers exist
    /// precisely because those calls can block on the network, so "check a flag, then call" is not
    /// enough: the player can be freed in the gap, and the worker then calls into freed memory —
    /// which crashes the process rather than throwing.
    /// </summary>
    [Fact]
    public void Disposal_drains_workers_that_are_inside_a_native_call()
    {
        var source = Media("VlcPlayer.cs");

        // The gate itself — claim under the lock, wait for the last worker out — is NativeCallGate,
        // tested by behaviour in NativeCallGateTests. What is pinned here is that this player uses
        // it on its OWN lock (so `lock (_gate) { if (Disposed) … }` stays atomic with the close)
        // and that Dispose closes it before freeing anything.
        Assert.Contains("new NativeCallGate(_gate)", source, StringComparison.Ordinal);
        Assert.Contains("private bool TryEnterNative() => _native.TryEnter();", source, StringComparison.Ordinal);
        var dispose = source[source.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        var close = dispose.IndexOf("_native.Close()", StringComparison.Ordinal);
        var free = dispose.IndexOf("_player.Dispose()", StringComparison.Ordinal);
        Assert.True(close >= 0 && close < free, "Dispose must close the native gate before freeing the player");

        // Every native MEDIA is built inside the gate (#317). The worker scan below only sees
        // Task.Run bodies, and the local-file path built its media after an `await` instead —
        // against the shared LibVLC instance the backend had already freed when the activity was
        // destroyed during startup. libvlc_retain asserted and the process died.
        var builds = source.Split("new VlcMedia(", StringSplitOptions.None).Length - 1;
        var gated = source.Split("Publish(() => new VlcMedia(", StringSplitOptions.None).Length - 1;
        Assert.True(builds > 0 && builds == gated, $"{builds - gated} of {builds} VlcMedia constructions are outside Publish");
        Assert.Contains("private void Publish(Func<VlcMedia> build) => _native.Run(", source, StringComparison.Ordinal);

        // Every worker that touches the native player enters the gate first. Checked per worker
        // rather than by counting, because one that does only file I/O rightly needs no gate.
        var workers = source.Split("Task.Run", StringSplitOptions.None).Skip(1).ToArray();
        Assert.NotEmpty(workers);
        foreach (var worker in workers)
        {
            // The body up to the next worker is close enough to attribute a native call to it.
            if (!worker.Contains("_player.", StringComparison.Ordinal)) continue;
            // Two guards are sound, and for the same reason — Dispose closes the gate under
            // _gate, so either holding the gate or taking the lock excludes it. A bare flag read
            // followed by a native call is the one that is not.
            Assert.True(worker.Contains("TryEnterNative()", StringComparison.Ordinal)
                        || worker.Contains("lock (_gate)", StringComparison.Ordinal),
                        $"a worker touching the native player is unguarded: {worker[..Math.Min(120, worker.Length)]}");
        }
    }

    /// <summary>
    /// The in-repo consumer must actually be able to PLAY, and one line decides it — a line that
    /// looks redundant beside the ProjectReference above it and would not survive a tidy-up.
    ///
    /// <para>VideoLAN.LibVLC.Android ships <c>libvlc.so</c> from its <c>build\*.targets</c>, as
    /// AndroidNativeLibrary items conditioned on the ABIs or RuntimeIdentifiers of the project
    /// importing them — and NuGet imports those targets only into the project that references the
    /// package. Measured here: in a LIBRARY both properties are empty
    /// (CupriFace.Media.Android: AndroidSupportedAbis="", RuntimeIdentifiers=""), so declaring it
    /// there contributes nothing; in an APP the Android SDK has set them
    /// (AndroidViewer: RuntimeIdentifiers="android-x64"), so declaring it here contributes the
    /// library. Referencing CupriFace.Media.Android alone yields the managed binding with no .so
    /// behind it, and nothing fails until LibVLC is constructed — on a device, which is the one
    /// place this suite cannot look.</para>
    ///
    /// <para>RuntimeIdentifiers is a SIZE choice rather than a correctness one (libvlc.so is ~43 MB
    /// per ABI) and is asserted only because the emulator gate installs this APK.</para>
    /// </summary>
    [Fact]
    public void The_android_sample_packages_the_native_decoder_it_opts_into()
    {
        var sample = Sample("AndroidViewer", "AndroidViewer.csproj");

        Assert.Contains("CupriFace.Media.Android.csproj", sample, StringComparison.Ordinal);
        Assert.Contains("VideoLAN.LibVLC.Android", sample, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifiers>", sample, StringComparison.Ordinal);
        // Unsupported since .NET Android 36 — it warns XA0036 and is ignored.
        Assert.DoesNotContain("<AndroidSupportedAbis>", sample, StringComparison.Ordinal);

        // And it opts in through the seam rather than reaching past it.
        var activity = Sample("AndroidViewer", "MainActivity.cs");
        Assert.Contains("CreateVideoBackend", activity, StringComparison.Ordinal);
        Assert.Contains("new VlcVideoBackend(services)", activity, StringComparison.Ordinal);
    }

    private static string Sample(string directory, string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "samples", directory, file));
    }

    private static string Project(string directory, string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", directory, file));
    }
}
