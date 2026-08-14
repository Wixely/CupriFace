using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The live-surface seam (<c>data-cupri-surface</c> + <see cref="SurfaceRegistry"/>): the generic
/// "element whose pixels come from an external producer" lane that video rides (and later 3D).
/// </summary>
public class SurfaceTests
{
    private sealed class FakeSurface : ISurfaceSource
    {
        public SKImage? CurrentFrame { get; set; }
        public (int W, int H)? NaturalSize { get; set; }
        public bool Ticking { get; set; }
    }

    private static SKImage Solid(int w, int h, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static int CountApprox(byte[] rgba, byte r, byte g, byte b)
    {
        var n = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
            if (Math.Abs(rgba[i] - r) < 8 && Math.Abs(rgba[i + 1] - g) < 8 && Math.Abs(rgba[i + 2] - b) < 8)
                n++;
        return n;
    }

    private const string Html = "<body><div class='v' data-cupri-surface='s'></div></body>";
    private const string Css = ".v { width: 100px; height: 60px; }";

    [Fact]
    public void Frames_paint_and_a_swap_repaints_the_new_frame()
    {
        using var t = new TestDoc(Html, Css);
        var s = new FakeSurface { CurrentFrame = Solid(100, 60, new SKColor(200, 30, 30)) };
        t.Doc.Surfaces.Register("s", s);

        var px1 = t.Doc.RenderToPixels(200, 120);
        Assert.True(CountApprox(px1, 200, 30, 30) > 4000, "the first frame must paint");

        s.CurrentFrame = Solid(100, 60, new SKColor(30, 30, 200));
        t.Doc.Surfaces.NotifyFrame();
        var px2 = t.Doc.RenderToPixels(200, 120);
        Assert.True(CountApprox(px2, 30, 30, 200) > 4000, "the swapped frame must paint");
        Assert.Equal(0, CountApprox(px2, 200, 30, 30));
    }

    [Fact]
    public void Poster_shows_until_the_first_frame_then_the_frame_wins()
    {
        // Poster = data: URI (the ImageSrc lane); the surface exists but has no frame yet.
        using var poster = Solid(100, 60, new SKColor(30, 160, 30));
        using var data = poster.Encode(SKEncodedImageFormat.Png, 100);
        var html = $"<body><div class='v' data-cupri-surface='s' " +
                   $"data-cupri-image='data:image/png;base64,{Convert.ToBase64String(data.ToArray())}'></div></body>";

        using var t = new TestDoc(html, Css);
        var s = new FakeSurface();
        t.Doc.Surfaces.Register("s", s);

        Assert.True(CountApprox(t.Doc.RenderToPixels(200, 120), 30, 160, 30) > 4000, "poster paints before the first frame");

        s.CurrentFrame = Solid(100, 60, new SKColor(30, 30, 200));
        t.Doc.Surfaces.NotifyFrame();
        var px = t.Doc.RenderToPixels(200, 120);
        Assert.True(CountApprox(px, 30, 30, 200) > 4000, "the live frame replaces the poster");
        Assert.Equal(0, CountApprox(px, 30, 160, 30));
    }

    [Fact]
    public void Natural_size_drives_layout_like_an_image()
    {
        using var t = new TestDoc("<body><div data-cupri-surface='s'></div></body>");   // no CSS size
        t.Doc.Surfaces.Register("s", new FakeSurface { NaturalSize = (200, 100) });
        t.Layout();

        var node = t.Find(n => n.SurfaceKey == "s")!;
        Assert.Equal(200, node.Width, 1);
        Assert.Equal(100, node.Height, 1);
    }

    [Fact]
    public void A_ticking_surface_keeps_the_render_loop_live()
    {
        using var t = new TestDoc(Html, Css);
        var s = new FakeSurface();
        t.Doc.Surfaces.Register("s", s);
        Assert.False(t.Doc.HasActiveAnimations);

        s.Ticking = true;                            // "playing"
        Assert.True(t.Doc.HasActiveAnimations);      // hosts keep painting, like a CSS animation

        s.Ticking = false;                           // "paused"
        Assert.False(t.Doc.HasActiveAnimations);     // idle window costs nothing again
    }

    private sealed class UnderlaySurface : ISurfaceSource
    {
        public SKImage? CurrentFrame => null;
        public (int W, int H)? NaturalSize => (100, 60);
        public bool Ticking => false;
        public bool Ready;                       // flips true when the host's element can show
        public bool HostComposited => Ready;
    }

    [Fact]
    public void A_host_composited_surface_punches_a_transparent_hole_and_suppresses_the_poster()
    {
        using var poster = Solid(100, 60, new SKColor(30, 160, 30));
        using var data = poster.Encode(SKEncodedImageFormat.Png, 100);
        var html = $"<body><div class='v' data-cupri-surface='s' " +
                   $"data-cupri-image='data:image/png;base64,{Convert.ToBase64String(data.ToArray())}'></div></body>";

        using var t = new TestDoc(html, Css);
        var s = new UnderlaySurface();
        t.Doc.Surfaces.Register("s", s);

        // Not ready yet: the poster paints (the underlay can't show anything).
        Assert.True(CountApprox(t.Doc.RenderToPixels(200, 120), 30, 160, 30) > 4000);

        // Ready: the element becomes a transparent hole — poster suppressed, alpha 0 inside,
        // page background intact outside.
        s.Ready = true;
        t.Doc.Surfaces.NotifyFrame();
        var px = t.Doc.RenderToPixels(200, 120);   // clear defaults to transparent
        Assert.Equal(0, CountApprox(px, 30, 160, 30));
        var holeAlpha = px[(10 * 200 + 10) * 4 + 3];      // inside the 100x60 box
        var outsideAlpha = px[(110 * 200 + 150) * 4 + 3]; // below/right of it
        Assert.Equal(0, holeAlpha);
        Assert.Equal(0, outsideAlpha);                    // transparent doc background stays transparent
    }

    [Fact]
    public void Frame_arrival_wakes_an_idle_host_exactly_once()
    {
        using var t = new TestDoc(Html, Css);
        t.Doc.ConsumeImageArrived();                 // drain the initial state

        Assert.False(t.Doc.ConsumeImageArrived());
        t.Doc.Surfaces.NotifyFrame();                // paused seek / first frame while idle
        Assert.True(t.Doc.ConsumeImageArrived());    // one repaint…
        Assert.False(t.Doc.ConsumeImageArrived());   // …not a busy loop

        t.Doc.Surfaces.Register("s2", new FakeSurface());
        Assert.True(t.Doc.ConsumeImageArrived());    // registration repaints too (shows first state)
    }

    [Fact]
    public void ScreenTransform_matches_the_paint_paths_matrix()
    {
        // The web underlay follows this matrix; the painted hole follows the rasteriser's. They
        // must be the SAME mapping — centre = the transformed node's own box centre, transforms
        // composed outermost-first. Box (20,20,100,60), centre (70,50), translateX(10) scale(2):
        // origin (20,20) → (70+10 + 2·(20−70), 50 + 2·(20−50)) = (−20, −10).
        using var t = new TestDoc(
            "<body style='margin:0'><div style='margin:20px; width:100px; height:60px; " +
            "transform: translateX(10px) scale(2)' data-cupri-surface='probe'></div></body>",
            "", components: true, width: 400, height: 300);
        t.Layout();

        var node = t.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "probe")!;
        var m = CupriFace.Interaction.HitTesting.ScreenTransform(node);
        Assert.False(m.IsIdentity);
        Assert.Equal(2, m.ScaleX, 3);
        Assert.Equal(2, m.ScaleY, 3);
        Assert.Equal(0, m.SkewX, 3);
        var mapped = m.MapPoint(20, 20);
        Assert.Equal(-20, mapped.X, 1);
        Assert.Equal(-10, mapped.Y, 1);

        // An untransformed node reports identity — hosts then skip the CSS transform entirely.
        using var plain = new TestDoc("<body><div data-cupri-surface='p2' style='width:50px;height:20px'></div></body>",
            "", components: true);
        plain.Layout();
        Assert.True(CupriFace.Interaction.HitTesting.ScreenTransform(
            plain.Find(n => n.Element?.GetAttribute("data-cupri-surface") == "p2")!).IsIdentity);
    }
}
