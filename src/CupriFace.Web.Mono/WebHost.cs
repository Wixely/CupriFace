using CupriFace;

namespace CupriFace.Web;

/// <summary>
/// Runs a <see cref="CupriApp"/> in the browser, on a &lt;canvas&gt;: the same app
/// <see cref="CupriFace.Shell.DesktopHost"/> runs in a window and <c>CupriActivity</c> runs on a
/// phone, rendered by the engine itself — no browser engine, no JS in the UI.
///
/// <para>An app's whole <c>Program.cs</c> is one line:</para>
/// <code>
/// WebHost.Run(new MyApp());
/// </code>
///
/// <para>Unlike the desktop host this returns immediately, and that is the browser's shape rather
/// than an omission: the page owns the frame loop (<c>requestAnimationFrame</c>), so <c>Main</c>
/// hands the host an app and gets out of the way. The .NET runtime stays resident and the JS half
/// drives it from there. A <c>Main</c> that blocked would hang the boot before the first frame,
/// because the loader awaits it before calling into the host at all.</para>
/// </summary>
public static class WebHost
{
    /// <param name="app">The portable application definition — the same <see cref="CupriApp"/> the
    /// desktop and Android hosts take, unchanged.</param>
    /// <param name="configure">Host-composition hook, run once after the document is built — the
    /// same seam <see cref="CupriFace.Shell.DesktopHost.Run"/> offers, for anything an app wants on
    /// the live document (a component registry, a font, an event handler). Kept OUT of
    /// <see cref="CupriApp.Configure"/> on purpose: the app class is shared with hosts that must
    /// not reference browser-only capabilities.</param>
    public static void Run(CupriApp app, Action<CupriDocument>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (Interop.Pending is not null || Interop.Started)
            throw new InvalidOperationException(
                "WebHost.Run was called twice. One page hosts one app; to change what is on screen, " +
                "push a new app through the running document instead.");
        Interop.Pending = app;
        Interop.Configure = configure;
        Console.WriteLine($"[CupriFace] host ready for {app.GetType().Name}.");
    }
}
