namespace CupriFace.Interaction;

/// <summary>
/// A window-level request the engine cannot perform itself (it owns pixels, not the OS window).
/// Raised via <see cref="CupriDocument.WindowCommandRequested"/> — from a
/// <c>data-window-command</c> element (e.g. a video's ⛶ button) — and performed by the host:
/// the desktop windows toggle their OS fullscreen state, the web host calls the browser's
/// Fullscreen API. Same engine→host split as <c>Navigated</c> and clipboard.
/// </summary>
public enum WindowCommand
{
    ToggleFullscreen,
    EnterFullscreen,
    ExitFullscreen,
}
