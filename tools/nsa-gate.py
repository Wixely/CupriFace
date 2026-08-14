#!/usr/bin/env python3
"""The NSAccessibility gate: drive the running Viewer as VoiceOver would.

The macOS counterpart of tests/UiaSmoke (FlaUI) and tools/atspi-gate.py (pyatspi): it talks to the
app through the AX client API — the same channel VoiceOver uses — so it can only pass if the bridge
genuinely serves accessibility.

The bar is the same as the other two: an action must be shown to CHANGE THE APP, not merely to be
accepted. And the baseline is known (recorded in .github/workflows/nsa-probe.yml): with no bridge,
the app exposes three unnamed traffic-light buttons, one static text and the system menu bar, and
NOT one of its own controls. Every check below is chosen to be unsatisfiable by that baseline.

Prints PASS/FAIL per check; exit code = number of failures.
"""
import subprocess
import sys
import time

from ApplicationServices import (
    AXUIElementCopyAttributeValue,
    AXUIElementCreateApplication,
    AXUIElementPerformAction,
    AXUIElementSetAttributeValue,
)

FAILURES = 0


def check(name, ok, detail=""):
    global FAILURES
    print(f"{'PASS' if ok else 'FAIL'}  {name}{('  [' + detail + ']') if detail else ''}", flush=True)
    if not ok:
        FAILURES += 1
    return ok


def section(name, fn, *args):
    """A raised exception becomes ONE failure, not a dead gate."""
    global FAILURES
    try:
        fn(*args)
    except Exception as e:                                       # noqa: BLE001
        print(f"FAIL  {name} raised {type(e).__name__}: {e}", flush=True)
        FAILURES += 1


def attr(element, name):
    try:
        err, value = AXUIElementCopyAttributeValue(element, name, None)
        return value if err == 0 else None
    except Exception:                                            # noqa: BLE001
        return None


def walk(element, depth=0, out=None, limit=400):
    if out is None:
        out = []
    if len(out) >= limit:
        return out
    out.append((depth, element))
    for child in (attr(element, "AXChildren") or []):
        walk(child, depth + 1, out, limit)
    return out


def app_element(timeout=60):
    """Our application element, once the Viewer is up."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        pids = subprocess.run(["pgrep", "-x", "Viewer"], capture_output=True, text=True).stdout.split()
        if pids:
            app = AXUIElementCreateApplication(int(pids[0]))
            if attr(app, "AXRole"):
                return app
        time.sleep(1)
    return None


def described(element):
    role = attr(element, "AXRole")
    label = attr(element, "AXTitle") or attr(element, "AXDescription") or ""
    return f"{role} {label!r}".strip()


def find(app, pred, limit=400):
    for _, element in walk(app, limit=limit):
        try:
            if pred(element):
                return element
        except Exception:                                        # noqa: BLE001
            continue
    return None


def named(element):
    return (attr(element, "AXTitle") or attr(element, "AXDescription") or "").strip()


def check_tree(app):
    nodes = walk(app)
    check("the semantics tree is served", len(nodes) >= 15, f"{len(nodes)} AX elements")

    roles = {}
    for _, n in nodes:
        r = attr(n, "AXRole")
        if r:
            roles[r] = roles.get(r, 0) + 1
    print("  roles:", ", ".join(f"{k}={v}" for k, v in sorted(roles.items())), flush=True)

    # The baseline has AXButtons, but all unnamed. A NAMED button can only come from the bridge.
    buttons = [n for _, n in nodes if attr(n, "AXRole") == "AXButton" and named(n)]
    check("named buttons are exposed", len(buttons) >= 3,
          ", ".join(named(b) for b in buttons[:4]) or "none")

    # Roles the window chrome simply does not have.
    for role in ("AXCheckBox", "AXSlider", "AXTextField"):
        check(f"the app's {role} controls are exposed", roles.get(role, 0) >= 1,
              f"{roles.get(role, 0)} found")


def rect(element):
    """An element's AXFrame as (x, y, w, h). It arrives as an AXValue wrapping a CGRect, which
    pyobjc will not unwrap by attribute access — AXValueGetValue is the supported way out."""
    value = attr(element, "AXFrame")
    if value is None:
        return None
    try:
        from ApplicationServices import AXValueGetValue, kAXValueCGRectType
        from Quartz import CGRect
        ok, r = AXValueGetValue(value, kAXValueCGRectType, None)
        if ok:
            return (r.origin.x, r.origin.y, r.size.width, r.size.height)
    except Exception:                                            # noqa: BLE001
        pass
    # Fall back to parsing the description, which prints the rect verbatim.
    import re
    m = re.search(r"x:([-\d.]+)\s+y:([-\d.]+)\s+w:([-\d.]+)\s+h:([-\d.]+)", str(value))
    return tuple(float(g) for g in m.groups()) if m else None


def check_frames(app):
    window = find(app, lambda n: attr(n, "AXRole") == "AXWindow")
    wr = rect(window) if window is not None else None
    if wr:
        print(f"  window frame: x={wr[0]:.0f} y={wr[1]:.0f} w={wr[2]:.0f} h={wr[3]:.0f}", flush=True)

    # By NAME, not by position: "Toggle sidebar" is the first element in ShowcaseApp.html
    # (sidebar > brand-row > collapse-btn), i.e. the top-left of the window. Naming it makes the
    # assumption the flip check rests on auditable instead of implicit.
    button = find(app, lambda n: named(n) == "Toggle sidebar") \
        or find(app, lambda n: attr(n, "AXRole") == "AXButton" and named(n))
    if not check("a named button is addressable", button is not None,
                 named(button) if button else "none"):
        return

    br = rect(button)
    check("it reports real on-screen extents", br is not None and br[2] > 0 and br[3] > 0,
          f"{br[2]:.0f}x{br[3]:.0f} at {br[0]:.0f},{br[1]:.0f}" if br else "no AXFrame")
    if br is None or wr is None:
        return

    # GEOMETRY, not just non-zero numbers.
    #
    # MIND THE COORDINATE SPACES, because they are not the same on both sides of the API. A bridge
    # RETURNS accessibilityFrame in AppKit space (origin bottom-left of the screen); a client READS
    # AXFrame in top-left-origin space, macOS having converted in between. So down here, in client
    # space, a control at the top of the document has a SMALLER y than the window's midpoint.
    #
    # Two assertions, each catching a different real failure.
    #
    # 1. THE ROOT FILLS THE CONTENT AREA. The engine's root node covers the whole viewport, so its
    #    frame must equal the window's content rectangle. This is the check that catches a dropped
    #    logical-units -> points scale exactly: with scale 0.897 the root is 940 points wide, and
    #    unscaled it would claim 1047 — against a 940-wide window.
    #    (Containment over every control is NOT the right test, and was tried: the semantics tree
    #    covers the whole document including everything below the fold, so ~88 controls legitimately
    #    report coordinates outside the window. Reported below as information, not as a failure.)
    root = find(app, lambda n: attr(n, "AXRole") == "AXGroup")
    rr = rect(root) if root is not None else None
    if rr:
        width_ok = abs(rr[2] - wr[2]) <= 4
        # The content area is the window minus its title bar, so a little shorter and never taller.
        height_ok = wr[3] - 44 <= rr[3] <= wr[3] + 1
        check("the root fills the window's content area (units are points, not logical)",
              width_ok and height_ok,
              f"root {rr[2]:.0f}x{rr[3]:.0f} vs window {wr[2]:.0f}x{wr[3]:.0f}")
    else:
        check("the root container is exposed", False, "no AXGroup found")

    def contained(r):
        return (wr[0] - 1 <= r[0] and r[0] + r[2] <= wr[0] + wr[2] + 1 and
                wr[1] - 1 <= r[1] and r[1] + r[3] <= wr[1] + wr[3] + 1)

    outside = sum(1 for _, e in walk(app)
                  if named(e) and attr(e, "AXRole") not in (None, "AXWindow", "AXApplication")
                  and (rect(e) or (0, 0, 0, 0)) and not contained(rect(e) or (0, 0, 0, 0)))
    print(f"  note: {outside} named controls lie outside the window — expected, that is the content "
          f"below the fold (see ROADMAP: off-screen nodes are not yet marked hidden)", flush=True)

    # 2. Y IS FLIPPED. Containment cannot see this — a perfectly mirrored tree is still inside the
    #    window — so it takes a control whose position in the document is known.
    centre_y = br[1] + br[3] / 2
    midpoint = wr[1] + wr[3] / 2
    check("Y is flipped correctly (the first control is in the window's top half)",
          centre_y < midpoint, f"button centre y={centre_y:.0f}, window midpoint y={midpoint:.0f}")


def check_press(app):
    """AXPress must actually change the app. A checkbox is the honest target: its value is
    observable, so 'the action was accepted' and 'the action worked' cannot be confused."""
    box = find(app, lambda n: attr(n, "AXRole") == "AXCheckBox")
    if not check("a checkbox is exposed", box is not None,
                 described(box) if box else "none found"):
        return

    before = attr(box, "AXValue")
    err = AXUIElementPerformAction(box, "AXPress")
    check("AXPress is accepted", err == 0, f"err={err}")

    after, deadline = before, time.time() + 5
    while time.time() < deadline:
        after = attr(box, "AXValue")
        if after != before:
            break
        time.sleep(0.25)
    check("AXPress actually toggles the control", after != before, f"AXValue {before} -> {after}")


def check_value(app):
    slider = find(app, lambda n: attr(n, "AXRole") == "AXSlider")
    if slider is None:
        print("note: no slider on the landing page — Value not exercised", flush=True)
        return

    lo, hi, cur = attr(slider, "AXMinValue"), attr(slider, "AXMaxValue"), attr(slider, "AXValue")
    check("a slider serves a value range", lo is not None and hi is not None and hi > lo,
          f"{cur} in [{lo}, {hi}]")
    if lo is None or hi is None or hi <= lo:
        return

    target = lo + (hi - lo) * 0.75
    tolerance = max(1.0, (hi - lo) * 0.1)
    AXUIElementSetAttributeValue(slider, "AXValue", target)
    now, deadline = cur, time.time() + 5
    while time.time() < deadline:
        now = attr(slider, "AXValue")
        if now is not None and abs(now - target) <= tolerance:
            break
        time.sleep(0.25)
    check("setting AXValue moves the control", now is not None and abs(now - target) <= tolerance,
          f"asked {target:.1f}, got {now}")


def main():
    app = app_element()
    if not check("the app is on the accessibility API", app is not None, "AXUIElement obtained"):
        return FAILURES
    check("it reports the application role", attr(app, "AXRole") == "AXApplication",
          f"role={attr(app, 'AXRole')!r} title={attr(app, 'AXTitle')!r}")

    section("tree", check_tree, app)
    section("frames", check_frames, app)
    section("value", check_value, app)
    section("press", check_press, app)     # last: activating can rebuild the tree
    return FAILURES


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:                                       # noqa: BLE001
        print(f"FAIL  gate crashed: {type(e).__name__}: {e}")
        sys.exit(1)
