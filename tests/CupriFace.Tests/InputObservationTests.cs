using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.InputObserved</c> — every input event, and what the engine made of it.
///
/// <para>The gap it closes is a reporting one rather than a behavioural one, so these tests assert
/// on what is SAID about an event, not on what the event did. A dispatch returns one bool across the
/// seam between an integration's input code and the engine's, which makes "the host never found the
/// pad", "the engine had no use for it" and "the engine ignored it because you told it to" a single
/// indistinguishable <c>false</c>. Two integrations have now lost time to exactly that.</para>
/// </summary>
public class InputObservationTests
{
    private const string Html = """
        <body>
          <div class='row'><div class='b' role='button'>Alpha</div><div class='b' role='button'>Beta</div></div>
          <div class='row'><div class='b' role='button'>Gamma</div><div class='b' role='button'>Delta</div></div>
        </body>
        """;

    private const string Css =
        "body{margin:0} .row{display:flex;gap:40px;padding:10px} .b{width:120px;height:40px}";

    private static TestDoc Doc(NavigationMode mode = NavigationMode.Spatial)
    {
        var t = new TestDoc(Html, Css, width: 400, height: 200);
        t.Doc.ArrowKeyNavigation = mode;
        return t;
    }

    private static List<InputObservation> Watch(TestDoc t)
    {
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;
        return seen;
    }

    // ---- what arrived, and what became of it -------------------------------------------------------

    /// <summary>A key that navigated says so, and says WHERE it landed — which is the whole point:
    /// the bool only ever said "something changed".</summary>
    [Fact]
    public void A_key_that_navigates_reports_the_action_and_the_control()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Key(EditKey.Down);

        var o = Assert.Single(seen);
        Assert.Equal(InputSource.Keyboard, o.Source);
        Assert.Equal("Down", o.Input);
        Assert.Equal(InputAction.Navigate, o.Action);
        Assert.Equal("Alpha", o.Target);        // entered from the top edge, as Down does
        Assert.True(o.Handled);
    }

    /// <summary>ONE event is ONE observation. A key reaches <c>MoveFocus</c>, which is itself a public
    /// entry point and instrumented as one, so the obvious implementation reports twice and a log
    /// becomes unreadable at exactly the moment someone needs it.</summary>
    [Fact]
    public void One_event_is_one_observation_even_though_it_passes_through_two_entry_points()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Key(EditKey.Down);
        t.Key(EditKey.Right);

        Assert.Equal(2, seen.Count);
        Assert.All(seen, o => Assert.Equal(InputAction.Navigate, o.Action));
    }

    /// <summary>An event that reached the engine and meant nothing to it is reported, and is
    /// distinguishable from one that never arrived (no line at all). That distinction is the reason
    /// the feature exists.</summary>
    [Fact]
    public void An_event_the_engine_had_no_use_for_is_still_reported()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Key(EditKey.Backspace);

        var o = Assert.Single(seen);
        Assert.Equal(InputAction.None, o.Action);
        Assert.False(o.Handled);
    }

    // ---- "it is my own setting doing this" ---------------------------------------------------------

    /// <summary>THE QUESTION AN INTEGRATION ACTUALLY ASKS. Input arrives, nothing moves — and the
    /// cause is a setting the application itself chose. The observation names it.</summary>
    [Fact]
    public void A_deliberately_silenced_key_says_so_and_names_the_setting()
    {
        using var t = Doc();
        t.Doc.KeyboardNavigation = InputRoute.Consume;
        var seen = Watch(t);

        t.Key(EditKey.Down);

        var o = Assert.Single(seen);
        Assert.Equal(InputAction.Swallowed, o.Action);
        Assert.Equal(InputRoute.Consume, o.Route);
        Assert.True(o.Handled);                 // consumed, so the host must not pass it on
        Assert.Contains("Consume", o.ToString());
    }

    /// <summary>The same for arrow navigation turned off, which is the other half of the same
    /// confusion — and <c>Route</c> stays Navigate, because the keyboard was not what was silenced.</summary>
    [Fact]
    public void Arrow_navigation_turned_off_is_reported_as_swallowed()
    {
        using var t = Doc(NavigationMode.Disabled);
        var seen = Watch(t);

        t.Key(EditKey.Tab);                     // focus something first: Tab is not arrow navigation
        t.Key(EditKey.Down);

        Assert.Equal(InputAction.Swallowed, seen[^1].Action);
        Assert.False(seen[^1].Handled);
    }

    // ---- who sent it -------------------------------------------------------------------------------

    /// <summary>A pad is not the keyboard, and — the distinction that matters — the HOST's pad is not
    /// the application's own reader. Both end in the one shared driver, which is what stops a push
    /// counting twice and is also why a document cannot otherwise tell them apart.</summary>
    [Fact]
    public void A_host_pad_and_an_applications_own_reader_are_distinguishable()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Doc.Gamepad.Press(NavigationDirection.Down);        // an app's own reader
        t.Doc.Gamepad.HostPress(NavigationDirection.Right);   // the host's wiring

        Assert.Equal(InputSource.Gamepad, seen[0].Source);
        Assert.Equal(InputSource.HostGamepad, seen[1].Source);
        Assert.All(seen, o => Assert.Equal(InputAction.Navigate, o.Action));
    }

    /// <summary>A host delivers a D-pad as the KEYS it stands for — correct, and it left a diagnostic
    /// unable to tell a pad from a keyboard. <c>AttributeInputTo</c> says which without changing any
    /// routing, so this is the one case where the source is not implied by the method called.</summary>
    [Fact]
    public void A_host_can_attribute_a_key_to_the_pad_that_sent_it()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Doc.AttributeInputTo(InputSource.HostGamepad);
        t.Doc.DispatchKey(null, EditKey.Down);
        t.Doc.DispatchKey(null, EditKey.Right);               // no attribution: a real keyboard

        Assert.Equal(InputSource.HostGamepad, seen[0].Source);
        Assert.Equal(InputSource.Keyboard, seen[1].Source);
    }

    /// <summary>Application code driving focus itself is neither, and says so rather than borrowing
    /// a device it does not have.</summary>
    [Fact]
    public void An_engine_call_with_no_device_behind_it_is_the_application()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Doc.MoveFocus(NavigationDirection.Down);

        Assert.Equal(InputSource.Application, Assert.Single(seen).Source);
    }

    // ---- the thing no test without hardware can check ----------------------------------------------

    /// <summary>
    /// A stick reports the READING, not the direction it resolved to.
    ///
    /// <para>This is deliberate and it is the most valuable line in the log: the sign of y on a
    /// desktop or Android pad is the one part of controller support that cannot be verified without
    /// hardware, and a diagnostic that said "Up" would be perfectly consistent with a host handing
    /// the engine an inverted axis. The numbers make it checkable by a person holding the pad.</para>
    /// </summary>
    [Fact]
    public void A_stick_reports_the_axis_values_so_an_inverted_y_is_visible()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Doc.Gamepad.HostStick(0f, 0.9f);      // pushed DOWN: y positive, in engine coordinates

        var o = Assert.Single(seen);
        Assert.Equal("stick 0.00,0.90", o.Input);
        Assert.Equal(InputAction.Navigate, o.Action);
        Assert.Equal(InputSource.HostGamepad, o.Source);
    }

    /// <summary>A stick held over is one move and then silence, so it is also one observation and then
    /// silence — a stream that repeated every sample would be useless at 60 Hz, which is the rate a
    /// host polls at.</summary>
    [Fact]
    public void A_held_stick_is_not_reported_again()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Doc.Gamepad.HostStick(0f, 0.9f);
        t.Doc.Gamepad.HostStick(0f, 0.9f);
        t.Doc.Gamepad.HostStick(0f, 0.88f);

        Assert.Single(seen);
    }

    // ---- confirm, release, text --------------------------------------------------------------------

    /// <summary>A confirm names what it activated, read BEFORE the activation runs: activating can
    /// move focus, and the useful answer is what was pressed rather than wherever focus ended up.</summary>
    [Fact]
    public void Confirm_reports_what_it_activated()
    {
        using var t = Doc();
        var clicks = 0;
        t.Doc.OnClick(".b", _ => clicks++);
        t.Doc.Gamepad.Press(NavigationDirection.Down);        // → Alpha
        var seen = Watch(t);

        t.Doc.Gamepad.Confirm();

        var o = Assert.Single(seen);
        Assert.Equal(InputAction.Activate, o.Action);
        Assert.Equal("Alpha", o.Target);
        Assert.Equal(1, clicks);
    }

    /// <summary>A RELEASE is observed too. "Releases never arrive" is the failure that makes the
    /// held-key path for diagonals silently fall back to timing, and it was invisible: a key-up
    /// changes nothing on its own, so there was nothing to see either way.</summary>
    [Fact]
    public void A_key_release_is_observed_even_though_it_changes_nothing()
    {
        using var t = Doc();
        t.Doc.ReportsKeyUp = true;
        var seen = Watch(t);

        t.Doc.DispatchKey(null, EditKey.Down);
        t.Doc.DispatchKeyUp(EditKey.Down);

        Assert.Equal(2, seen.Count);
        Assert.Equal("Down up", seen[1].Input);
        Assert.False(seen[1].Handled);
    }

    /// <summary>Text going into a field is named — the one action no single site inside the engine
    /// owns, so it is derived at the boundary from "handled, and carrying text".</summary>
    private sealed class Form { public string Name { get; set; } = ""; }

    [Fact]
    public void Typing_into_a_field_is_reported_as_text()
    {
        // A BOUND field: an unbound one has nowhere to write, so the insert is a no-op and the event
        // is honestly reported as having changed nothing — which is right, and is not this test.
        var model = new Form();
        using var t = new TestDoc(
            "<body><cupri-textfield class='f' value='{{Name}}'></cupri-textfield></body>",
            "body{margin:0} .f{display:block;width:200px;height:30px}",
            model, width: 400, height: 200, components: true);
        t.ClickNode(t.FindRole("textbox"));
        var seen = Watch(t);

        t.Type("x");

        var o = Assert.Single(seen);
        Assert.Equal(InputAction.Text, o.Action);
        Assert.Equal(InputSource.Keyboard, o.Source);
        Assert.Equal("x", model.Name);
    }

    // ---- costs nothing, leaks nothing --------------------------------------------------------------

    /// <summary>
    /// An attribution set while NOTHING is listening must not surface on some later event.
    ///
    /// <para>A host calls <c>AttributeInputTo</c> unconditionally — that is the point of it being
    /// free — so a value left sitting there would be picked up by whatever happened to come next
    /// after a listener was attached, and a diagnostic that lies occasionally is worse than none.
    /// </para>
    /// </summary>
    [Fact]
    public void An_attribution_made_while_unobserved_does_not_leak_into_a_later_event()
    {
        using var t = Doc();

        t.Doc.AttributeInputTo(InputSource.HostGamepad);
        t.Doc.DispatchKey(null, EditKey.Down);                // nobody listening: discarded

        var seen = Watch(t);
        t.Doc.DispatchKey(null, EditKey.Right);               // a real keyboard event

        Assert.Equal(InputSource.Keyboard, Assert.Single(seen).Source);
    }

    /// <summary>Detaching stops it, and the document carries on working — a diagnostic must not
    /// become load-bearing.</summary>
    [Fact]
    public void Detaching_stops_the_stream_and_changes_nothing()
    {
        using var t = Doc();
        var seen = new List<InputObservation>();
        Action<InputObservation> sink = seen.Add;

        t.Doc.InputObserved += sink;
        t.Key(EditKey.Down);
        t.Doc.InputObserved -= sink;
        t.Key(EditKey.Right);

        Assert.Single(seen);
        Assert.Equal("Beta", t.FocusedName());      // the second press still moved the selection
    }

    /// <summary>The line a human reads, pinned so it stays readable — this is the format
    /// <c>CUPRIFACE_KEY_DEBUG</c> writes on desktop.</summary>
    [Fact]
    public void The_log_line_names_source_input_action_and_target()
    {
        using var t = Doc();
        var seen = Watch(t);

        t.Doc.Gamepad.HostPress(NavigationDirection.Down);

        Assert.Equal("HostGamepad Down -> Navigate \"Alpha\" handled", seen[0].ToString());
    }
}
