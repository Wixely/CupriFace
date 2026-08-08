using System;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-context-menu&gt;</c>: right-clicking its region opens the menu at the pointer; picking a
/// leaf row runs its action and closes; an outside click or Escape dismisses it. Menu items are the
/// same <c>&lt;cupri-menu-item&gt;</c>s used elsewhere, so fly-out submenus come for free.
/// </summary>
public class CustomContextMenuTests
{
    private const string Html =
        "<body><div style='padding:40px'>" +
        "<cupri-context-menu style='width:200px;height:120px'>" +
        "  <div>Right-click me</div>" +
        "  <cupri-menu-item class='act-rename'>Rename</cupri-menu-item>" +
        "  <cupri-menu-item label='Share'>" +
        "    <cupri-menu-item>Email link</cupri-menu-item>" +
        "  </cupri-menu-item>" +
        "  <cupri-menu-item class='act-del'>Delete</cupri-menu-item>" +
        "</cupri-context-menu>" +
        "</div></body>";

    private static RenderNode? Menu(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-ctx-menu") == true && n.Style.Display != DisplayType.None);

    [Fact]
    public void Right_click_opens_the_menu_at_the_pointer()
    {
        using var t = new TestDoc(Html, "", components: true, width: 400, height: 300);
        Assert.Null(Menu(t));                                       // closed at rest

        var (rx, ry) = TestDoc.Center(t.FindClass("cupri-ctx-host"));
        t.Doc.DispatchContextMenu(rx, ry); t.Layout();

        var menu = Menu(t);
        Assert.NotNull(menu);
        Assert.True(menu!.Width > 0 && menu.Height > 0);            // revealed + laid out
        var b = HitTesting.AbsoluteBox(menu);
        Assert.True(Math.Abs(b.X - rx) < 30 && Math.Abs(b.Y - ry) < 30, $"menu at {b.X},{b.Y} ~ pointer {rx},{ry}");

        // The authored items were relocated into the popup and expanded.
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("act-del") == true));
    }

    [Fact]
    public void Clicking_a_leaf_row_runs_its_action_and_closes_the_menu()
    {
        var clicked = "";
        using var t = new TestDoc(Html, "", components: true, width: 400, height: 300);
        t.Doc.OnClick(".act-rename", _ => clicked = "rename");

        var (rx, ry) = TestDoc.Center(t.FindClass("cupri-ctx-host"));
        t.Doc.DispatchContextMenu(rx, ry); t.Layout();

        var item = t.Find(n => n.Element?.ClassList.Contains("act-rename") == true)!;
        var (ix, iy) = TestDoc.Center(item);
        t.Click(ix, iy);

        Assert.Equal("rename", clicked);   // the row's handler fired
        Assert.Null(Menu(t));              // and the menu closed
    }

    [Fact]
    public void Escape_and_an_outside_click_both_dismiss_the_menu()
    {
        using var t = new TestDoc(Html, "", components: true, width: 400, height: 300);
        var (rx, ry) = TestDoc.Center(t.FindClass("cupri-ctx-host"));

        t.Doc.DispatchContextMenu(rx, ry); t.Layout();
        Assert.NotNull(Menu(t));
        t.Key(EditKey.Escape);
        Assert.Null(Menu(t));

        t.Doc.DispatchContextMenu(rx, ry); t.Layout();
        Assert.NotNull(Menu(t));
        t.Click(6, 6);                     // far corner, outside the popup
        Assert.Null(Menu(t));
    }
}
