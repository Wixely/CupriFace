using System.Net;
using System.Net.Sockets;
using System.Text;
using CupriFace.Components;
using CupriFace.Resources;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// <c>CupriDocument.Settle</c>: render until the next frame is complete.
///
/// <para>Tested against a real socket rather than a stub, because the thing under test is a race —
/// a fetch that starts during layout and finishes on a thread pool thread — and a stub that
/// resolves synchronously would pass whatever the code did.</para>
/// </summary>
public class SettleTests(ITestOutputHelper output) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<Task> _served = [];

    /// <summary>Serve one PNG after <paramref name="delayMs"/>, once, and hand back its URL.</summary>
    private string ServeOnePng(int delayMs, SKColor colour)
    {
        if (_served.Count == 0) _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        using var bmp = new SKBitmap(40, 40);
        using (var c = new SKCanvas(bmp)) c.Clear(colour);
        var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90).ToArray();

        _served.Add(Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var s = client.GetStream();
            var buf = new byte[4096];
            await s.ReadAsync(buf);
            await Task.Delay(delayMs);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(head);
            await s.WriteAsync(png);
            await s.FlushAsync();
        }));
        return $"http://127.0.0.1:{port}/img{_served.Count}.png";
    }

    private static CupriDocument Doc(string url) =>
        CupriDocument.Load(
                $"<body><cupri-image src='{url}'></cupri-image></body>",
                "body { margin:0; font-family:sans-serif; } cupri-image { width:40px; height:40px; }")
            .UseComponents(ComponentRegistry.Default())
            .UseImageUrlOptions(new CupriSourceOptions
            {
                RequireHttps = false,
                AllowedHosts = ["127.0.0.1"],
                Timeout = TimeSpan.FromSeconds(5),
            });

    /// <summary>
    /// The trap this exists for: a remote fetch does not start until a layout asks for the image,
    /// so a document that has never rendered reports "loaded" because nothing has BEGUN. A caller
    /// polling <c>IsLoaded</c> to decide the first frame is ready captures it without the image.
    /// </summary>
    [Fact]
    public void IsLoaded_is_true_before_the_first_render_because_nothing_has_started()
    {
        var url = ServeOnePng(300, SKColors.Green);
        using var doc = Doc(url);
        doc.Refresh();

        Assert.True(doc.IsLoaded);          // and it means nothing
        Assert.Equal(0, doc.PendingLoads);

        using (doc.RenderToImage(80, 80)) { }
        output.WriteLine($"after one render: IsLoaded={doc.IsLoaded}, PendingLoads={doc.PendingLoads}");
        Assert.False(doc.IsLoaded);         // NOW there is something to wait for
        Assert.Equal(1, doc.PendingLoads);
    }

    /// <summary>Settle renders first, so it waits for the fetch its own render started — and the
    /// frame after it contains the image.</summary>
    [Fact]
    public void Settle_waits_for_an_image_and_the_next_frame_contains_it()
    {
        var url = ServeOnePng(300, new SKColor(0x22, 0x88, 0x44));
        using var doc = Doc(url);
        doc.Refresh();

        Assert.True(doc.Settle(80, 80, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, doc.PendingLoads);

        using var frame = doc.RenderToImage(80, 80, SKColors.White);
        using var bmp = SKBitmap.FromImage(frame);
        var px = bmp.GetPixel(20, 20);
        output.WriteLine($"pixel {px}");
        Assert.True(px.Green > px.Red && px.Green > px.Blue, $"the image should be painted; got {px}");
    }

    /// <summary>A document with nothing to fetch settles on its first pass rather than waiting out
    /// a poll interval — the common case must not cost anything.</summary>
    [Fact]
    public void A_document_with_no_remote_content_settles_immediately()
    {
        using var doc = CupriDocument.Load("<body><div>plain</div></body>", "body { font-family:sans-serif; }");
        doc.Refresh();

        var started = DateTime.UtcNow;
        Assert.True(doc.Settle(80, 80, TimeSpan.FromSeconds(5)));
        var ms = (DateTime.UtcNow - started).TotalMilliseconds;
        output.WriteLine($"settled in {ms:0} ms");
        Assert.True(ms < 500, $"should return on the first pass, took {ms:0} ms");
    }

    /// <summary>A fetch that never completes must return false, not hang and not claim success. A
    /// caller that ignores the result ships a frame with holes; one that reads it can say so.</summary>
    [Fact]
    public void Settle_reports_failure_rather_than_hanging_when_a_fetch_never_finishes()
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _served.Add(Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await Task.Delay(TimeSpan.FromSeconds(30));      // accepted, never answered
        }));

        using var doc = Doc($"http://127.0.0.1:{port}/never.png");
        doc.Refresh();

        var started = DateTime.UtcNow;
        var settled = doc.Settle(80, 80, TimeSpan.FromMilliseconds(400));
        var ms = (DateTime.UtcNow - started).TotalMilliseconds;
        output.WriteLine($"returned {settled} after {ms:0} ms");

        Assert.False(settled);
        Assert.True(ms is > 300 and < 4000, $"should honour the timeout, took {ms:0} ms");
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
    }
}
