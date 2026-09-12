using CupriFace.Hosting;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The monitor-transition state machine (#137), and the regression fence for the bug reported
/// against its first cut: dragging 100% → 150% resized only on mouse-up, and dragging back to 100%
/// did not resize at all, leaving the window oversized.
///
/// <para><b>Why these tests are written as event ORDERINGS rather than as arithmetic.</b> The defect
/// was a race, and it presented as "happens seemingly randomly". While a window is dragged the OS
/// runs a modal loop: the framebuffer callback is delivered throughout it, the host's frame tick is
/// not delivered at all until the mouse is released, and whether a resize arrives before or after
/// the DPI change depends on how fast the user drags. The old code recomputed the logical size as
/// <c>framebuffer / cachedScale</c>, so one of those orderings silently corrupted it and the other
/// did not — which is exactly what intermittent looks like from the outside.</para>
///
/// <para>So the property under test is not "the arithmetic is right" but "every interleaving lands
/// in the same place". Each transition is driven twice, once in each order.</para>
/// </summary>
public class WindowScaleTrackerTests
{
    // The reported case: a 900x790 window created on a 100% monitor.
    private static WindowScaleTracker Fresh() => new(900, 790);

    [Fact]
    public void StartsAtItsLogicalSize()
    {
        var t = Fresh();

        Assert.Equal(1f, t.DeviceScale);
        Assert.Equal((900, 790), t.WantedPhysical);
    }

    // ---- the reported bug, both orderings ------------------------------------------------------

    /// <summary>Crossing to a 150% monitor when the SCALE is noticed first (the frame tick wins the
    /// race). The window must be told to grow, and the logical size must not move.</summary>
    [Fact]
    public void CrossingUp_scale_observed_first()
    {
        var t = Fresh();

        var resize = t.ObserveScale(1.5f);

        Assert.Equal((1350, 1185), resize);
        Assert.Equal(900, t.LogicalWidth);
        Assert.Equal(790, t.LogicalHeight);
    }

    /// <summary>The SAME crossing when the FRAMEBUFFER event arrives first — the ordering the modal
    /// drag loop actually produces, and the one that used to corrupt the logical size. The
    /// framebuffer is reported at its old physical size because nothing has resized the window yet;
    /// the scale read at that moment is already 1.5.</summary>
    [Fact]
    public void CrossingUp_framebuffer_observed_first()
    {
        var t = Fresh();

        var resize = t.ObserveFramebuffer(900, 790, observedDeviceScale: 1.5f);

        Assert.Equal((1350, 1185), resize);
        Assert.Equal(900, t.LogicalWidth);      // preserved, NOT recomputed as 900/1.5
        Assert.Equal(790, t.LogicalHeight);
    }

    /// <summary>The round trip that was broken: 100% → 150% → 100% must return the window to exactly
    /// the size it started at, whichever event leads each crossing. Four orderings, one answer.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RoundTripRestoresTheOriginalSize(bool fbFirstUp, bool fbFirstDown)
    {
        var t = Fresh();

        Cross(t, 1.5f, fbFirstUp);
        Assert.Equal((1350, 1185), t.WantedPhysical);

        Cross(t, 1f, fbFirstDown);
        Assert.Equal((900, 790), t.WantedPhysical);
        Assert.Equal(1f, t.DeviceScale);
    }

    /// <summary>Ten crossings back and forth must not drift. Rounding happens on every transition,
    /// and a tracker that recomputed its logical size from the rounded physical one would walk the
    /// window a pixel at a time — invisible on one crossing and obvious after a morning's work.</summary>
    [Fact]
    public void RepeatedCrossingsDoNotDrift()
    {
        var t = Fresh();

        for (var i = 0; i < 10; i++)
        {
            Cross(t, 1.5f, fbFirst: i % 2 == 0);
            Cross(t, 1.25f, fbFirst: i % 3 == 0);
            Cross(t, 1f, fbFirst: i % 2 == 1);
        }

        Assert.Equal((900, 790), t.WantedPhysical);
        Assert.Equal(900, t.LogicalWidth);
        Assert.Equal(790, t.LogicalHeight);
    }

    // ---- user resizes are the opposite case ----------------------------------------------------

    /// <summary>A resize at an UNCHANGED scale is the user dragging an edge: the new size is adopted,
    /// and no resize is issued back at them (which would fight the drag).</summary>
    [Fact]
    public void UserResizeIsAdoptedNotFoughtBack()
    {
        var t = Fresh();

        var resize = t.ObserveFramebuffer(1200, 900, observedDeviceScale: 1f);

        Assert.Null(resize);
        Assert.Equal(1200, t.LogicalWidth);
        Assert.Equal(900, t.LogicalHeight);
    }

    /// <summary>A window the user resized keeps THEIR size across a monitor change, not the size the
    /// app was created at. This is why the tracker holds a logical size at all rather than reusing
    /// the app's startup dimensions.</summary>
    [Fact]
    public void UserResizeSurvivesARoundTrip()
    {
        var t = Fresh();
        t.ObserveFramebuffer(1200, 900, observedDeviceScale: 1f);   // user drags the window bigger

        Cross(t, 2f, fbFirst: true);
        Assert.Equal((2400, 1800), t.WantedPhysical);

        Cross(t, 1f, fbFirst: false);
        Assert.Equal((1200, 900), t.WantedPhysical);
    }

    /// <summary>A user resize ON the scaled monitor is recorded in logical units, so coming back
    /// gives the physical size that logical size deserves at 100%.</summary>
    [Fact]
    public void UserResizeWhileScaledIsRecordedInLogicalUnits()
    {
        var t = Fresh();
        Cross(t, 1.5f, fbFirst: true);

        t.ObserveFramebuffer(1500, 1050, observedDeviceScale: 1.5f);   // user drags an edge at 150%
        Assert.Equal(1000, t.LogicalWidth);                            // 1500 / 1.5
        Assert.Equal(700, t.LogicalHeight);

        Cross(t, 1f, fbFirst: false);
        Assert.Equal((1000, 700), t.WantedPhysical);
    }

    // ---- noise ---------------------------------------------------------------------------------

    /// <summary>125% arrives as 120/96, which is not 1.25 exactly. Re-observing the same scale by a
    /// different route must not read as a transition, or the window would resize every frame.</summary>
    [Fact]
    public void TheSameScaleByADifferentRouteIsNotATransition()
    {
        var t = Fresh();
        t.ObserveScale(1.25f);

        Assert.Null(t.ObserveScale(120f / 96f));
        Assert.Null(t.ObserveFramebuffer(1125, 988, 120f / 96f));
    }

    /// <summary>A minimised window reports a zero framebuffer. Adopting that would set the logical
    /// size to nothing and the window would never come back.</summary>
    [Fact]
    public void MinimisedWindowIsIgnored()
    {
        var t = Fresh();

        Assert.Null(t.ObserveFramebuffer(0, 0, 1f));
        Assert.Equal((900, 790), t.WantedPhysical);
    }

    /// <summary>
    /// Drive one monitor crossing, choosing which of the two racing events arrives first. Both are
    /// always delivered — the point is that the order must not matter.
    /// </summary>
    private static void Cross(WindowScaleTracker t, float scale, bool fbFirst)
    {
        if (fbFirst)
        {
            // The order the modal drag loop actually produces, and the one that caused the bug:
            // Windows has ALREADY resized the window across the DPI boundary (it hands PMv2 apps a
            // suggested rect and scales the non-client area itself), so the framebuffer callback
            // arrives carrying the new physical size while nothing has yet re-read the scale.
            // Dividing that by the cached scale is what corrupted the logical size.
            var suggested = ((int)MathF.Round(t.LogicalWidth * scale), (int)MathF.Round(t.LogicalHeight * scale));
            t.ObserveFramebuffer(suggested.Item1, suggested.Item2, scale);
            t.ObserveScale(scale);                                  // the tick, later, is a no-op
        }
        else
        {
            t.ObserveScale(scale);
            var physical = t.WantedPhysical;
            t.ObserveFramebuffer(physical.Width, physical.Height, scale);   // the resize we caused
        }
    }

    /// <summary>
    /// The reported sequence, with the numbers off the reporter's screenshots, as one test.
    ///
    /// <para>A 900×790 window on a 100% monitor is dragged to a 150% one. Windows resizes it across
    /// the boundary and the framebuffer callback lands at 1350×1185 while the cached scale is still
    /// 1 — so the old code recorded a logical size of 1350×1185, then on mouse-up asked for
    /// 1350×1.5 = 2025 wide, which the screen clamped. The window came back from that crossing
    /// oversized and never returned to 900×790.</para>
    ///
    /// <para>Pinned as an explicit assertion on the logical size rather than only on the round trip,
    /// because the corruption is what persists: every later crossing inherits it.</para>
    /// </summary>
    [Fact]
    public void ReportedSequence_windowDoesNotGrowAcrossACrossing()
    {
        var t = Fresh();

        // Mid-drag: the OS has already resized us, and the scale it reports is the new monitor's.
        var resize = t.ObserveFramebuffer(1350, 1185, observedDeviceScale: 1.5f);

        // The logical size must NOT become 1350×1185 (the old bug), and no runaway resize is asked
        // for — 1350×1185 is already correct for 900×790 logical at 1.5.
        Assert.Equal(900, t.LogicalWidth);
        Assert.Equal(790, t.LogicalHeight);
        Assert.Equal((1350, 1185), resize);
        Assert.Equal((1350, 1185), t.WantedPhysical);

        // …and the trip home lands exactly where it started, which is what did not happen.
        Assert.Equal((900, 790), t.ObserveScale(1f));
        Assert.Equal((900, 790), t.WantedPhysical);
    }
}
