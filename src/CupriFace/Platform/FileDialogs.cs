namespace CupriFace.Platform;

/// <summary>
/// One entry in a file dialog's type list — <c>new FileFilter("Images", "png", "jpg")</c>.
///
/// <para>Extensions are accepted in every spelling people actually write — <c>"png"</c>,
/// <c>".png"</c> and <c>"*.png"</c> — and stored as the bare <c>"png"</c>, because each OS wants a
/// different one (Windows a <c>*.png</c> pattern, macOS a bare extension, the Linux portal a glob)
/// and the host is the place to produce it. <c>"*"</c> (or <c>"*.*"</c>) means every file.</para>
///
/// <para>Anything that is not an extension is refused when the filter is built rather than when a
/// dialog is shown: a pattern with a wildcard in the middle, a path, a list joined with
/// <c>;</c>. Each would mean something different, or nothing, on each OS, and a filter that
/// silently matches nothing on one platform is exactly the kind of mistake nobody sees.</para>
/// </summary>
public sealed class FileFilter
{
    /// <summary>A filter matching every file.</summary>
    public static FileFilter AllFiles { get; } = new("All files", "*");

    /// <param name="name">What the dialog shows for this entry, e.g. <c>"Images"</c>.</param>
    /// <param name="extensions">One or more extensions; see the class remarks for the accepted forms.</param>
    public FileFilter(string name, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A file filter needs a name the dialog can show.", nameof(name));
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Length == 0)
            throw new ArgumentException($"The file filter \"{name}\" has no extensions; use \"*\" for every file.", nameof(extensions));
        var list = new List<string>(extensions.Length);
        foreach (var raw in extensions)
        {
            var ext = Normalise(raw, name);
            if (!list.Contains(ext, StringComparer.OrdinalIgnoreCase)) list.Add(ext);
        }
        Name = name.Trim();
        Extensions = list;
    }

    /// <summary>What the dialog shows for this entry.</summary>
    public string Name { get; }

    /// <summary>The extensions, bare (<c>"png"</c>, never <c>".png"</c>), or <c>"*"</c> for every file.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>True when this filter admits every file.</summary>
    public bool MatchesAll => Extensions.Contains("*");

    /// <summary>The Windows pattern list — <c>"*.png;*.jpg"</c>, or <c>"*.*"</c> for every file.</summary>
    public string ToWindowsPattern() =>
        MatchesAll ? "*.*" : string.Join(";", Extensions.Select(e => "*." + e));

    private static string Normalise(string? raw, string filterName)
    {
        var ext = raw?.Trim() ?? "";
        if (ext is "*" or "*.*") return "*";
        if (ext.StartsWith("*.", StringComparison.Ordinal)) ext = ext[2..];
        else if (ext.StartsWith('.')) ext = ext[1..];
        if (ext.Length == 0 || ext.IndexOfAny(['*', '?', ';', ',', '/', '\\', ' ', '\t']) >= 0 || ext.StartsWith('.'))
            throw new ArgumentException(
                $"\"{raw}\" in the file filter \"{filterName}\" is not an extension. Write \"png\", \".png\" or \"*.png\"; " +
                "\"*\" means every file; give a list as separate arguments rather than joined with ';'.");
        return ext;
    }
}

/// <summary>What <see cref="FileDialogs.OpenFilesAsync"/> shows.</summary>
public sealed record OpenFileOptions
{
    /// <summary>The dialog's title; null for the OS default ("Open").</summary>
    public string? Title { get; init; }

    /// <summary>The type list, first entry selected. Empty shows every file.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>Let the user pick more than one file.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>The folder the dialog opens in; null lets the OS choose (usually the last one used).</summary>
    public string? InitialDirectory { get; init; }
}

/// <summary>What <see cref="FileDialogs.SaveFileAsync"/> shows.</summary>
public sealed record SaveFileOptions
{
    /// <summary>The dialog's title; null for the OS default ("Save As").</summary>
    public string? Title { get; init; }

    /// <summary>The type list, first entry selected. Empty shows every file.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>The folder the dialog opens in; null lets the OS choose.</summary>
    public string? InitialDirectory { get; init; }

    /// <summary>The file name filled in for the user — a NAME, <c>"report.csv"</c>, not a path
    /// (put the folder in <see cref="InitialDirectory"/>).</summary>
    public string? DefaultName { get; init; }
}

/// <summary>What <see cref="FileDialogs.PickFolderAsync"/> shows.</summary>
public sealed record PickFolderOptions
{
    /// <summary>The dialog's title; null for the OS default.</summary>
    public string? Title { get; init; }

    /// <summary>The folder the dialog opens in; null lets the OS choose.</summary>
    public string? InitialDirectory { get; init; }
}

/// <summary>
/// The operating system's Open, Save and folder dialogs — <see cref="CupriDocument.FileDialogs"/>.
///
/// <para><b>Null on a host that has none</b> (a browser, Android, a headless test), so an app asks
/// once and hides its "Open…" button rather than showing one that cannot work. Nothing here is ever
/// faked: a host either shows the OS dialog or does not offer this object at all.</para>
///
/// <code>
/// if (doc.FileDialogs is { } dialogs)
/// {
///     var path = await dialogs.OpenFileAsync(new OpenFileOptions
///     {
///         Filters = [new FileFilter("Markdown", "md", "markdown")],
///     });
///     if (path is not null) _model.Load(File.ReadAllText(path));
/// }
/// </code>
///
/// <para><b>Results arrive on the document's thread.</b> The dialog itself runs elsewhere — on its
/// own thread on Windows, as a sheet or a portal request on macOS and Linux — so the app's window
/// keeps drawing while it is open. When the user is done the result is handed back through
/// <see cref="CupriDocument.Post"/>, so the code after the <c>await</c> runs on the thread that
/// renders, exactly as a click handler does, and may touch the document and its model freely.</para>
///
/// <para><b>Cancelled is null, not an exception.</b> Closing the dialog is an ordinary answer. A
/// <see cref="CancellationToken"/> that fires closes the dialog and the task is cancelled; a dialog
/// the OS failed to show faults the task with the reason.</para>
///
/// <para><b>One at a time.</b> A second request while a dialog from the same window is open throws
/// <see cref="InvalidOperationException"/>; <see cref="IsShowing"/> says whether one is.</para>
/// </summary>
public abstract class FileDialogs
{
    private readonly Action<Action> _post;
    private int _showing;

    /// <param name="post">How a result reaches the document's thread — a host passes the owning
    /// document's <see cref="CupriDocument.Post"/>.</param>
    protected FileDialogs(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
    }

    /// <summary>True while a dialog requested through this object is open.</summary>
    public bool IsShowing => Volatile.Read(ref _showing) != 0;

    /// <summary>Ask for one or more existing files. The chosen paths, or <b>null</b> when the user
    /// cancelled.</summary>
    public Task<string[]?> OpenFilesAsync(OpenFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenFileOptions();
        Validate(options.Filters, options.InitialDirectory);
        return Run(ct => ShowOpenAsync(options, ct), cancellationToken);
    }

    /// <summary>Ask for one existing file — <see cref="OpenFilesAsync"/> with
    /// <see cref="OpenFileOptions.AllowMultiple"/> off. The path, or <b>null</b> when cancelled.</summary>
    public async Task<string?> OpenFileAsync(OpenFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        var files = await OpenFilesAsync((options ?? new OpenFileOptions()) with { AllowMultiple = false }, cancellationToken);
        return files is { Length: > 0 } ? files[0] : null;
    }

    /// <summary>Ask where to save. The chosen path, or <b>null</b> when the user cancelled. The OS has
    /// already asked about overwriting an existing file; nothing has been written.</summary>
    public Task<string?> SaveFileAsync(SaveFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SaveFileOptions();
        Validate(options.Filters, options.InitialDirectory);
        if (options.DefaultName is { } name &&
            (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException(
                $"SaveFileOptions.DefaultName \"{name}\" must be a file name, not a path; put the folder in InitialDirectory.",
                nameof(options));
        return Run(ct => ShowSaveAsync(options, ct), cancellationToken);
    }

    /// <summary>Ask for a folder. Its path, or <b>null</b> when the user cancelled.</summary>
    public Task<string?> PickFolderAsync(PickFolderOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PickFolderOptions();
        Validate([], options.InitialDirectory);
        return Run(ct => ShowFolderAsync(options, ct), cancellationToken);
    }

    /// <summary>Show the OS open dialog. May complete on any thread; null when cancelled.</summary>
    protected abstract Task<string[]?> ShowOpenAsync(OpenFileOptions options, CancellationToken cancellationToken);

    /// <summary>Show the OS save dialog. May complete on any thread; null when cancelled.</summary>
    protected abstract Task<string?> ShowSaveAsync(SaveFileOptions options, CancellationToken cancellationToken);

    /// <summary>Show the OS folder picker. May complete on any thread; null when cancelled.</summary>
    protected abstract Task<string?> ShowFolderAsync(PickFolderOptions options, CancellationToken cancellationToken);

    private static void Validate(IReadOnlyList<FileFilter>? filters, string? initialDirectory)
    {
        if (filters is null) throw new ArgumentException("Filters cannot be null; leave it empty for every file.");
        foreach (var f in filters)
            if (f is null) throw new ArgumentException("Filters cannot contain null.");
        if (initialDirectory is { } dir && (dir.Length == 0 || !Path.IsPathFullyQualified(dir)))
            throw new ArgumentException($"InitialDirectory \"{dir}\" must be an absolute path.");
    }

    // The one place a result crosses back to the document's thread. The completion source is made
    // WITHOUT RunContinuationsAsynchronously on purpose: it is completed from inside the posted work,
    // which runs on the document's thread, so the awaiting code continues right there — inline, in
    // that frame, before layout — rather than being bounced to the thread pool where it could not
    // safely touch the document at all.
    private Task<T?> Run<T>(Func<CancellationToken, Task<T?>> show, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _showing, 1, 0) != 0)
            throw new InvalidOperationException(
                "A file dialog from this window is already open; wait for it (FileDialogs.IsShowing) before asking for another.");
        Task<T?> shown;
        try { shown = show(cancellationToken); }
        catch (Exception ex) { shown = Task.FromException<T?>(ex); }

        var result = new TaskCompletionSource<T?>();
        shown.ContinueWith(t => _post(() =>
        {
            Volatile.Write(ref _showing, 0);
            if (cancellationToken.IsCancellationRequested) result.TrySetCanceled(cancellationToken);
            else if (t.IsFaulted) result.TrySetException(t.Exception!.InnerExceptions);
            else if (t.IsCanceled) result.TrySetCanceled();
            else result.TrySetResult(t.Result);
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return result.Task;
    }
}
