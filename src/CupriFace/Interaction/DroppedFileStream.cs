namespace CupriFace.Interaction;

/// <summary>
/// A dropped file read a chunk at a time, for a host whose bytes live somewhere else — which in
/// practice means a browser, where the file is a blob behind a promise.
///
/// <para>This is what makes a large file survivable on the web. <see cref="DroppedFile.ReadBytesAsync()"/>
/// materialises everything and is capped for good reason: in wasm32 the whole process shares a 4GB
/// address space, and an uncapped read of whatever the user dragged in ends the tab rather than
/// throwing something catchable. Pulling <c>blob.slice()</c> ranges instead means a 4GB video can be
/// hashed, parsed or uploaded while never holding more than <see cref="ChunkSize"/>.</para>
///
/// <para><b>Asynchronous only.</b> <see cref="Read(byte[],int,int)"/> throws. It is not an oversight
/// and not laziness: the page is single-threaded, so blocking a synchronous read on the promise that
/// would deliver the bytes stops that promise ever resolving — the deadlock takes the tab with it.
/// Throwing at the call is the kinder failure, and the message says which method to use.</para>
///
/// <para>Seekable, because a range fetch is random access by nature — which is what lets a caller
/// read a header, skip, and read a trailer without touching the middle at all.</para>
/// </summary>
internal sealed class DroppedFileStream(
    string name, long length, Func<long, int, CancellationToken, Task<byte[]>> readRange) : Stream
{
    /// <summary>How much is fetched per round trip. Each one crosses into JS and back, so too small
    /// is chatty; too large defeats the point of streaming.</summary>
    internal const int ChunkSize = 1024 * 1024;

    private readonly Func<long, int, CancellationToken, Task<byte[]>> _readRange = readRange;
    private readonly string _name = name;
    private readonly long _length = length < 0 ? 0 : length;

    private byte[] _chunk = [];
    private long _chunkStart = -1;     // where _chunk begins in the file; -1 = nothing cached
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= _length || buffer.IsEmpty) return 0;

        // Serve from the cached chunk when we can; fetch the one containing _position otherwise.
        // Aligned to ChunkSize so a sequential read walks chunk boundaries exactly once each, and a
        // seek backwards into a chunk already held costs no round trip.
        if (_chunkStart < 0 || _position < _chunkStart || _position >= _chunkStart + _chunk.Length)
        {
            var start = _position / ChunkSize * ChunkSize;
            var want = (int)Math.Min(ChunkSize, _length - start);
            if (want <= 0) return 0;

            _chunk = await _readRange(start, want, ct).ConfigureAwait(false)
                     ?? throw new IOException($"'{_name}': the host returned nothing for bytes {start}–{start + want}.");
            _chunkStart = start;

            if (_chunk.Length == 0)
                throw new IOException(
                    $"'{_name}': the host returned no bytes for {start}–{start + want}, which would loop for ever. " +
                    "The file may have changed since it was dropped.");
        }

        var offset = (int)(_position - _chunkStart);
        var n = Math.Min(buffer.Length, _chunk.Length - offset);
        if (n <= 0) return 0;

        _chunk.AsSpan(offset, n).CopyTo(buffer.Span);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    /// <summary>Always throws. See the note on the class: a synchronous read in a browser deadlocks
    /// the single thread that would deliver the bytes.</summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(
        $"'{_name}' can only be read asynchronously — use ReadAsync. A synchronous read cannot work " +
        "in a browser: the page has one thread, and blocking it stops the read it is waiting for.");

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
