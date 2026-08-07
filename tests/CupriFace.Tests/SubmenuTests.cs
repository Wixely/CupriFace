using System;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Fly-out submenus: a <c>&lt;cupri-menu-item&gt;</c> that contains its own items becomes a parent
/// row that reveals its children in a panel to the right on hover. The panel is an absolutely
/// positioned child of the row inside the (fixed) menu popup, hidden until the row — or anything
/// inside its panel — is hovered.
/// </summary>
public class SubmenuTests
{
    // "File" opens; "Share" is a submenu parent holding two links.
    private const string Html =
        "<body><div style='padding:30px'>" +
        "<cupri-menu label='File' open>" +
        "  <cupri-menu-item>New</cupri-menu-item>" +
        "  <cupri-menu-item label='Share'>" +
        "    <cupri-menu-item>Email link</cupri-menu-item>" +
        "    <cupri-menu-item>Copy link</cupri-menu-item>" +
        "  </cupri-menu-item>" +
        "</cupri-menu></div></body>";

    private static RenderNode? Flyout(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-submenu") == true && n.Style.Display != DisplayType.None);

    private static int MenuItemsIn(RenderNode n)
    {
        var count = n.Element?.ClassList.Contains("cupri-menu-item") == true ? 1 : 0;
        foreach (var c in n.Children) count += MenuItemsIn(c);
        return count;
    }

    [Fact]
    public void The_panel_is_hidden_until_the_parent_row_is_hovered()
    {
        using var t = new TestDoc(Html, "", components: true, width: 360, height: 260);
        Assert.Null(Flyout(t));                                  // no panel showing at rest

        var (px, py) = TestDoc.Center(t.FindClass("cupri-menu-parent"));
        t.Move(px, py);
        var fly = Flyout(t);
        Assert.NotNull(fly);                                     // revealed on hover
        Assert.True(fly!.Width > 0 && fly.Height > 0);           // and laid out

        t.Move(4, 4);                                            // move well away
        Assert.Null(Flyout(t));                                  // hidden again
    }

    [Fact]
    public void The_panel_flies_out_to_the_right_of_its_row()
    {
        using var t = new TestDoc(Html, "", components: true, width: 360, height: 260);
        var row = t.FindClass("cupri-menu-parent");
        var rb = HitTesting.AbsoluteBox(row);

        var (px, py) = TestDoc.Center(row);
        t.Move(px, py);
        var fb = HitTesting.AbsoluteBox(Flyout(t)!);

        Assert.True(fb.X > rb.X + rb.W * 0.6f, $"panel X {fb.X} should be right of the row (x {rb.X}, w {rb.W})");
        Assert.True(Math.Abs(fb.Y - rb.Y) < 12f, $"panel should be roughly top-aligned (row {rb.Y}, panel {fb.Y})");
    }

    [Fact]
    public void Hovering_into_the_panel_keeps_it_open()
    {
        using var t = new TestDoc(Html, "", components: true, width: 360, height: 260);
        var (px, py) = TestDoc.Center(t.FindClass("cupri-menu-parent"));
        t.Move(px, py);                                          // open the panel

        var (fx, fy) = TestDoc.Center(Flyout(t)!);
        t.Move(fx, fy);                                          // pointer now inside the panel
        Assert.NotNull(Flyout(t));                               // still open (row stays hovered via its descendant)
    }

    [Fact]
    public void The_nested_items_are_relocated_into_the_panel_and_expanded()
    {
        using var t = new TestDoc(Html, "", components: true, width: 360, height: 260);
        var (px, py) = TestDoc.Center(t.FindClass("cupri-menu-parent"));
        t.Move(px, py);

        // Both links live inside the panel as fully-expanded menu items.
        Assert.Equal(2, MenuItemsIn(Flyout(t)!));
    }
}
