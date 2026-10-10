using System.Runtime.InteropServices;
using CupriFace.Media;
using CupriFace.Media.Windows;
using CupriFace.Paint;
using CupriFace.Shell;

// The Windows media gate. Everything here goes through the shipped adapter rather than LibVLCSharp
// directly, because the adapter is what breaks: the two faults the media-player integration found
// were its native-library discovery and its resume path, and neither is visible from the outside.

var failures = 0;
void Fail(string why) { Console.WriteLine($"::error::{why}"); failures++; }
void Ok(string what) => Console.WriteLine($"  ok   {what}");

var media = args.Length > 0 ? args[0] : null;
if (media is null || !File.Exists(media))
{
    Fail($"usage: dotnet run -- <media-file>   (got {media ?? "nothing"})");
    return 1;
}
Console.WriteLine($"media: {media} ({new FileInfo(media).Length} bytes)");

// 1. Construction resolves the native directory and initialises LibVLC. This is the whole of #303:
//    under IncludeAllContentForSelfExtract the obvious directories point at the extraction folder,
//    and the failure is "could not load native library" at the first Open, far from the cause.
LibVlcVideoBackend backend;
try
{
    backend = new LibVlcVideoBackend();
    Ok("backend constructed — LibVLC natives found and initialised");
}
catch (Exception e)
{
    Fail($"could not construct the backend: {e.GetType().Name}: {e.Message}");
    return 1;
}

using (backend)
using (var host = new HiddenHostWindow())
{
    var player = backend.Open(new VideoSourceProbe(media).Source);

    // 2. The adapter only starts once it has a parent window to put its child HWND in, so the gate
    //    stands a real one up rather than skipping the path that actually runs in an app.
    if (player is IHostCompositedSurfaceSource composited)
    {
        composited.Attach(new GateSurfaceContext(host.Handle));
        composited.Arrange(new HostSurfacePlacement(0, 0, 320, 180, 0, 0, 0, 0,
                                                    true, "contain", HostSurfaceTransform.Identity, 1f));
        Ok("attached a child window to a real parent HWND");
    }
    else Fail("the player is not host-composited — the Win32 surface path was not exercised");

    player.Muted = true;
    player.Play();

    if (!Wait(() => player.Playing, TimeSpan.FromSeconds(20))) Fail("playback never started");
    else if (!Wait(() => player.Surface.NaturalSize is { W: > 0, H: > 0 }, TimeSpan.FromSeconds(10)))
        Fail("no natural size reported — the video track was never opened");
    else Ok($"playing — {player.Surface.NaturalSize?.W}x{player.Surface.NaturalSize?.H}");

    if (player is IVideoTrackSelector tracks)
    {
        if (!Wait(() => tracks.AudioTracks.Count > 0, TimeSpan.FromSeconds(5)))
            Fail("no audio tracks enumerated from a file that has them");
        else Ok($"tracks — audio={tracks.AudioTracks.Count} subtitle={tracks.SubtitleTracks.Count}");
    }
    else Fail("the player does not implement IVideoTrackSelector");

    // 3. The resume regression: Play(media) reassigns the media and restarts a paused stream from
    //    zero, where parameterless Play() resumes the existing input. Caught in the field, not here,
    //    which is the reason this gate exists at all.
    if (!Wait(() => player.Position > 0.15, TimeSpan.FromSeconds(10)))
    {
        // Without this guard the resume check below passes on 0.00s -> 0.00s, which is the shape a
        // gate takes when it has quietly stopped testing anything.
        Fail("playback position never advanced — resume could not be tested");
    }
    else
    {
        player.Pause();
        Thread.Sleep(400);
        var paused = player.Position;
        player.Play();
        Thread.Sleep(900);
        var resumed = player.Position;
        if (paused <= 0.05) Fail($"paused at {paused:0.00}s, so resume proves nothing");
        else if (resumed + 0.05 < paused) Fail($"resume restarted the stream: paused at {paused:0.00}s, resumed at {resumed:0.00}s");
        else Ok($"resume kept its place — paused {paused:0.00}s, resumed {resumed:0.00}s");
    }

    if (player is IHostCompositedSurfaceSource d) { d.Detach(); Ok("detached cleanly"); }
    player.Dispose();
}

Console.WriteLine(failures == 0 ? "\nmedia gate: PASS" : $"\nmedia gate: FAIL ({failures})");
return failures == 0 ? 0 : 1;

static bool Wait(Func<bool> until, TimeSpan limit)
{
    var t0 = DateTime.UtcNow;
    while (!until() && DateTime.UtcNow - t0 < limit) Thread.Sleep(50);
    return until();
}

/// <summary>Builds the internal VideoSource the adapter expects, from a plain path.</summary>
file sealed class VideoSourceProbe(string path)
{
    public VideoSource Source { get; } =
        (VideoSource)typeof(VideoSource)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0]
            .Invoke([new Uri(path).AbsoluteUri, null, null]);
}

/// <summary>The one host feature the Windows adapter asks for.</summary>
file sealed class GateSurfaceContext(nint hwnd) : IHostSurfaceContext
{
    private readonly Win32HostWindow _win32 = new(() => hwnd);
    public object? GetFeature(Type featureType) => featureType == typeof(Win32HostWindow) ? _win32 : null;
    public void RequestFrame() { }
}

/// <summary>A real top-level window for the adapter to parent its video child to. Hidden: the gate
/// is about whether the native path WORKS, and a runner has nobody to show it to.</summary>
internal sealed partial class HiddenHostWindow : IDisposable
{
    private const uint WsOverlapped = 0x00000000, WsClipChildren = 0x02000000;
    public nint Handle { get; }

    public HiddenHostWindow()
    {
        Handle = CreateWindowExW(0, "STATIC", "CupriFace media gate", WsOverlapped | WsClipChildren,
                                 0, 0, 640, 360, 0, 0, 0, 0);
        if (Handle == 0) throw new InvalidOperationException(
            $"could not create the host window (Win32 error {Marshal.GetLastPInvokeError()})");
    }

    public void Dispose() { if (Handle != 0) DestroyWindow(Handle); }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint ex, string cls, string name, uint style,
                                                int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
