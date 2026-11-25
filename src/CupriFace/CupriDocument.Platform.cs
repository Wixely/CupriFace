namespace CupriFace;

public sealed partial class CupriDocument
{
    /// <summary>
    /// The operating system's Open / Save / folder dialogs for the window this document is in, or
    /// <b>null</b> when the host has none — a browser, Android, a headless test. See
    /// <see cref="Platform.FileDialogs"/>.
    ///
    /// <para>Set by the HOST, after <see cref="CupriApp.Configure"/> has run, so read it when it is
    /// needed (in a click handler) rather than caching it during Configure, where it is still null.</para>
    /// </summary>
    public Platform.FileDialogs? FileDialogs { get; set; }

    /// <summary>
    /// Opens more windows beside this one, or <b>null</b> when the host cannot — see
    /// <see cref="Platform.AppWindows"/>. Set by the host, like <see cref="FileDialogs"/>; a document
    /// shown in a second window gets one too, so that window can open others.
    /// </summary>
    public Platform.AppWindows? Windows { get; set; }
}
