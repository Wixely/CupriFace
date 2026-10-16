using CupriFace.Media;

namespace CupriFace.Android;

/// <summary>
/// What the Android host needs from a video backend, over and above <see cref="IVideoBackend"/>.
///
/// <para>This interface is the whole reason the decoder can live in a DIFFERENT package. The host
/// has to do two things no portable backend contract covers — clear the frame to transparent only
/// while something is actually underneath it, and move each underlay view onto its element's box
/// after every painted frame — and both are answers only the backend has. Naming the two keeps
/// <c>CupriFace.Android</c> free of any decoder, so an app with no <c>&lt;cupri-video&gt;</c> pays
/// neither the native libraries nor their licence obligations.</para>
///
/// <para>Implemented by <c>CupriFace.Media.Android</c>; an application may implement it instead to
/// put its own decoder under the hole.</para>
/// </summary>
public interface IAndroidVideoBackend : IVideoBackend, IDisposable
{
    /// <summary>True while any player can show pixels. The frame clears to TRANSPARENT only then,
    /// so an app whose video has not loaded paints its opaque background exactly as before.</summary>
    bool AnyReady { get; }

    /// <summary>True when an active player can be handed to Android's system picture-in-picture
    /// window. Backends that do not support PiP keep the default false value.</summary>
    bool PictureInPictureReady => false;

    /// <summary>The active picture's natural aspect ratio, used to size the system PiP window.</summary>
    (int W, int H)? PictureInPictureSize => null;

    /// <summary>Move the active platform video view into or out of the activity-wide PiP layout.
    /// The decoder and media session must remain alive across this transition.</summary>
    void SetPictureInPictureMode(bool active) { }

    /// <summary>Called after each painted frame: move every underlay to its element's on-screen box,
    /// in PHYSICAL pixels. The engine has already punched a transparent hole there, so the two must
    /// agree to the pixel or the video shows a seam.</summary>
    void SyncRects(CupriDocument doc, float inputScale);
}

/// <summary>
/// The host-owned pieces a video backend needs in order to exist: where to put its views, and how
/// to reach the two threads that matter. Handed to
/// <c>CupriActivity.CreateVideoBackend</c> so a backend in another package can be constructed
/// without that package knowing anything about the host's internals.
/// </summary>
/// <param name="Context">The activity, for platform objects (cache dir, media session, views).</param>
/// <param name="Underlays">The layer BENEATH the GL surface. Video views go here, so the engine's
/// punched hole reveals them; it is already added below the surface by the activity.</param>
/// <param name="Invalidate">Request a repaint. Thread-safe.</param>
/// <param name="RunOnUi">Run an action on the Android UI thread — view work must go through this.</param>
public sealed record AndroidVideoServices(
    global::Android.Content.Context Context,
    global::Android.Widget.FrameLayout Underlays,
    Action Invalidate,
    Action<Action> RunOnUi);
