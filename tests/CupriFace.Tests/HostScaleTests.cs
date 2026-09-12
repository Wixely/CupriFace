using CupriFace;
using CupriFace.Dom;
using CupriFace.Hosting;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The host scale model (#137): OS device scale D, application present scale P, and the effective
/// scale T = D*P that is the only one anything should paint with.
///
/// <para>The desktop host used to have no D at all. It handed <c>app.Present</c> framebuffer PIXELS
/// and then used <c>PresentInfo.Scale</c> alone for the canvas, for surface allocation, for damage
/// and for accessibility geometry — so on any display above 100% either Windows bitmap-virtualised
/// the process (soft UI) or the app was rendered physically smaller and did not keep its logical
/// size when moved to another monitor.</para>
///
/// <para>These tests are the reason the model is a standalone file: none of them needs a window, a
/// monitor or a GL context, so the arithmetic that used to live in host lambdas is now pinned in
/// milliseconds on every OS. The transform is the whole bug — if T is applied twice, or D is divided
/// out twice, it shows up here rather than on someone's 150% laptop.</para>
/// </summary>
public class HostScaleTests
{
    // ---- D alone: the framebuffer becomes a logical client size -------------------------------

    /// <summary>The regression fence for every machine that is not HiDPI: at D=1 nothing about the
    /// old behaviour may move. Logical client == framebuffer, and T == P.</summary>
    [Fact]
    public void DeviceScaleOfOneIsTheIdentity()
    {
        var s = HostScale.ForFramebuffer(1024, 768, 1f);

        Assert.Equal(1024f, s.LogicalClientWidth);
        Assert.Equal(768f, s.LogicalClientHeight);
        Assert.Equal(1f, s.EffectiveScale);
        Assert.Equal(1f, s.PresentScale);

        // …and an app that zooms still gets exactly its own factor back, unmodified.
        Assert.Equal(1.25f, s.WithPresentScale(1.25f).EffectiveScale, 5);
    }

    /// <summary>The Windows display-scaling ladder. A 150% monitor must hand the application a
    /// SMALLER logical window than the framebuffer, which is the half the old host never did.</summary>
    [Theory]
    [InlineData(1.25f, 1536, 864)]
    [InlineData(1.5f, 1920, 1080)]
    [InlineData(2f, 2560, 1440)]
    public void FramebufferBecomesLogicalClientSize(float d, int fbW, int fbH)
    {
        var s = HostScale.ForFramebuffer(fbW, fbH, d);

        Assert.Equal(fbW / d, s.LogicalClientWidth, 4);
        Assert.Equal(fbH / d, s.LogicalClientHeight, 4);
        Assert.Equal(fbW, s.FramebufferWidth);
        Assert.Equal(fbH, s.FramebufferHeight);
        // With no application scale yet, the effective scale is just the monitor's.
        Assert.Equal(d, s.EffectiveScale, 5);
    }

    // ---- D and P together ---------------------------------------------------------------------

    /// <summary>The case the issue names explicitly: D=1.5 with P=1.25 is T=1.875, and the client
    /// size stays the size of the WINDOW — the app's zoom changes the viewport it lays out at, not
    /// how big its window is.</summary>
    [Fact]
    public void EffectiveScaleMultipliesDeviceAndPresent()
    {
        var s = HostScale.ForFramebuffer(1920, 1080, 1.5f).WithPresentScale(1.25f);

        Assert.Equal(1.875f, s.EffectiveScale, 5);
        Assert.Equal(1.5f, s.DeviceScale, 5);
        Assert.Equal(1.25f, s.PresentScale, 4);
        Assert.Equal(1280f, s.LogicalClientWidth, 4);   // 1920/1.5 — unchanged by the present scale
        Assert.Equal(720f, s.LogicalClientHeight, 4);
    }

    /// <summary>Folding in a present scale must not compound: asking twice with the same P gives the
    /// same T, not P². The host calls this once per frame on a value it keeps, so a <c>with</c>
    /// expression that multiplied into the existing T instead of rebuilding from D would drift the
    /// UI larger every frame — visible only after a few seconds of running.</summary>
    [Fact]
    public void WithPresentScaleIsIdempotent()
    {
        var s = HostScale.ForFramebuffer(1920, 1080, 1.5f);

        Assert.Equal(s.WithPresentScale(1.25f), s.WithPresentScale(1.25f).WithPresentScale(1.25f));
    }

    // ---- one factor, applied exactly once ------------------------------------------------------

    /// <summary>T reaches rendering, surface allocation, damage and accessibility as the SAME
    /// number. This is the "exactly once" requirement stated as an equality rather than as prose:
    /// every consumer is fed from one field, so there is no second place for a stray multiply.</summary>
    [Fact]
    public void OneEffectiveScaleFeedsEveryConsumer()
    {
        var s = HostScale.ForFramebuffer(1920, 1080, 1.5f).WithPresentScale(1.25f);

        var surfaces = new Paint.SurfaceRegistry { DeviceScale = s.EffectiveScale };
        Assert.Equal(1.875f, surfaces.DeviceScale, 5);

        // A 100x50 logical rect covers exactly 187.5x93.75 device pixels; damage rounds outward.
        var damage = CupriDocument.ScaleDamageToDevice(
            new SKRectI(0, 0, 100, 50), s.EffectiveScale, s.FramebufferWidth, s.FramebufferHeight);
        Assert.Equal(188, damage.Right);
        Assert.Equal(94, damage.Bottom);
    }

    /// <summary>Damage still rounds OUTWARD once the device scale is folded in. A rectangle short by
    /// a fraction of a device pixel leaves a stale seam, and fractional scales are exactly where
    /// that happens — 1.25 and 1.5 land off the pixel grid constantly.</summary>
    [Theory]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(1.875f)]
    public void DamageRoundsOutwardAtFractionalScales(float t)
    {
        var logical = new SKRectI(3, 7, 11, 23);
        var device = CupriDocument.ScaleDamageToDevice(logical, t, 4096, 4096);

        Assert.True(device.Left <= logical.Left * t);
        Assert.True(device.Top <= logical.Top * t);
        Assert.True(device.Right >= logical.Right * t);
        Assert.True(device.Bottom >= logical.Bottom * t);
    }

    // ---- pointer coordinates -------------------------------------------------------------------

    /// <summary>The round trip a pointer coordinate makes: a backend reporting PHYSICAL pixels
    /// normalises to logical client units once, then the host divides out the app's own scale. The
    /// physical centre of the window must land on the centre of the document.</summary>
    [Fact]
    public void PhysicalPointerCentreMapsToDocumentCentre()
    {
        var s = HostScale.ForFramebuffer(1920, 1080, 1.5f).WithPresentScale(1.25f);

        var docX = s.ToDocument(s.ToLogicalClient(960f));   // physical centre of a 1920px framebuffer
        var docY = s.ToDocument(s.ToLogicalClient(540f));

        // 960 / 1.875 = 512, which is half of the 1024-unit document (1920/1.875).
        Assert.Equal(512f, docX, 3);
        Assert.Equal(288f, docY, 3);
        Assert.Equal(1920f / s.EffectiveScale / 2f, docX, 3);
    }

    /// <summary>The trap the issue warns about, pinned: a backend that already reports LOGICAL
    /// coordinates must skip <see cref="HostScale.ToLogicalClient"/>. Dividing by D twice puts the
    /// pointer at two-thirds of where the user clicked on a 150% monitor — which is a mis-hit, not a
    /// wobble.</summary>
    [Fact]
    public void DividingByDeviceScaleTwiceMissesTheTarget()
    {
        var s = HostScale.ForFramebuffer(1920, 1080, 1.5f);

        var correct = s.ToDocument(960f);                      // backend already gave logical units
        var doubled = s.ToDocument(s.ToLogicalClient(960f));   // …and the host divided again

        Assert.Equal(960f, correct, 3);
        Assert.Equal(640f, doubled, 3);
        Assert.NotEqual(correct, doubled, 3);
    }

    // ---- monitor transitions --------------------------------------------------------------------

    /// <summary>Moving between monitors: the framebuffer grows with the DPI, and the logical size
    /// the app lays out at does NOT move. That stability is the whole point of Per-Monitor-V2 — the
    /// window keeps its physical size on the desk and simply gains pixels.</summary>
    [Fact]
    public void LogicalSizeSurvivesADeviceScaleTransition()
    {
        var at100 = HostScale.ForFramebuffer(1280, 720, 1f);
        var at150 = HostScale.ForFramebuffer(1920, 1080, 1.5f);

        Assert.Equal(at100.LogicalClientWidth, at150.LogicalClientWidth, 4);
        Assert.Equal(at100.LogicalClientHeight, at150.LogicalClientHeight, 4);
        Assert.True(at150.DeviceScaleChangedFrom(at100));
    }

    /// <summary>A transition is detected on the DEVICE scale alone. An app changing its own zoom is
    /// not a monitor move and must not invalidate surfaces, and a resize on one monitor is not
    /// either — over-reporting here means a full surface rebuild on frames that did not need one.</summary>
    [Fact]
    public void OnlyTheDeviceScaleCountsAsATransition()
    {
        var s = HostScale.ForFramebuffer(1920, 1080, 1.5f);

        Assert.False(s.WithPresentScale(1.25f).DeviceScaleChangedFrom(s));       // app zoomed
        Assert.False(HostScale.ForFramebuffer(1600, 900, 1.5f).DeviceScaleChangedFrom(s)); // resized
        Assert.True(HostScale.ForFramebuffer(1920, 1080, 2f).DeviceScaleChangedFrom(s));   // moved
    }

    /// <summary>125% is 1.25 only in decimal: it arrives as 120/96, and comparing those for equality
    /// across frames flickers. The comparison is toleranced, and this is the case that proves it.</summary>
    [Fact]
    public void FractionalDpiDoesNotFlickerAsATransition()
    {
        var fromDpi = HostScale.ForFramebuffer(1920, 1080, 120f / 96f);
        var literal = HostScale.ForFramebuffer(1920, 1080, 1.25f);

        Assert.False(fromDpi.DeviceScaleChangedFrom(literal));
    }

    // ---- bad readings ---------------------------------------------------------------------------

    /// <summary>A backend that cannot report a scale, or reports a broken one, gets 1 — never 0,
    /// which would divide the client size to infinity and lay the document out at nothing. The same
    /// defence <c>PresentInfo</c> already applies to P, at the other end of the pipeline.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-2f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void UnusableDeviceScalesFallBackToOne(float bad)
    {
        var s = HostScale.ForFramebuffer(1024, 768, bad);

        Assert.Equal(1f, s.DeviceScale);
        Assert.Equal(1024f, s.LogicalClientWidth);
        Assert.Equal(1f, s.EffectiveScale);
    }

    /// <summary>An absurd reading is clamped rather than trusted. 8x is past every real panel, and a
    /// host that believed a wild number would allocate a surface sized from it.</summary>
    [Fact]
    public void AbsurdDeviceScalesAreClamped()
    {
        Assert.Equal(HostScale.MaxDeviceScale, HostScale.ForFramebuffer(1024, 768, 40f).DeviceScale);
        Assert.Equal(HostScale.MinDeviceScale, HostScale.ForFramebuffer(1024, 768, 0.01f).DeviceScale);
    }

    /// <summary>A zero-sized framebuffer — a minimised window — must not produce NaN client sizes
    /// that then reach layout.</summary>
    [Fact]
    public void MinimisedWindowStaysFinite()
    {
        var s = HostScale.ForFramebuffer(0, 0, 1.5f);

        Assert.Equal(0f, s.LogicalClientWidth);
        Assert.Equal(0f, s.LogicalClientHeight);
    }
}
