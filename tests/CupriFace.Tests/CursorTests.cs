using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;

namespace CupriFace.Tests;

/// <summary><c>CursorAt(x,y)</c> tells the host which cursor to show: explicit CSS <c>cursor</c> (inherited)
/// wins, otherwise it's inferred from the element (pointer over links/buttons/clickables, text over fields),
/// with drag affordances (a resize grip, a resizable table's column edge) taking priority under the pointer.</summary>
public class CursorTests
{
    private static CursorType At(TestDoc t, RenderNode n) { var (x, y) = TestDoc.Center(n); return t.Doc.CursorAt(x, y); }

    [Fact]
    public void Explicit_css_cursor_is_honoured()
    {
        using var t = new TestDoc(
            "<body><div class='x' style='cursor:crosshair;width:80px;height:60px'></div></body>", "", width: 200, height: 160);
        Assert.Equal(CursorType.Crosshair, At(t, t.FindClass("x")));
    }

    [Fact]
    public void Cursor_inherits_to_descendants()
    {
        using var t = new TestDoc(
            "<body><div style='cursor:grab;width:120px;height:60px'><span class='in'>drag</span></div></body>", "", width: 220, height: 160);
        Assert.Equal(CursorType.Grab, At(t, t.FindClass("in")));  // span has no cursor of its own → inherits grab
    }

    [Fact]
    public void Text_fields_show_the_text_cursor_and_clickables_the_pointer()
    {
        var m = new { V = "hi" };
        using var t = new TestDoc(
            "<body><div style='padding:12px'>" +
            "<cupri-textfield value=\"{{V}}\"></cupri-textfield>" +
            "<div class='btn' data-set-path=\"V\" data-set-value=\"x\" style='width:60px;height:30px'>Set</div>" +
            "</div></body>", "", m, components: true, width: 320, height: 160);

        Assert.Equal(CursorType.Text, At(t, t.FindRole("textbox")));
        Assert.Equal(CursorType.Pointer, At(t, t.FindClass("btn")));
    }

    [Fact]
    public void Empty_space_is_the_default_cursor()
    {
        using var t = new TestDoc("<body><div style='width:40px;height:20px'></div></body>", "", width: 300, height: 200);
        Assert.Equal(CursorType.Default, t.Doc.CursorAt(280, 190));   // far from any content
    }

    [Fact]
    public void A_resizable_table_column_boundary_shows_the_resize_cursor()
    {
        var m = new { Cols = "" };
        using var t = new TestDoc(
            "<body><cupri-table resize=\"{{Cols}}\" style=\"width:300px\">" +
            "<cupri-row header><cupri-cell>Name</cupri-cell><cupri-cell>Role</cupri-cell></cupri-row>" +
            "<cupri-row><cupri-cell>Ada</cupri-cell><cupri-cell>Admin</cupri-cell></cupri-row>" +
            "</cupri-table></body>", "", m, components: true, width: 340, height: 160);

        var h0 = t.Find(n => n.Element?.LocalName == "cupri-cell" && n.Element.GetAttribute("data-col") == "0"
                             && n.Parent?.Element?.ClassList.Contains("header") == true)!;
        var b = HitTesting.AbsoluteBox(h0);
        Assert.Equal(CursorType.EwResize, t.Doc.CursorAt(b.X + b.W - 2, b.Y + b.H / 2)); // on the boundary
        Assert.Equal(CursorType.Default, t.Doc.CursorAt(b.X + b.W / 2, b.Y + b.H / 2));  // mid-cell → not the boundary
    }

    [Fact]
    public void Css_names_map_for_web_hosts()
    {
        Assert.Equal("pointer", CupriFace.CupriDocument.CursorCss(CursorType.Pointer));
        Assert.Equal("ew-resize", CupriFace.CupriDocument.CursorCss(CursorType.EwResize));
        Assert.Equal("default", CupriFace.CupriDocument.CursorCss(CursorType.Auto));
    }
}
