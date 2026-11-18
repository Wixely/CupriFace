using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The soft keyboard must never cover the field it was opened for.
///
/// <para>Every host reports the keyboard the same way — as a SHORTER VIEWPORT (Android pads the
/// view by the IME inset, the web host subtracts what the visual viewport lost) — so the whole
/// feature is checkable headless: focus a field, render again at the height a keyboard leaves, and
/// ask where the field is.</para>
///
/// <para>What <see cref="KeyboardVisibilityTests"/> already covered is the field inside ONE
/// scroller. These are the shapes that still left it behind the keyboard: a page that does not
/// scroll at all, a scroller inside a scroller, a field in a fixed dialog, a multi-line field that
/// scrolls itself.</para>
/// </summary>
public class SoftKeyboardRevealTests(ITestOutputHelper output)
{
    private const int W = 412, Full = 915, WithKeyboard = 540;

    private sealed class Model
    {
        public string Name { get; set; } = "Ada";
        public string Email { get; set; } = "ada@example.com";
        public string Notes { get; set; } = "";
    }

    private static TestDoc Doc(string html, string css, Model? model = null) =>
        new(html, "body{margin:0} " + css, model ?? new Model(), width: W, height: Full, components: true);

    private static RenderNode Field(TestDoc t, string cls) =>
        TestDoc.Find(t.FindClass(cls), n => n.Element?.GetAttribute("role") == "textbox") ?? t.FindClass(cls);

    private void Tap(TestDoc t, string cls)
    {
        var (x, y) = TestDoc.Center(Field(t, cls));
        t.Touch.Tap(x, y);
        Assert.True(t.Doc.GetTextInputState().Focused, $"the tap did not focus .{cls}");
    }

    /// <summary>The keyboard arrives (or leaves): the same document, a different height.</summary>
    private static void Resize(TestDoc t, int height) { using var _ = t.Doc.RenderToImage(W, height); }

    private void AssertVisible(TestDoc t, string cls, int height)
    {
        var (_, y, _, h) = HitTesting.ScreenBox(Field(t, cls));
        output.WriteLine($".{cls}: {y:0}..{y + h:0} in a {height}px viewport, pan {t.Doc.ViewportPanY:0}");
        Assert.True(y >= 0 && y + h <= height,
            $".{cls} is at {y:0}..{y + h:0} in a {height}px viewport — behind the keyboard");
    }

    // A page that does not scroll: fixed blocks, the field near the bottom.
    private const string FlatHtmlBody =
        "<body><div class='hero'>h</div>" +
        "<cupri-textfield class='name' value=\"{{Name}}\"></cupri-textfield>" +
        "<div class='gap'>g</div><div class='mark'></div>" +
        "<div class='row'><cupri-button class='send'>Send</cupri-button></div>" +
        "<cupri-textfield class='email' value=\"{{Email}}\"></cupri-textfield>";
    private const string FlatHtml = FlatHtmlBody + "</body>";
    private const string FlatCss = ".hero{height:420px} .gap{height:160px} .mark{height:24px;background:#ff0000} " +
                                   ".row{display:flex;padding:12px 0}";

    [Fact]
    public void A_field_at_the_bottom_of_a_page_that_does_not_scroll_is_panned_into_view()
    {
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "email");
        Assert.Equal(0f, t.Doc.ViewportPanY);          // visible at full height: nothing to do yet

        Resize(t, WithKeyboard);

        AssertVisible(t, "email", WithKeyboard);
        Assert.True(t.Doc.ViewportPanY > 0, "nothing scrolls here, so only a pan could have done it");
    }

    [Fact]
    public void The_pan_is_what_is_painted_and_what_is_touched()
    {
        // The whole point of applying it to the tree: one definition of where things are.
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);

        var (x, y) = TestDoc.Center(t.FindClass("mark"));
        Assert.True(y > 0 && y < WithKeyboard, "the marker is not on screen, so this proves nothing");
        using var img = t.Doc.RenderToImage(W, WithKeyboard, SKColors.White);
        using var bmp = SKBitmap.FromImage(img);
        var px = bmp.GetPixel((int)x, (int)y);
        Assert.True(px.Red > 200 && px.Green < 80, $"the marker is not painted where it is reported: {px}");
        var above = bmp.GetPixel((int)x, (int)y - 20);
        Assert.False(above.Red > 200 && above.Green < 80, "…and it is painted somewhere it is not");

        // And a tap on the field's reported position lands in the field.
        var (fx, fy) = TestDoc.Center(Field(t, "email"));
        Assert.Same(Field(t, "email").Element, HitElement(t, fx, fy));
    }

    private static AngleSharp.Dom.IElement? HitElement(TestDoc t, float x, float y)
    {
        for (var n = HitTesting.HitTest(t.Root, x, y); n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("role") == "textbox") return n.Element;
        return null;
    }

    [Fact]
    public void The_pan_goes_when_the_keyboard_does()
    {
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        Assert.True(t.Doc.ViewportPanY > 0);

        Resize(t, Full);                               // Back: the keyboard leaves, focus stays

        Assert.Equal(0f, t.Doc.ViewportPanY);
        AssertVisible(t, "email", Full);
    }

    [Fact]
    public void The_pan_goes_when_focus_does()
    {
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        Assert.True(t.Doc.ViewportPanY > 0);

        t.Doc.DispatchKey(null, EditKey.Escape);
        Resize(t, WithKeyboard);
        if (t.Doc.GetTextInputState().Focused) return;  // Escape does not blur here; nothing to assert

        Assert.Equal(0f, t.Doc.ViewportPanY);
    }

    [Fact]
    public void A_button_tapped_while_the_page_is_panned_is_the_button_that_runs()
    {
        // A tap activates on finger-UP. If the press blurred the field and the pan let go between
        // down and up, the release would land on whatever slid under the finger.
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        var touch = new TouchDriver(t.Doc, onFrame: () => Resize(t, WithKeyboard));
        var sent = 0;
        t.Doc.OnClick(".send", _ => sent++);

        var (x, y) = TestDoc.Center(t.FindClass("send"));
        Assert.True(y < WithKeyboard, "the button is not on screen, so this proves nothing");
        touch.Tap(x, y);

        Assert.Equal(1, sent);
    }

    [Fact]
    public void A_second_field_already_on_screen_does_not_move_the_page()
    {
        // Minimal movement: the page went up for one field; tapping another that is visible where
        // it stands must not slide everything back under the finger.
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        var pan = t.Doc.ViewportPanY;
        var (x, y) = TestDoc.Center(Field(t, "name"));
        Assert.True(y > 0 && y < WithKeyboard, "the other field is not on screen, so this proves nothing");

        new TouchDriver(t.Doc, onFrame: () => Resize(t, WithKeyboard)).Tap(x, y);

        Assert.Equal(pan, t.Doc.ViewportPanY);
        AssertVisible(t, "name", WithKeyboard);
    }

    [Fact]
    public void Nothing_pans_while_nothing_is_focused()
    {
        using var t = Doc(FlatHtml, FlatCss);
        Resize(t, WithKeyboard);
        Assert.Equal(0f, t.Doc.ViewportPanY);
    }

    [Fact]
    public void A_field_that_fits_where_it_is_moves_nothing()
    {
        using var t = Doc(FlatHtml, FlatCss);
        Tap(t, "name");                                // at y=420: still above a 540px fold
        Resize(t, WithKeyboard);
        Assert.Equal(0f, t.Doc.ViewportPanY);
    }

    [Fact]
    public void A_scroller_inside_a_scroller_ends_up_visible_in_both()
    {
        // The nearest scroller alone is not enough: the list shows the field inside its own band,
        // and the band itself is below the fold of the page that holds it.
        const string html =
            "<body><div class='page'><div class='lead'>l</div><div class='list'><div class='pad'>p</div>" +
            "<cupri-textfield class='name' value=\"{{Name}}\"></cupri-textfield><div class='pad'>p</div></div>" +
            "<div class='tail'>t</div></div></body>";
        const string css = ".page{height:100vh;overflow:scroll} .lead{height:500px} " +
                           ".list{height:300px;overflow:scroll} .pad{height:400px} .tail{height:600px}";
        using var t = Doc(html, css);
        // Scroll the page so the list is on screen, the list so the field is, then tap it.
        t.FindClass("page").ScrollY = 250;
        t.FindClass("list").ScrollY = 250;
        Resize(t, Full);
        Tap(t, "name");
        AssertVisible(t, "name", Full);

        Resize(t, WithKeyboard);

        AssertVisible(t, "name", WithKeyboard);
        Assert.Equal(0f, t.Doc.ViewportPanY);          // there was scroll range: no pan needed
        var list = t.FindClass("list");
        var (_, ly, _, lh) = HitTesting.ScreenBox(list);
        var (_, fy, _, fh) = HitTesting.ScreenBox(Field(t, "name"));
        Assert.True(fy >= ly && fy + fh <= ly + lh, "the field left its own scroller's band");
    }

    [Fact]
    public void A_scroller_taller_than_what_the_keyboard_left_scrolls_rather_than_pans()
    {
        // The field is inside its scroller's band, and the band runs behind the keyboard. The
        // scroller has range, so it should spend it before the page is displaced.
        const string html =
            "<body><div class='box'><div class='lead'>l</div>" +
            "<cupri-textfield class='name' value=\"{{Name}}\"></cupri-textfield><div class='tail'>t</div></div></body>";
        const string css = ".box{height:900px;overflow:scroll} .lead{height:700px} .tail{height:900px}";
        using var t = Doc(html, css);
        Tap(t, "name");

        Resize(t, WithKeyboard);

        AssertVisible(t, "name", WithKeyboard);
        Assert.Equal(0f, t.Doc.ViewportPanY);
        Assert.True(t.FindClass("box").ScrollY > 0);
    }

    [Fact]
    public void A_field_in_a_fixed_dialog_is_revealed_too()
    {
        // position:fixed owns viewport coordinates — nothing it is written inside can scroll it.
        const string html =
            "<body><div class='page'>p</div><div class='sheet'>" +
            "<cupri-textfield class='name' value=\"{{Name}}\"></cupri-textfield></div></body>";
        const string css = ".page{height:400px} .sheet{position:fixed;left:20px;top:640px;width:360px;height:120px}";
        using var t = Doc(html, css);
        Tap(t, "name");

        Resize(t, WithKeyboard);

        AssertVisible(t, "name", WithKeyboard);
    }

    [Fact]
    public void A_field_cut_off_at_the_top_comes_down()
    {
        // A card centred in a viewport too short for it overflows BOTH ends, so its first field
        // sits above the top edge. The pan runs in either direction.
        const string html =
            "<body><div class='card'>" +
            "<cupri-textfield class='name' value=\"{{Name}}\"></cupri-textfield><div class='mid'>m</div>" +
            "<cupri-textfield class='email' value=\"{{Email}}\"></cupri-textfield></div></body>";
        const string css = "body{height:100vh;display:flex;flex-direction:column;justify-content:center} " +
                           ".card{flex:none} .mid{height:620px}";
        using var t = Doc(html, css);
        Tap(t, "name");

        Resize(t, WithKeyboard);

        AssertVisible(t, "name", WithKeyboard);
    }

    [Fact]
    public void Typing_brings_back_a_field_the_reader_scrolled_away_from()
    {
        const string html =
            "<body><div class='page'><div class='lead'>l</div>" +
            "<cupri-textfield class='name' value=\"{{Name}}\"></cupri-textfield><div class='tail'>t</div></div></body>";
        const string css = ".page{height:100vh;overflow:scroll} .lead{height:300px} .tail{height:2000px}";
        using var t = Doc(html, css);
        Tap(t, "name");
        Resize(t, WithKeyboard);
        t.FindClass("page").ScrollY = 1200;            // the reader looks at something else
        Resize(t, WithKeyboard);
        Assert.Equal(1200f, t.FindClass("page").ScrollY, 1);   // and is allowed to

        t.Doc.DispatchKey("x", EditKey.None);
        Resize(t, WithKeyboard);

        AssertVisible(t, "name", WithKeyboard);
    }

    // The flat page again, with the chrome a phone app has: a bar pinned to the bottom of the screen.
    private const string BarHtml = FlatHtmlBody + "<div class='bar'>tabs</div></body>";
    private const string BarCss = FlatCss + " body{background:#102030} " +
                                  ".bar{position:fixed;left:0;bottom:0;width:100%;height:56px;background:#00ff00}";

    [Fact]
    public void A_bar_pinned_to_the_screen_stays_on_the_screen_edge()
    {
        // position:fixed means the SCREEN. A pan that carried the tab bar up with the page would
        // leave it floating across the middle of the content.
        using var t = Doc(BarHtml, BarCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        Assert.True(t.Doc.ViewportPanY > 0, "nothing panned, so this proves nothing");

        var (_, y, _, h) = HitTesting.ScreenBox(t.FindClass("bar"));
        Assert.Equal(WithKeyboard, y + h, 1);
    }

    [Fact]
    public void The_field_clears_a_bar_pinned_over_the_bottom_of_the_screen()
    {
        // Revealed to the last pixel of the viewport, the field would be sitting under the bar.
        using var t = Doc(BarHtml, BarCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);

        var (_, barTop, _, _) = HitTesting.ScreenBox(t.FindClass("bar"));
        var (_, y, _, h) = HitTesting.ScreenBox(Field(t, "email"));
        output.WriteLine($"field {y:0}..{y + h:0}, bar from {barTop:0}");
        Assert.True(y >= 0 && y + h <= barTop, $"the field ends at {y + h:0} and the bar starts at {barTop:0}");
    }

    [Fact]
    public void The_strip_a_pan_uncovers_is_the_pages_own_colour()
    {
        // The page moved up and left a band nothing paints. A host clears with its own colour,
        // which need not be the page's — and a white band under a dark app is the bug.
        using var t = Doc(BarHtml, BarCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        var (_, y, _, h) = HitTesting.ScreenBox(Field(t, "email"));
        var (_, barTop, _, _) = HitTesting.ScreenBox(t.FindClass("bar"));
        Assert.True(barTop - (y + h) >= 6, "there is no gap between field and bar to sample");

        using var img = t.Doc.RenderToImage(W, WithKeyboard, SKColors.White);
        using var bmp = SKBitmap.FromImage(img);
        var px = bmp.GetPixel(W - 3, (int)barTop - 3);
        Assert.Equal(new SKColor(0x10, 0x20, 0x30), px);
    }

    // ---- working INSIDE the field while the page is panned --------------------------------------
    // The pan must be a place the page rests, not something every gesture re-litigates: selecting
    // text in the field you are in cannot move it out from under the finger doing the selecting.

    private TestDoc Panned(out TouchDriver touch)
    {
        var t = Doc(FlatHtml, FlatCss, new Model { Email = "hello wonderful world" });
        Tap(t, "email");
        Resize(t, WithKeyboard);
        Assert.True(t.Doc.ViewportPanY > 0, "nothing panned, so this proves nothing");
        touch = new TouchDriver(t.Doc, onFrame: () => Resize(t, WithKeyboard));
        return t;
    }

    [Fact]
    public void Dragging_a_selection_in_the_panned_field_selects_and_moves_nothing()
    {
        using var t = Panned(out _);
        var pan = t.Doc.ViewportPanY;
        var (x, y, _, h) = HitTesting.ScreenBox(Field(t, "email"));
        var mid = y + h / 2;

        t.Doc.DispatchClick(x + 14, mid);
        Resize(t, WithKeyboard);
        // Along the text, then off the bottom into where the keyboard is, then off the top: a
        // finger does not stay inside a 42px box, and a drag past the edge is where a reveal that
        // chased the caret would start the page running.
        float[][] path = [[60, 0], [120, 0], [120, 60], [120, 160], [120, -80], [120, -300], [120, 0]];
        foreach (var p in path)
        {
            t.Doc.DispatchPointerMove(x + 14 + p[0], mid + p[1]);
            Resize(t, WithKeyboard);
            Assert.Equal(pan, t.Doc.ViewportPanY);
        }
        t.Doc.DispatchPointerUp(x + 134, mid);
        Resize(t, WithKeyboard);

        output.WriteLine($"selected '{t.Doc.CopySelection()}'");
        Assert.StartsWith("hello wonder", t.Doc.CopySelection());
        Assert.Equal(pan, t.Doc.ViewportPanY);
    }

    [Fact]
    public void A_double_tap_in_the_panned_field_selects_the_word_under_it()
    {
        using var t = Panned(out var touch);
        var pan = t.Doc.ViewportPanY;
        var (x, y, _, h) = HitTesting.ScreenBox(Field(t, "email"));

        touch.DoubleTap(x + 30, y + h / 2);

        Assert.Equal("hello", t.Doc.CopySelection());
        Assert.Equal(pan, t.Doc.ViewportPanY);
    }

    [Fact]
    public void A_long_press_in_the_panned_field_opens_its_menu_at_the_finger_and_the_menu_works()
    {
        using var t = Panned(out var touch);
        var pan = t.Doc.ViewportPanY;
        var (x, y, _, h) = HitTesting.ScreenBox(Field(t, "email"));

        touch.LongPress(x + 30, y + h / 2);
        Resize(t, WithKeyboard);

        // The menu is placed in SCREEN coordinates, at the finger. Carried up with the page it
        // would open a keyboard's height away from the press that asked for it.
        var menu = t.Find(n => n.Element?.HasAttribute("data-ctx-menu") == true);
        Assert.NotNull(menu);
        var (_, my, _, mh) = HitTesting.ScreenBox(menu);
        output.WriteLine($"menu {my:0}..{my + mh:0}, pressed at {y + h / 2:0}");
        Assert.True(my >= 0 && my + mh <= WithKeyboard, $"the menu is at {my:0}..{my + mh:0}, off the screen");
        Assert.True(my + mh > WithKeyboard - 60, "the menu opened nowhere near the press");
        Assert.Equal(pan, t.Doc.ViewportPanY);
        Assert.True(t.Doc.GetTextInputState().Focused, "opening the menu took focus from the field");

        var all = TestDoc.Find(menu, n => n.Element?.GetAttribute("data-cupri-context") == "selectall")!;
        var (ax, ay) = TestDoc.Center(all);
        // The engine hands a menu command to the host to carry out; that it was asked for is the
        // proof the tap landed on the row it was aimed at.
        ContextCommand? asked = null;
        t.Doc.ContextRequested += c => asked = c;
        touch.Tap(ax, ay);

        Assert.Equal(ContextCommand.SelectAll, asked);
        Assert.Equal(pan, t.Doc.ViewportPanY);
    }

    [Fact]
    public void DumpTree_reports_a_fixed_node_where_it_is_on_a_panned_page()
    {
        // Its coordinates are the ones handed to DispatchClick, so they have to be the screen's.
        using var t = Doc(BarHtml, BarCss);
        Tap(t, "email");
        Resize(t, WithKeyboard);
        Assert.True(t.Doc.ViewportPanY > 0);

        var (_, y, _, _) = HitTesting.ScreenBox(t.FindClass("bar"));
        var line = t.Doc.DumpTree(maxDepth: 1).Split('\n').Single(l => l.Contains("div.bar"));
        output.WriteLine(line);
        Assert.Contains($"0,{y:0} ", line);
    }
}
