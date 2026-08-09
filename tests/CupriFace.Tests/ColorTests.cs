using System.Collections.Generic;
using System.Linq;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary><c>&lt;cupri-color&gt;</c> — a swatch trigger that opens an anchored hue×shade palette;
/// clicking a swatch writes its <c>#RRGGBB</c> back to the bound value and closes, and the swatch
/// matching the current value is ringed.</summary>
public class ColorTests
{
    private sealed class Model { public string Hex { get; set; } = "#B87333"; public bool Open { get; set; } }

    private const string Html =
        "<body><div style='padding:20px'><cupri-color value=\"{{Hex}}\" open=\"{{Open}}\"></cupri-color></div></body>";

    private static RenderNode? Pop(TestDoc t) => t.Find(n => n.Element?.ClassList.Contains("cupri-color-pop") == true);

    private static List<RenderNode> Swatches(TestDoc t)
    {
        var outp = new List<RenderNode>();
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-color-sw") == true) outp.Add(n); foreach (var c in n.Children) W(c); }
        W(t.Root);
        return outp;
    }

    [Fact]
    public void Trigger_opens_the_palette_of_hue_shade_and_neutral_swatches()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        Assert.Null(Pop(t));                                     // closed initially

        t.ClickMatch(n => n.Element?.ClassList.Contains("cupri-color-trigger") == true);
        Assert.True(m.Open);
        Assert.NotNull(Pop(t));
        Assert.Equal(60, Swatches(t).Count);                    // 10 hues × 5 shades + 10 neutrals
    }

    [Fact]
    public void Clicking_a_swatch_sets_the_hex_and_closes()
    {
        var m = new Model { Open = true };
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);

        var white = Swatches(t).First(n => n.Element!.GetAttribute("data-set-value") == "#FFFFFF");
        Assert.True(white.Width > 0 && white.Height > 0);       // anchored + laid out (hittable)
        t.ClickNode(white);

        Assert.Equal("#FFFFFF", m.Hex);
        Assert.False(m.Open);                                    // a pick closes the palette
        Assert.Null(Pop(t));
    }

    [Fact]
    public void The_swatch_matching_the_current_value_is_ringed()
    {
        var m = new Model { Hex = "#FFFFFF", Open = true };      // exactly the white neutral
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        var selected = Swatches(t).Where(n => n.Element!.ClassList.Contains("selected")).ToList();
        Assert.Single(selected);
        Assert.Equal("#FFFFFF", selected[0].Element!.GetAttribute("data-set-value"));
    }

    [Fact]
    public void Short_and_lowercase_hex_still_matches_its_swatch()
    {
        var m = new Model { Hex = "#fff", Open = true };         // #fff ⇒ #FFFFFF, case-insensitive
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        Assert.Contains(Swatches(t), n =>
            n.Element!.ClassList.Contains("selected") && n.Element.GetAttribute("data-set-value") == "#FFFFFF");
    }

    [Fact]
    public void Clicking_outside_closes_the_palette()
    {
        var m = new Model { Open = true };
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 420);
        Assert.NotNull(Pop(t));
        t.Click(352, 414);                                       // empty space beyond the popup
        Assert.False(m.Open);
        Assert.Null(Pop(t));
    }
}
