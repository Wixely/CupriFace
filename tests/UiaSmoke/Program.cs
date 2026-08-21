using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

// The UIA gate. Launches the Viewer given as argv[0] and interrogates it over UI Automation —
// the exact channel a screen reader uses. Every check polls, because AT actions round-trip
// through the bridge's action queue and land a frame later. Exit code = number of failures.

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: UiaSmoke <path-to-Viewer.exe>");
    return 2;
}
// CreateProcess wants a real Windows path; forward-slash relative paths reach it verbatim.
var viewerPath = Path.GetFullPath(args[0]);

var failures = 0;
void Check(string name, Func<(bool Ok, string Detail)> test)
{
    try
    {
        var (ok, detail) = test();
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
        if (!ok) failures++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}  [{ex.GetType().Name}: {ex.Message}]");
        failures++;
    }
}

static bool Poll(Func<bool> condition, int timeoutMs = 5000)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (condition()) return true;
        Thread.Sleep(100);
    }
    return condition();
}

// SendInput delivers to the FOREGROUND window, and on Windows Server SetForegroundWindow is
// routinely denied to a background process — the keys then land in the console, silently, and a
// keyboard check "fails" while the code under test never saw a key. So: take foreground the
// documented way (a synthetic Alt makes this thread the last-input owner, which unlocks the
// call), and VERIFY with GetForegroundWindow rather than trusting the attempt. Callers put the
// verdict in the failure detail, so a red gate says which side of the keyboard was broken.
static bool BringToFront(FlaUI.Core.AutomationElements.Window w)
{
    var target = new IntPtr(w.Properties.NativeWindowHandle.Value);
    for (var attempt = 0; attempt < 20; attempt++)
    {
        if (GetForegroundWindow() == target) return true;
        Keyboard.Type(VirtualKeyShort.ALT);
        SetForegroundWindow(target);
        Thread.Sleep(100);
    }
    return GetForegroundWindow() == target;
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);

using var app = FlaUI.Core.Application.Launch(viewerPath);
try
{
    using var automation = new UIA3Automation();

    // First launch of a single-file build unpacks the bundle before the window exists.
    var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(60));
    Console.WriteLine($"window: \"{window.Title}\"");

    Check("the semantics tree is served over UIA", () =>
    {
        AutomationElement[] all = [];
        Poll(() => (all = window.FindAllDescendants()).Length >= 15, 15000);
        var cupri = all.Count(e =>
        {
            try { return e.Properties.FrameworkId.ValueOrDefault == "CupriFace"; }
            catch { return false; }
        });
        return (cupri >= 15, $"{all.Length} elements, {cupri} from CupriFace");
    });

    // Content below the fold must say so. Narrator uses IsOffscreen to skip a control; a bridge that
    // reports every node as on screen makes the user walk the whole document to reach the page.
    // Both halves matter: that something IS marked, and that the marking agrees with the geometry.
    Check("content below the fold reports IsOffscreen", () =>
    {
        var all = window.FindAllDescendants();
        var offscreen = 0;
        var lying = new List<string>();
        var bounds = window.BoundingRectangle;
        foreach (var e in all)
        {
            try
            {
                if (e.Properties.FrameworkId.ValueOrDefault != "CupriFace") continue;
                if (e.Properties.IsOffscreen.ValueOrDefault) { offscreen++; continue; }
                var r = e.BoundingRectangle;
                if (r.Width > 0 && r.Height > 0 && !bounds.IntersectsWith(r))
                    lying.Add($"{e.Properties.Name.ValueOrDefault} at {r.X},{r.Y}");
            }
            catch { /* an element that vanished mid-walk is not a finding */ }
        }
        return (offscreen >= 1 && lying.Count == 0,
            $"{offscreen} offscreen; {lying.Count} claim on-screen but sit outside the window"
            + (lying.Count > 0 ? ": " + string.Join("; ", lying.Take(3)) : ""));
    });

    Check("a named button advertises Invoke", () =>
    {
        var button = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => !string.IsNullOrEmpty(b.Name) && b.Patterns.Invoke.IsSupported);
        return (button is not null, button is null ? "no button found" : $"\"{button.Name}\"");
    });

    Check("Toggle flips a named checkbox and reports the new state", () =>
    {
        var box = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
        if (box is null) return (false, "no checkbox/switch on the landing page");
        var name = box.Properties.Name.ValueOrDefault;
        var toggle = box.Patterns.Toggle.Pattern;
        var before = toggle.ToggleState.Value;
        toggle.Toggle();
        var flipped = Poll(() => toggle.ToggleState.Value != before);
        // The name matters as much as the flip: an unnamed checkbox is "checkbox" to Narrator.
        return (flipped && !string.IsNullOrEmpty(name),
            $"\"{name ?? "(unnamed)"}\" {before} -> {toggle.ToggleState.Value}");
    });

    Check("RangeValue reads and writes a slider through its binding", () =>
    {
        var slider = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Slider));
        if (slider is null) return (false, "no slider on the landing page");
        var range = slider.Patterns.RangeValue.Pattern;
        var (min, max) = (range.Minimum.Value, range.Maximum.Value);
        if (max <= min) return (false, $"degenerate range [{min}..{max}]");
        var v = range.Value.Value;
        if (v < min || v > max) return (false, $"value {v} outside [{min}..{max}]");
        var target = Math.Round(min + (max - min) / 2) == v ? min + 1 : Math.Round(min + (max - min) / 2);
        range.SetValue(target);
        var applied = Poll(() => Math.Abs(range.Value.Value - target) < 0.5);
        return (applied, $"[{min}..{max}] {v} -> {range.Value.Value} (asked {target})");
    });

    Check("Tab moves keyboard focus and UIA sees it", () =>
    {
        // Strict on purpose: the original version asserted "anything has focus after Tab", which
        // an earlier pattern action could satisfy — a keyboard check that never needed the
        // keyboard. This one requires focus to MOVE, so it fails when the keystroke doesn't land.
        if (!BringToFront(window)) return (false, "could not take foreground - keys would go elsewhere");
        string? FocusedId() => window.FindAllDescendants().FirstOrDefault(e =>
        {
            try { return e.Properties.HasKeyboardFocus.ValueOrDefault; }
            catch { return false; }
        })?.Properties.AutomationId.ValueOrDefault;
        var before = FocusedId();
        Keyboard.Type(VirtualKeyShort.TAB);
        var moved = Poll(() => FocusedId() is { } now && now != before);
        return (moved, $"focus {before ?? "(none)"} -> {FocusedId() ?? "(none)"}");
    });

    Check("Ctrl+= zooms the page where an AT can see it, and Ctrl+0 undoes it", () =>
    {
        // Zoom is only real if what assistive tech is TOLD moves with it: the engine scales the
        // semantics tree's bounds, and this reads them back over the wire. A checkbox glyph has a
        // fixed logical size, so one ladder step (×1.1) must widen its UIA rect by that ratio no
        // matter how the surrounding content reflows.
        var box = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
        if (box is null) return (false, "no checkbox to measure");
        var before = box.BoundingRectangle.Width;
        if (before <= 0) return (false, "degenerate rect before zoom");

        if (!BringToFront(window)) return (false, "could not take foreground - keys would go elsewhere");
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(VirtualKeyShort.OEM_PLUS);
        var grew = Poll(() => box.BoundingRectangle.Width > before * 1.05);
        var zoomed = box.BoundingRectangle.Width;

        using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(VirtualKeyShort.KEY_0);
        var restored = Poll(() => Math.Abs(box.BoundingRectangle.Width - before) <= 1);

        return (grew && restored,
            $"width {before:0} -> {zoomed:0} -> {box.BoundingRectangle.Width:0}");
    });

    return failures;
}
finally
{
    try { app.Kill(); } catch { /* already gone */ }
}
