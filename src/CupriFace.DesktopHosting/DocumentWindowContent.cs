using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Hosting;

/// <summary>
/// Everything a window an app opened beside its main one does with its document, minus the window:
/// where input goes, when a frame is due, and how one is drawn. The native window (SDL, in the
/// Shell) only turns OS events into these calls and blits what <see cref="Render"/> drew.
///
/// <para>Kept apart from the window so the routing can be tested headless. Coordinates in are the
/// window's LOGICAL client units — the monitor's scale already divided out by the window, as the
/// main window's do — and this class divides out the document's own present scale, the host's
/// usual last step.</para>
///
/// <para>Clipboard and context-menu commands route through the same <see cref="DocumentInput"/> the
/// main window uses, so Ctrl+C in a second window does exactly what it does in the first.</para>
/// </summary>
internal sealed class DocumentWindowContent : IDisposable
{
    private const int MiddleMousePointerId = -1;

    private readonly CupriApp? _app;
    private readonly Func<string?> _readClipboard;
    private readonly Action<string> _writeClipboard;
    private readonly Action<string>? _openExternal;
    private readonly Action<string> _onClipboardWrite;
    private readonly Action<ContextCommand> _onContext;
    private readonly Action<NavigateEvent> _onNavigated;
    private bool _dirty = true;
    private bool _middleCaptured;
    private float _scale = 1f;          // the document's own present scale (P)
    private double _lastRefresh;

    /// <param name="document">The document to show.</param>
    /// <param name="app">The app it was created from, when it was — its <see cref="CupriApp.Present"/>
    /// lays the document out and its <see cref="CupriApp.RefreshIntervalSeconds"/> re-binds it; null
    /// lays it out responsively at the window's size.</param>
    /// <param name="background">What is behind the document.</param>
    /// <param name="readClipboard">The OS clipboard, read.</param>
    /// <param name="writeClipboard">The OS clipboard, written.</param>
    /// <param name="openExternal">Opens an external link in the OS browser; null leaves links to the app.</param>
    public DocumentWindowContent(CupriDocument document, CupriApp? app, SKColor background,
        Func<string?> readClipboard, Action<string> writeClipboard, Action<string>? openExternal = null)
    {
        Document = document;
        _app = app;
        Background = background;
        _readClipboard = readClipboard;
        _writeClipboard = writeClipboard;
        _openExternal = openExternal;

        _onClipboardWrite = v => _writeClipboard(v);
        _onContext = cmd => { DocumentInput.ContextAction(Document, cmd, _readClipboard, _writeClipboard); _dirty = true; };
        _onNavigated = e => { if (e.External) _openExternal?.Invoke(e.Href); };
        Document.ClipboardWriteRequested += _onClipboardWrite;
        Document.ContextRequested += _onContext;
        Document.Navigated += _onNavigated;
        // This window forwards key releases, as the main windows do; see DiagonalNavigation.
        Document.ReportsKeyUp = true;
    }

    public CupriDocument Document { get; }

    public SKColor Background { get; }

    /// <summary>The document's present scale as of the last frame; pointer coordinates are divided by it.</summary>
    public float PresentScale => _scale;

    /// <summary>Something outside input changed what this window shows: draw at the next chance.</summary>
    public void Invalidate() => _dirty = true;

    // ---- input: each returns whether it changed something (and marks a frame due if so) -----------

    public bool PointerDown(float x, float y, int clicks) => Mark(DocumentInput.PointerDown(Document, x / _scale, y / _scale, Math.Max(1, clicks)));

    public bool PointerMove(float x, float y)
    {
        var changed = DocumentInput.PointerMove(Document, x / _scale, y / _scale);
        if (_middleCaptured) changed |= Document.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Move, x / _scale, y / _scale);
        return Mark(changed);
    }

    public bool PointerUp(float x, float y) => Mark(DocumentInput.PointerUp(Document, x / _scale, y / _scale));

    public bool RightPointerDown(float x, float y) => Mark(Document.DispatchContextMenu(x / _scale, y / _scale));

    public bool MiddlePointerDown(float x, float y) =>
        Mark(_middleCaptured = Document.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Down, x / _scale, y / _scale));

    public bool MiddlePointerUp(float x, float y)
    {
        var changed = _middleCaptured && Document.DispatchMiddlePointer(MiddleMousePointerId, PointerPhase.Up, x / _scale, y / _scale);
        _middleCaptured = false;
        return Mark(changed);
    }

    /// <summary>A wheel turned at (x, y) by <paramref name="notches"/> (positive = away from the user).</summary>
    public bool Wheel(float x, float y, float notches, KeyMods mods) =>
        Mark(DocumentInput.Wheel(Document, x / _scale, y / _scale, notches, mods));

    public bool Key(EditKey key, KeyMods mods) => Mark(Document.DispatchKey(null, key, mods));

    public bool KeyUp(EditKey key) => Mark(Document.DispatchKeyUp(key));

    public bool Text(string text) => Mark(Document.DispatchKey(text, EditKey.None));

    /// <summary>A Ctrl/Cmd chord: the clipboard and undo ones, page zoom, or the app's own shortcut.</summary>
    public bool Shortcut(char ch, KeyMods mods)
    {
        DocumentInput.Shortcut(Document, ch, mods, _readClipboard, _writeClipboard);
        return Mark(true);
    }

    /// <summary>The window lost the keyboard: the key-ups for anything held now go elsewhere.</summary>
    public void FocusLost() => Document.ReleaseAllKeys();

    public bool FilesDropped(float x, float y, string[] paths) =>
        Mark(Document.DispatchFileDrop(x / _scale, y / _scale, Array.ConvertAll(paths, DroppedFile.FromPath)));

    public Style.CursorType CursorAt(float x, float y) => Document.CursorAt(x / _scale, y / _scale);

    private bool Mark(bool changed)
    {
        if (changed) _dirty = true;
        return changed;
    }

    // ---- frames --------------------------------------------------------------------------------

    /// <summary>
    /// Whether a frame is due at <paramref name="now"/> (seconds on the host's clock): input changed
    /// something, work was posted to the document, an image arrived, something is animating, or the
    /// app's refresh interval elapsed. Consumes the reasons it reports.
    /// </summary>
    public bool NeedsRender(double now)
    {
        if (_app is { RefreshIntervalSeconds: > 0 } app && now - _lastRefresh >= app.RefreshIntervalSeconds)
        {
            _lastRefresh = now;
            Document.Refresh();
            _dirty = true;
        }
        if (Document.ConsumeImageArrived()) _dirty = true;
        // Posted work counts here too (HasActiveAnimations includes it): a dialog's result for THIS
        // document arrives through its Post, and only a frame drains it.
        if (Document.HasActiveAnimations) _dirty = true;
        var due = _dirty;
        _dirty = false;
        return due;
    }

    /// <summary>
    /// Draw a frame into a canvas of <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/>
    /// device pixels on a monitor of scale <paramref name="deviceScale"/>.
    /// </summary>
    public void Render(SKCanvas canvas, int pixelWidth, int pixelHeight, float deviceScale, double now)
    {
        var host = HostScale.ForFramebuffer(pixelWidth, pixelHeight, deviceScale);
        var p = _app?.Present(host.LogicalClientWidth, host.LogicalClientHeight)
                ?? PresentInfo.Responsive(host.LogicalClientWidth, host.LogicalClientHeight);
        _scale = p.Scale <= 0 ? 1f : p.Scale;
        var effective = host.WithPresentScale(_scale).EffectiveScale;
        Document.Surfaces.DeviceScale = effective;
        canvas.Clear(Background);
        if (Document.HasAnimations || Document.HasActiveTransitions) Document.Animate(now);
        canvas.Save();
        if (effective != 1f) canvas.Scale(effective);
        Document.Render(canvas, p.LogicalWidth, p.LogicalHeight);
        canvas.Restore();
    }

    public void Dispose()
    {
        Document.ClipboardWriteRequested -= _onClipboardWrite;
        Document.ContextRequested -= _onContext;
        Document.Navigated -= _onNavigated;
    }
}
