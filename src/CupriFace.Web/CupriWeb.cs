using CupriFace;

namespace CupriFace.Web;

/// <summary>
/// The browser host's entry point — the web sibling of <c>DesktopHost.Run</c> and
/// <c>CupriActivity</c>. An app's whole <c>Program.cs</c> is one line:
///
/// <code>
/// CupriWeb.Run(new MyApp());
/// </code>
///
/// <para>Unlike the desktop host this does not block, and that is the browser's shape rather than
/// an omission: the page owns the frame loop (<c>requestAnimationFrame</c>), so <c>Main</c> hands
/// the host an app and returns. The .NET runtime stays resident and the JS half drives it from
/// there. A <c>Main</c> that never returned would hang the boot before the first frame, because
/// the loader awaits it before calling into the host at all.</para>
/// </summary>
public static class CupriWeb
{
    /// <summary>Hand the host the app to run. Call once, from <c>Main</c>.</summary>
    /// <param name="app">The app to run — the same <see cref="CupriApp"/> the desktop and Android
    /// hosts take, unchanged.</param>
    /// <param name="configure">Optional hook to touch the document after it is created and before
    /// the first frame (the host-composition seam the desktop host calls <c>ConfigureDocument</c>).
    /// Use it for anything an app wants on the live document — a custom component registry, a font,
    /// an event handler.</param>
    public static void Run(CupriApp app, Action<CupriDocument>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (WebHost.Pending is not null || WebHost.Started)
            throw new InvalidOperationException(
                "CupriWeb.Run was called twice. One page hosts one app; to swap what is on screen, " +
                "push a new app through the running document instead.");
        WebHost.Pending = app;
        WebHost.Configure = configure;
        Console.WriteLine($"[CupriFace] host ready for {app.GetType().Name}.");
    }
}
