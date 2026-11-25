using System.Diagnostics;
using CupriFace.Diagnostics;

namespace CupriFace.DevTools;

/// <summary>
/// Hot reload for one running document: re-reads the app's markup and stylesheet when their files
/// are saved, or when a C# hot reload has patched the code that produces them, swaps them into the
/// live document with the model and every piece of UI state intact, and runs
/// <see cref="CupriDoctor"/> over the result.
///
/// <para><b>What survives a reload is what is not source:</b> the bound model, focus, scroll
/// positions, zoom and registered handlers (<see cref="CupriDocument.ReplaceSource"/>). That is most
/// of what makes a reload useful, and it costs nothing, because a document is rebuilt from template
/// and model on every interaction already.</para>
///
/// <para><b>Where the files are.</b> An asset embedded with CupriFace.Resources.targets
/// (<c>Assets/App.html</c>) is traced back to its project folder — stamped into a Debug build, or
/// found by walking up from the output directory — and from then on is read from disk rather than
/// from the stale copy compiled in. A <see cref="Resources.CupriSource.File"/> is watched where it
/// is. Markup written as a C# string has no file; a C# hot reload (the debugger's, or
/// <c>dotnet watch</c>) re-reads it instead.</para>
///
/// <para><b>The doctor's findings are printed in the compiler's format</b>,
/// <c>path(line): warning CF0060: …</c>, so an editor's problem matcher — VS Code's
/// <c>$msCompile</c> — lists them alongside build errors and a click goes to the line.</para>
///
/// <para>A development tool: hosts create one only when <see cref="CupriDevTools.IsSupported"/>.</para>
/// </summary>
public sealed class HotReloadSession : IDisposable
{
    private static readonly List<WeakReference<HotReloadSession>> Live = [];

    private readonly CupriApp _app;
    private readonly CupriDocument _doc;
    private readonly TextWriter _log;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private readonly HashSet<string> _watchedFiles = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Start watching the app's sources. Call on the document's thread.</summary>
    /// <param name="app">The app the document was created from — its <c>Html</c> and <c>Css</c>
    /// are what a reload re-reads.</param>
    /// <param name="doc">The live document.</param>
    /// <param name="log">Where status lines and the doctor's findings go. The console by default.</param>
    public HotReloadSession(CupriApp app, CupriDocument doc, TextWriter? log = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _log = log ?? Console.Out;
        _debounce = new Timer(_ => Request(), null, Timeout.Infinite, Timeout.Infinite);

        MarkupFile = Track(app.MarkupSourceForDevTools);
        StyleFile = Track(app.StyleSourceForDevTools);
        lock (Live) Live.Add(new WeakReference<HotReloadSession>(this));

        var watched = new[] { MarkupFile, StyleFile }.OfType<string>().Select(DisplayName).ToList();
        Status = watched.Count > 0
            ? "Watching " + string.Join(", ", watched)
            : "Markup is not from a file — a C# hot reload re-reads it";
        _log.WriteLine($"[CupriFace] Hot reload: {Status.ToLowerInvariant()[0]}{Status[1..]}.");
    }

    /// <summary>The markup file being watched, or null when the markup has none.</summary>
    public string? MarkupFile { get; }

    /// <summary>The stylesheet file being watched, or null.</summary>
    public string? StyleFile { get; }

    /// <summary>One line saying what is being watched and how the last reload went.</summary>
    public string Status { get; private set; }

    /// <summary>The doctor's report on the document as of the last reload (or the first check).</summary>
    public DoctorReport? LastReport { get; private set; }

    /// <summary>Why the last reload was rejected — the markup threw while being built — or null.
    /// The document keeps the last source that worked.</summary>
    public string? LastError { get; private set; }

    /// <summary>How many reloads have changed the document.</summary>
    public int ReloadCount { get; private set; }

    /// <summary>Raised on the document's thread after every reload attempt and doctor run.</summary>
    public event Action? Reloaded;

    /// <summary>Re-read the sources and apply them, then run the doctor. Call on the document's
    /// thread; from anywhere else, <see cref="Request"/>.</summary>
    public void ReloadNow()
    {
        if (_disposed) return;
        var sw = Stopwatch.StartNew();
        string html, css;
        var oldHtml = _doc.TemplateHtml;
        var oldCss = _doc.Stylesheet ?? "";
        try
        {
            html = _app.Html;
            css = _app.Css;
            if (!_doc.ReplaceSource(html, css))
            {
                CheckNow();
                return;
            }
        }
        catch (Exception ex)
        {
            // Put the last source that built back, so a half-typed tag costs the edit, not the app.
            try { _doc.ReplaceSource(oldHtml, oldCss); } catch { /* the old source built once; leave it */ }
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Reload failed — kept the previous source";
            _log.WriteLine($"[CupriFace] Hot reload failed, keeping the previous source: {LastError}");
            Reloaded?.Invoke();
            return;
        }

        LastError = null;
        ReloadCount++;
        var took = sw.ElapsedMilliseconds;
        Status = $"Reloaded at {DateTime.Now:HH:mm:ss} ({took} ms)";
        _log.WriteLine($"[CupriFace] Hot reload: applied in {took} ms.");
        CheckNow();
    }

    /// <summary>Run the doctor over the live document as it is now and print what it finds. Call on
    /// the document's thread.</summary>
    public void CheckNow()
    {
        if (_disposed) return;
        // The check renders the document once on a CPU surface of its own. Drop the retained frame
        // after it, so a host drawing incrementally repaints in full rather than diffing against a
        // frame it did not present.
        try { LastReport = CupriDoctor.Check(_doc); _doc.InvalidateRetainedFrame(); }
        catch (Exception ex)
        {
            _log.WriteLine($"[CupriFace] Doctor could not check the document: {ex.GetType().Name}: {ex.Message}");
            Reloaded?.Invoke();
            return;
        }
        Print(LastReport);
        Reloaded?.Invoke();
    }

    /// <summary>Ask for a reload from any thread. The host runs it at its next <see cref="Pump"/>,
    /// and an idle host is woken to do so.</summary>
    public void Request()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pending, 1);
        // Posting nothing still wakes a render-on-demand host (HasActiveAnimations / FrameRequested),
        // which is the only reason to post: the reload itself must NOT run as posted work. Posted
        // work runs inside a frame, and the doctor lays the document out and renders it — a frame
        // inside a frame.
        _doc.Post(static () => { });
    }

    /// <summary>Run a requested reload, if there is one. Hosts call this on the document's thread
    /// between frames — before deciding whether to draw — and draw when it returns true.</summary>
    public bool Pump()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return false;
        ReloadNow();
        return true;
    }

    private int _pending;

    /// <summary>Called when a C# hot reload has been applied (see <see cref="HotReloadHandler"/>).
    /// Any thread.</summary>
    internal static void NotifyCodeUpdated()
    {
        List<HotReloadSession> sessions = [];
        lock (Live)
        {
            Live.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in Live) if (w.TryGetTarget(out var s)) sessions.Add(s);
        }
        foreach (var s in sessions) s.Request();
    }

    /// <summary>The file a finding belongs to, in the compiler's diagnostic format. The CSS codes
    /// carry a stylesheet line when the app has a stylesheet; every other line is in the markup.</summary>
    internal string FormatFinding(Finding f)
    {
        var (file, line) = Locate(f);
        var severity = f.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "info" };
        var fix = f.Fix.Length > 0 ? "  -> " + f.Fix : "";
        return $"{file}({line}): {severity} {f.Code}: {f.Message}{fix}";
    }

    /// <summary>The file and line a finding is about. Line 1 when the finding has none: a problem
    /// matcher needs a number, and the top of the file is the honest answer for "somewhere in
    /// here". A source with no file is named after the app property it came from.</summary>
    internal (string File, int Line) Locate(Finding f)
    {
        var inStyle = f.Code.StartsWith("CF005", StringComparison.Ordinal) && !string.IsNullOrEmpty(_doc.Stylesheet);
        var file = inStyle
            ? StyleFile ?? $"{_app.GetType().Name}.Css"
            : MarkupFile ?? $"{_app.GetType().Name}.Html";
        return (file, Math.Max(1, f.Line));
    }

    private void Print(DoctorReport report)
    {
        // The two marker lines bracket one report, so a background problem matcher in VS Code
        // (beginsPattern / endsPattern) clears the previous report's problems before listing these.
        _log.WriteLine("[CupriFace] Doctor: checking.");
        foreach (var f in report.Findings) _log.WriteLine(FormatFinding(f));
        _log.WriteLine(report.IsClean
            ? "[CupriFace] Doctor: no problems found."
            : $"[CupriFace] Doctor: {Summary(report)}.");
    }

    /// <summary>"2 errors, 1 warning" — the counts that are not zero.</summary>
    internal static string Summary(DoctorReport report)
    {
        if (report.IsClean) return "no problems";
        var parts = new List<string>();
        void Add(Severity s, string one)
        {
            var n = report.Count(s);
            if (n > 0) parts.Add($"{n} {one}{(n == 1 ? "" : "s")}");
        }
        Add(Severity.Error, "error");
        Add(Severity.Warning, "warning");
        Add(Severity.Info, "note");
        return string.Join(", ", parts);
    }

    /// <summary>Resolve where a source lives on disk and watch it. Null when it has no file.</summary>
    private string? Track(Resources.CupriSource? source)
    {
        string? file = null;
        if (source?.FilePath is { } path) file = path;
        else if (source is { EmbeddedIn: { } asm, LogicalName: { } logical }
                 && AssetRedirect.FindRoot(asm, logical) is { } root)
        {
            AssetRedirect.Register(asm, root);
            file = AssetRedirect.FileFor(asm, logical);
        }
        if (file is null || !File.Exists(file)) return null;

        _watchedFiles.Add(Path.GetFullPath(file));
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (_watchers.Any(w => string.Equals(w.Path, dir, StringComparison.OrdinalIgnoreCase))) return file;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            // Editors save in different ways — in place, or by writing a temporary file and renaming
            // it over the original — so every kind of event counts, filtered to the files we read.
            w.Changed += OnFileEvent;
            w.Created += OnFileEvent;
            w.Renamed += OnFileEvent;
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _log.WriteLine($"[CupriFace] Hot reload cannot watch {dir}: {ex.Message}");
        }
        return file;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!_watchedFiles.Contains(Path.GetFullPath(e.FullPath))) return;
        // One save is several events (truncate, write, attribute change); answer the last of them.
        try { _debounce.Change(120, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private static string DisplayName(string path)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrEmpty(dir) ? Path.GetFileName(path) : $"{dir}/{Path.GetFileName(path)}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce.Dispose();
        lock (Live) Live.RemoveAll(w => !w.TryGetTarget(out var s) || ReferenceEquals(s, this));
    }
}
