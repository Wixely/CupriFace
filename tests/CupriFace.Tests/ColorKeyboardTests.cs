using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>The colour palette is a grid, so it must be usable from the keyboard like the date picker's
/// day grid: arrows move a visible cursor over every swatch — including the neutral ramp — and Enter
/// picks the highlighted one.</summary>
public class ColorKeyboardTests
{
    private sealed class Model { public string Hex { get; set; } = ""; public bool Open { get; set; } = true; }

    private const string Html =
        "<body><div style='padding:16px'><cupri-color value=\"{{Hex}}\" open=\"{{Open}}\"></cupri-color></div></body>";

    private static List<RenderNode> Swatches(TestDoc t)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-color-sw") == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Arrow_keys_reach_every_swatch_including_the_neutral_ramp()
    {
        // The greys live after the 50 hue×shade swatches. They were unreachable while the palette was
        // emitted as two separate [data-gridnav] grids — only the first one is navigable.
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        Assert.Equal(60, Swatches(t).Count);

        for (var i = 0; i < 5; i++) t.Key(EditKey.Down);   // 5 rows down from the first swatch = the ramp
        t.Key(EditKey.Enter);

        Assert.Equal("#FFFFFF", m.Hex);                    // the first neutral (white)
        Assert.False(m.Open);                              // picking closes the palette
    }

    [Fact]
    public void Arrows_move_within_a_row_and_enter_picks_that_swatch()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        var expected = Swatches(t)[2].Element!.GetAttribute("data-set-value");

        t.Key(EditKey.Right); t.Key(EditKey.Right);
        t.Key(EditKey.Enter);

        Assert.Equal(expected, m.Hex);
    }

    [Fact]
    public void The_keyboard_cursor_is_visible()
    {
        // Navigating with no visible cursor is unusable: the highlighted swatch must actually look
        // different, not merely carry an attribute.
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        using var before = t.Render(SKColors.White);

        t.Key(EditKey.Right);

        var highlighted = Swatches(t).Where(s => s.Element!.HasAttribute("data-highlight")).ToList();
        Assert.Single(highlighted);                        // exactly one cursor

        using var after = t.Render(SKColors.White);
        var changed = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
                if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed++;
        Assert.True(changed > 50, $"the highlighted swatch must be visibly marked (only {changed}px changed)");
    }
}
