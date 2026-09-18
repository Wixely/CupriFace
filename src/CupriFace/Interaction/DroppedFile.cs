namespace CupriFace.Interaction;

/// <summary>
/// One file the user dropped on the window.
///
/// <para><b>Metadata is synchronous; bytes are not.</b> That split is what lets one handler serve
/// every host. A browser hands a drop over as a <c>File</c> — a blob whose contents can only be read
/// through a promise — so there is no way to expose the bytes synchronously, and no way to expose a
/// filesystem path at all (the web deliberately withholds it). What a browser <i>does</i> give
/// immediately is the name, the size and the type, which is exactly what an app branches on first:
/// is this a <c>.cupriproj</c>, and is it four gigabytes? So the cheap questions are answered
/// in-hand and the expensive one is awaited, and nothing is read merely to discover it had the wrong
/// extension.</para>
///
/// <para><see cref="Path"/> is the one desktop-only member, and it is nullable to say so. It stays
/// because it is genuinely useful there — an app that wants to reopen the same project next launch
/// needs the location, not the bytes — but an app that reads it without a null check is an app that
/// works everywhere except the browser.</para>
/// </summary>
public sealed class DroppedFile
{
    private readonly Func<CancellationToken, Task<byte[]>> _read;

    /// <summary>The file's name with no directory part — <c>"budget.csv"</c>. Always present: it is
    /// the one thing every platform supplies, and usually the thing an app dispatches on.</summary>
    public string Name { get; }

    /// <summary>Length in bytes, or <c>-1</c> when the platform did not say. Present before any read,
    /// so an app can refuse something too large without first pulling it into memory.</summary>
    public long Size { get; }

    /// <summary>The media type — <c>"text/csv"</c>. A browser reports what it inferred; elsewhere it
    /// is derived from the extension, falling back to <c>application/octet-stream</c>. A hint, not a
    /// guarantee: it comes from the file's name or the OS, never from its contents.</summary>
    public string MediaType { get; }

    /// <summary>The absolute path, or <b>null in the browser</b>, where no path exists and asking for
    /// one is meaningless. Read it only to remember a location; read the bytes with
    /// <see cref="ReadBytesAsync"/>, which works on every host.</summary>
    public string? Path { get; }

    /// <summary>
    /// Whether this is a folder rather than a file. <b>Check it before reading</b> — a folder has no
    /// bytes, and asking for them throws.
    ///
    /// <para>Worth a property rather than leaving it to the read: dragging a folder onto a window is
    /// ordinary (drag a project in), and a caller that learns about it by catching an exception has
    /// already been surprised. Both platforms report it — the OS says so directly, and a browser is
    /// asked through <c>webkitGetAsEntry</c> — so a handler that skips folders behaves the same
    /// everywhere instead of failing two different ways.</para>
    /// </summary>
    public bool IsDirectory { get; }

    private DroppedFile(string name, long size, string mediaType, string? path, bool isDirectory,
        Func<CancellationToken, Task<byte[]>> read)
    {
        Name = name;
        Size = size;
        MediaType = mediaType;
        Path = path;
        IsDirectory = isDirectory;
        _read = read;
    }

    /// <summary>The contents. Asynchronous because a browser cannot be anything else — awaiting it on
    /// desktop costs a thread-pool hop over a file read that was going to happen anyway.</summary>
    public Task<byte[]> ReadBytesAsync(CancellationToken ct = default) => _read(ct);

    /// <summary>The contents as text, decoded like every other <see cref="Resources.CupriSource"/>:
    /// a BOM wins, otherwise UTF-8.</summary>
    public async Task<string> ReadTextAsync(CancellationToken ct = default) =>
        Resources.CupriSource.Bytes(Name, await _read(ct).ConfigureAwait(false)).ReadText();

    /// <summary>This file as a <see cref="Resources.CupriSource"/>, so a drop can feed anything that
    /// already takes one. Trust is <see cref="Resources.ResourceTrust.LocalFile"/> — the user pointed
    /// at it, but the app never chose it, which makes it the least trustworthy local input there
    /// is.</summary>
    public async Task<Resources.CupriSource> ToSourceAsync(CancellationToken ct = default) =>
        Resources.CupriSource.Bytes(Name, await _read(ct).ConfigureAwait(false));

    // ---- factories -------------------------------------------------------------------------------

    /// <summary>A file on disk, read when it is asked for. The desktop hosts' factory: a drop of a
    /// 4GB video costs nothing until someone wants its bytes.</summary>
    public static DroppedFile FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = System.IO.Path.GetFullPath(path);
        // A trailing separator would make GetFileName return "", and a dropped directory often has
        // one — so the name is taken from the trimmed path.
        var name = System.IO.Path.GetFileName(full.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (name.Length == 0) name = full;                  // a drive root: "C:\\" has no file name

        var isDir = Directory.Exists(full);
        long size = -1;
        if (!isDir)
            try { size = new FileInfo(full).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }

        return new DroppedFile(name, size, MediaTypeFor(name), full, isDir,
            ct => ReadFileAsync(full, name, isDir, ct));
    }

    // Every way a local read can fail, reported as ONE exception type.
    //
    // It did not used to be: reading a dropped folder threw UnauthorizedAccessException — which is
    // not an IOException, so it slipped past the only catch the API documents — while the browser
    // path faulted with IOException for the same gesture. An app that handled the drop correctly on
    // one host crashed on the other, which is exactly the split this whole type exists to prevent.
    private static async Task<byte[]> ReadFileAsync(string full, string name, bool isDir, CancellationToken ct)
    {
        if (isDir || Directory.Exists(full))
            throw new IOException($"'{name}' is a folder, not a file — check IsDirectory before reading.");
        try
        {
            return await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException($"'{name}' could not be read: {e.Message}", e);
        }
    }

    /// <summary>Bytes already in hand. Used by tests and by <see cref="DropDriver"/>, and by any host
    /// that has materialised the content before dispatching.</summary>
    public static DroppedFile FromBytes(string name, byte[] bytes, string? mediaType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bytes);
        return new DroppedFile(name, bytes.LongLength, mediaType ?? MediaTypeFor(name), path: null,
            isDirectory: false, _ => Task.FromResult(bytes));
    }

    /// <summary>A file whose metadata is known but whose bytes are still on the other side of
    /// something asynchronous. The browser host's factory: the blob handle stays in JS and
    /// <paramref name="read"/> is the round trip that fetches it.</summary>
    public static DroppedFile Deferred(string name, long size, string? mediaType,
        Func<CancellationToken, Task<byte[]>> read, bool isDirectory = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(read);
        return new DroppedFile(name, size, mediaType is { Length: > 0 } m ? m : MediaTypeFor(name),
            path: null, isDirectory,
            isDirectory
                // The browser reports a folder as a zero-byte File whose read fails with a DOMException
                // the page turns into an IOException. Refusing here instead keeps the message useful
                // and identical to the desktop one, rather than whatever that turn of events produced.
                ? _ => Task.FromException<byte[]>(new IOException(
                    $"'{name}' is a folder, not a file — check IsDirectory before reading."))
                : read);
    }

    // ---- media types -----------------------------------------------------------------------------

    /// <summary>A small extension table — enough to spare an app writing its own for the common cases,
    /// and honest about being a guess for everything else. Deliberately not exhaustive: a media type
    /// derived from a name is a hint, and an app that needs certainty must look at the bytes.</summary>
    internal static string MediaTypeFor(string name) =>
        System.IO.Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".txt" or ".log" => "text/plain",
            ".md" or ".markdown" => "text/markdown",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
}
