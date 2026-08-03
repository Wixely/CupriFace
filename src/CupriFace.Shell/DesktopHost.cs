using System.Diagnostics;
using CupriFace;

namespace CupriFace.Shell;

/// <summary>
/// Runs a <see cref="CupriApp"/> in a desktop window: tries GPU (GL), falls back to the
/// SDL software window, and wires pointer input to the app's document. The web host
/// (<c>CupriView</c> in the WASM project) renders the *same* app to a &lt;canvas&gt;.
/// </summary>
public static class DesktopHost
{
    public static void Run(CupriApp app)
    {
        var doc = app.CreateDocument();
        var clock = Stopwatch.StartNew();

        void Draw(RenderContext ctx)
        {
            ctx.Canvas.Clear(app.Background);
            if (doc.HasAnimations) doc.Animate(clock.Elapsed.TotalSeconds); // drive @keyframes (spinner, etc.)
            doc.Render(ctx.Canvas, ctx.Width, ctx.Height);
        }

        try
        {
            var window = new SkiaWindow(app.Title, app.Width, app.Height);
            window.Render += Draw;
            window.PointerDown += (x, y) => doc.DispatchClick(x, y);
            window.PointerMove += (x, y) => doc.DispatchPointerMove(x, y);
            window.PointerUp += (x, y) => doc.DispatchPointerUp(x, y);
            window.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CupriFace] GPU unavailable ({ex.GetType().Name}); using the SDL software window.");
            using var window = new SdlSoftwareWindow(app.Title, app.Width, app.Height);
            window.Render += Draw;
            window.PointerDown += (x, y) => doc.DispatchClick(x, y);
            window.PointerMove += (x, y) => doc.DispatchPointerMove(x, y);
            window.PointerUp += (x, y) => doc.DispatchPointerUp(x, y);
            window.Run();
        }
    }
}
