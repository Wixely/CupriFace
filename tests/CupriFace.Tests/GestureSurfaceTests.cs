using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// "It follows one of my fingers and not the other — no scaling, no rotation." Five CI rounds of
/// gate-building proved the whole Android input path correct, which left the geometry: the tile is
/// 90dp — roughly 14mm on a phone — and two adult fingertips cannot both land on a 14mm square.
/// The second finger fell outside, capture is per-element, and the gesture stayed one-finger
/// forever. Every headless test and the emulator gate passed, because their fingers are points.
///
/// The stage now owns the gesture, the tile only carries the transform — the collage-editor
/// pattern. These tests pin the human-hand scenario: fingers that do NOT both fit on the tile.
/// </summary>
public class GestureSurfaceTests
{
    private static (CupriDocument Doc, MobileModel Model, RenderNode Stage, RenderNode Tile) App()
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
        var stage = F(doc.Root, n => n.Element?.ClassList.Contains("stage") == true)!;
        var tile = F(doc.Root, n => n.Element?.ClassList.Contains("tile") == true)!;
        return (doc, (MobileModel)app.Model!, stage, tile);
    }

    [Fact]
    public void A_second_finger_beside_the_tile_still_joins_the_pinch()
    {
        var (doc, model, stage, tile) = App();
        using var _ = doc;

        var (sx, sy, sw, sh) = HitTesting.ScreenBox(stage);
        var (tx, ty, tw, th) = HitTesting.ScreenBox(tile);
        float cy = ty + th / 2;

        // Finger 1 on the tile; finger 2 in the stage but clearly OFF the tile — where a real
        // second fingertip lands. The old surface made this exact placement a one-finger pan.
        float f1 = tx + tw / 2, f2 = sx + 20;
        Assert.True(f2 < tx - 4, "the test finger is not actually outside the tile");

        doc.DispatchPointer(1, PointerPhase.Down, f1, cy);
        doc.DispatchPointer(2, PointerPhase.Down, f2, cy);
        // Spread apart.
        doc.DispatchPointer(1, PointerPhase.Move, f1 + 60, cy);
        doc.DispatchPointer(2, PointerPhase.Move, f2 - 10, cy);

        Assert.True(model.TileScale > 1.2,
            $"scale is {model.TileScale:F2} — the finger beside the tile never joined the gesture");
    }

    [Fact]
    public void A_finger_outside_the_stage_does_not_join()
    {
        // The boundary still exists — it is just drawn around the stage, not the 14mm tile.
        var (doc, model, stage, tile) = App();
        using var _ = doc;

        var (sx, sy, sw, sh) = HitTesting.ScreenBox(stage);
        var (tx, ty, tw, th) = HitTesting.ScreenBox(tile);

        doc.DispatchPointer(1, PointerPhase.Down, tx + tw / 2, ty + th / 2);
        doc.DispatchPointer(2, PointerPhase.Down, sx + sw / 2, sy - 40);   // above the stage
        doc.DispatchPointer(1, PointerPhase.Move, tx + tw / 2 + 50, ty + th / 2);

        Assert.True(model.TileScale < 1.1 && model.TileScale > 0.9,
            $"scale is {model.TileScale:F2} — a finger outside the stage was counted into the pinch");
    }
}
