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
///
/// <para><b>Large files.</b> <see cref="ReadBytesAsync()"/> materialises the whole file and is capped
/// at <see cref="MaxReadBytes"/> — in a browser the bytes land in a 4GB address space shared with
/// everything else, and an uncapped read of a file the user happened to drag in is how a tab dies.
/// For anything bigger, <see cref="OpenReadAsync"/> streams it in chunks and never holds more than a
/// buffer, on every host.</para>
/// </summary>
public sealed class DroppedFile
{
    private readonly Func<CancellationToken, Task<byte[]>> _readAll;
    private readonly Func<CancellationToken, Task<Stream>>? _openStream;

    /// <summary>
    /// The ceiling for <see cref="ReadBytesAsync()"/>, in bytes. Default <b>128 MiB</b>.
    ///
    /// <para>Strict by default and raised deliberately, the same bargain
    /// <see cref="Resources.CupriSourceOptions.MaxBytes"/> makes for a network fetch. A dropped file
    /// deserves a bigger number than a URL does — the user chose it — but not an unbounded one: on
    /// wasm32 the whole process lives in 4GB, and the failure mode for exceeding it is not an
    /// exception you can catch, it is the tab going away.</para>
    ///
    /// <para>Raise it if your app genuinely wants large files in memory. If you want them
    /// <i>handled</i> rather than held, use <see cref="OpenReadAsync"/> instead and leave this
    /// alone.</para>
    /// </summary>
    public static long MaxReadBytes { get; set; } = 128L * 1024 * 1024;

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
    /// <see cref="ReadBytesAsync()"/>, which works on every host.</summary>
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
        Func<CancellationToken, Task<byte[]>> readAll,
        Func<CancellationToken, Task<Stream>>? openStream)
    {
        Name = name;
        Size = size;
        MediaType = mediaType;
        Path = path;
        IsDirectory = isDirectory;
        _readAll = readAll;
        _openStream = openStream;
    }

    // ---- reading -----------------------------------------------------------------------------

    /// <summary>The whole file, capped at <see cref="MaxReadBytes"/>.</summary>
    public Task<byte[]> ReadBytesAsync(CancellationToken ct = default) => ReadBytesAsync(MaxReadBytes, ct);

    /// <summary>
    /// The whole file, capped at <paramref name="maxBytes"/> for this call only.
    ///
    /// <para>The cap is checked against <see cref="Size"/> <b>before</b> anything is transferred, so
    /// refusing a file costs nothing — and then again against what actually arrived, because a size
    /// the platform reported is not a promise. Exceeding it is an <see cref="IOException"/> naming
    /// <see cref="OpenReadAsync"/>, which is what the caller should do instead.</para>
    /// </summary>
    public async Task<byte[]> ReadBytesAsync(long maxBytes, CancellationToken ct = default)
    {
        if (IsDirectory)
            throw new IOException($"'{Name}' is a folder, not a file — check IsDirectory before reading.");

        // A byte[] cannot hold more than this on ANY host, wasm or not, so it is a separate refusal
        // with its own advice: no cap can be raised far enough to make it work.
        if (Size > Array.MaxLength)
            throw new IOException(
                $"'{Name}' is {Describe(Size)}, which cannot fit in a single array on any platform " +
                $"({Describe(Array.MaxLength)} maximum). Use OpenReadAsync to stream it.");

        if (Size > maxBytes) throw TooBig(Size, maxBytes);

        var bytes = await _readAll(ct).ConfigureAwait(false);

        // Size was -1 (unknown), or the platform was wrong, or the file grew between the drop and
        // the read. Whichever — it is in memory now, so the check is about refusing to HAND IT ON,
        // and about the caller learning that its limit did not hold.
        if (bytes.LongLength > maxBytes) throw TooBig(bytes.LongLength, maxBytes);
        return bytes;
    }

    private IOException TooBig(long actual, long limit) => new(
        $"'{Name}' is {Describe(actual)}, over the {Describe(limit)} read limit. " +
        "Use OpenReadAsync to stream it, or raise DroppedFile.MaxReadBytes if you mean to hold it in memory.");

    /// <summary>The contents as text, decoded like every other <see cref="Resources.CupriSource"/>:
    /// a BOM wins, otherwise UTF-8. Capped like <see cref="ReadBytesAsync()"/>.</summary>
    public async Task<string> ReadTextAsync(CancellationToken ct = default) =>
        Resources.CupriSource.Bytes(Name, await ReadBytesAsync(ct).ConfigureAwait(false)).ReadText();

    /// <summary>This file as a <see cref="Resources.CupriSource"/>, so a drop can feed anything that
    /// already takes one. Trust is <see cref="Resources.ResourceTrust.LocalFile"/> — the user pointed
    /// at it, but the app never chose it, which makes it the least trustworthy local input there
    /// is.</summary>
    public async Task<Resources.CupriSource> ToSourceAsync(CancellationToken ct = default) =>
        Resources.CupriSource.Bytes(Name, await ReadBytesAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// The file as a stream, without holding it. <b>No cap applies</b> — nothing is being
    /// materialised, so a 4GB video can be hashed, parsed or uploaded with a buffer.
    ///
    /// <para>On desktop this is a <c>FileStream</c>. In a browser it pulls <c>blob.slice()</c> ranges
    /// a chunk at a time, so it is seekable but <b>asynchronous only</b>: call <c>ReadAsync</c>, never
    /// <c>Read</c>. A synchronous read cannot work there at all — the page is single-threaded, so
    /// blocking on the promise that would deliver the bytes prevents it from ever resolving, and the
    /// stream throws rather than hanging the tab.</para>
    ///
    /// <para>Dispose it. In a browser it holds nothing of its own, but on desktop it is an open file
    /// handle like any other.</para>
    /// </summary>
    public async Task<Stream> OpenReadAsync(CancellationToken ct = default)
    {
        if (IsDirectory)
            throw new IOException($"'{Name}' is a folder, not a file — check IsDirectory before reading.");
        if (_openStream is { } open) return await open(ct).ConfigureAwait(false);

        // No streaming source: buffer it. Only reachable for a host that supplied a whole-file
        // reader and nothing else, and the cap still applies because this really is in memory.
        return new MemoryStream(await ReadBytesAsync(ct).ConfigureAwait(false), writable: false);
    }

    private static string Describe(long bytes) => bytes switch
    {
        < 0 => "of unknown size",
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
    };

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
            ct => ReadFileAsync(full, name, isDir, ct),
            _ => Task.FromResult<Stream>(OpenFile(full, name, isDir)));
    }

    private static Stream OpenFile(string full, string name, bool isDir)
    {
        if (isDir || Directory.Exists(full))
            throw new IOException($"'{name}' is a folder, not a file — check IsDirectory before reading.");
        try
        {
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 64 * 1024, useAsync: true);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException($"'{name}' could not be read: {e.Message}", e);
        }
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
            isDirectory: false, _ => Task.FromResult(bytes),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
    }

    /// <summary>
    /// A file whose metadata is known but whose bytes are still on the other side of something
    /// asynchronous. The browser host's factory: the blob handle stays in JS and
    /// <paramref name="read"/> is the round trip that fetches it.
    /// </summary>
    /// <param name="readRange">
    /// Fetches one range — offset, length — so <see cref="OpenReadAsync"/> can stream rather than
    /// buffer. A host that supplies it lets an app handle a file far larger than memory; one that
    /// does not still works, by buffering, subject to the cap.
    /// </param>
    public static DroppedFile Deferred(string name, long size, string? mediaType,
        Func<CancellationToken, Task<byte[]>> read, bool isDirectory = false,
        Func<long, int, CancellationToken, Task<byte[]>>? readRange = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(read);

        var readAll = isDirectory
            // The browser reports a folder as a zero-byte File whose read fails with a DOMException
            // the page turns into an IOException. Refusing here instead keeps the message useful
            // and identical to the desktop one, rather than whatever that turn of events produced.
            ? _ => Task.FromException<byte[]>(new IOException(
                $"'{name}' is a folder, not a file — check IsDirectory before reading."))
            : read;

        Func<CancellationToken, Task<Stream>>? open = null;
        if (!isDirectory && readRange is not null)
            open = _ => Task.FromResult<Stream>(new DroppedFileStream(name, size, readRange));

        return new DroppedFile(name, size, mediaType is { Length: > 0 } m ? m : MediaTypeFor(name),
            path: null, isDirectory, readAll, open);
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
