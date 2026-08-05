using System.Diagnostics;
using CupriFace;
using CupriFace.Interaction;

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

            // Transparent apps clear to a fully-transparent framebuffer so the desktop shows through
            // (premultiplied output is exactly what the OS compositor wants — no conversion needed).
            ctx.Canvas.Clear(app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background);
            if (doc.HasAnimations) doc.Animate(clock.Elapsed.TotalSeconds); // drive @keyframes (spinner, etc.)

            ctx.Canvas.Save();
            if (scale != 1f) ctx.Canvas.Scale(scale);
            doc.Render(ctx.Canvas, p.LogicalWidth, p.LogicalHeight);
            ctx.Canvas.Restore();
        }

        try
        {
            var window = new SkiaWindow(app.Title, app.Width, app.Height, app.Transparent, app.Frameless, app.TopMost);
            window.Render += Draw;
            window.PointerDown += (x, y, clicks) => doc.DispatchClick(x / scale, y / scale, clicks);
            window.RightPointerDown += (x, y) => doc.DispatchContextMenu(x / scale, y / scale);
            window.PointerMove += (x, y) => doc.DispatchPointerMove(x / scale, y / scale);
            window.PointerUp += (x, y) => doc.DispatchPointerUp(x / scale, y / scale);
            window.PointerWheel += (x, y, dy) => doc.DispatchWheel(x / scale, y / scale, -dy * 50f); // wheel up → scroll up
            window.TextEntered += t => doc.DispatchKey(t, EditKey.None);
            window.EditKeyPressed += (k, mods) => doc.DispatchKey(null, k, mods);
            window.Shortcut += (ch, mods) => Shortcut(doc, ch, mods, () => window.ClipboardText, v => window.ClipboardText = v);
            doc.ContextRequested += cmd => ContextAction(doc, cmd, () => window.ClipboardText, v => window.ClipboardText = v);
            window.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CupriFace] GPU unavailable ({ex.GetType().Name}); using the SDL software window.");
            using var window = new SdlSoftwareWindow(app.Title, app.Width, app.Height, app.Transparent, app.Frameless, app.TopMost);
            window.Render += Draw;
            window.PointerDown += (x, y, clicks) => doc.DispatchClick(x / scale, y / scale, clicks);
            window.RightPointerDown += (x, y) => doc.DispatchContextMenu(x / scale, y / scale);
            window.PointerMove += (x, y) => doc.DispatchPointerMove(x / scale, y / scale);
            window.PointerUp += (x, y) => doc.DispatchPointerUp(x / scale, y / scale);
            window.PointerWheel += (x, y, dy) => doc.DispatchWheel(x / scale, y / scale, -dy * 50f); // wheel up → scroll up
            window.TextEntered += t => doc.DispatchKey(t, EditKey.None);
            window.EditKeyPressed += (k, mods) => doc.DispatchKey(null, k, mods);
            window.Shortcut += (ch, mods) => Shortcut(doc, ch, mods, () => window.ClipboardText, v => window.ClipboardText = v);
            doc.ContextRequested += cmd => ContextAction(doc, cmd, () => window.ClipboardText, v => window.ClipboardText = v);
            window.Run();
        }
    }

    // Text shortcuts common to both hosts. The engine owns selection/editing; the host owns the
    // OS clipboard (get/set passed in) — keeping the engine free of any platform clipboard code.
    private static void Shortcut(CupriDocument doc, char ch, KeyMods mods, Func<string?> getClip, Action<string> setClip)
    {
        switch (ch)
        {
            case 'a': doc.DispatchKey(null, EditKey.SelectAll); break;
            case 'c': if (doc.CopySelection() is { } cp) setClip(cp); break;
            case 'x': if (doc.CutSelection() is { } ct) setClip(ct); break;
            case 'v': if (getClip() is { Length: > 0 } pv) doc.DispatchKey(pv, EditKey.None); break;
            case 'z': if (mods.HasFlag(KeyMods.Shift)) doc.Redo(); else doc.Undo(); break; // Ctrl+Shift+Z = redo
            case 'y': doc.Redo(); break;
        }
    }

    // A context-menu command routes through the SAME clipboard seam as the keyboard shortcuts.
    private static void ContextAction(CupriDocument doc, ContextCommand cmd, Func<string?> getClip, Action<string> setClip)
    {
        switch (cmd)
        {
            case ContextCommand.Cut: if (doc.CutSelection() is { } ct) setClip(ct); break;
            case ContextCommand.Copy: if (doc.CopySelection() is { } cp) setClip(cp); break;
            case ContextCommand.Paste: if (getClip() is { Length: > 0 } pv) doc.DispatchKey(pv, EditKey.None); break;
            case ContextCommand.SelectAll: doc.DispatchKey(null, EditKey.SelectAll); break;
        }
    }
}
