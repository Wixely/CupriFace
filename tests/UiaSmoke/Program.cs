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

// Ask the window to testify: every key/focus event either window hands the host goes to this
// file, printed when the gate ends. On a machine reachable only through CI, this is the
// difference between "the keyboard legs failed" and knowing whether the window ever heard a key
// at all. Passed EXPLICITLY on the child's start info — not via this process's environment,
// which is one more inheritance assumption this hunt does not need.
var keyLogPath = Path.Combine(Path.GetTempPath(), $"cupri-keylog-{Environment.ProcessId}.txt");

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

// SendInput delivers to the window with KEYBOARD FOCUS in the FOREGROUND queue, and on a server
// session both links break separately: SetForegroundWindow is denied to background processes
// (the Alt tap makes this thread the last-input owner, which unlocks it), and — the subtler one,
// observed on the CI runner — a window can BE foreground while its thread's focus was never
// established, so injected keys evaporate. The cure for that half is documented too: attach this
// thread's input state to the window's thread and call SetFocus directly. Every step is VERIFIED
// (GetForegroundWindow, then GetFocus through the attachment) and the verdict string goes into
// the failure detail, so a red gate names the broken link instead of shrugging.
static string BringToFront(FlaUI.Core.AutomationElements.Window w, System.Drawing.Point clickAt)
{
    var target = new IntPtr(w.Properties.NativeWindowHandle.Value);

    // First, focus the way a person does: click the window. The runner proved both halves of
    // why this is delicate. Without a real click, SendInput payload keys never reach the SDL
    // window there (SDL routes keys by its own focus bookkeeping, fed by real activation); with
    // a click on the TITLE STRIP, the OS modal window-move loop swallowed the event loop whole —
    // on that virtual display it never exited, and every check after read a frozen window. So
    // the click lands on CONTENT the caller vouches for — the same checkbox the legs measure —
    // twice, so the toggle it flips is flipped straight back. Real activation, no drag region,
    // and delivery of mouse input proven as a side effect.
    FlaUI.Core.Input.Mouse.Click(clickAt);
    Thread.Sleep(120);
    FlaUI.Core.Input.Mouse.Click(clickAt);
    Thread.Sleep(200);

    for (var attempt = 0; attempt < 20 && GetForegroundWindow() != target; attempt++)
    {
        Keyboard.Type(VirtualKeyShort.ALT);
        SetForegroundWindow(target);
        Thread.Sleep(100);
    }
    if (GetForegroundWindow() != target) return "no-foreground";

    var windowThread = GetWindowThreadProcessId(target, IntPtr.Zero);
    var attached = AttachThreadInput(GetCurrentThreadId(), windowThread, true);
    try
    {
        SetFocus(target);
        return GetFocus() == target ? "ok"
             : attached ? "foreground-but-no-focus" : "foreground-but-attach-denied";
    }
    finally
    {
        if (attached) AttachThreadInput(GetCurrentThreadId(), windowThread, false);
    }
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern uint GetCurrentThreadId();
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr SetFocus(IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr GetFocus();

// Typing with SCANCODES, the way a physical keyboard does. FlaUI's Keyboard.Type injects
// virtual-key-only input (scancode field zero) — WinForms and GLFW resolve keys from the VK and
// never notice, but SDL resolves from the scancode bits and hears NOTHING for the key itself
// (its modifier tracking takes another path, which is how "only Ctrl arrived" became the tell).
// A gate that drives an SDL window must speak scancode.
static void TypeScan(params ushort[] scans)
{
    var inputs = new INPUT[scans.Length * 2];
    for (var i = 0; i < scans.Length; i++)
    {
        inputs[i] = ScanInput(scans[i], up: false);                          // downs in order…
        inputs[^(i + 1)] = ScanInput(scans[i], up: true);                    // …ups in reverse
    }
    SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    Thread.Sleep(60);
}

static INPUT ScanInput(ushort scan, bool up) => new()
{
    type = 1, // INPUT_KEYBOARD
    ki = new KEYBDINPUT { wScan = scan, dwFlags = 0x0008 /* SCANCODE */ | (up ? 0x0002u /* KEYUP */ : 0) },
};

const ushort ScanTab = 0x0F, ScanCtrl = 0x1D, ScanEquals = 0x0D, ScanZero = 0x0B;

[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

// The wheel, sent raw. Run 9 proved wheel-UP end to end on the runner (one ladder rung, 46→50 in
// the UIA rect) while four wheel-DOWN attempts moved nothing despite Ctrl arriving each time —
// the down direction had ridden a library call whose negative-amount semantics we never
// verified. This sends MOUSEEVENTF_WHEEL with ±WHEEL_DELTA directly; both directions are the
// same code path with a sign.
static void Wheel(int direction)
{
    var input = new MINPUT { type = 0, mouseData = unchecked((uint)(120 * direction)), dwFlags = 0x0800 };
    SendMouseInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<MINPUT>());
    Thread.Sleep(60);
}

[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")]
static extern uint SendMouseInput(uint nInputs, MINPUT[] pInputs, int cbSize);

// WHICH injection style reaches the window under test — probed with the key CLASS the legs
// actually need? Asked of the definitive witness: the window's own key log (CUPRIFACE_KEY_DEBUG).
// Every softer witness lied in turn across nine runs — foreground [ok], thread focus [ok],
// GetAsyncKeyState registering presses, a WinForms window in this process hearing everything —
// and TWO final twists: sessions differ on which SendInput dialect they deliver (scancode vs
// virtual-key), and the runner's input stack passes exotic keys while eating ordinary ones (F13
// arrived; Tab, =, 0 never did, in either dialect). A canary probing F13 therefore declared a
// keyboard alive that could not type. So the probe IS a Tab — the first key the legs press —
// and its side effect (one focus move) lands before any leg records its baseline. The legs then
// type in a dialect the window demonstrably hears, and skip only when neither delivers.
static (bool Scan, bool Vk) StylesTheWindowHears(string keyLogPath)
{
    int TabLines()
    {
        try { return File.Exists(keyLogPath) ? File.ReadAllLines(keyLogPath).Count(l => l.Contains("Tab")) : 0; }
        catch { return -1; /* mid-write — caller retries */ }
    }
    bool Probe(Action send)
    {
        for (var i = 0; i < 3; i++)
        {
            var before = TabLines();
            if (before < 0) { Thread.Sleep(100); continue; }
            send();
            if (Poll(() => TabLines() > before, 800)) return true;
        }
        return false;
    }
    var scan = Probe(() => TypeScan(ScanTab));
    var vk = Probe(() => Keyboard.Type(VirtualKeyShort.TAB));
    return (scan, vk);
}

var startInfo = new ProcessStartInfo(viewerPath) { UseShellExecute = false };
startInfo.EnvironmentVariables["CUPRIFACE_KEY_DEBUG"] = keyLogPath;
// The gate verifies ONE window deterministically: the SDL software window — what CI's GPU-less
// runner always gets, and what every RDP/VM user gets. Without this, a local machine where GL
// happens to work swaps the window under test mid-hunt (it did; the GL window then surfaced its
// own, separate UIA focus gap — see ROADMAP). The GL window's UIA remains the stated caveat.
startInfo.EnvironmentVariables["CUPRIFACE_SOFTWARE"] = "1";
using var app = FlaUI.Core.Application.Launch(startInfo);
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

    // The one content pixel a focus click can vouch for: the checkbox the legs measure anyway.
    // Its toggle is flipped twice per activation, so state always comes back restored.
    System.Drawing.Point SafeClick()
    {
        var cb = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
        var rc = cb?.BoundingRectangle ?? window.BoundingRectangle;
        return new System.Drawing.Point(rc.Left + rc.Width / 2, rc.Top + rc.Height / 2);
    }

    // The chord-driven legs. Blocking wherever SOME injection style reaches the window under
    // test — its own key log is the witness, per style, because sessions differ on which style
    // they deliver — and the legs then type in a style the window demonstrably hears. Where no
    // style gets through, they SKIP and say so. The wheel-zoom leg never skips.
    BringToFront(window, SafeClick());
    var styles = StylesTheWindowHears(keyLogPath);
    var keysAlive = styles.Scan || styles.Vk;
    void SendKey(VirtualKeyShort vk, ushort scan)
    {
        if (styles.Scan) TypeScan(scan);
        else { Keyboard.Type(vk); Thread.Sleep(60); }
    }
    void SendChord(VirtualKeyShort vk, ushort scan)
    {
        if (styles.Scan) TypeScan(ScanCtrl, scan);
        else { using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Keyboard.Type(vk); Thread.Sleep(60); }
    }
    void KeyboardCheck(string name, Func<(bool Ok, string Detail)> test)
    {
        if (!keysAlive)
            Console.WriteLine($"SKIP  {name}  [no injection style reaches this window in this session - its own log heard neither probe]");
        else Check(name, test);
    }

    KeyboardCheck("Tab moves keyboard focus and UIA sees it", () =>
    {
        // Strict on purpose: the original version asserted "anything has focus after Tab", which
        // an earlier pattern action could satisfy — a keyboard check that never needed the
        // keyboard. This one requires focus to MOVE, so it fails when the keystroke doesn't land.
        var front = BringToFront(window, SafeClick());
        string? FocusedId() => window.FindAllDescendants().FirstOrDefault(e =>
        {
            try { return e.Properties.HasKeyboardFocus.ValueOrDefault; }
            catch { return false; }
        })?.Properties.AutomationId.ValueOrDefault;
        var before = FocusedId();
        SendKey(VirtualKeyShort.TAB, ScanTab);
        var moved = Poll(() => FocusedId() is { } now && now != before);
        return (moved, $"[{front}] focus {before ?? "(none)"} -> {FocusedId() ?? "(none)"}");
    });

    KeyboardCheck("Ctrl+= steps the zoom ladder from the keyboard", () =>
    {
        var box = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
        if (box is null) return (false, "no checkbox to measure");
        var before = box.BoundingRectangle.Width;
        var front = BringToFront(window, SafeClick());
        // A synthetic chord occasionally evaporates even on a healthy desktop (~1 run in 4 here),
        // so the leg does what a person does: press again, bounded. Three lost presses in a row
        // is no longer injection luck — a genuinely broken path fails all three.
        bool PressUntil(VirtualKeyShort vk, ushort scan, Func<bool> done)
        {
            for (var attempt = 0; attempt < 3 && !done(); attempt++)
            {
                SendChord(vk, scan);
                if (Poll(done, 1500)) return true;
            }
            return done();
        }
        var grew = PressUntil(VirtualKeyShort.OEM_PLUS, ScanEquals, () => box.BoundingRectangle.Width > before * 1.05);
        var zoomed = box.BoundingRectangle.Width;
        var restored = PressUntil(VirtualKeyShort.KEY_0, ScanZero, () => Math.Abs(box.BoundingRectangle.Width - before) <= 1);
        return (grew && restored, $"[{front}] width {before:0} -> {zoomed:0} -> {box.BoundingRectangle.Width:0}");
    });

    Check("Ctrl+wheel zooms the page where an AT can see it, and zooms it back", () =>
    {
        // The zoom leg that never skips. Its two ingredients are the inputs proven to deliver in
        // EVERY session this hunt visited: modifier state (the one thing the deaf runs still
        // received) and mouse input (the focus click lands everywhere). Zoom is only real if what
        // assistive tech is TOLD moves with it — the engine scales the semantics tree's bounds,
        // and this reads them back over the wire: a checkbox glyph has a fixed logical size, so
        // one ladder rung (×1.1) must widen its UIA rect by that ratio however content reflows.
        var box = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
        if (box is null) return (false, "no checkbox to measure");
        var before = box.BoundingRectangle.Width;
        if (before <= 0) return (false, "degenerate rect before zoom");

        var front = BringToFront(window, SafeClick());
        var r = window.BoundingRectangle;
        FlaUI.Core.Input.Mouse.Position = new System.Drawing.Point(r.Left + r.Width / 2, r.Top + r.Height / 2);

        bool WheelUntil(int direction, Func<bool> done)
        {
            for (var attempt = 0; attempt < 4 && !done(); attempt++)
            {
                // The modifier rides VK-style — run 8's lesson: scan-style Ctrl never arrived
                // on the runner, SDL's modifier state stayed empty, and the wheel scrolled
                // instead of zooming. VK modifiers have arrived in every session observed.
                using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
                {
                    Thread.Sleep(40);                  // let the modifier state land before the wheel
                    Wheel(direction);
                    Thread.Sleep(40);
                }
                if (Poll(done, 1500)) return true;
            }
            return done();
        }
        var grew = WheelUntil(+1, () => box.BoundingRectangle.Width > before * 1.05);
        var zoomed = box.BoundingRectangle.Width;
        var restored = WheelUntil(-1, () => Math.Abs(box.BoundingRectangle.Width - before) <= 1);

        return (grew && restored,
            $"[{front}] width {before:0} -> {zoomed:0} -> {box.BoundingRectangle.Width:0}");
    });

    return failures;
}
finally
{
    try { app.Kill(); } catch { /* already gone */ }
    try
    {
        Console.WriteLine("--- what the window itself heard (CUPRIFACE_KEY_DEBUG) ---");
        Console.WriteLine(File.Exists(keyLogPath) && new FileInfo(keyLogPath).Length > 0
            ? File.ReadAllText(keyLogPath).TrimEnd()
            : "(nothing - no key or focus event ever reached the SDL window)");
        File.Delete(keyLogPath);
    }
    catch { /* diagnostics never fail the gate */ }
}

// Top-level rule: type declarations only after the last statement — so the interop shapes live here.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct INPUT { public uint type; public KEYBDINPUT ki; public long pad1, pad2; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
// The explicit pad0 is the whole bug run 10 found: on x64 the INPUT union sits at offset 8
// (MOUSEINPUT ends in an 8-aligned pointer), but bare int fields after `type` pack at offset 4 —
// every field lands 4 bytes early and Windows reads a garbage event. The keyboard struct never
// hit this because its nested KEYBDINPUT is itself 8-aligned, which bumps the offset by accident.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct MINPUT { public uint type, pad0; public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
