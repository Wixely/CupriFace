using System;
using CupriFace.Dom;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Damage-clipped repainting under scale (#99).
///
/// Damage rectangles are computed in DOCUMENT space and hosts re-upload a DEVICE rectangle, so a
/// scaled page needs the two mapped. Rather than write that mapping, both paths used to give up and
/// repaint everything: <c>RenderIncremental</c> bailed on <c>_zoom != 1</c>, and the web host would
/// not even call it when <c>PresentInfo.Scale != 1</c>. The comment justifying it called zoom "a
/// deliberate, occasional act" — which is true of Ctrl+plus and false of a device pixel ratio. A
/// HiDPI screen is 2, fractional desktop scaling is 1.25 or 1.5, and fitting a design to a viewport
/// lands anywhere at all, so scale 1 is the exception rather than the rule and an ordinary laptop
/// gave up damage tracking entirely.
///
/// Zoom is a uniform <c>canvas.Scale</c>, so the mapping is an exact multiply. The only care needed
/// is rounding OUTWARD: a rectangle short by a fraction of a device pixel leaves a stale seam, and
/// one a pixel too large costs nothing.
/// </summary>
public class ZoomDamageTests
{
    private const int W = 420, H = 320;

    private const string Html = """
        <body style="margin:0">
          <h1 style="margin:0;padding:8px">Heading</h1>
          <div class="hand" style="height:60px">hover me</div>
          <p style="padding:8px">tail</p>
        </body>
        """;
    private const string Css = ".hand{background:#e8eef7}.hand:hover{background:#b9c8de}";

    /// <summary>The guard that matters: paint incrementally into a retained bitmap, then require the
    /// result to match a fresh FULL render of the same state. A damage rect that is too small leaves
    /// the old pixels behind and this fails loudly — which is the failure the original bail-out was
    /// avoiding, now prevented by testing for it instead.</summary>
    private static SKRectI? IncrementalMatchesFull(TestDoc t, Action mutate)
    {
        using var retained = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(retained);
        Assert.NotNull(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));   // first frame: full

        mutate();
        var damage = t.Doc.RenderIncremental(canvas, W, H, SKColors.White);
        canvas.Flush();

        using var reference = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var rc = new SKCanvas(reference))
        {
            rc.Clear(SKColors.White);
            t.Doc.Render(rc, W, H);
            rc.Flush();
        }

        // AA coverage is computed against the clip, so pixels of primitives crossing the damage
        // boundary drift by a few LSBs. Structural failure — a stale region left unrepainted — is
        // orders of magnitude larger than that, which is what this catches.
        var a = retained.Bytes; var e = reference.Bytes;
        Assert.Equal(e.Length, a.Length);
        var worst = 0; var worstAt = -1;
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i] - e[i]);
            if (d > worst) { worst = d; worstAt = i; }
        }
        Assert.True(worst <= 16,
            $"byte {worstAt} differs by {worst} (damage {damage}) — a region was left unrepainted");
        return damage;
    }

    private static TestDoc Hovered(float zoom, out Action mutate)
    {
        var t = new TestDoc(Html, Css, width: W, height: H);
        t.Doc.Zoom = zoom;                       // BEFORE the first frame: setting it drops the retained list
        var doc = t.Doc;
        // Pointer coordinates are HOST pixels — the document divides by zoom. Scaling the point keeps
        // the SAME logical spot under the pointer at every zoom, so each case hovers the one element
        // whose background changes. A fixed host point lands somewhere different at each scale and
        // stops hitting it, which reports no damage and looks exactly like the feature working.
        mutate = () => doc.DispatchPointerMove(60 * zoom, 80 * zoom);
        return t;
    }

    /// <summary>Scale 1 is the case that always worked — kept as the control, so a regression here
    /// is told apart from one that only appears under scale.</summary>
    [Fact]
    public void At_scale_one_a_hover_damages_less_than_the_whole_surface()
    {
        using var t = Hovered(1f, out var hover);
        var damage = IncrementalMatchesFull(t, hover);
        Assert.NotNull(damage);
        Assert.True(damage!.Value.Width * damage.Value.Height < W * H,
            $"expected a partial rect, got {damage} of {W}x{H}");
    }

    /// <summary>The report's table: 1.5 and 2 are ordinary device pixel ratios, 0.72 is a real
    /// fit-to-viewport factor from the reporter's browser gate.</summary>
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(0.72f)]
    [InlineData(1.25f)]
    public void Under_scale_a_hover_still_damages_less_than_the_whole_surface(float zoom)
    {
        using var t = Hovered(zoom, out var hover);
        var damage = IncrementalMatchesFull(t, hover);

        Assert.NotNull(damage);
        Assert.True(damage!.Value.Width * damage.Value.Height < W * H,
            $"at zoom {zoom} the whole surface was repainted ({damage} of {W}x{H})");
    }

    /// <summary>The rectangle is what the host re-uploads, so it must be inside the surface however
    /// the scaling rounds — a rect running past the edge is an out-of-bounds upload.</summary>
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(0.72f)]
    public void The_damage_rect_stays_within_the_surface(float zoom)
    {
        using var t = Hovered(zoom, out var hover);
        var damage = IncrementalMatchesFull(t, hover);

        var d = damage!.Value;
        Assert.True(d.Left >= 0 && d.Top >= 0, $"{d} starts outside the surface");
        Assert.True(d.Right <= W && d.Bottom <= H, $"{d} runs past {W}x{H}");
    }

    /// <summary>An unchanged frame must still report nothing to do, whatever the scale — the
    /// no-op case is where the whole optimisation pays for itself.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(0.72f)]
    public void An_identical_frame_reports_no_damage_at_any_scale(float zoom)
    {
        using var t = new TestDoc(Html, Css, width: W, height: H);
        t.Doc.Zoom = zoom;
        using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);

        Assert.NotNull(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));   // first: full
        Assert.Null(t.Doc.RenderIncremental(canvas, W, H, SKColors.White));      // second: nothing changed
    }
}
