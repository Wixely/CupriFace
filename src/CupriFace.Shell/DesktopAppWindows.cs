using System.Diagnostics;
using CupriFace.Hosting;
using CupriFace.Interaction;
using CupriFace.Platform;
using CupriFace.Shell.Dialogs;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// The desktop host's <see cref="AppWindows"/>: each window an app opens is an
/// <see cref="SdlDocumentWindow"/> beside the main one, driven from the main window's tick.
/// </summary>
internal sealed class DesktopAppWindows : AppWindows
{
    private readonly CupriDocument _main;
    private readonly SdlChildWindows _host;
    private readonly Action<string> _openExternal;
    private readonly Stopwatch _clock;
    private int _opened;

    public DesktopAppWindows(CupriDocument main, SdlChildWindows host, Action<string> openExternal, Stopwatch clock)
        : base(main)
    {
        _main = main;
        _host = host;
        _openExternal = openExternal;
        _clock = clock;
    }

    protected override AppWindow? OpenCore(CupriDocument document, WindowOptions options, CupriApp? app)
    {
        if (!_host.EnsureVideo()) return null;
        var native = new SdlDocumentWindow(document, app, options, _host, _openExternal, _clock, ++_opened);
        if (!native.Open())
        {
            native.Dispose();
            return null;
        }
        var window = new DesktopAppWindow(native, options.Title);
        native.Owner = window;
        // A document in a second window can open windows and dialogs of its own; its dialogs are
        // owned by ITS window, so they come up over the window the user was looking at.
        document.Windows = this;
        document.FileDialogs = DesktopFileDialogs.Create(
            () => native.Win32Hwnd, () => native.CocoaWindow,
            // A result for a window the user has since closed goes to the main document instead: a
            // closed window draws no more frames, so its own Post would never run and the awaiting
            // code would wait for ever. Every window shares one thread, so either is correct.
            work => (native.IsOpen ? document : _main).Post(work));
        return window;
    }
}

/// <summary>The <see cref="AppWindow"/> an app holds; the native window behind it is <see cref="SdlDocumentWindow"/>.</summary>
internal sealed class DesktopAppWindow(SdlDocumentWindow native, string title) : AppWindow(native.Document, title)
{
    protected override void CloseCore() => native.RequestClose();

    protected override void SetTitleCore(string title) => native.SetTitle(title);

    internal void RaiseClosed(WindowCloseReason reason) => NotifyClosed(reason);
}

/// <summary>
/// A second window showing a <see cref="CupriDocument"/>: <see cref="SdlChildWindow"/> for the
/// window, <see cref="DocumentWindowContent"/> for what input means and how a frame is drawn.
///
/// <para>Closing is DEFERRED to the next <see cref="Pump"/>, whoever asks for it. The close button
/// arrives in the middle of SDL's event dispatch, and <see cref="AppWindow.Close"/> is most often
/// called from a click handler running inside this very window's input — destroying the window
/// there would pull the surface out from under the code that is still handling the click.</para>
/// </summary>
internal sealed class SdlDocumentWindow : SdlChildWindow
{
    private readonly DocumentWindowContent _content;
    private readonly WindowOptions _options;
    private readonly SdlChildWindows _host;
    private readonly Stopwatch _clock;
    private readonly Action<Interaction.WindowCommand> _onWindowCommand;
    private readonly Action<Interaction.WindowMove> _onWindowMove;
    private WindowCloseReason? _pendingClose;

    public SdlDocumentWindow(CupriDocument document, CupriApp? app, WindowOptions options, SdlChildWindows host,
        Action<string> openExternal, Stopwatch clock, int ordinal)
        : base("window" + ordinal)
    {
        _options = options;
        _host = host;
        _clock = clock;
        _content = new DocumentWindowContent(document, app, options.Background,
            () => ClipboardText, v => ClipboardText = v, openExternal);
        // The same window seams the main window serves, on this window: a video's fullscreen button
        // fills the display with THIS window, and a frameless drag region moves this one.
        _onWindowCommand = cmd =>
        {
            SetFullscreen(cmd switch
            {
                Interaction.WindowCommand.EnterFullscreen => true,
                Interaction.WindowCommand.ExitFullscreen => false,
                _ => !IsFullscreen,
            });
            Document.NotifyHostFullscreen(IsFullscreen);
        };
        _onWindowMove = m => MoveBy(m.Dx, m.Dy);
        document.WindowCommandRequested += _onWindowCommand;
        document.WindowMoveRequested += _onWindowMove;
    }

    public CupriDocument Document => _content.Document;

    /// <summary>The app's handle on this window, told when it closes.</summary>
    public DesktopAppWindow? Owner { get; set; }

    public bool Open()
    {
        if (!Create(_options.Title, Centered, Centered, _options.Width, _options.Height, _options.Resizable, applyScale: true))
            return false;
        _host.Add(this);
        Document.ReportRenderer(new RendererInfo(RendererKind.Cpu, "SDL software (second window)"));
        return true;
    }

    /// <summary>Close at the next pump, as the app asked.</summary>
    public void RequestClose() => _pendingClose ??= WindowCloseReason.App;

    public override void Pump()
    {
        if (_pendingClose is { } reason)
        {
            Shut(reason);
            return;
        }
        base.Pump();
    }

    protected override bool WantsFrame() => _content.NeedsRender(_clock.Elapsed.TotalSeconds);

    protected override void Paint(SKCanvas canvas, int pixelWidth, int pixelHeight, float deviceScale) =>
        _content.Render(canvas, pixelWidth, pixelHeight, deviceScale, _clock.Elapsed.TotalSeconds);

    protected override void OnCloseButton() => _pendingClose ??= WindowCloseReason.User;

    protected override bool OnPointerDown(float x, float y, int clicks) => _content.PointerDown(x, y, clicks);
    protected override bool OnPointerMove(float x, float y) => _content.PointerMove(x, y);
    protected override bool OnPointerUp(float x, float y) => _content.PointerUp(x, y);
    protected override bool OnRightPointerDown(float x, float y) => _content.RightPointerDown(x, y);
    protected override bool OnMiddlePointerDown(float x, float y) => _content.MiddlePointerDown(x, y);
    protected override bool OnMiddlePointerUp(float x, float y) => _content.MiddlePointerUp(x, y);
    protected override bool OnWheel(float x, float y, float notches, KeyMods mods) => _content.Wheel(x, y, notches, mods);

    protected override bool OnKey(EditKey key, KeyMods mods)
    {
        var handled = _content.Key(key, mods);
        // Escape the document did not take leaves fullscreen — the OS convention, as on the main window.
        if (!handled && key == EditKey.Escape && IsFullscreen)
        {
            SetFullscreen(false);
            Document.NotifyHostFullscreen(false);
            return true;
        }
        return handled;
    }

    protected override bool OnKeyUp(EditKey key) => _content.KeyUp(key);
    protected override bool OnText(string text) => _content.Text(text);
    protected override bool OnShortcut(char ch, KeyMods mods) => _content.Shortcut(ch, mods);
    protected override bool OnFilesDropped(float x, float y, string[] paths) => _content.FilesDropped(x, y, paths);
    protected override Style.CursorType CursorAt(float x, float y) => _content.CursorAt(x, y);

    protected override void OnFocusChanged(bool focused)
    {
        if (!focused) _content.FocusLost();
    }

    private void Shut(WindowCloseReason reason)
    {
        _pendingClose = null;
        _host.Remove(this);
        DestroyWindow();
        Document.WindowCommandRequested -= _onWindowCommand;
        Document.WindowMoveRequested -= _onWindowMove;
        _content.Dispose();
        Owner?.RaiseClosed(reason);
    }

    /// <summary>The host is going: close with <see cref="WindowCloseReason.Host"/>, unless a close
    /// was already on its way, in which case that is the reason the app is told.</summary>
    public override void Dispose()
    {
        if (IsOpen) Shut(_pendingClose ?? WindowCloseReason.Host);
        base.Dispose();
    }
}
