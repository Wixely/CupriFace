using System.Diagnostics;
using System.Threading;
using CupriFace.Threading;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

// The commit-snapshot render-thread seam (DESIGN §7.2): the UI thread builds an immutable DisplayList
// and Commit()s it; a background thread rasterises the latest snapshot. These verify the split is
// correct (same pixels as single-threaded) and that the producer never blocks on rasterisation.
public class ThreadedRendererTests
{
    [Fact]
    public void Rasterises_a_committed_snapshot_to_the_same_pixels_as_single_threaded()
    {
        const string html = "<body><div class='p'></div></body>";
        const string css = "body{margin:0;background:#ffffff} .p{width:60px;height:60px;margin:20px;background:#ff0000}";
        using var doc = CupriDocument.Load(html, css);
        var list = doc.BuildDisplayList(120, 120);       // UI-thread half: layout + paint → DisplayList

        SKBitmap? captured = null;
        using var done = new ManualResetEventSlim(false);
        using (var renderer = new ThreadedRenderer(img =>
        {
            var bmp = new SKBitmap(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            img.ReadPixels(bmp.PeekPixels(), 0, 0);      // copy before the render thread disposes img
            captured = bmp;
            done.Set();
        }))
        {
            renderer.Commit(list, 120, 120, SKColors.White);
            Assert.True(done.Wait(3000), "render thread never presented");
        }

        using var single = doc.RenderToImage(120, 120, SKColors.White);
        using var singleBmp = SKBitmap.FromImage(single);
        Assert.Equal(singleBmp.GetPixel(50, 50), captured!.GetPixel(50, 50)); // inside the red box
        Assert.Equal(singleBmp.GetPixel(5, 5), captured.GetPixel(5, 5));       // white margin
        captured.Dispose();
    }

    [Fact]
    public void Commit_never_blocks_on_rasterisation_and_coalesces_to_the_latest()
    {
        using var doc = CupriDocument.Load("<body></body>", "body{margin:0}");
        var list = doc.BuildDisplayList(20, 20);

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var presented = 0;
        using var renderer = new ThreadedRenderer(_ =>
        {
            Interlocked.Increment(ref presented);
            entered.Set();
            release.Wait();                              // pin the render thread inside present
        });

        renderer.Commit(list, 20, 20, SKColors.White);
        Assert.True(entered.Wait(3000));                 // render thread is now busy rasterising/presenting
        Assert.Equal(1, presented);

        // Flood commits while the render thread is pinned. If Commit blocked on rasterisation this
        // would deadlock (the render thread can't progress); it completing at all proves non-blocking.
        // The time bound is generous — 200 lock+signal pairs are microseconds of real work.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) renderer.Commit(list, 20, 20, SKColors.White);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Commit blocked: {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, presented);                      // all 200 coalesced into one pending snapshot (latest-wins)

        release.Set();
    }

    [Fact]
    public void ThreadedPresenter_presents_the_latest_frame_to_a_ui_thread_canvas()
    {
        const string css = "body{margin:0;background:#ffffff} .p{width:60px;height:60px;margin:20px;background:#008000}";
        using var doc = CupriDocument.Load("<body><div class='p'></div></body>", css);
        var list = doc.BuildDisplayList(120, 120);

        using var presenter = new ThreadedPresenter();
        presenter.Submit(list, 120, 120, SKColors.White);

        // Poll Present until the render thread has a frame ready (it returns false until then — this
        // avoids racing FramesRendered, which increments before the present callback copies the frame).
        using var surface = SKSurface.Create(new SKImageInfo(120, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        var ok = false;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000) { if (presenter.Present(surface.Canvas)) { ok = true; break; } Thread.Sleep(5); }
        Assert.True(ok, "presenter never produced a frame");
        surface.Canvas.Flush();
        using var got = SKBitmap.FromImage(surface.Snapshot());

        using var single = doc.RenderToImage(120, 120, SKColors.White);
        using var singleBmp = SKBitmap.FromImage(single);
        Assert.Equal(singleBmp.GetPixel(50, 50), got.GetPixel(50, 50)); // green box
        Assert.Equal(singleBmp.GetPixel(5, 5), got.GetPixel(5, 5));      // white margin
    }

    /// <summary>
    /// The render thread honours the host's logical-to-device scale (#137).
    ///
    /// <para>This path used to rasterise at a hard-coded scale 1 — a display list built in logical
    /// units painted straight into a physical surface — so on a 150% monitor a threaded app drew its
    /// UI at two-thirds size in the corner of its window. The list is still built in logical units;
    /// the scale is applied on the render thread, so glyphs and vectors are re-rasterised at the size
    /// they are actually shown at rather than resampled afterwards.</para>
    ///
    /// <para>Asserted geometrically rather than by pixel equality: a 20px logical margin must land at
    /// 40 physical pixels at scale 2, so the point that was background becomes box.</para>
    /// </summary>
    [Fact]
    public void Rasterises_at_the_hosts_effective_scale()
    {
        const string css = "body{margin:0;background:#ffffff} .p{width:60px;height:60px;margin:20px;background:#ff0000}";
        using var doc = CupriDocument.Load("<body><div class='p'></div></body>", css);
        var list = doc.BuildDisplayList(120, 120);       // 120x120 LOGICAL units

        SKBitmap? captured = null;
        using var done = new ManualResetEventSlim(false);
        using (var renderer = new ThreadedRenderer(img =>
        {
            var bmp = new SKBitmap(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            img.ReadPixels(bmp.PeekPixels(), 0, 0);
            captured = bmp;
            done.Set();
        }))
        {
            // A 240x240 device surface for the same 120x120 logical frame: D*P = 2.
            renderer.Commit(list, 240, 240, SKColors.White, 2f);
            Assert.True(done.Wait(3000), "render thread never presented");
        }

        Assert.Equal(240, captured!.Width);
        // The red box spans logical 20..80, so physical 40..160 at scale 2.
        Assert.Equal(new SKColor(0xff, 0, 0), captured.GetPixel(100, 100));  // well inside the box
        Assert.Equal(SKColors.White, captured.GetPixel(20, 20));             // still in the margin
        // The proof it SCALED rather than merely drew into a bigger surface: at scale 1 this point
        // is past the box's 80px edge and would be white.
        Assert.Equal(new SKColor(0xff, 0, 0), captured.GetPixel(150, 150));
        captured.Dispose();
    }
}
