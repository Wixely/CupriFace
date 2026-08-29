using System.Runtime.InteropServices.JavaScript;

namespace CupriFace.Web;

// The page pushing browser-video truth back in: metadata, readiness, play state, position, end.
// Declarations only — what each one does to a player is WebHostCore's, shared with the other host.

internal partial class Interop
{
    [JSExport] internal static void VideoMeta(int id, double duration, int width, int height)
        => WebHostCore.VideoMeta(id, duration, width, height);

    [JSExport] internal static void VideoReady(int id) => WebHostCore.VideoReady(id);

    [JSExport] internal static void VideoPlayState(int id, bool playing) => WebHostCore.VideoPlayState(id, playing);

    [JSExport] internal static void VideoTime(int id, double seconds) => WebHostCore.VideoTime(id, seconds);

    [JSExport] internal static void VideoEnded(int id) => WebHostCore.VideoEnded(id);
}
