using CupriFace;

namespace CupriFace.Web;

/// <summary>
/// Runs a <see cref="CupriApp"/> in the browser, on a &lt;canvas&gt;, compiled ahead of time with
/// the NativeAOT-LLVM backend. The API is deliberately identical to
/// <c>CupriFace.Web.Mono</c>'s — same namespace, same type, same method — so an app moves between
/// the two runtimes by changing a <c>PackageReference</c> and nothing else.
///
/// <para>An app's whole <c>Program.cs</c>:</para>
/// <code>
/// WebHost.Run(new MyApp());
/// </code>
///
/// <para>Like the Mono host this returns immediately: the page owns the frame loop
/// (<c>requestAnimationFrame</c>), so <c>Main</c> hands the host an app and gets out of the way.
/// The runtime stays resident and the JS half drives it from there.</para>
/// </summary>
public static class WebHost
{
    /// <param name="app">The portable application definition — the same <see cref="CupriApp"/> the
    /// desktop, Android and Mono-web hosts take, unchanged.</param>
    /// <param name="configure">Host-composition hook, run once after the document is built — the
    /// same seam every other host offers, for anything an app wants on the live document.</param>
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
