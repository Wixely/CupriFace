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
        var scale = 1f; // current present scale, for transforming pointer coordinates
        var lastRefresh = 0.0;

        void Draw(RenderContext ctx)
        {
            var p = app.Present(ctx.Width, ctx.Height);
            scale = p.Scale <= 0 ? 1f : p.Scale;

            // Periodic re-bind so live computed values (e.g. diagnostics) update on their own.
            if (app.RefreshIntervalSeconds > 0 &&
                clock.Elapsed.TotalSeconds - lastRefresh >= app.RefreshIntervalSeconds)
            {
                lastRefresh = clock.Elapsed.TotalSeconds;
                doc.Refresh();
            }

            ctx.Canvas.Clear(app.Background);
            if (doc.HasAnimations) doc.Animate(clock.Elapsed.TotalSeconds); // drive @keyframes (spinner, etc.)

            ctx.Canvas.Save();
            if (scale != 1f) ctx.Canvas.Scale(scale);
            doc.Render(ctx.Canvas, p.LogicalWidth, p.LogicalHeight);
            ctx.Canvas.Restore();
        }

        try
        {
            var window = new SkiaWindow(app.Title, app.Width, app.Height);
            window.Render += Draw;
            window.PointerDown += (x, y) => doc.DispatchClick(x / scale, y / scale);
            window.PointerMove += (x, y) => doc.DispatchPointerMove(x / scale, y / scale);
            window.PointerUp += (x, y) => doc.DispatchPointerUp(x / scale, y / scale);
            window.PointerWheel += (x, y, dy) => doc.DispatchWheel(x / scale, y / scale, -dy * 50f); // wheel up → scroll up
            window.TextEntered += t => doc.DispatchKey(t, CupriFace.Interaction.EditKey.None);
            window.EditKeyPressed += k => doc.DispatchKey(null, k);
            window.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CupriFace] GPU unavailable ({ex.GetType().Name}); using the SDL software window.");
            using var window = new SdlSoftwareWindow(app.Title, app.Width, app.Height);
            window.Render += Draw;
            window.PointerDown += (x, y) => doc.DispatchClick(x / scale, y / scale);
            window.PointerMove += (x, y) => doc.DispatchPointerMove(x / scale, y / scale);
            window.PointerUp += (x, y) => doc.DispatchPointerUp(x / scale, y / scale);
            window.PointerWheel += (x, y, dy) => doc.DispatchWheel(x / scale, y / scale, -dy * 50f); // wheel up → scroll up
            window.TextEntered += t => doc.DispatchKey(t, CupriFace.Interaction.EditKey.None);
            window.EditKeyPressed += k => doc.DispatchKey(null, k);
            window.Run();
        }
    }
}
