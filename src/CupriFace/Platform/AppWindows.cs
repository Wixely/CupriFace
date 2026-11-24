using SkiaSharp;

namespace CupriFace.Platform;

/// <summary>Why an <see cref="AppWindow"/> closed — <see cref="AppWindow.Closed"/>.</summary>
public enum WindowCloseReason
{
    /// <summary>The user closed it: its close button, Alt+F4, the window manager.</summary>
    User,

    /// <summary>The app closed it with <see cref="AppWindow.Close"/>.</summary>
    App,

    /// <summary>The host is shutting down — the app's main window closed — and took it with it.</summary>
    Host,
}

/// <summary>How a window opened by <see cref="AppWindows.Open(CupriDocument, WindowOptions?)"/> looks.</summary>
public sealed record WindowOptions
{
    /// <summary>The largest width or height accepted, in logical units.</summary>
    public const int MaxSize = 16384;

    /// <summary>The title bar text.</summary>
    public string Title { get; init; } = "CupriFace";

    /// <summary>The initial client width in logical units (the monitor's scale is applied by the host).</summary>
    public int Width { get; init; } = 640;

    /// <summary>The initial client height in logical units.</summary>
    public int Height { get; init; } = 480;

    /// <summary>The colour behind the document, as <see cref="CupriApp.Background"/>.</summary>
    public SKColor Background { get; init; } = SKColors.White;

    /// <summary>Whether the user can resize it.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>The options a <see cref="CupriApp"/> describes for itself: its title, size and background.</summary>
    public static WindowOptions From(CupriApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new WindowOptions { Title = app.Title, Width = app.Width, Height = app.Height, Background = app.Background };
    }

    internal void Validate()
    {
        if (Title is null) throw new ArgumentException("WindowOptions.Title cannot be null.");
        if (Width is < 1 or > MaxSize || Height is < 1 or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(Width),
                $"A window must be between 1 and {MaxSize} logical units on each side; {Width}x{Height} was asked for.");
    }
}

/// <summary>
/// A window an app opened beside its main one, showing a document of its own —
/// <see cref="AppWindows.Open(CupriDocument, WindowOptions?)"/>.
///
/// <para>Every window in a process runs on ONE thread, the host's, so the document here is driven
/// on the same thread as the main window's and code in either may touch the other directly. Input in
/// this window goes to this window's document: pointer, wheel, keys, typed text, the clipboard chords.</para>
/// </summary>
public abstract class AppWindow
{
    private string _title;
    private bool _closedRaised;

    /// <param name="document">The document it shows.</param>
    /// <param name="title">Its title bar text.</param>
    protected AppWindow(CupriDocument document, string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        _title = title ?? "";
    }

    /// <summary>The document this window shows.</summary>
    public CupriDocument Document { get; }

    /// <summary>False once it has closed, or <see cref="Close"/> has been asked for.</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>The title bar text. Setting it on a closed window changes nothing.</summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? "";
            if (IsOpen) SetTitleCore(_title);
        }
    }

    /// <summary>
    /// Raised once, on the host's thread, when the window has closed — by the user
    /// (<see cref="WindowCloseReason.User"/>), by <see cref="Close"/>
    /// (<see cref="WindowCloseReason.App"/>), or because the host shut down
    /// (<see cref="WindowCloseReason.Host"/>). The document is still usable; it is simply no longer
    /// drawn anywhere.
    /// </summary>
    public event Action<WindowCloseReason>? Closed;

    /// <summary>Close the window. Safe to call from this window's own handlers — the window goes at
    /// the end of the current tick, and <see cref="Closed"/> follows with
    /// <see cref="WindowCloseReason.App"/>. Does nothing if it is already closed.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        CloseCore();
    }

    /// <summary>For hosts: destroy the native window (now or at the end of the tick) and then call
    /// <see cref="NotifyClosed"/> with <see cref="WindowCloseReason.App"/>.</summary>
    protected abstract void CloseCore();

    /// <summary>For hosts: retitle the native window.</summary>
    protected abstract void SetTitleCore(string title);

    /// <summary>For hosts: the native window has gone. Raises <see cref="Closed"/> once, whatever the
    /// reason and however many times it is called.</summary>
    protected void NotifyClosed(WindowCloseReason reason)
    {
        IsOpen = false;
        if (_closedRaised) return;
        _closedRaised = true;
        Closed?.Invoke(reason);
    }
}

/// <summary>
/// Opens more windows — <see cref="CupriDocument.Windows"/>.
///
/// <para><b>Null on a host that cannot</b> (a browser tab, an Android activity, a headless test), so
/// an app can fall back to an in-app panel or dialog instead. Never faked: a host either opens real
/// windows or offers no object at all.</para>
///
/// <code>
/// if (doc.Windows is { } windows)
/// {
///     var settings = windows.Open(new SettingsApp());
///     if (settings is not null)
///         settings.Closed += why => _model.Status = $"settings closed ({why})";
/// }
/// </code>
///
/// <para>Call it on the document's thread — from a handler, or through <see cref="CupriDocument.Post"/>.
/// Every window of a process shares that one thread.</para>
/// </summary>
public abstract class AppWindows
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly HashSet<CupriDocument> _shown = new(ReferenceEqualityComparer.Instance);
    private readonly List<AppWindow> _open = [];

    /// <param name="mainDocument">The document the host's main window shows, so it cannot also be
    /// opened in a second window. Constructed on the host's thread, which is the one
    /// <see cref="Open(CupriDocument, WindowOptions?)"/> must be called on.</param>
    protected AppWindows(CupriDocument mainDocument)
    {
        ArgumentNullException.ThrowIfNull(mainDocument);
        _shown.Add(mainDocument);
    }

    /// <summary>The windows opened through this object that are still open.</summary>
    public IReadOnlyList<AppWindow> OpenWindows => _open;

    /// <summary>
    /// Open a window showing <paramref name="document"/>. The window lays the document out at its
    /// own size (<see cref="PresentInfo.Responsive"/>). Returns <b>null</b> when the OS refused to
    /// create a window; throws when the document is already shown in one, or when called off the
    /// host's thread.
    /// </summary>
    public AppWindow? Open(CupriDocument document, WindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return OpenChecked(document, options ?? new WindowOptions(), app: null);
    }

    /// <summary>
    /// Open a window running another <see cref="CupriApp"/>: a fresh
    /// <see cref="CupriApp.CreateDocument"/>, laid out by the app's own <see cref="CupriApp.Present"/>,
    /// with its title, size and background. Null when the OS refused.
    /// </summary>
    public AppWindow? Open(CupriApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = WindowOptions.From(app);
        options.Validate();
        CheckThread();
        return OpenChecked(app.CreateDocument(), options, app);
    }

    private AppWindow? OpenChecked(CupriDocument document, WindowOptions options, CupriApp? app)
    {
        CheckThread();
        options.Validate();
        if (_shown.Contains(document))
            throw new InvalidOperationException("That document is already shown in a window; a document can be in one window at a time.");
        var window = OpenCore(document, options, app);
        if (window is null) return null;
        _shown.Add(document);
        _open.Add(window);
        window.Closed += _ =>
        {
            _open.Remove(window);
            _shown.Remove(document);
        };
        return window;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException(
                "Windows can only be opened on the host's thread. From another thread, use doc.Post(() => doc.Windows.Open(...)).");
    }

    /// <summary>For hosts: create and show the native window. <paramref name="app"/> is set when the
    /// window was opened for a <see cref="CupriApp"/>, whose <see cref="CupriApp.Present"/> should
    /// lay it out. Null when the OS refused.</summary>
    protected abstract AppWindow? OpenCore(CupriDocument document, WindowOptions options, CupriApp? app);
}
