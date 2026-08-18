using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "It works initially, but when I go to resize it again the cube resets size and position…
/// and on the third time it only follows one finger."
///
/// Two defects, both in the sample, neither in the engine. The recogniser reports
/// cumulative-SINCE-GESTURE-START — as every platform's recogniser does — and the handler wrote
/// that straight into the model, so each new grab snapped the tile back to 1x/origin. And once
/// grown or dragged, the cube's visual extent hung outside the 150dp stage strip: a finger on the
/// overhanging part touches ground the stage never captures, and the gesture stays one-finger.
/// The handler now composes onto a banked base, the clamps keep the cube inside the stage, and
/// the stage is tall enough to contain it at every clamped extreme.
/// </summary>
public class GestureContinuityTests
{
    private static (CupriDocument Doc, MobileModel Model, Func<RenderNode> Stage, Func<RenderNode> Tile) App()
    {
        var app = new MobileApp();
        var doc = app.CreateDocument();
        using (doc.RenderToImage(393, 771)) { }

        static RenderNode? F(RenderNode n, Func<RenderNode, bool> p)
        {
            if (p(n)) return n;
            foreach (var c in n.Children) if (F(c, p) is { } f) return f;
            return null;
        }
        return (doc, (MobileModel)app.Model!,
            () => F(doc.Root, n => n.Element?.ClassList.Contains("stage") == true)!,
            () => F(doc.Root, n => n.Element?.ClassList.Contains("tile") == true)!);
    }

    private static void Pinch(CupriDocument doc, float cx, float cy, float from, float to, int p1 = 1, int p2 = 2)
    {
        doc.DispatchPointer(p1, PointerPhase.Down, cx - from, cy);
        doc.DispatchPointer(p2, PointerPhase.Down, cx + from, cy);
        doc.DispatchPointer(p1, PointerPhase.Move, cx - to, cy);
        doc.DispatchPointer(p2, PointerPhase.Move, cx + to, cy);
        doc.DispatchPointer(p1, PointerPhase.Up, cx - to, cy);
        doc.DispatchPointer(p2, PointerPhase.Up, cx + to, cy);
    }

    [Fact]
    public void A_second_grab_continues_from_where_the_first_left_off()
    {
        var (doc, model, stage, _) = App();
        using var _d = doc;
        var (sx, sy, sw, sh) = HitTesting.ScreenBox(stage());
        float cx = sx + sw / 2, cy = sy + sh / 2;

        Pinch(doc, cx, cy, 30, 45);                      // 1.5x
        var after = model.TileScale;
        Assert.True(after > 1.3, $"first pinch reached {after:F2} — setup proves nothing");

        // The regression: the FIRST EVENT of a new gesture snapped the model back to 1.
        doc.DispatchPointer(1, PointerPhase.Down, cx, cy);
        Assert.Equal(after, model.TileScale, 2);

        doc.DispatchPointer(1, PointerPhase.Move, cx + 20, cy);
        doc.DispatchPointer(1, PointerPhase.Up, cx + 20, cy);
        Assert.Equal(after, model.TileScale, 2);         // a drag pans; it must not rescale
        Assert.True(model.TilePanX > 10, "the drag did not pan");

        // And a third gesture composes again on top of both.
        Pinch(doc, cx, cy, 30, 36);
        Assert.True(model.TileScale > after, "the third gesture did not continue from the second's result");
    }

    [Fact]
    public void The_cube_can_never_leave_the_stage()
    {
        var (doc, model, stage, _) = App();
        using var _d = doc;
        var (sx, sy, sw, sh) = HitTesting.ScreenBox(stage());
        float cx = sx + sw / 2, cy = sy + sh / 2;

        // Grow to the clamp, then drag hard toward a corner, twice.
        Pinch(doc, cx, cy, 20, 90);
        for (var i = 0; i < 2; i++)
        {
            doc.DispatchPointer(1, PointerPhase.Down, cx, cy);
            doc.DispatchPointer(1, PointerPhase.Move, cx + 300, cy + 200);
            doc.DispatchPointer(1, PointerPhase.Up, cx + 300, cy + 200);
        }

        Assert.True(model.TileScale <= 1.8 + 0.01, $"scale {model.TileScale:F2} exceeded the clamp");
        Assert.True(Math.Abs(model.TilePanX) <= 70.5, $"panX {model.TilePanX:F0} left the stage");
        Assert.True(Math.Abs(model.TilePanY) <= 25.5, $"panY {model.TilePanY:F0} left the stage");

        // The point of the clamps: the cube's whole box is still over the gesture surface.
        using (doc.RenderToImage(393, 771)) { }
        var (tx, ty, tw, th) = HitTesting.ScreenBox(stage());
        var (bx, by, bw, bh) = HitTesting.ScreenBox(
            ((Func<RenderNode>)(() => { RenderNode? f = null;
                void W(RenderNode n) { if (n.Element?.ClassList.Contains("tile") == true) f = n; foreach (var c in n.Children) W(c); }
                W(doc.Root); return f!; }))());
        // ScreenBox reads layout boxes (the transform is paint-level), so assert via the model:
        // centre offset + scaled half-extent must stay inside the stage box.
        var half = 45 * model.TileScale;
        Assert.True(Math.Abs(model.TilePanX) + half <= tw / 2 + 1,
            $"horizontally out: pan {model.TilePanX:F0} + half {half:F0} vs stage {tw / 2:F0}");
        Assert.True(Math.Abs(model.TilePanY) + half <= th / 2 + 1,
            $"vertically out: pan {model.TilePanY:F0} + half {half:F0} vs stage {th / 2:F0}");
    }

    [Fact]
    public void A_grab_at_the_cube_after_growing_and_dragging_still_pinches()
    {
        // The third-time-broken shape end to end: grow, drag, release — then land BOTH fingers
        // where the cube now visually sits, and expect a working pinch.
        var (doc, model, stage, _) = App();
        using var _d = doc;
        var (sx, sy, sw, sh) = HitTesting.ScreenBox(stage());
        float cx = sx + sw / 2, cy = sy + sh / 2;

        Pinch(doc, cx, cy, 30, 45);
        doc.DispatchPointer(1, PointerPhase.Down, cx, cy);
        doc.DispatchPointer(1, PointerPhase.Move, cx + 200, cy + 100);   // clamps to +70,+25
        doc.DispatchPointer(1, PointerPhase.Up, cx + 200, cy + 100);
        using (doc.RenderToImage(393, 771)) { }

        var before = model.TileScale;
        float gx = cx + (float)model.TilePanX, gy = cy + (float)model.TilePanY;
        Pinch(doc, gx, gy, 25, 40, p1: 5, p2: 6);

        Assert.True(model.TileScale > before + 0.1,
            $"scale stayed at {model.TileScale:F2} — the grab at the cube's new position did not pinch");
    }
}
