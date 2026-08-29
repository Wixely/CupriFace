using System.Runtime.InteropServices;

namespace CupriFace.Web;

// The page pushing browser-video truth back in: metadata, readiness, play state, position, end.
// Declarations only — what each does to a player is WebHostCore's, shared with the Mono host.
// ints rather than bools, because the C ABI this host talks over has no bool.

public static unsafe partial class Interop
{
    [UnmanagedCallersOnly(EntryPoint = "VideoMeta")]
    public static void VideoMeta(int id, double duration, int width, int height)
        => Guard("VideoMeta", () => WebHostCore.VideoMeta(id, duration, width, height));

    [UnmanagedCallersOnly(EntryPoint = "VideoReady")]
    public static void VideoReady(int id) => Guard("VideoReady", () => WebHostCore.VideoReady(id));

    [UnmanagedCallersOnly(EntryPoint = "VideoPlayState")]
    public static void VideoPlayState(int id, int playing)
        => Guard("VideoPlayState", () => WebHostCore.VideoPlayState(id, playing != 0));

    [UnmanagedCallersOnly(EntryPoint = "VideoTime")]
    public static void VideoTime(int id, double seconds) => Guard("VideoTime", () => WebHostCore.VideoTime(id, seconds));

    [UnmanagedCallersOnly(EntryPoint = "VideoEnded")]
    public static void VideoEnded(int id) => Guard("VideoEnded", () => WebHostCore.VideoEnded(id));
}
