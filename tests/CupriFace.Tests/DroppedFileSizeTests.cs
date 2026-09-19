using System.Security.Cryptography;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// What happens when the file someone dragged in is large (#182).
///
/// <para>The deferred design means a drop costs nothing — but the first <c>ReadBytesAsync</c>
/// materialises everything, and in a browser that lands in wasm32's 4GB address space alongside the
/// heap, the runtime and the framebuffer. There was no cap, so an app that forgot to check
/// <c>Size</c> got a dead tab rather than an exception: the one failure mode you cannot catch, log,
/// or apologise for.</para>
///
/// <para>Two answers, and this pins both. A capped read refuses early and says what to do instead;
/// <c>OpenReadAsync</c> streams, so the size stops mattering at all. The stream is the real fix —
/// the cap only turns a crash into a message.</para>
/// </summary>
public class DroppedFileSizeTests(ITestOutputHelper output)
{
    private static DroppedFile Big(long size, Func<long, int, CancellationToken, Task<byte[]>>? range = null)
        => DroppedFile.Deferred("huge.bin", size, "application/octet-stream",
            _ => throw new InvalidOperationException("a refused read must never transfer anything"),
            readRange: range);

    // ---- the cap -----------------------------------------------------------------------------

    /// <summary>Over the cap is refused from the METADATA, before a byte moves. The deferred read
    /// here throws if it is ever called, so the test fails loudly if the check happens too late.</summary>
    [Fact]
    public async Task A_file_over_the_cap_is_refused_without_transferring_anything()
    {
        var ex = await Assert.ThrowsAsync<IOException>(
            () => Big(4L * 1024 * 1024 * 1024).ReadBytesAsync(maxBytes: 8 * 1024 * 1024));

        output.WriteLine(ex.Message);
        Assert.Contains("4 GiB", ex.Message);
        Assert.Contains("OpenReadAsync", ex.Message);   // the actionable half
    }

    /// <summary>Under the cap reads normally — a guard that refused everything would also pass the
    /// test above.</summary>
    [Fact]
    public async Task A_file_under_the_cap_reads_normally()
    {
        var f = DroppedFile.FromBytes("small.txt", "hello"u8.ToArray());
        Assert.Equal("hello", await f.ReadTextAsync());
    }

    /// <summary>The default is a real number rather than "unlimited", which is the whole point.</summary>
    [Fact]
    public void There_is_a_default_cap()
    {
        output.WriteLine($"default MaxReadBytes = {DroppedFile.MaxReadBytes:N0}");
        Assert.InRange(DroppedFile.MaxReadBytes, 1, 1024L * 1024 * 1024);
    }

    /// <summary>An app that means to hold a large file can say so — the cap is a default, not a
    /// policy. Restored afterwards because it is process-wide.</summary>
    [Fact]
    public async Task An_app_can_raise_the_cap()
    {
        var original = DroppedFile.MaxReadBytes;
        try
        {
            DroppedFile.MaxReadBytes = 4;
            var f = DroppedFile.FromBytes("five.txt", "hello"u8.ToArray());
            await Assert.ThrowsAsync<IOException>(() => f.ReadBytesAsync());

            DroppedFile.MaxReadBytes = 1024;
            Assert.Equal("hello", await f.ReadTextAsync());
        }
        finally { DroppedFile.MaxReadBytes = original; }
    }

    /// <summary>A size the platform under-reported does not get to slip past: the arriving bytes are
    /// checked too. A browser's File.size is a snapshot, and a file can change after the drop.</summary>
    [Fact]
    public async Task A_lie_about_the_size_is_still_caught_after_the_read()
    {
        var f = DroppedFile.Deferred("liar.bin", size: 4, "application/octet-stream",
            _ => Task.FromResult(new byte[5000]));      // says 4, delivers 5000

        var ex = await Assert.ThrowsAsync<IOException>(() => f.ReadBytesAsync(maxBytes: 1000));
        output.WriteLine(ex.Message);
        Assert.Contains("read limit", ex.Message);
    }

    /// <summary>An unknown size (-1) cannot be pre-checked, so it must be checked on arrival rather
    /// than waved through — otherwise "unknown" becomes the way around the cap.</summary>
    [Fact]
    public async Task An_unknown_size_is_checked_on_arrival()
    {
        var f = DroppedFile.Deferred("mystery.bin", size: -1, null, _ => Task.FromResult(new byte[9000]));
        await Assert.ThrowsAsync<IOException>(() => f.ReadBytesAsync(maxBytes: 1000));
        Assert.Equal(9000, (await f.ReadBytesAsync(maxBytes: 100_000)).Length);
    }

    /// <summary>Beyond about 2GB no cap can be raised far enough — a byte[] cannot hold it on ANY
    /// platform, wasm or not — so that is a separate refusal with its own wording. It used to
    /// truncate silently through an int in the browser interop.</summary>
    [Fact]
    public async Task Past_the_array_ceiling_the_refusal_says_so_whatever_the_cap()
    {
        var original = DroppedFile.MaxReadBytes;
        try
        {
            DroppedFile.MaxReadBytes = long.MaxValue;      // as raised as it goes
            var ex = await Assert.ThrowsAsync<IOException>(
                () => Big(3L * 1024 * 1024 * 1024).ReadBytesAsync());

            output.WriteLine(ex.Message);
            Assert.Contains("any platform", ex.Message);
            Assert.Contains("OpenReadAsync", ex.Message);
        }
        finally { DroppedFile.MaxReadBytes = original; }
    }

    /// <summary>ReadTextAsync and ToSourceAsync go through the same cap — a limit one of three doors
    /// ignores is not a limit.</summary>
    [Fact]
    public async Task Every_whole_file_read_honours_the_cap()
    {
        var original = DroppedFile.MaxReadBytes;
        try
        {
            DroppedFile.MaxReadBytes = 4;
            var f = DroppedFile.FromBytes("five.txt", "hello"u8.ToArray());
            await Assert.ThrowsAsync<IOException>(() => f.ReadBytesAsync());
            await Assert.ThrowsAsync<IOException>(() => f.ReadTextAsync());
            await Assert.ThrowsAsync<IOException>(() => f.ToSourceAsync());
        }
        finally { DroppedFile.MaxReadBytes = original; }
    }

    // ---- streaming: the answer rather than the guard rail --------------------------------------

    /// <summary>
    /// <b>The point of the whole exercise.</b> A file far past the cap — and past what a byte[] could
    /// hold — is read end to end while never holding more than a chunk. The range reader counts the
    /// largest single allocation it hands out, so "never buffered it" is measured rather than hoped.
    /// </summary>
    [Fact]
    public async Task A_file_larger_than_the_cap_streams_without_ever_holding_it()
    {
        const long size = 3L * 1024 * 1024 * 1024;      // 3 GiB: past the cap AND past Array.MaxLength
        long served = 0, biggest = 0;

        var f = Big(size, (offset, len, _) =>
        {
            served += len;
            biggest = Math.Max(biggest, len);
            return Task.FromResult(new byte[len]);      // pretend bytes; only the sizes matter here
        });

        await using var stream = await f.OpenReadAsync();
        var buffer = new byte[64 * 1024];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0) total += n;

        output.WriteLine($"read {total:N0} bytes; largest single allocation {biggest:N0}");
        Assert.Equal(size, total);
        Assert.Equal(size, served);
        Assert.True(biggest <= 1024 * 1024, $"a chunk was {biggest:N0} bytes — that is buffering, not streaming");
    }

    /// <summary>The bytes are the right bytes, in the right order — a chunked reader that loses its
    /// place still passes a length check. Hashed against the same content read whole.</summary>
    [Fact]
    public async Task What_streams_out_is_what_went_in()
    {
        var content = new byte[3 * DroppedFileStreamChunk + 517];   // deliberately not a chunk multiple
        Random.Shared.NextBytes(content);

        var f = DroppedFile.Deferred("data.bin", content.Length, null,
            _ => Task.FromResult(content),
            readRange: (offset, len, _) => Task.FromResult(content.AsSpan((int)offset, len).ToArray()));

        await using var stream = await f.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)),
                     Convert.ToHexString(SHA256.HashData(ms.ToArray())));
    }

    private const int DroppedFileStreamChunk = 1024 * 1024;

    /// <summary>Seekable, so a caller can read a header and a trailer without the middle — which is
    /// the practical reason to want random access over a 3GB file.</summary>
    [Fact]
    public async Task The_stream_seeks_so_a_header_and_trailer_cost_two_chunks()
    {
        var content = new byte[5 * DroppedFileStreamChunk];
        content[0] = 0xC0; content[1] = 0xFF;
        content[^2] = 0xEE; content[^1] = 0x77;

        var fetches = 0;
        var f = DroppedFile.Deferred("data.bin", content.Length, null,
            _ => Task.FromResult(content),
            readRange: (offset, len, _) => { fetches++; return Task.FromResult(content.AsSpan((int)offset, len).ToArray()); });

        await using var stream = await f.OpenReadAsync();
        var head = new byte[2];
        await stream.ReadExactlyAsync(head);

        stream.Seek(-2, SeekOrigin.End);
        var tail = new byte[2];
        await stream.ReadExactlyAsync(tail);

        output.WriteLine($"{fetches} range fetches over a {content.Length:N0}-byte file");
        Assert.Equal([0xC0, 0xFF], head);
        Assert.Equal([0xEE, 0x77], tail);
        Assert.Equal(2, fetches);
    }

    /// <summary>
    /// A synchronous Read throws rather than deadlocking. In a browser the page has one thread, so
    /// blocking it on the promise that would deliver the bytes means the promise can never resolve —
    /// the tab hangs with no error at all. Throwing at the call is the kinder failure.
    /// </summary>
    [Fact]
    public async Task A_synchronous_read_throws_rather_than_hanging_the_tab()
    {
        var f = Big(1024, (o, l, _) => Task.FromResult(new byte[l]));
        await using var stream = await f.OpenReadAsync();

        var ex = Assert.Throws<NotSupportedException>(() => stream.Read(new byte[16], 0, 16));
        output.WriteLine(ex.Message);
        Assert.Contains("ReadAsync", ex.Message);
    }

    /// <summary>Streaming is not a way around the folder check.</summary>
    [Fact]
    public async Task A_folder_cannot_be_streamed_either()
    {
        var f = DroppedFile.Deferred("project", 0, "", _ => Task.FromResult<byte[]>([]), isDirectory: true);
        var ex = await Assert.ThrowsAsync<IOException>(() => f.OpenReadAsync());
        Assert.Contains("is a folder", ex.Message);
    }

    /// <summary>A host with no range reader still streams — by buffering, subject to the cap. Worse,
    /// but not broken, and the caller's code is identical.</summary>
    [Fact]
    public async Task Without_a_range_reader_the_stream_falls_back_to_buffering()
    {
        var f = DroppedFile.Deferred("small.bin", 5, null, _ => Task.FromResult("hello"u8.ToArray()));
        await using var stream = await f.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal("hello"u8.ToArray(), ms.ToArray());
    }

    /// <summary>A real file on disk streams as a FileStream — the desktop half of the same call.</summary>
    [Fact]
    public async Task A_file_on_disk_streams_too()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "streamed from disk");
        try
        {
            await using var stream = await DroppedFile.FromPath(path).OpenReadAsync();
            using var reader = new StreamReader(stream);
            Assert.Equal("streamed from disk", await reader.ReadToEndAsync());
        }
        finally { File.Delete(path); }
    }

    /// <summary>The shape an app actually writes for something big: hash it without holding it.</summary>
    [Fact]
    public async Task An_app_can_hash_a_file_it_could_never_hold()
    {
        var content = new byte[2 * DroppedFileStreamChunk + 9];
        Random.Shared.NextBytes(content);

        var f = DroppedFile.Deferred("big.bin", content.Length, null,
            _ => throw new InvalidOperationException("hashing must not buffer the file"),
            readRange: (offset, len, _) => Task.FromResult(content.AsSpan((int)offset, len).ToArray()));

        await using var stream = await f.OpenReadAsync();
        var hash = await SHA256.HashDataAsync(stream);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), Convert.ToHexString(hash));
    }
}
