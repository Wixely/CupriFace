using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.HostGamepadInput</c> — which parts of the HOST's controller drive the document, and the
/// routing that goes with treating a pad as a pad.
///
/// <para>Three faults, all of them the same mistake in different clothes: a controller arrives as
/// the KEYS it stands for, every host does this, and the document could not tell the two apart.
/// So <c>KeyboardNavigation = Consume</c> silenced the pad in the player's hands; a host D-pad moved
/// in DOCUMENT ORDER while the stick beside it moved by geometry; and an application that wanted the
/// host's D-pad while reading the sticks itself had to switch the host off entirely and reimplement
/// the half it never meant to take over.</para>
/// </summary>
public class HostGamepadCapabilityTests
{
    // Document order and geometry DISAGREE here, which is the whole point: after a1, "next" is b1
    // and "down" is a2. A test that cannot tell the two apart cannot see this bug.
    private const string Grid = """
        <body>
          <div class='row'><div class='b' role='button'>a1</div><div class='b' role='button'>b1</div></div>
          <div class='row'><div class='b' role='button'>a2</div><div class='b' role='button'>b2</div></div>
          <div class='row'><div class='b' role='button'>a3</div><div class='b' role='button'>b3</div></div>
        </body>
        """;
    private const string Css = """
        body { margin:0; }
        .row { display:flex; gap:20px; padding:10px; }
        .b { width:160px; height:48px; }
        """;

    private static TestDoc Open() => new(Grid, Css, width: 600, height: 400);

    private static void Pad(TestDoc t, EditKey key)
    {
        t.Doc.DispatchGamepadKey(key, down: true);
        t.Doc.DispatchGamepadKey(key, down: false);
        t.Layout();
    }

    // ---- a pad is not a keyboard ------------------------------------------------------------------

    /// <summary>
    /// THE BUG. An application that says "I navigate, not you" is talking about the KEYBOARD, and
    /// used to silence the controller too — because the controller was arriving as keys.
    ///
    /// <para>The same shape as the confirm bug fixed in v0.33.0-alpha.6, surviving in the one path
    /// that still had it. <c>HostGamepadInput</c> is a pad's off switch; this is not.</para>
    /// </summary>
    [Fact]
    public void Consuming_the_keyboard_does_not_silence_the_pad()
    {
        using var t = Open();
        t.Doc.KeyboardNavigation = InputRoute.Consume;

        Pad(t, EditKey.Down);
        Assert.Equal("a1", t.FocusedName());     // the pad still enters from the top edge

        Pad(t, EditKey.Down);
        Assert.Equal("a2", t.FocusedName());
    }

    /// <summary>…and the keyboard is still consumed, or the setting would mean nothing. Both halves
    /// matter: the fix is a DISTINCTION, not a removal.</summary>
    [Fact]
    public void Consuming_the_keyboard_still_consumes_the_keyboard()
    {
        using var t = Open();
        t.Doc.KeyboardNavigation = InputRoute.Consume;

        t.Key(EditKey.Down);

        Assert.Equal("", t.FocusedName());
    }

    /// <summary>A pad navigates by GEOMETRY whatever the arrow keys are set to — and the default is
    /// Sequential, so this was wrong by default. The grid disagrees about where Down goes: document
    /// order says b1, geometry says a2.</summary>
    [Fact]
    public void A_host_dpad_navigates_spatially_even_on_the_default_arrow_setting()
    {
        using var t = Open();
        Assert.Equal(NavigationMode.Sequential, t.Doc.ArrowKeyNavigation);   // the default

        Pad(t, EditKey.Down);
        Pad(t, EditKey.Down);

        Assert.Equal("a2", t.FocusedName());     // not b1, which is where Tab order would have gone
    }

    /// <summary>The arrow KEYS are unaffected by that, and still follow their own setting — the two
    /// are separate questions about separate hardware.</summary>
    [Fact]
    public void The_arrow_keys_still_follow_their_own_setting()
    {
        using var t = Open();

        t.Key(EditKey.Tab);                      // a1
        t.Key(EditKey.Down);

        Assert.Equal("b1", t.FocusedName());     // document order, which is what Sequential means
    }

    /// <summary>Turning arrow navigation off does not turn the PAD off. An app that drives focus from
    /// its own code wants the arrows out of the way; that is not a statement about a controller, and
    /// <c>HostGamepadInput</c> is where one is made.</summary>
    [Fact]
    public void Disabling_arrow_navigation_does_not_disable_the_pad()
    {
        using var t = Open();
        t.Doc.ArrowKeyNavigation = NavigationMode.Disabled;

        Pad(t, EditKey.Down);

        Assert.Equal("a1", t.FocusedName());
    }

    // ---- the capabilities --------------------------------------------------------------------------

    /// <summary>THE FEATURE: an app that reads the sticks itself keeps the host's D-pad. The boolean
    /// could not say this, so such an app switched the host off and reimplemented the rest.</summary>
    [Fact]
    public void An_app_can_take_the_sticks_and_leave_the_dpad()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.Dpad | HostGamepad.Buttons;

        Assert.False(t.Doc.DispatchGamepadStick(0f, 0.9f));   // the host's stick is not listened to
        Assert.Equal("", t.FocusedName());

        Pad(t, EditKey.Down);
        Assert.Equal("a1", t.FocusedName());                  // …and its D-pad still is
    }

    /// <summary>And the reverse, which is the other half of "independent".</summary>
    [Fact]
    public void An_app_can_take_the_dpad_and_leave_the_sticks()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.Stick;

        Pad(t, EditKey.Down);
        Assert.Equal("", t.FocusedName());

        Assert.True(t.Doc.DispatchGamepadStick(0f, 0.9f));
        t.Layout();
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>Buttons are separate from the D-pad: a confirm can be taken over without losing the
    /// directions, which is the split an app that runs its own menus wants.</summary>
    [Fact]
    public void Buttons_are_a_capability_of_their_own()
    {
        using var t = Open();
        var clicks = 0;
        t.Doc.OnClick(".b", _ => clicks++);
        t.Doc.HostGamepadInput = HostGamepad.Dpad;

        Pad(t, EditKey.Down);
        Assert.Equal("a1", t.FocusedName());     // directions arrive

        Pad(t, EditKey.Enter);
        Assert.Equal(0, clicks);                 // the confirm does not
    }

    /// <summary>None silences all of it, which is what the old boolean's false meant.</summary>
    [Fact]
    public void None_silences_the_host_pad_entirely()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.None;

        Pad(t, EditKey.Down);
        Assert.False(t.Doc.DispatchGamepadStick(0f, 0.9f));

        Assert.Equal("", t.FocusedName());
        // …but the application's OWN source is unaffected: this was never about that.
        t.Doc.Gamepad.Press(NavigationDirection.Down);
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>The obsolete boolean still means what it meant, in both directions.</summary>
    [Fact]
    public void The_old_boolean_still_maps_onto_the_capabilities()
    {
        using var t = Open();

#pragma warning disable CS0618
        Assert.True(t.Doc.HostGamepadNavigation);                 // All, the default

        t.Doc.HostGamepadNavigation = false;
        Assert.Equal(HostGamepad.None, t.Doc.HostGamepadInput);
        Assert.False(t.Doc.HostGamepadNavigation);

        t.Doc.HostGamepadNavigation = true;
        Assert.Equal(HostGamepad.All, t.Doc.HostGamepadInput);

        // A partial setting reads as "on", which is the only honest answer a bool can give.
        t.Doc.HostGamepadInput = HostGamepad.Stick;
        Assert.True(t.Doc.HostGamepadNavigation);
#pragma warning restore CS0618
    }

    // ---- what must NOT have changed ----------------------------------------------------------------

    /// <summary>
    /// REGRESSION GUARD. A hat reports two directions at once and the hosts deliver them as two key
    /// presses, which the held-key path pairs into one corner move. Routing a pad's D-pad through
    /// the driver instead — the obvious way to "treat a pad as a pad" — would have lost that
    /// silently: two moves instead of one, visible only on hardware.
    /// </summary>
    [Fact]
    public void Two_hat_directions_held_together_are_still_one_corner_move()
    {
        using var t = Open();
        t.Doc.ReportsKeyUp = true;
        t.Doc.DiagonalNavigation = true;

        t.Doc.DispatchGamepadKey(EditKey.Down, down: true);     // enter at a1
        t.Doc.DispatchGamepadKey(EditKey.Down, down: false);
        t.Layout();
        Assert.Equal("a1", t.FocusedName());

        t.Doc.DispatchGamepadKey(EditKey.Right, down: true);    // …and now the corner, both held
        t.Doc.DispatchGamepadKey(EditKey.Down, down: true);
        t.Layout();

        Assert.Equal("b2", t.FocusedName());                    // ONE move to the corner, not two
    }

    // ---- a capability that is OFF is still SEEN ----------------------------------------------------

    /// <summary>
    /// THE DEFECT, reported against v0.33.0-alpha.7. A capability that is switched off returned
    /// quietly, so the event produced no observation at all — which made "the host never delivered
    /// it" and "the host delivered it and you told me to ignore it" the same silence. That is the
    /// one distinction <c>InputObserved</c> exists to make, so the feature undid itself at exactly
    /// the point it was most needed.
    ///
    /// <para>It bit hardest where the capabilities are most used: an application whose own reader
    /// owns the controller sets <c>None</c>, so EVERY host pad event takes this path and the log it
    /// was reading said nothing whatsoever.</para>
    /// </summary>
    [Fact]
    public void A_pad_event_turned_away_by_policy_is_still_reported()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.None;
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadKey(EditKey.Down, down: true);

        var o = Assert.Single(seen);
        Assert.Equal(InputSource.HostGamepad, o.Source);
        Assert.Equal("Down", o.Input);
        Assert.Equal(InputAction.Swallowed, o.Action);
        Assert.Equal(InputRoute.Ignore, o.Route);   // not acted on, not consumed: still the host's
        Assert.False(o.Handled);
    }

    /// <summary>A release is reported too, or a host checking that its presses arrive in pairs would
    /// see half of them.</summary>
    [Fact]
    public void A_turned_away_release_is_reported_as_a_release()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.None;
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadKey(EditKey.Down, down: false);

        Assert.Equal("Down up", Assert.Single(seen).Input);
    }

    /// <summary>The stick too — and it carries its READING, so the one thing that cannot be checked
    /// without hardware is visible even for an event the engine refused to act on.</summary>
    [Fact]
    public void A_turned_away_stick_is_reported_with_its_reading()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.None;
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadStick(0f, 0.9f);

        var o = Assert.Single(seen);
        Assert.Equal("stick 0.00,0.90", o.Input);
        Assert.Equal(InputAction.Swallowed, o.Action);
        Assert.Equal(InputRoute.Ignore, o.Route);
    }

    /// <summary>
    /// …once per PUSH, not once per sample. A host polls the stick every frame, so a pad held over
    /// with the capability off would file sixty lines a second and bury everything else — and a
    /// diagnostic nobody can read is the failure this whole feature exists to avoid. One report per
    /// push is exactly what an ACCEPTED stick produces; a rejected one must not be noisier than the
    /// thing it stands in for.
    /// </summary>
    [Fact]
    public void A_turned_away_stick_is_reported_once_per_push_not_once_per_sample()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.None;
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadStick(0f, 0.9f);     // a push
        t.Doc.DispatchGamepadStick(0f, 0.9f);     // …still held
        t.Doc.DispatchGamepadStick(0f, 0.88f);    // …still held
        Assert.Single(seen);

        t.Doc.DispatchGamepadStick(0f, 0f);       // back to centre: silent, and re-arms
        Assert.Single(seen);

        t.Doc.DispatchGamepadStick(0f, 0.9f);     // pushed again
        Assert.Equal(2, seen.Count);
    }

    /// <summary>A capability that is ON is unaffected by any of that: the two paths must not start
    /// reporting each other's events.</summary>
    [Fact]
    public void Only_the_capability_that_is_off_reports_a_rejection()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.Dpad;        // directions in, buttons out
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadKey(EditKey.Down, down: true);
        t.Doc.DispatchGamepadKey(EditKey.Enter, down: true);

        Assert.Equal(InputAction.Navigate, seen[0].Action);
        Assert.Equal(InputRoute.Navigate, seen[0].Route);
        Assert.Equal(InputAction.Swallowed, seen[1].Action);
        Assert.Equal(InputRoute.Ignore, seen[1].Route);
    }

    /// <summary>A rejection must not leave its attribution lying around for the NEXT event to pick
    /// up — nothing downstream ran, so nothing consumed it.</summary>
    [Fact]
    public void A_rejection_does_not_leak_its_attribution_into_the_next_event()
    {
        using var t = Open();
        t.Doc.HostGamepadInput = HostGamepad.None;
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadKey(EditKey.Down, down: true);   // turned away
        t.Doc.DispatchKey(null, EditKey.Down);                // a real keyboard press

        Assert.Equal(InputSource.HostGamepad, seen[0].Source);
        Assert.Equal(InputSource.Keyboard, seen[1].Source);
        Assert.Equal("Down", seen[1].Input);                  // not "stick …" or a stale description
    }

    /// <summary>The pad's events are reported as the pad's, with the keyboard's routing no longer
    /// attached to them — the diagnostic and the routing have to tell the same story.</summary>
    [Fact]
    public void A_pad_event_is_observed_as_a_pad_event_with_no_keyboard_route()
    {
        using var t = Open();
        t.Doc.KeyboardNavigation = InputRoute.Consume;
        var seen = new List<InputObservation>();
        t.Doc.InputObserved += seen.Add;

        t.Doc.DispatchGamepadKey(EditKey.Down, down: true);

        var o = Assert.Single(seen);
        Assert.Equal(InputSource.HostGamepad, o.Source);
        Assert.Equal(InputAction.Navigate, o.Action);
        Assert.Equal(InputRoute.Navigate, o.Route);     // not Consume: that is the keyboard's policy
        Assert.Equal("a1", o.Target);
    }
}
