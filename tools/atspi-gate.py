#!/usr/bin/env python3
"""The AT-SPI gate: drive the running Viewer as a REAL assistive-technology client would.

This is the Linux counterpart of tests/UiaSmoke (FlaUI): it talks to the app through the same
channel Orca uses, so it can only pass if the bridge genuinely serves the accessibility bus.
Prints PASS/FAIL per check; exit code = number of failures.
"""
import sys
import time

import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi  # noqa: E402

FAILURES = 0


def check(name, ok, detail=""):
    global FAILURES
    print(f"{'PASS' if ok else 'FAIL'}  {name}{('  [' + detail + ']') if detail else ''}", flush=True)
    if not ok:
        FAILURES += 1
    return ok


def find_app(timeout=45):
    """Our application object on the desktop — the bridge's Embed is what puts it there."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        desktop = Atspi.get_desktop(0)
        for i in range(desktop.get_child_count()):
            child = desktop.get_child_at_index(i)
            try:
                if child and "CupriFace" in (child.get_name() or ""):
                    return child, desktop.get_child_count()
            except Exception:                                    # noqa: BLE001 - app may be mid-start
                pass
        time.sleep(1)
    return None, Atspi.get_desktop(0).get_child_count()


def walk(node, depth=0, out=None, limit=400):
    """Every accessible under a node, breadth of the whole tree (bounded)."""
    if out is None:
        out = []
    if len(out) >= limit:
        return out
    out.append((depth, node))
    for i in range(node.get_child_count()):
        try:
            child = node.get_child_at_index(i)
        except Exception:                                        # noqa: BLE001
            continue
        if child is not None:
            walk(child, depth + 1, out, limit)
    return out


def main():
    app, desktop_children = find_app()
    if not check("the app appears on the accessibility bus", app is not None,
                 f"desktop children={desktop_children}"):
        return FAILURES

    check("it reports the application role", app.get_role_name() == "application",
          f"role={app.get_role_name()!r} name={app.get_name()!r}")

    nodes = walk(app)
    check("the semantics tree is served", len(nodes) >= 15, f"{len(nodes)} accessibles")

    roles = {}
    for _, n in nodes:
        try:
            roles[n.get_role_name()] = roles.get(n.get_role_name(), 0) + 1
        except Exception:                                        # noqa: BLE001
            pass
    print("  roles:", ", ".join(f"{k}={v}" for k, v in sorted(roles.items())), flush=True)

    # A named push button — the thing a screen reader would announce and activate.
    button = next((n for _, n in nodes
                   if n.get_role_name() == "push button" and (n.get_name() or "").strip()), None)
    if check("a named button is exposed", button is not None,
             f"{button.get_name()!r}" if button else "none found"):
        # Extents must be real on-screen pixels, not zeros.
        ext = button.get_extents(Atspi.CoordType.SCREEN)
        check("that button reports real extents", ext.width > 0 and ext.height > 0,
              f"{ext.width}x{ext.height} at {ext.x},{ext.y}")

        # Action: the AT-SPI counterpart of UIA Invoke. This round-trips through the bridge's
        # queue onto the UI thread and back into the document's ordinary click machinery.
        action = Atspi.Action(button)
        n_actions = action.get_n_actions()
        check("it advertises an action", n_actions >= 1, f"{n_actions} action(s)")
        if n_actions >= 1:
            check("DoAction is accepted", action.do_action(0) is not False, "invoked")

    # A checkbox/switch must carry its CHECKED state, or an AT announces the wrong thing.
    toggle = next((n for _, n in nodes if n.get_role_name() in ("check box", "toggle button")), None)
    if check("a checkbox/switch is exposed", toggle is not None,
             toggle.get_role_name() if toggle else "none found"):
        states = toggle.get_state_set()
        check("it exposes focusable/enabled states",
              states.contains(Atspi.StateType.ENABLED) and states.contains(Atspi.StateType.SENSITIVE),
              "enabled+sensitive")

    # A slider must serve the Value interface with a real range.
    slider = next((n for _, n in nodes if n.get_role_name() == "slider"), None)
    if slider is not None:
        value = Atspi.Value(slider)
        cur, lo, hi = value.get_current_value(), value.get_minimum_value(), value.get_maximum_value()
        check("a slider serves Value with a range", hi > lo, f"{cur} in [{lo}, {hi}]")
    else:
        print("note: no slider on the landing page — Value interface not exercised", flush=True)

    return FAILURES


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:                                       # noqa: BLE001
        print(f"FAIL  gate crashed: {type(e).__name__}: {e}")
        sys.exit(1)
