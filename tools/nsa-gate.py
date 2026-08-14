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


def check_frames(app):
    button = find(app, lambda n: attr(n, "AXRole") == "AXButton" and named(n))
    if not check("a named button is addressable", button is not None,
                 named(button) if button else "none"):
        return
    frame = attr(button, "AXFrame")
    # AXFrame comes back as an NSValue-wrapped CGRect; pyobjc renders it as a struct-like object.
    ok = frame is not None
    size = ""
    if ok:
        try:
            size = f"{frame.size.width:.0f}x{frame.size.height:.0f} at {frame.origin.x:.0f},{frame.origin.y:.0f}"
            ok = frame.size.width > 0 and frame.size.height > 0
        except Exception:                                        # noqa: BLE001
            size = str(frame)
    check("it reports real on-screen extents", ok, size or "no AXFrame")


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
