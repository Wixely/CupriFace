using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Saying "I navigate, not you" — <c>ArrowKeyNavigation</c> and <c>KeyboardNavigation</c>.
///
/// <para>Both exist because an integration could not say it. <c>ArrowNavigation = false</c> read as
/// "arrows off" and meant "arrows move in document order", so an application driving focus through
/// its own model found the engine moving the selection underneath it — and worked around it by
/// registering no-op handlers on eight keys. An API that has to be defeated is a defect.</para>
/// </summary>
public class InputRoutingTests
{
    private const string Grid = """
        <body>
          <div class='row'><div class='b' role='button'>a1</div><div class='b' role='button'>b1</div></div>
          <div class='row'><div class='b' role='button'>a2</div><div class='b' role='button'>b2</div></div>
        </body>
        """;
    private const string Css = """
        body { margin:0 }
        .row { display:flex; gap:20px; padding:10px }
        .b { width:160px; height:48px; background:#345; color:#fff; font-size:15px }
        """;

    private static TestDoc Open() => new(Grid, Css, width: 600, height: 400);

    [Fact]
    public void Sequential_is_the_default()
    {
        using var t = Open();
        Assert.Equal(NavigationMode.Sequential, t.Doc.ArrowKeyNavigation);
        Assert.Equal(InputRoute.Navigate, t.Doc.KeyboardNavigation);
    }

    /// <summary>
    /// THE BUG THE ENUM EXISTS FOR. The old boolean's "false" still moved focus — in document order —
    /// so an application that wanted arrows to do NOTHING had no way to ask. This asserts the old
    /// meaning survives (nobody's app changes behaviour) and that the state it could not express now
    /// exists.
    /// </summary>
    [Fact]
    public void The_boolean_could_not_say_off_and_the_enum_can()
    {
        using var t = Open();
        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());

        t.Doc.ArrowKeyNavigation = NavigationMode.Sequential;   // what `= false` always meant
        t.Key(EditKey.Down);
        Assert.Equal("b1", t.FocusedName());                    // …it MOVED. That was the surprise.

        t.Doc.ArrowKeyNavigation = NavigationMode.Disabled;     // what `= false` was taken to mean
        t.Key(EditKey.Down);
        Assert.Equal("b1", t.FocusedName());                    // now it genuinely does nothing
        t.Key(EditKey.Up);
        Assert.Equal("b1", t.FocusedName());
    }

    /// <summary>The obsolete boolean keeps working and keeps meaning what it meant, so an app on it
    /// is not broken by the replacement — only told there is a better word.</summary>
    [Fact]
    public void The_obsolete_boolean_still_maps_both_ways()
    {
        using var t = Open();
#pragma warning disable CS0618
        t.Doc.ArrowNavigation = true;
        Assert.Equal(NavigationMode.Spatial, t.Doc.ArrowKeyNavigation);
        Assert.True(t.Doc.ArrowNavigation);

        t.Doc.ArrowNavigation = false;
        Assert.Equal(NavigationMode.Sequential, t.Doc.ArrowKeyNavigation);

        t.Doc.ArrowKeyNavigation = NavigationMode.Disabled;
        Assert.False(t.Doc.ArrowNavigation);     // the boolean cannot see the difference — the point
#pragma warning restore CS0618
    }

    /// <summary>Disabled stops NAVIGATION, not a control's own arrow behaviour. A slider still nudges:
    /// that belongs to the slider, and an app saying "I choose what is selected" is not asking for its
    /// controls to stop working.</summary>
    [Fact]
    public void A_disabled_arrow_still_nudges_a_focused_slider()
    {
        var model = new Model();
        using var t = new TestDoc(
            "<body><cupri-slider min='0' max='100' value='{{Vol}}' style='width:200px'></cupri-slider></body>",
            "body{margin:0}", model: model, width: 400, height: 200, components: true);
        t.Doc.ArrowKeyNavigation = NavigationMode.Disabled;

        t.Key(EditKey.Tab);
        var before = model.Vol;
        t.Key(EditKey.Right);
        Assert.True(model.Vol > before, $"the slider should still nudge; was {before}, now {model.Vol}");
    }

    // ---- KeyboardNavigation: the eight no-op handlers, replaced ---------------------------------

    /// <summary>
    /// CONSUME REPLACES THE WORKAROUND. All eight keys the integration had to neutralise — the four
    /// arrows, Tab, Enter, Space and Escape — do nothing, and report themselves HANDLED so the host
    /// does not apply its own fallback either.
    /// </summary>
    [Theory]
    [InlineData(EditKey.Left)]
    [InlineData(EditKey.Right)]
    [InlineData(EditKey.Up)]
    [InlineData(EditKey.Down)]
    [InlineData(EditKey.Tab)]
    [InlineData(EditKey.Enter)]
    [InlineData(EditKey.Space)]
    [InlineData(EditKey.Escape)]
    public void Consume_takes_every_navigation_key_and_does_nothing_with_it(EditKey key)
    {
        using var t = Open();
        t.Doc.KeyboardNavigation = InputRoute.Consume;

        Assert.True(t.Doc.DispatchKey(null, key), "reported handled, so the host does not fall back");
        t.Layout();
        Assert.Equal("", t.FocusedName());     // …and nothing was selected
    }

    /// <summary>Ignore is the same refusal without the claim: the host is told nobody wanted it, and
    /// is free to act — which on the desktop is how Escape leaves fullscreen.</summary>
    [Fact]
    public void Ignore_refuses_the_key_and_says_so()
    {
        using var t = Open();
        t.Doc.KeyboardNavigation = InputRoute.Ignore;

        Assert.False(t.Doc.DispatchKey(null, EditKey.Escape), "unhandled: the host may still act");
        Assert.False(t.Doc.DispatchKey(null, EditKey.Tab));
        t.Layout();
        Assert.Equal("", t.FocusedName());
    }

    /// <summary>An application that steers itself still steers. Turning the keyboard off is not
    /// turning focus off — this is the whole shape of the integration that asked for it.</summary>
    [Fact]
    public void The_application_can_still_move_focus_itself()
    {
        using var t = Open();
        t.Doc.KeyboardNavigation = InputRoute.Consume;

        t.Doc.Gamepad.Press(NavigationDirection.Down);     // its own source, unaffected
        t.Layout();
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>
    /// TYPING IS NOT NAVIGATION. Inside a text field the arrows move a caret and Enter submits, and an
    /// app saying "I navigate, not you" is not asking for its text boxes to stop working. Without this
    /// guard the flag would be unusable in any app with a search box.
    /// </summary>
    [Fact]
    public void A_focused_text_field_is_untouched_by_the_route()
    {
        var model = new Model();
        using var t = new TestDoc(
            "<body><cupri-textfield value='{{Name}}'></cupri-textfield></body>",
            "body{margin:0}", model: model, width: 400, height: 200, components: true);
        t.Doc.KeyboardNavigation = InputRoute.Consume;

        t.Key(EditKey.Tab);                    // reaches the field: nothing is focused yet, so the
        t.Doc.KeyboardNavigation = InputRoute.Navigate;
        t.Key(EditKey.Tab);
        t.Doc.KeyboardNavigation = InputRoute.Consume;

        t.Type("abc");
        t.Key(EditKey.Left);                   // a caret move, not a navigation
        t.Type("X");
        t.Key(EditKey.Tab);                    // blur commits
        Assert.Equal("abXc", model.Name);
    }

    private sealed class Model
    {
        public string Name { get; set; } = "";
        public int Vol { get; set; } = 50;
    }
}
