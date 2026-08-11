namespace CupriFace.Media;

/// <summary>
/// Opens video sources for <c>&lt;cupri-video&gt;</c>. The engine defines only this seam — it has
/// no codecs. Implementations attach at the HOST composition root (never inside a portable app
/// class, which is shared across hosts): the desktop Viewer registers <c>CupriFace.Media</c>'s
/// WebM backend via <c>DesktopHost.Run(app, d =&gt; d.UseVideo(...))</c>; the web host registers a
/// browser-decoded backend. No backend registered → the element shows its poster and the
/// controls do nothing.
/// </summary>
public interface IVideoBackend
{
    /// <summary>Open a source (same schemes as images: embedded / file / URL). Throwing is
    /// allowed for an unusable source — the document catches it and leaves the poster up.</summary>
    IVideoPlayer Open(string src);
}

/// <summary>
/// One playing (or paused) video. The frames flow through <see cref="Surface"/> into the
/// element's <c>data-cupri-surface</c>; transport state is read by the document each rebuild to
/// label the controls.
///
/// Threading: transport calls (<see cref="Play"/>/<see cref="Pause"/>/setters) arrive on the UI
/// thread, synchronously inside input dispatch — the web backend RELIES on that (the browser
/// only allows unmuted playback while a user gesture is on the stack, so the call chain
/// click → engine → <c>video.play()</c> must never defer). <see cref="Ended"/> MAY be raised
/// from any thread; the document coalesces it and reacts on its own thread.
/// </summary>
public interface IVideoPlayer : IDisposable
{
    /// <summary>The live pixel producer the element paints (see <see cref="Paint.ISurfaceSource"/>).</summary>
    Paint.ISurfaceSource Surface { get; }

    void Play();
    void Pause();
    bool Playing { get; }

    bool Muted { get; set; }
    /// <summary>0..1.</summary>
    double Volume { get; set; }
    bool Loop { get; set; }

    /// <summary>Seconds; 0 until known.</summary>
    double Duration { get; }
    /// <summary>Seconds; setting seeks.</summary>
    double Position { get; set; }

    /// <summary>Playback reached the end (not raised when looping). Any thread.</summary>
    event Action? Ended;
}
