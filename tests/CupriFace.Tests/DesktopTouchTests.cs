using System.Text.RegularExpressions;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Desktop touch (#143): taps did nothing in a desktop build because neither window delivered them.
///
/// <para>The diagnosis was settled by running a probe on the reporting Steam Deck rather than by
/// reading the source, and the numbers below come from that session. X11 emulates a core pointer
/// from touch, so the GLFW window appears to work in a desktop session; Wayland does not, so the
/// same build is completely inert in Game Mode. GLFW exposes no touch API at all, which is why the
/// fix lives in the SDL window.</para>
///
/// <para>Split in two on purpose. The engine half — several pointers at once, captured
/// independently — is real behaviour and is executed. The host half needs an SDL window, a
/// compositor and a finger, so it is checked against the source in the manner of
/// <see cref="CursorTableTests"/>, and each assertion corresponds to something the probe MEASURED
/// rather than to something that merely seemed sensible.</para>
/// </summary>
public class DesktopTouchTests(ITestOutputHelper output)
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Shell(string file) =>
        File.ReadAllText(Path.Combine(Root(), "src", "CupriFace.Shell", file));

    // ---- the engine half, actually executed ----------------------------------------------------

    private const string Html = """
        <body>
          <div class="pad" data-grab="left">LEFT</div>
          <div class="pad" data-grab="right">RIGHT</div>
        </body>
        """;

    private const string Css = """
        /* Flex, not inline-block: the pads must sit SIDE BY SIDE for two fingers to be on two
           different elements, and inline-block stacked them vertically here — which the first
           version of this test asserted its way past until DumpTree showed 0,0 and 0,200. */
        body { font-family:sans-serif; margin:0; display:flex; }
        .pad { width:200px; height:200px; }
        """;

    /// <summary>
    /// Two fingers, two elements, at the same time — the thing a hardcoded pointer id makes
    /// impossible. The probe recorded exactly this on the Deck (fingers 1 and 2 with independent
    /// move streams), so it is the case the host has to be able to represent.
    /// </summary>
    [Fact]
    public void Two_pointers_are_captured_independently()
    {
        var seen = new List<string>();
        using var doc = CupriDocument.Load(Html, Css);
        doc.OnPointer("data-grab", e =>
        {
            seen.Add($"{e.Id}:{e.Phase}:{e.Element?.GetAttribute("data-grab")}");
            return true;
        });
        doc.Refresh();
        using (doc.RenderToImage(400, 200)) { }

        Assert.True(doc.DispatchPointer(1, PointerPhase.Down, 100, 100));   // left pad
        Assert.True(doc.DispatchPointer(2, PointerPhase.Down, 300, 100));   // right pad

        Assert.True(doc.IsPointerCaptured(1));
        Assert.True(doc.IsPointerCaptured(2));
        // The mouse's id must be untouched by either of them.
        Assert.False(doc.IsPointerCaptured(0));

        // Each stays with the element it went down on, even while the other is still held.
        doc.DispatchPointer(1, PointerPhase.Move, 120, 130);
        doc.DispatchPointer(2, PointerPhase.Move, 320, 130);
        doc.DispatchPointer(1, PointerPhase.Up, 120, 130);

        Assert.False(doc.IsPointerCaptured(1));
        Assert.True(doc.IsPointerCaptured(2));      // the other finger is still down

        output.WriteLine(string.Join("\n", seen));
        Assert.Contains(seen, s => s.StartsWith("1:Down:left", StringComparison.Ordinal));
        Assert.Contains(seen, s => s.StartsWith("2:Down:right", StringComparison.Ordinal));
        Assert.Contains(seen, s => s.StartsWith("1:Move:left", StringComparison.Ordinal));
        Assert.Contains(seen, s => s.StartsWith("2:Move:right", StringComparison.Ordinal));
    }

    /// <summary>A finger that leaves the window reports normalised coordinates outside 0..1 — the
    /// Deck produced <c>-0.098</c> during a pinch. The engine must take them: a captured drag has to
    /// keep tracking past the edge, exactly as a mouse dragged out of the window does. Clamping
    /// would look tidier and would silently pin every such drag to the border.</summary>
    [Fact]
    public void A_pointer_dragged_outside_the_window_keeps_its_capture()
    {
        using var doc = CupriDocument.Load(Html, Css);
        doc.OnPointer("data-grab", _ => true);
        doc.Refresh();
        using (doc.RenderToImage(400, 200)) { }

        Assert.True(doc.DispatchPointer(1, PointerPhase.Down, 100, 100));
        doc.DispatchPointer(1, PointerPhase.Move, -59, -10);     // off the top-left, as measured

        Assert.True(doc.IsPointerCaptured(1));
        doc.DispatchPointer(1, PointerPhase.Up, -59, -10);
        Assert.False(doc.IsPointerCaptured(1));
    }

    // ---- the host half, against the source -----------------------------------------------------

    /// <summary>
    /// Every tap arrives TWICE: SDL manufactures a mouse event from it as well as the finger events.
    /// Measured, not assumed — 131 of 166 mouse events in one Deck session carried
    /// <c>SDL_TOUCH_MOUSEID</c>, and the probe double-counted every tap until they were dropped.
    /// Without this filter a tap is two presses.
    /// </summary>
    [Fact]
    public void Mouse_events_synthesised_from_touch_are_dropped()
    {
        var src = Shell("SdlSoftwareWindow.cs");

        Assert.Contains("private const uint TouchMouseId = 0xFFFFFFFFu;", src, StringComparison.Ordinal);
        // All three, or the half that slips through desynchronises press from release.
        Assert.Contains("EventType.Mousebuttondown when e.Button.Which == TouchMouseId", src, StringComparison.Ordinal);
        Assert.Contains("EventType.Mousebuttonup when e.Button.Which == TouchMouseId", src, StringComparison.Ordinal);
        Assert.Contains("EventType.Mousemotion when e.Motion.Which == TouchMouseId", src, StringComparison.Ordinal);
    }

    /// <summary>The SDL window handles all three finger events. A missing Up leaves a pointer
    /// captured for ever.</summary>
    [Theory]
    [InlineData("EventType.Fingerdown")]
    [InlineData("EventType.Fingermotion")]
    [InlineData("EventType.Fingerup")]
    public void The_sdl_window_handles_every_finger_event(string evt)
    {
        Assert.Contains(evt, Shell("SdlSoftwareWindow.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Pointer ids for fingers start at 1 and are mapped, never cast.
    ///
    /// <para>Two reasons, both from the probe: pointer 0 is the mouse's for ever, so a finger using
    /// it would make the two fight over capture; and the Deck's finger ids are longs that climb for
    /// the session (354, 355, 356 …), so casting one into the engine's small-integer pointer id is
    /// a collision waiting to happen.</para>
    /// </summary>
    [Fact]
    public void Finger_pointer_ids_never_collide_with_the_mouse()
    {
        var src = Shell("SdlSoftwareWindow.cs");

        Assert.Contains("_nextFingerPointer = 1", src, StringComparison.Ordinal);
        Assert.Contains("Dictionary<long, int> _fingerPointers", src, StringComparison.Ordinal);
        // Released on the way up, or a long session leaks an entry per touch.
        Assert.Contains("_fingerPointers.Remove", src, StringComparison.Ordinal);
    }

    /// <summary>SDL reports fingers normalised to 0..1, so they must be scaled by the window size
    /// before the device scale is divided out — the same space the mouse already arrives in. Skip it
    /// and every tap lands in the top-left corner; use the wrong size and it lands short by exactly
    /// the scale factor, which only shows up on a scaled display.</summary>
    [Fact]
    public void Normalised_finger_coordinates_are_converted_to_logical_client_units()
    {
        var src = Shell("SdlSoftwareWindow.cs");
        var body = src[src.IndexOf("private void DispatchFinger", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    /// <summary>SDL's pointer coordinates", StringComparison.Ordinal)];

        Assert.Contains("ToLogicalClient(f.X * _width, f.Y * _height)", body, StringComparison.Ordinal);
        // And NOT clamped — see the drag test above for why.
        Assert.DoesNotContain("Math.Clamp", body);
        output.WriteLine("finger -> window px -> logical client, unclamped");
    }

    /// <summary>
    /// The GL window is left alone, and that is a decision rather than an oversight: GLFW exposes no
    /// touch API at all, so there is nothing to wire there. Anyone wondering why a Steam Deck with
    /// working GL still cannot be tapped is looking at this: the GL window is the one it picks.
    /// </summary>
    [Fact]
    public void Only_the_sdl_path_wires_touch()
    {
        var host = Shell("DesktopHost.cs");
        Assert.Equal(1, Regex.Matches(host, @"window\.TouchPointer \+=").Count);
        Assert.DoesNotContain("TouchPointer", Shell("SkiaWindow.cs"));

        // …and the shared helpers must carry the id rather than the old hardcoded 0, or two fingers
        // become one pointer and capture is handed back and forth between them.
        Assert.Contains("doc.IsPointerCaptured(pointerId)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("doc.IsPointerCaptured(0)", host);
    }
}
