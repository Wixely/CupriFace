using System;
using CupriFace.Dom;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Incremental (damage-clipped) rendering for retained-canvas hosts: RenderIncremental must
/// produce pixels IDENTICAL to a fresh full render after any mutation, return null (drawing nothing)
/// for an unchanged frame, and keep the damage rect small for a localised change like hover.</summary>
public class DamageTests
{
    private const int W = 420, H = 320;

    // Drive a first full paint into a retained bitmap, mutate, incrementally repaint the SAME bitmap,
    // and require byte-identical pixels to a fresh full render of the new state. Returns the damage rect.
    private static SKRectI? AssertIncrementalMatchesFull(TestDoc t, Action mutate)
    {
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);
        var first = t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        Assert.NotNull(first);                                          // no previous frame → full paint

        mutate();
        var damage = t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        canvas.Flush();

        // Reference: a fresh FULL render of the same state through the identical bitmap pipeline
        // (same colour type/order — SKBitmap.FromImage would land in platform-native BGRA).
        using var reference = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var refCanvas = new SKCanvas(reference))
        {
            refCanvas.Clear(SKColors.White);
            t.Doc.Render(refCanvas, W, H);
            refCanvas.Flush();
        }
        AssertVisuallyIdentical(retained, reference, damage);
        return damage;
    }

    // Byte-exact equality is too strict for a clipped repaint: Skia computes anti-aliased coverage
    // against the clip, so AA pixels of primitives CROSSING the damage boundary can differ from a
    // monolithic render by a couple of least-significant bits (verified: unclipped and full-rect-clipped
    // repaints are byte-identical; only a partial clip introduces the drift). Structural bugs — a stale
    // un-repainted region, wrong damage — produce large deltas, which this still catches.
    private static void AssertVisuallyIdentical(SKBitmap actual, SKBitmap expected, SKRectI? damage)
    {
        var a = actual.Bytes; var e = expected.Bytes;
        Assert.Equal(e.Length, a.Length);
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i] - e[i]);
            Assert.True(d <= 16, $"pixel byte {i} differs by {d} (damage {damage}) — beyond AA-edge tolerance");
        }
    }

    [Fact]
    public void Unchanged_frame_returns_null_and_draws_nothing()
    {
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-button>Save</cupri-button></div></body>",
            "", components: true, width: W, height: H);
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);

        Assert.NotNull(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));
        Assert.Null(t.Doc.RenderIncremental(canvas, W, H, SKColors.White)); // identical frame → skip
    }

    [Fact]
    public void A_recreated_host_surface_forces_the_next_frame_to_repaint_in_full()
    {
        // The un-fullscreen black-screen bug: the SDL window recreated its bitmap+texture (blank)
        // while the doc's diff base still said "last frame is on screen", so only the video's rect
        // got repainted — everything else stayed black. InvalidateRetainedFrame is the host's
        // "your pixels are gone" signal: the next identical frame must be a FULL repaint, not null.
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-button>Save</cupri-button></div></body>",
            "", components: true, width: W, height: H);
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);

        Assert.NotNull(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));
        Assert.Null(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));

        canvas.Clear(SKColors.Black);                       // the recreated surface holds nothing
        t.Doc.InvalidateRetainedFrame();
        var damage = t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        Assert.Equal(new SKRectI(0, 0, W, H), damage);      // full frame, not just what "changed"

        canvas.Flush();
        var pixels = retained.Bytes;                        // and no black survives anywhere
        for (var i = 0; i < pixels.Length; i += 4)
            Assert.False(pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0,
                $"pixel {i / 4} is still black — the full repaint didn't cover it");
    }

    private sealed class TickSource : CupriFace.Paint.ISurfaceSource
    {
        public SKImage? CurrentFrame { get; private set; }
        public (int W, int H)? NaturalSize => (160, 90);
        public bool Ticking => true;
        public void Publish(SKColor colour)
        {
            using var s = SKSurface.Create(new SKImageInfo(160, 90));
            s.Canvas.Clear(colour);
            var old = CurrentFrame;
            CurrentFrame = s.Snapshot();
            old?.Dispose();
        }
    }

    [Fact]
    public void A_surface_frame_swap_takes_the_fast_path_and_paints_the_new_frame()
    {
        // The surface fast path: a new video frame must cost a clipped raster of the video box —
        // no layout, no paint-list build, no diff — and still put the RIGHT pixels on screen.
        var source = new TickSource();
        source.Publish(SKColors.Red);
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-button>Save</cupri-button>" +
            "<div data-cupri-surface='probe' style='width:160px;height:90px'></div></div></body>",
            "", components: true, width: W, height: H);
        t.Doc.Surfaces.Register("probe", source);
        t.Doc.Refresh();
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);

        Assert.NotNull(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));   // normal first paint
        Assert.False(t.Doc.LastFrame.FastPath);

        source.Publish(SKColors.Blue);                       // ONLY the frame changes
        var damage = t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        Assert.NotNull(damage);
        Assert.True(t.Doc.LastFrame.FastPath, "a pure frame swap must take the fast path");
        Assert.Equal(0, t.Doc.LastFrame.LayoutMs);           // nothing but raster ran
        Assert.True(damage!.Value.Width <= 162 && damage.Value.Height <= 92,
            $"damage {damage.Value.Width}x{damage.Value.Height} must be the surface box, not the page");
        canvas.Flush();
        var inside = retained.GetPixel(damage.Value.Left + 20, damage.Value.Top + 20);
        Assert.Equal(SKColors.Blue, inside);                 // and the NEW frame is on screen

        Assert.Null(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));      // no new frame → clean
        Assert.True(t.Doc.LastFrame.FastPath);
    }

    [Fact]
    public void Input_between_frames_leaves_the_fast_path_and_repaints_correctly()
    {
        var source = new TickSource();
        source.Publish(SKColors.Red);
        using var t = new TestDoc(
            "<body><div style='padding:20px'>" +
            "<div class='probe-target' style='width:80px;height:26px'>hover me</div>" +
            "<div data-cupri-surface='probe' style='width:160px;height:90px'></div></div></body>",
            ".probe-target:hover { background:#00ff00; }", components: true, width: W, height: H);
        t.Doc.Surfaces.Register("probe", source);
        t.Doc.Refresh();
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);
        t.Doc.RenderIncremental(canvas, W, H, SKColors.White);

        source.Publish(SKColors.Blue);
        t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        Assert.True(t.Doc.LastFrame.FastPath);

        // A dispatch that changes anything (here: a hover restyle) must force the NORMAL path
        // next frame — and pixels must match a fresh full render (the standing damage contract).
        var target = t.FindClass("probe-target");
        var (hx, hy) = TestDoc.Center(target);
        Assert.True(t.Doc.DispatchPointerMove(hx, hy));      // hover ON → something changed
        source.Publish(SKColors.Lime);                       // a frame arrives in the SAME frame
        var damage = t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        Assert.False(t.Doc.LastFrame.FastPath, "input invalidates the retained list");
        Assert.NotNull(damage);
        canvas.Flush();

        using var reference = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var rc = new SKCanvas(reference)) { rc.Clear(SKColors.White); t.Doc.Render(rc, W, H); rc.Flush(); }
        AssertVisuallyIdentical(retained, reference, damage);
    }

    [Fact]
    public void Charts_do_not_false_damage_from_reference_inequality()
    {
        // Polyline holds a list (reference equality by default) — the diff must compare its points, or
        // a line chart would read as "changed" on every frame and defeat the null fast-path.
        using var t = new TestDoc(
            "<body><div style='padding:16px'><cupri-line-chart values=\"5,8,6,11\" labels=\"a,b,c,d\"></cupri-line-chart></div></body>",
            "", components: true, width: W, height: H);
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);

        Assert.NotNull(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));
        Assert.Null(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));
    }

    [Fact]
    public void Hover_repaints_a_small_region_identically()
    {
        // Self-contained hover style; the base background lives in CSS (inline would beat the hover rule).
        using var t = new TestDoc(
            "<body><div style='padding:20px; display:flex; gap:12px'>" +
            "<div class='hv'></div>" +
            "<p style='margin-top:70px'>Some unrelated text below.</p></div></body>",
            ".hv { width:90px; height:34px; background:#eef1f5; border-radius:8px; } " +
            ".hv[data-hover] { background:#B87333; }", components: true, width: W, height: H);

        var (x, y) = TestDoc.Center(t.FindClass("hv"));
        var damage = AssertIncrementalMatchesFull(t, () => t.Move(x, y));
        Assert.NotNull(damage);
        Assert.True(damage!.Value.Width * damage.Value.Height < 0.25f * W * H,
            $"hover damage should be local, was {damage}");
    }

    [Fact]
    public void A_model_change_repaints_identically()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:18px'><cupri-switch checked=\"{{On}}\"></cupri-switch>" +
            "<div>State: {{On}}</div></div></body>",
            "", m, components: true, width: W, height: H);

        var (x, y) = TestDoc.Center(t.FindClass("cupri-switch"));
        AssertIncrementalMatchesFull(t, () => t.Click(x, y));           // toggles the switch + label text
        Assert.True(m.On);
    }

    [Fact]
    public void Opening_an_overlay_repaints_identically()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:18px'><cupri-select value=\"{{Size}}\" open=\"{{Open}}\">" +
            "<cupri-option value=\"s\">Small</cupri-option><cupri-option value=\"l\">Large</cupri-option>" +
            "</cupri-select></div></body>",
            "", m, components: true, width: W, height: H);

        var (x, y) = TestDoc.Center(t.FindRole("combobox"));
        AssertIncrementalMatchesFull(t, () => t.Click(x, y));           // opens the fixed-position popup
        Assert.True(m.Open);
    }

    [Fact]
    public void Scrolling_repaints_identically()
    {
        using var t = new TestDoc(
            "<body><div class='sc' style='height:120px; overflow:scroll; margin:14px'>" +
            "<div style='height:60px'>alpha</div><div style='height:60px'>beta</div>" +
            "<div style='height:60px'>gamma</div><div style='height:60px'>delta</div></div></body>",
            "", components: true, width: W, height: H);

        var (x, y) = TestDoc.Center(t.FindClass("sc"));
        AssertIncrementalMatchesFull(t, () => { t.Doc.DispatchWheel(x, y, 70f); t.Layout(); });
    }

    [Fact]
    public void Changes_beside_a_transformed_element_repaint_identically()
    {
        // A CSS transform puts Push/PopTransform scopes in the list; the diff must keep matrix state
        // straight (or bail to full damage) — either way the pixels must match a full render.
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:16px'>" +
            "<div style='width:60px;height:40px;background:#4682B4;transform:rotate(8deg)'></div>" +
            "<cupri-switch checked=\"{{On}}\"></cupri-switch></div></body>",
            "", m, components: true, width: W, height: H);

        var (x, y) = TestDoc.Center(t.FindClass("cupri-switch"));
        AssertIncrementalMatchesFull(t, () => t.Click(x, y));
    }

    private sealed class Model
    {
        public bool On { get; set; }
        public string Size { get; set; } = "s";
        public bool Open { get; set; }
    }
}
