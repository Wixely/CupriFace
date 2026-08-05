using System.Diagnostics;
using System.Net;
using CupriFace.Paint;
using CupriFace.Resources;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

public class ImageStoreTests
{
    private static byte[] MakePng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(new SKColor(0x40, 0x80, 0xC0));
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Remote_image_loads_asynchronously_without_blocking()
    {
        var png = MakePng(64, 48);
        const int port = 18099;
        var url = $"http://localhost:{port}/pic.png";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var hits = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                Interlocked.Increment(ref hits);
                await Task.Delay(250);                       // simulate a slow network
                ctx.Response.ContentType = "image/png";
                ctx.Response.OutputStream.Write(png);
                ctx.Response.Close();
            }
        });

        using var store = new ImageStore { UrlOptions = new CupriSourceOptions { RequireHttps = false } };

        var sw = Stopwatch.StartNew();
        var first = store.Get(url);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"first Get took {sw.ElapsedMilliseconds}ms");
        Assert.Null(first);                                  // not ready while loading
        Assert.Null(store.Size(url));                        // no layout block
        Assert.False(store.TakeArrived());

        _ = store.Get(url);                                  // a 2nd Get while pending must not re-fetch

        var arrived = false;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000) { if (store.TakeArrived()) { arrived = true; break; } Thread.Sleep(10); }
        Assert.True(arrived, "TakeArrived never fired");

        var loaded = store.Get(url);
        Assert.NotNull(loaded);
        Assert.Equal(64, loaded!.Width);
        Assert.Equal(48, loaded.Height);
        Assert.Equal(1, hits);                               // pending de-duped to one server fetch

        listener.Stop();
    }

    [Fact]
    public void Data_uri_decodes_synchronously()
    {
        var png = MakePng(10, 8);
        var uri = "data:image/png;base64," + System.Convert.ToBase64String(png);
        using var store = new ImageStore();
        var img = store.Get(uri);                            // local source → available immediately
        Assert.NotNull(img);
        Assert.Equal(10, img!.Width);
    }
}
