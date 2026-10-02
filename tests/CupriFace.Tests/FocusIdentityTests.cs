using CupriFace.Interaction;
using Xunit;


namespace CupriFace.Tests;

/// <summary>
/// Keyboard focus survives a rebuild, because it is carried as an IDENTITY rather than as a position.
///
/// <para>Focus is an index into the focusable list, and that list is rebuilt from scratch on every
/// rebuild — which is every keystroke, every model change, and every mouse move that triggers a
/// <c>:hover</c> restyle. While the tree keeps its shape the index happens to still point at the
/// right thing, which is why this was easy to miss. The moment a control appears or disappears
/// ANYWHERE EARLIER, every index after it slides by one and the selection is silently on a different
/// control than the one the user was looking at.</para>
///
/// <para>A controller UI is where this bites hardest: it is driven entirely by the selection, and it
/// is exactly the kind of UI whose contents change as the model does.</para>
/// </summary>
public class FocusIdentityTests
{
    private sealed class Model
    {
        // A repeat is how controls actually come and go in this engine — there is no conditional
        // attribute — and it is also how a real app does it: the list IS the state.
        public List<string> Banners { get; set; } = [];
        public string Label { get; set; } = "two";
    }

    // The banner is a FOCUSABLE control that comes and goes before the others — the shape of every
    // "an alert appeared" or "the connect button became available" case.
    private const string Html = """
        <body>
          <div class='b' id='banner' role='button' data-repeat='Banners'>{{.}}</div>
          <div class='b' id='one' role='button'>one</div>
          <div class='b' id='two' role='button'>{{Label}}</div>
          <div class='b' id='three' role='button'>three</div>
        </body>
        """;
    private const string Css = "body{margin:0} .b{width:200px;height:40px;background:#345;color:#fff}";

    private static TestDoc Open(Model m) => new(Html, Css, model: m, width: 400, height: 400);

    /// <summary>
    /// THE BUG. Focus "two", then make a control appear above it. Nothing about "two" changed, and
    /// the user is still looking at it — so focus must still be on it. With a bare index it slides
    /// to "one", because everything after the banner shifted down by one.
    /// </summary>
    [Fact]
    public void A_control_appearing_above_does_not_drag_the_selection()
    {
        var m = new Model();
        using var t = Open(m);
        t.Key(EditKey.Tab); t.Key(EditKey.Tab);          // one, two
        Assert.Equal("two", t.FocusedName());

        m.Banners = ["banner"];
        t.Doc.Refresh();
        t.Layout();

        Assert.Equal("two", t.FocusedName());
    }

    /// <summary>And the same going the other way: a control disappearing from above must not drag
    /// the selection down the list.</summary>
    [Fact]
    public void A_control_disappearing_from_above_does_not_drag_it_either()
    {
        var m = new Model { Banners = ["banner"] };
        using var t = Open(m);
        t.Key(EditKey.Tab); t.Key(EditKey.Tab); t.Key(EditKey.Tab);   // banner, one, two
        Assert.Equal("two", t.FocusedName());

        m.Banners = [];
        t.Doc.Refresh();
        t.Layout();

        Assert.Equal("two", t.FocusedName());
    }

    /// <summary>An ordinary rebuild that only re-renders text leaves focus exactly where it was.
    /// This passed before the change too — it is here so that a future "simplification" of the
    /// identity has to keep the easy case working as well as the hard one.</summary>
    [Fact]
    public void A_text_change_leaves_focus_alone()
    {
        var m = new Model();
        using var t = Open(m);
        t.Key(EditKey.Tab); t.Key(EditKey.Tab);
        Assert.Equal("two", t.FocusedName());

        m.Label = "two (updated)";
        t.Doc.Refresh();
        t.Layout();

        Assert.Equal("two (updated)", t.FocusedName());   // the same control, new text
    }

    /// <summary>Navigation continues from where focus actually is, not from where the index used to
    /// point. The selection being right for one frame is no use if the next press leaves from the
    /// wrong place.</summary>
    [Fact]
    public void Navigation_continues_from_the_control_that_is_focused()
    {
        var m = new Model();
        using var t = Open(m);
        t.Key(EditKey.Tab); t.Key(EditKey.Tab);
        Assert.Equal("two", t.FocusedName());

        m.Banners = ["banner"];
        t.Doc.Refresh();
        t.Layout();

        t.Key(EditKey.Tab);
        Assert.Equal("three", t.FocusedName());           // onward from two, not from one
    }

    /// <summary>A control with NO key is tracked by its LABEL, which is how a person would re-find
    /// it. Same markup, ids removed. A structural path cannot do this job — it shifts with the
    /// insertion exactly as the index does, and would confidently return "one".</summary>
    [Fact]
    public void An_unkeyed_control_is_tracked_by_its_label()
    {
        var m = new Model();
        using var t = new TestDoc(
            """
            <body>
              <div class='b' role='button' data-repeat='Banners'>{{.}}</div>
              <div class='b' role='button'>one</div>
              <div class='b' role='button'>two</div>
            </body>
            """, Css, model: m, width: 400, height: 400);

        t.Key(EditKey.Tab); t.Key(EditKey.Tab);
        Assert.Equal("two", t.FocusedName());

        m.Banners = ["banner"];
        t.Doc.Refresh();
        t.Layout();

        Assert.Equal("two", t.FocusedName());
    }

    /// <summary>When the focused control is GONE there is nothing to carry, and the index must still
    /// land inside the list. Left past the end of a list that just got shorter it reads as "nothing
    /// focused", which leaves a controller with no way back except Tab.</summary>
    [Fact]
    public void Losing_the_focused_control_still_leaves_focus_somewhere_usable()
    {
        var m = new Model { Banners = ["banner"] };
        using var t = Open(m);
        t.Key(EditKey.Tab);                               // the banner itself
        Assert.Equal("banner", t.FocusedName());

        m.Banners = [];                             // focus is standing on a trapdoor
        t.Doc.Refresh();
        t.Layout();

        Assert.NotEqual("", t.FocusedName());             // something is focused…
        t.Key(EditKey.Tab);
        Assert.NotEqual("", t.FocusedName());             // …and Tab still works from it
    }

    /// <summary>A :hover restyle rebuilds the tree too, down a different code path (<c>ReStyle</c>),
    /// which is why the carry had to be added in two places rather than one.
    ///
    /// <para>This passes with or without the carry, because hovering does not change which controls
    /// are focusable here — it is a regression guard for the second path, not a demonstration of the
    /// bug. The four tests above are the ones that fail against the old code.</para>
    /// </summary>
    [Fact]
    public void A_hover_restyle_does_not_move_the_selection()
    {
        var m = new Model();
        using var t = new TestDoc(Html, Css + " .b:hover{background:#567}", model: m, width: 400, height: 400);
        t.Key(EditKey.Tab); t.Key(EditKey.Tab);
        Assert.Equal("two", t.FocusedName());

        t.Move(100, 20);                                  // hover the FIRST control
        Assert.Equal("two", t.FocusedName());
        t.Move(100, 60);
        Assert.Equal("two", t.FocusedName());
    }
}
