using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A context menu has to be able to say what it was opened over.
///
/// Before this, nothing reached the app about the element under a right-click: a right-click is not
/// a click, so <c>OnAction</c> never fired for it; <c>ContextRequested</c> carries a command and no
/// target; and the pointer position that would make <c>HitTest</c> usable was never published. An
/// app could only act on something it already knew, which for a list meant it could not act on the
/// row you right-clicked — the repository's own chat sample copied the most recent image rather
/// than the one under the pointer for exactly this reason (#85).
/// </summary>
public class ContextTargetTests
{
    private sealed class Row { public string Id { get; set; } = ""; public string Text { get; set; } = ""; }

    private sealed class Model
    {
        public List<Row> Rows { get; } =
        [
            new() { Id = "a", Text = "first" },
            new() { Id = "b", Text = "second" },
            new() { Id = "c", Text = "third" },
        ];
    }

    private const string Html = """
        <body><div id="list">
          <div class="row" data-repeat="Rows" data-msg="{{Id}}" style="height:40px">{{Text}}</div>
        </div></body>
        """;

    private const string Css = "body{margin:0;padding:0}.row{display:block}";

    [Fact]
    public void A_right_click_names_the_row_it_landed_on()
    {
        var m = new Model();
        using var t = new TestDoc(Html, Css, m, width: 200, height: 200);

        CupriActionEvent? seen = null;
        t.Doc.OnContext("data-msg", e => { seen = e; return true; });

        // The second row: rows are 40px tall from the top of a zero-margin body.
        t.Doc.DispatchContextMenu(20, 50);

        Assert.NotNull(seen);
        Assert.Equal("b", seen!.Value.Value);       // the attribute's value ON THAT ROW — how the row is named

        // Model is the ROOT model, not the row's. That is how CupriActionEvent has always behaved:
        // data-repeat substitutes each item's bindings and discards the item, so a RenderNode never
        // knows which item produced it. Binding the row's key into the attribute — data-msg="{{Id}}"
        // above — is therefore how a row is identified, and it is sufficient: "b" names the row.
        Assert.Same(m, seen.Value.Model);
    }

    [Fact]
    public void Each_row_is_addressable_not_just_the_first()
    {
        var m = new Model();
        using var t = new TestDoc(Html, Css, m, width: 200, height: 200);
        var hits = new List<string>();
        t.Doc.OnContext("data-msg", e => { hits.Add(e.Value); return true; });

        t.Doc.DispatchContextMenu(20, 10);    // row 0
        t.Doc.DispatchContextMenu(20, 50);    // row 1
        t.Doc.DispatchContextMenu(20, 90);    // row 2

        Assert.Equal(["a", "b", "c"], hits);
    }

    /// <summary>The attribute may sit on an ancestor of whatever the pointer actually hit — the text
    /// inside a row, say — exactly as OnAction bubbles.</summary>
    [Fact]
    public void The_target_bubbles_to_an_ancestor_carrying_the_attribute()
    {
        const string nested = """
            <body><div data-msg="outer" style="padding:20px">
              <span id="inner">click me</span>
            </div></body>
            """;
        using var t = new TestDoc(nested, Css, width: 200, height: 200);
        string? seen = null;
        t.Doc.OnContext("data-msg", e => { seen = e.Value; return true; });

        t.Doc.DispatchContextMenu(30, 30);    // over the span, not the div carrying the attribute
        Assert.Equal("outer", seen);
    }

    /// <summary>The touch recognizer's long-press raises the same menu, so it must carry the same
    /// target — otherwise the feature exists on a mouse and not on a phone.</summary>
    [Fact]
    public void A_long_press_names_its_row_too()
    {
        var m = new Model();
        using var t = new TestDoc(Html, Css, m, width: 200, height: 200);
        string? seen = null;
        t.Doc.OnContext("data-msg", e => { seen = e.Value; return true; });

        var touch = new TouchInput(t.Doc);
        touch.Down(20, 50, 0.0);
        touch.Tick(1.0);                       // held still past the long-press deadline
        Assert.Equal("b", seen);
    }

    [Fact]
    public void The_last_context_point_and_target_are_published()
    {
        var m = new Model();
        using var t = new TestDoc(Html, Css, m, width: 200, height: 200);
        Assert.Null(t.Doc.LastContext);        // nothing has opened one yet

        t.Doc.DispatchContextMenu(20, 50);

        var last = t.Doc.LastContext;
        Assert.NotNull(last);
        Assert.Equal(20f, last!.Value.X, 3);
        Assert.Equal(50f, last.Value.Y, 3);
        Assert.NotNull(last.Value.Target);     // …and what was under it, for an app that would rather HitTest
    }

    [Fact]
    public void A_right_click_over_nothing_registered_reports_no_target_but_still_records_the_point()
    {
        using var t = new TestDoc("<body><div style=\"height:20px\">plain</div></body>", Css,
                                  width: 200, height: 200);
        var fired = false;
        t.Doc.OnContext("data-msg", _ => { fired = true; return true; });

        t.Doc.DispatchContextMenu(100, 150);

        Assert.False(fired, "no element carries the attribute, so no handler should claim it");
        Assert.NotNull(t.Doc.LastContext);     // the point is still worth publishing
    }

    /// <summary>Returning false lets it keep bubbling, as OnAction does.</summary>
    [Fact]
    public void Returning_false_lets_an_outer_handler_see_it()
    {
        const string nested = """
            <body><div data-outer="O" style="padding:20px">
              <div data-inner="I" style="height:20px">x</div>
            </div></body>
            """;
        using var t = new TestDoc(nested, Css, width: 200, height: 200);
        var order = new List<string>();
        t.Doc.OnContext("data-inner", e => { order.Add("inner:" + e.Value); return false; });
        t.Doc.OnContext("data-outer", e => { order.Add("outer:" + e.Value); return true; });

        t.Doc.DispatchContextMenu(30, 30);
        Assert.Equal(["inner:I", "outer:O"], order);
    }
}
