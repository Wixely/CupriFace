using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The gesture recogniser — the optional layer above raw pointers. Every other cross-platform
/// toolkit ships one; not shipping ours meant every author rewriting the same trigonometry, and
/// the engine's own first sample proved the point by getting the focal point wrong.
/// </summary>
public class ManipulationTests
{
    private const string Html = "<body><div class='tile' data-gesture='photo'>t</div></body>";
    private const string Css = "body{margin:0} .tile{width:200px;height:200px}";

    private static (CupriDocument Doc, List<ManipulationEvent> Seen) Recognising()
    {
        var doc = CupriDocument.Load(Html, Css);
        var seen = new List<ManipulationEvent>();
        doc.OnManipulate("data-gesture", e => { seen.Add(e); return true; });
        doc.BuildFrame(400, 400);
        return (doc, seen);
    }

    [Fact]
    public void One_finger_pans_and_neither_scales_nor_turns()
    {
        var (doc, seen) = Recognising();
        using var _ = doc;

        doc.DispatchPointer(1, PointerPhase.Down, 100, 100);
        doc.DispatchPointer(1, PointerPhase.Move, 130, 145);

        var last = seen[^1];
        Assert.Equal(30, last.PanX, 1);
        Assert.Equal(45, last.PanY, 1);
        Assert.Equal(1, last.Scale, 3);
        Assert.Equal(0, last.Rotation, 3);
    }

    [Fact]
    public void Two_fingers_spreading_scale_about_the_point_between_them()
    {
        var (doc, seen) = Recognising();
        using var _ = doc;

        // 40px apart, spread to 80: double. The focal point stays at the midpoint, which is what
        // makes the content feel pinned under the fingers instead of sliding out from under them.
        doc.DispatchPointer(1, PointerPhase.Down, 80, 100);
        doc.DispatchPointer(2, PointerPhase.Down, 120, 100);
        doc.DispatchPointer(1, PointerPhase.Move, 60, 100);
        doc.DispatchPointer(2, PointerPhase.Move, 140, 100);

        var last = seen[^1];
        Assert.Equal(2, last.Scale, 2);
        Assert.Equal(100, last.FocusX, 1);
        Assert.Equal(100, last.FocusY, 1);
        Assert.Equal(0, last.PanX, 1);      // spreading evenly is not a drag
    }

    [Fact]
    public void Two_fingers_twisting_report_the_turn()
    {
        var (doc, seen) = Recognising();
        using var _ = doc;

        doc.DispatchPointer(1, PointerPhase.Down, 80, 100);
        doc.DispatchPointer(2, PointerPhase.Down, 120, 100);       // horizontal: 0°
        doc.DispatchPointer(1, PointerPhase.Move, 100, 80);
        doc.DispatchPointer(2, PointerPhase.Move, 100, 120);       // vertical: 90°

        Assert.Equal(90, seen[^1].Rotation, 1);
        Assert.Equal(1, seen[^1].Scale, 1);                        // the span did not change
    }

    [Fact]
    public void Adding_a_finger_mid_gesture_does_not_make_the_content_jump()
    {
        // The re-baselining that hand-rolled versions forget: a second finger changes what "span"
        // means, so what has happened so far must be banked rather than recomputed.
        var (doc, seen) = Recognising();
        using var _ = doc;

        // Both fingers must land ON the element — capture is per target, so a finger outside it
        // is simply not part of this gesture.
        doc.DispatchPointer(1, PointerPhase.Down, 60, 100);
        doc.DispatchPointer(1, PointerPhase.Move, 110, 100);       // panned 50
        Assert.Equal(50, seen[^1].PanX, 1);

        doc.DispatchPointer(2, PointerPhase.Down, 150, 100);       // a second finger arrives
        Assert.Equal(50, seen[^1].PanX, 1);                        // …and the pan is still 50
        Assert.Equal(1, seen[^1].Scale, 2);                        // …and nothing scaled

        doc.DispatchPointer(1, PointerPhase.Move, 90, 100);
        doc.DispatchPointer(2, PointerPhase.Move, 190, 100);       // now spread: 40 -> 100
        Assert.True(seen[^1].Scale > 1.4, $"scale {seen[^1].Scale:0.00} — the spread was ignored");
        Assert.True(Math.Abs(seen[^1].PanX - 50) < 60, "the earlier pan should be preserved, not lost");
    }

    [Fact]
    public void A_turn_across_the_seam_stays_small()
    {
        // Crossing ±180° must read as a few degrees, not most of a revolution.
        var (doc, seen) = Recognising();
        using var _ = doc;

        doc.DispatchPointer(1, PointerPhase.Down, 100, 100);
        doc.DispatchPointer(2, PointerPhase.Down, 60, 100);        // pointing left: 180°
        doc.DispatchPointer(2, PointerPhase.Move, 60, 96);         // just past the seam

        Assert.True(Math.Abs(seen[^1].Rotation) < 20,
            $"rotation read {seen[^1].Rotation:0}° for a few degrees of turn");
    }

    [Fact]
    public void Raw_pointers_are_still_available_for_anything_else()
    {
        // The recogniser is a convenience over the seam, not a replacement for it.
        using var doc = CupriDocument.Load(Html, Css);
        var raw = 0;
        doc.OnPointer("data-gesture", _ => { raw++; return true; });
        doc.BuildFrame(400, 400);

        doc.DispatchPointer(1, PointerPhase.Down, 100, 100);
        doc.DispatchPointer(1, PointerPhase.Move, 110, 100);
        Assert.Equal(2, raw);
    }
}
