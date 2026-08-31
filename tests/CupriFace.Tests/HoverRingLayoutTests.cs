using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Hovering a control must not resize it (#93).
///
/// The field components draw a 2px ring, and every state rule redeclared the whole <c>border</c>
/// shorthand — <c>[data-hover] { border:2px … }</c>. An attribute selector outranks an app's plain
/// class, so an app writing <c>border: 0</c> got width 0 at rest and 2px back the moment the pointer
/// crossed it: the control grew 4px and every sibling in the flex row moved with it. In a chat
/// composer the whole bar twitched as the mouse travelled to the Send button.
///
/// The state rules now set <c>border-color</c> alone. Width belongs to whoever declared it — the
/// component at rest, or the app when it overrode it — and the states only recolour what is there.
/// </summary>
public class HoverRingLayoutTests
{
    private sealed class Model { public string V { get; set; } = ""; }

    // The field is BOUND: focus is keyed on data-bind-value (else id), so an unbound field can never
    // take focus and a :focus assertion on one would pass for the wrong reason.
    private const string Html =
        "<body><div class='row'>" +
        "<cupri-textarea class='field' value=\"{{V}}\" placeholder='Message'></cupri-textarea>" +
        "<cupri-button class='b'>Send</cupri-button>" +
        "</div></body>";

    private const string Css = """
        body { font-size:14px; margin:0 }
        .row { display:flex; flex-direction:row; align-items:center; width:560px; padding:8px }
        .b { padding:6px 14px; font-size:13px; border:1px solid #333 }
        .field { flex:1; min-width:0; border:0; padding:5px 0 }
        """;

    private const string Bare =
        "<body><div class='row'><cupri-textarea class='field' value=\"{{V}}\"></cupri-textarea></div></body>";

    /// <summary>The reporter's measurement: an app that declares no border keeps its size on hover.</summary>
    [Fact]
    public void Hovering_a_borderless_textarea_does_not_change_its_size()
    {
        using var t = new TestDoc(Html, Css, new Model(), width: 600, height: 200, components: true);

        var resting = t.FindClass("field").Height;
        var buttonY = t.FindClass("b").Y;

        t.HoverClass("field");

        Assert.Equal(resting, t.FindClass("field").Height, 3);
        Assert.Equal(buttonY, t.FindClass("b").Y, 3);      // …and nothing beside it moved
    }

    /// <summary>Focus took the same path as hover and must stay put too.</summary>
    [Fact]
    public void Focusing_a_borderless_textarea_does_not_change_its_size()
    {
        using var t = new TestDoc(Html, Css, new Model(), width: 600, height: 200, components: true);

        var resting = t.FindClass("field").Height;
        var buttonY = t.FindClass("b").Y;

        var f = t.FindClass("field");
        t.Doc.DispatchClick(f.X + 20, f.Y + 10);
        t.Layout();
        Assert.True(t.Doc.GetTextInputState().Focused, "the field must actually be focused");

        Assert.Equal(resting, t.FindClass("field").Height, 3);
        Assert.Equal(buttonY, t.FindClass("b").Y, 3);
    }

    /// <summary>The default look is not the thing being fixed: a component the app has not restyled
    /// still draws its 2px ring at rest, and still does not resize when hovered.</summary>
    [Fact]
    public void An_unstyled_field_keeps_its_ring_and_its_size()
    {
        using var t = new TestDoc(Bare, "body{margin:0}.row{display:flex}", new Model(),
                                  width: 600, height: 200, components: true);

        var node = t.FindClass("field");
        var resting = node.Height;
        Assert.True(node.Style.BorderTop > 1.5f, "the component's own ring should still be there");

        t.HoverClass("field");
        Assert.Equal(resting, t.FindClass("field").Height, 3);
        Assert.True(t.FindClass("field").Style.BorderTop > 1.5f);
    }

    /// <summary>The point of the ring is that it responds, so proving it no longer resizes is only
    /// half an answer: it must still RECOLOUR. A fix that quietly dropped the affordance instead of
    /// moving it out of layout would pass every test above.</summary>
    [Fact]
    public void The_ring_still_changes_colour_on_hover_and_on_focus()
    {
        using var t = new TestDoc(Bare, "body{margin:0}.row{display:flex}", new Model(),
                                  width: 600, height: 200, components: true);

        var resting = t.FindClass("field").Style.BorderColor;

        t.HoverClass("field");
        var hovered = t.FindClass("field").Style.BorderColor;
        Assert.NotEqual(resting, hovered);

        // Focus is read with the pointer moved AWAY: [data-hover] and :focus (rewritten to
        // [data-focus]) have equal specificity, so while the pointer is still over the field the
        // hover colour keeps winning on source order. That was true before this change and after it —
        // only the property name differed — so it is not what #93 is about.
        var f = t.FindClass("field");
        t.Doc.DispatchClick(f.X + 20, f.Y + 10);
        t.Move(595, 195);
        var focused = t.FindClass("field").Style.BorderColor;

        Assert.NotEqual(resting, focused);
        Assert.NotEqual(hovered, focused);
    }

    /// <summary>The same rule shape is in every field component, so the fix has to be too — this is
    /// the one that would otherwise be fixed for a textarea and left in the nine beside it.</summary>
    [Theory]
    [InlineData("cupri-textfield")]
    [InlineData("cupri-textarea")]
    [InlineData("cupri-search")]
    [InlineData("cupri-password")]
    [InlineData("cupri-number")]
    public void No_field_component_resizes_on_hover_when_the_app_removed_its_border(string tag)
    {
        var html = $"<body><div class='row'><{tag} class='field' value=\"{{{{V}}}}\"></{tag}></div></body>";
        const string css = "body{margin:0}.row{display:flex;width:400px}.field{flex:1;border:0;padding:5px 0}";
        using var t = new TestDoc(html, css, new Model(), width: 600, height: 200, components: true);

        var resting = t.FindClass("field").Height;
        t.HoverClass("field");

        Assert.Equal(resting, t.FindClass("field").Height, 3);
    }
}
