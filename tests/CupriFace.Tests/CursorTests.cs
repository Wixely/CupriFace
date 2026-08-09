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

    // ---- everything wired to act on a click shows the pointer, however it was wired ----

    [Fact]
    public void A_row_wired_only_by_OnClick_shows_the_pointer()
    {
        // The showcase's sidebar nav: a plain <div class="nav"> whose behaviour comes entirely from a
        // registered OnClick selector. It was the one interactive thing left showing an arrow.
        using var t = new TestDoc(
            "<body><div class='nav' style='width:140px;height:36px'>Charts</div>" +
            "<div class='plain' style='width:140px;height:36px'>Not a link</div></body>", "", width: 260, height: 200);
        t.Doc.OnClick(".nav", _ => { });

        Assert.Equal(CursorType.Pointer, At(t, t.FindClass("nav")));
        Assert.Equal(CursorType.Default, At(t, t.FindClass("plain"))); // unwired siblings stay plain
    }

    [Fact]
    public void An_element_carrying_a_registered_OnAction_attribute_shows_the_pointer()
    {
        using var t = new TestDoc(
            "<body><div class='s' data-sort-by='name' style='width:120px;height:30px'>Name</div></body>",
            "", width: 220, height: 160);
        t.Doc.OnAction("data-sort-by", _ => true);
        Assert.Equal(CursorType.Pointer, At(t, t.FindClass("s")));
    }

    [Fact]
    public void Toggles_steppers_and_sliders_show_the_pointer()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:10px'>" +
            "<cupri-switch checked=\"{{On}}\"></cupri-switch>" +
            "<cupri-checkbox checked=\"{{On}}\"></cupri-checkbox>" +
            "<cupri-number value=\"{{N}}\" min=\"0\" max=\"9\"></cupri-number>" +
            "<cupri-slider min=\"0\" max=\"100\" value=\"{{N}}\" style='width:150px'></cupri-slider>" +
            "</div></body>", "", m, components: true, width: 420, height: 260);

        Assert.Equal(CursorType.Pointer, At(t, t.FindRole("switch")));
        Assert.Equal(CursorType.Pointer, At(t, t.FindRole("checkbox")));
        Assert.Equal(CursorType.Pointer, At(t, t.FindRole("slider")));
        // The number field's +/- steppers act on click (data-cupri-step) even though the field is text.
        Assert.Equal(CursorType.Pointer, At(t, t.Find(n => n.Element?.HasAttribute("data-cupri-step") == true)!));
        Assert.Equal(CursorType.Text, At(t, t.FindRole("spinbutton")));
    }

    [Fact]
    public void A_controls_text_label_shows_the_controls_cursor()
    {
        // Clicking the label toggles the control (HTML <label> behaviour), so it must not look inert.
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:10px'>" +
            "<cupri-checkbox checked=\"{{On}}\"></cupri-checkbox><span class='lbl'>Notifications</span>" +
            "</div></body>", "", m, components: true, width: 320, height: 160);
        Assert.Equal(CursorType.Pointer, At(t, t.FindClass("lbl")));
    }

    [Fact]
    public void A_disabled_control_shows_not_allowed()
    {
        // A pagination arrow on page 1 keeps role=button for a11y but drops its activation hook.
        var m = new Model();
        using var t = new TestDoc(
            "<body><cupri-pagination page=\"{{Page}}\" pages=\"5\"></cupri-pagination></body>",
            "", m, components: true, width: 380, height: 140);

        var prev = t.Find(n => n.Element?.ClassList.Contains("cupri-page-nav") == true
                               && n.Element.ClassList.Contains("disabled"))!;
        Assert.Equal(CursorType.NotAllowed, At(t, prev));

        var next = t.Find(n => n.Element?.ClassList.Contains("cupri-page-nav") == true
                               && !n.Element.ClassList.Contains("disabled"))!;
        Assert.Equal(CursorType.Pointer, At(t, next)); // the enabled arrow still invites a click
    }

    [Fact]
    public void Components_get_the_pointer_from_inference_not_hand_rolled_css()
    {
        // These used to carry their own `cursor:pointer` in component CSS. The engine infers it from
        // their activation hooks now, so the declarations were removed — this guards that removal.
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:10px'>" +
            "<cupri-accordion><cupri-accordion-item label=\"Panel\" open=\"{{On}}\">Body</cupri-accordion-item></cupri-accordion>" +
            "<cupri-table select=\"{{Sel}}\">" +
            "<cupri-row header><cupri-cell>Name</cupri-cell></cupri-row>" +
            "<cupri-row><cupri-cell>Ada</cupri-cell></cupri-row></cupri-table>" +
            "</div></body>", "", m, components: true, width: 360, height: 300);

        Assert.Equal(CursorType.Pointer, At(t, t.FindClass("cupri-acc-header")));
        var bodyRow = t.Find(n => n.Element?.ClassList.Contains("cupri-row") == true
                                  && !n.Element.ClassList.Contains("header"))!;
        Assert.Equal(CursorType.Pointer, At(t, bodyRow));
    }

    [Fact]
    public void The_cursor_is_right_before_the_next_layout()
    {
        // Hosts lay out once per frame and THEN dispatch input, so they ask for the cursor while the
        // tree a hover change just restyled into has no layout yet. Hit-testing that tree finds nothing,
        // which showed the plain arrow every time the pointer entered a control — on every host.
        using var t = new TestDoc(
            "<body><div style='padding:20px'>" +
            "<div class='a' style='width:100px;height:40px'>A</div>" +
            "<cupri-button>Save</cupri-button></div></body>",
            ".a[data-hover]{background:#eee}", components: true, width: 300, height: 220);

        var (ax, ay) = TestDoc.Center(t.FindClass("a"));
        var (bx, by) = TestDoc.Center(t.FindClass("cupri-button"));
        t.Move(ax, ay);                        // park elsewhere so entering the button changes hover

        t.Doc.DispatchPointerMove(bx, by);     // the host sequence: dispatch…
        Assert.Equal(CursorType.Pointer, t.Doc.CursorAt(bx, by)); // …then ask, before rendering
    }

    // ---- an in-flight drag owns the cursor, even when the pointer leaves the control ----

    [Fact]
    public void A_slider_drag_keeps_its_cursor_when_the_pointer_leaves_the_thumb()
    {
        var m = new Model();
        using var t = new TestDoc(
            "<body><div style='padding:20px'><cupri-slider min=\"0\" max=\"100\" value=\"{{N}}\" style='width:160px'></cupri-slider></div>" +
            "<div style='height:80px'>text below</div></body>", "", m, components: true, width: 320, height: 260);

        var (sx, sy) = TestDoc.Center(t.FindRole("slider"));
        t.Click(sx, sy);                       // grab the thumb
        Assert.Equal(CursorType.Pointer, t.Doc.CursorAt(sx, sy + 90)); // dragged well off the control
        t.Up(sx, sy + 90);
    }

    private sealed class Model
    {
        public bool On { get; set; }
        public int N { get; set; } = 3;
        public int Page { get; set; } = 1;
        public string Sel { get; set; } = "";
    }
}
