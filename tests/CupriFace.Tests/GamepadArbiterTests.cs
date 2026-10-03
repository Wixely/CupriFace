using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// One driver per document, and a host that can be told to stand aside.
///
/// <para>The problem these solve was reported from a real integration. A driver holds which direction
/// is currently HELD — that is what turns a continuous axis into discrete moves — so two drivers hold
/// it twice, and a stick seen by both the host and an application's own evdev reader moves the
/// selection twice per push. The application's only recourse was to switch one path off.</para>
/// </summary>
public class GamepadArbiterTests
{
    private const string Grid = """
        <body>
          <div class='row'><div class='b' role='button'>a1</div><div class='b' role='button'>b1</div></div>
          <div class='row'><div class='b' role='button'>a2</div><div class='b' role='button'>b2</div></div>
          <div class='row'><div class='b' role='button'>a3</div><div class='b' role='button'>b3</div></div>
        </body>
        """;
    private const string Css = """
        body { margin:0 }
        .row { display:flex; gap:20px; padding:10px }
        .b { width:160px; height:48px; background:#345; color:#fff; font-size:15px }
        """;

    private static TestDoc Open()
    {
        var t = new TestDoc(Grid, Css, width: 600, height: 400);
        t.Doc.ArrowNavigation = true;
        return t;
    }

    /// <summary>The same driver every time, so the held direction is one fact rather than two.</summary>
    [Fact]
    public void A_document_has_exactly_one_driver()
    {
        using var t = Open();
        Assert.Same(t.Doc.Gamepad, t.Doc.Gamepad);
    }

    /// <summary>
    /// THE DUPLICATE COLLAPSES. Two sources — the host's pad and an application's own reader —
    /// reporting the same push through the shared driver move the selection ONCE, because the second
    /// reports a direction the first already claimed.
    /// </summary>
    [Fact]
    public void Two_sources_reporting_one_push_move_once()
    {
        using var t = Open();
        t.Doc.Gamepad.Press(NavigationDirection.Down);     // entry: a1
        Assert.Equal("a1", t.FocusedName());

        Assert.True(t.Doc.Gamepad.Stick(0f, 0.9f), "the first source moves it");
        Assert.False(t.Doc.Gamepad.Stick(0f, 0.9f), "the second reports what is already held");
        Assert.Equal("a2", t.FocusedName());              // one square, not two
    }

    /// <summary>And with two SEPARATE drivers it does not — which is the bug, stated so that anyone
    /// tempted to hand a host its own driver again can see what it costs.</summary>
    [Fact]
    public void Two_separate_drivers_double_the_move()
    {
        using var t = Open();
        var host = new GamepadDriver(t.Doc);              // what the host used to make
        var app = new GamepadDriver(t.Doc);               // what the application had to make
        host.Press(NavigationDirection.Down);
        Assert.Equal("a1", t.FocusedName());

        host.Stick(0f, 0.9f);
        app.Stick(0f, 0.9f);                              // each holds its own idea of "held"
        Assert.Equal("a3", t.FocusedName());              // two squares for one push
    }

    /// <summary>The shared driver takes its deadzone and corner policy from the document, so every
    /// source agrees about them by construction rather than by convention.</summary>
    [Fact]
    public void The_shared_driver_follows_the_documents_settings()
    {
        using var t = Open();
        t.Doc.GamepadDeadzone = 0.8f;
        t.Doc.Gamepad.Press(NavigationDirection.Down);

        Assert.False(t.Doc.Gamepad.Stick(0f, 0.6f), "0.6 is at rest when the document says 0.8");
        t.Doc.GamepadDeadzone = 0.3f;
        Assert.True(t.Doc.Gamepad.Stick(0f, 0.6f));
    }

    /// <summary>`HostGamepadNavigation` is on unless an app says otherwise — a pad works out of the
    /// box, which is the common case.</summary>
    [Fact]
    public void The_hosts_pad_drives_by_default()
    {
        using var t = Open();
        Assert.True(t.Doc.HostGamepadNavigation);
    }

    /// <summary>Turning it off silences the HOST's pad, not the keyboard. An application with its own
    /// reader is arbitrating controllers; a person at a keyboard is not part of that.</summary>
    [Fact]
    public void Turning_the_host_pad_off_leaves_the_keyboard_alone()
    {
        using var t = Open();
        t.Doc.HostGamepadNavigation = false;

        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());
        t.Key(EditKey.Down);
        Assert.Equal("a2", t.FocusedName());              // arrows still navigate

        // …and the app's own source still drives, because it posts through the document rather than
        // arriving from the host.
        t.Doc.Gamepad.Press(NavigationDirection.Down);
        Assert.Equal("a3", t.FocusedName());
    }

    /// <summary>Connection events reach the application, which previously could only find this out by
    /// reading a diagnostic LOG FILE — unusable for deciding whether to start a fallback reader.</summary>
    [Fact]
    public void A_connected_pad_is_reported_to_the_application()
    {
        using var t = Open();
        var seen = new List<GamepadInfo>();
        t.Doc.GamepadConnected += seen.Add;

        t.Doc.ReportGamepadConnected(new GamepadInfo("Steam Virtual Gamepad", "glfw", Recognised: true));
        t.Doc.ReportGamepadConnected(new GamepadInfo("odd pad", "sdl", Recognised: false));

        Assert.Equal(2, seen.Count);
        Assert.Equal("glfw", seen[0].Backend);
        Assert.True(seen[0].Recognised);
        // Recognised: false is the one worth branching on — it says the axes are a guess, so an app
        // with its own device knowledge should prefer its own reader.
        Assert.False(seen[1].Recognised);
        Assert.Contains("fallback", seen[1].ToString());
    }

    [Fact]
    public void A_disconnected_pad_is_reported_too()
    {
        using var t = Open();
        var gone = 0;
        t.Doc.GamepadDisconnected += _ => gone++;
        t.Doc.ReportGamepadDisconnected(new GamepadInfo("pad", "glfw", true));
        Assert.Equal(1, gone);
    }
}
