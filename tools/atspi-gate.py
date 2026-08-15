#!/usr/bin/env python3
"""The AT-SPI gate: drive the running Viewer as a REAL assistive-technology client would.

This is the Linux counterpart of tests/UiaSmoke (FlaUI): it talks to the app through the same
channel Orca uses, so it can only pass if the bridge genuinely serves the accessibility bus.
Prints PASS/FAIL per check; exit code = number of failures.

The bar is deliberately higher than "the call returned without error". An action must be shown to
CHANGE THE APP, and the app must be shown to TELL the AT it changed — a bridge that accepts
DoAction and silently does nothing passes the first kind of test and is useless in practice.
"""
import sys
import time
import warnings

import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib  # noqa: E402

FAILURES = 0


def check(name, ok, detail=""):
    global FAILURES
    print(f"{'PASS' if ok else 'FAIL'}  {name}{('  [' + detail + ']') if detail else ''}", flush=True)
    if not ok:
        FAILURES += 1
    return ok


def section(name, fn, *args):
    """Run a group of checks; an exception inside becomes ONE failure instead of a dead gate — a
    broken Value interface should still let the working Action interface report for itself."""
    global FAILURES
    try:
        fn(*args)
    except Exception as e:                                       # noqa: BLE001
        print(f"FAIL  {name} raised {type(e).__name__}: {e}", flush=True)
        FAILURES += 1


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


def find(app, pred):
    """A node matching pred, re-walked from the app so it is never a stale handle from before
    some earlier check rebuilt the tree."""
    for _, n in walk(app):
        try:
            if pred(n):
                return n
        except Exception:                                        # noqa: BLE001
            continue
    return None


def pump(action, quit_when, seconds=8):
    """Run the GLib loop (which is what delivers AT-SPI events) while `action` happens.

    NOTE ON THE BINDINGS: AtspiAccessible *implements* Action/Component/Value, so those interface
    methods live directly on the accessible. `Atspi.Action(node)` is not a cast — it tries to
    construct a GInterface and raises TypeError."""
    fired = []

    def on_event(event):
        fired.append(event)
        if quit_when(event):
            Atspi.event_quit()

    listener = Atspi.EventListener.new(on_event)
    for kind in ("object:state-changed", "object:property-change"):
        listener.register(kind)
    GLib.timeout_add(300, lambda: (action(), False)[1])
    GLib.timeout_add(int(seconds * 1000), lambda: (Atspi.event_quit(), False)[1])
    Atspi.event_main()
    for kind in ("object:state-changed", "object:property-change"):
        listener.deregister(kind)
    return fired


def check_slider(app):
    slider = find(app, lambda n: n.get_role_name() == "slider")
    if slider is None:
        print("note: no slider on the landing page — Value interface not exercised", flush=True)
        return

    cur, lo, hi = slider.get_current_value(), slider.get_minimum_value(), slider.get_maximum_value()
    check("a slider serves Value with a range", hi > lo, f"{cur} in [{lo}, {hi}]")

    # Value.Set is the AT's WRITE path, and it lands in the same place a drag does.
    target = lo + (hi - lo) * 0.75
    tolerance = max(1.0, (hi - lo) * 0.1)
    slider.set_current_value(target)
    now, deadline = cur, time.time() + 5
    while time.time() < deadline:
        slider.clear_cache()
        now = slider.get_current_value()
        if abs(now - target) <= tolerance:
            break
        time.sleep(0.25)
    check("Value.Set moves the control", abs(now - target) <= tolerance,
          f"asked {target:.1f}, got {now:.1f}")


def check_toggle(app):
    toggle = find(app, lambda n: n.get_role_name() in ("check box", "toggle button")
                  and n.get_n_actions() >= 1)
    if not check("a checkbox/switch is exposed", toggle is not None,
                 toggle.get_role_name() if toggle else "none found"):
        return

    states = toggle.get_state_set()
    check("it exposes focusable/enabled states",
          states.contains(Atspi.StateType.ENABLED) and states.contains(Atspi.StateType.SENSITIVE),
          "enabled+sensitive")
    before = states.contains(Atspi.StateType.CHECKED)

    events = pump(lambda: toggle.do_action(0),
                  lambda e: e.type.startswith("object:state-changed:checked"))
    kinds = ", ".join(sorted({e.type for e in events})) or "none arrived"
    check("a state change reaches the AT as a signal",
          any(e.type.startswith("object:state-changed:checked") for e in events), kinds)

    toggle.clear_cache()                     # ask the APP, not libatspi's memory of it
    after = toggle.get_state_set().contains(Atspi.StateType.CHECKED)
    check("DoAction actually toggles the control", after != before, f"checked {before} -> {after}")


def check_button(app):
    """Left for last on purpose: activating a real button can rebuild the whole tree, which would
    strand every accessible an earlier check was holding."""
    button = find(app, lambda n: n.get_role_name() == "push button" and (n.get_name() or "").strip())
    if not check("a named button is exposed", button is not None,
                 f"{button.get_name()!r}" if button else "none found"):
        return

    ext = button.get_extents(Atspi.CoordType.SCREEN)
    check("that button reports real extents", ext.width > 0 and ext.height > 0,
          f"{ext.width}x{ext.height} at {ext.x},{ext.y}")
    print("  interfaces:", ", ".join(button.get_interfaces()), flush=True)

    n_actions = button.get_n_actions()
    check("it advertises an action", n_actions >= 1, f"{n_actions} action(s)")
    if n_actions >= 1:
        with warnings.catch_warnings():          # get_action_name is deprecated; it is also the
            warnings.simplefilter("ignore")      # only accessor libatspi offers for this
            named = button.get_action_name(0) or ""
        check("that action is named", named == "click", repr(named))
        check("DoAction is accepted", button.do_action(0) is not False, "invoked")
        # ...and the tree still answers afterwards, which is what proves the bridge survives the
        # rebuild an activation causes (stale ids here would read as "the application no longer
        # exists" — the exact failure the cache signals exist to prevent).
        time.sleep(2)
        check("the tree is still served after an activation", len(walk(app)) >= 15,
              f"{len(walk(app))} accessibles")


def check_offscreen(app):
    """Content below the fold must be marked, or Orca reads the whole document instead of the page.

    AT-SPI's idiom differs from the other two platforms: the node STAYS in the tree (an AT may
    legitimately want all of it) and loses the SHOWING state, keeping VISIBLE. So the assertions are
    that both kinds exist and that they agree with the geometry."""
    nodes = walk(app)
    showing, hidden = [], []
    for _, n in nodes:
        try:
            states = n.get_state_set()
            if not states.contains(Atspi.StateType.VISIBLE):
                continue
            (showing if states.contains(Atspi.StateType.SHOWING) else hidden).append(n)
        except Exception:                                        # noqa: BLE001
            continue

    check("content below the fold is marked not-showing", len(hidden) >= 1,
          f"{len(hidden)} of {len(showing) + len(hidden)} visible nodes are off screen")

    # And the marking has to agree with where things actually are: everything still SHOWING must
    # have real extents inside the window, which is what catches the flag being merely decorative.
    frame = next((n for _, n in nodes if n.get_role_name() == "frame"), None)
    if frame is None:
        print("note: no frame node — extents cross-check skipped", flush=True)
        return
    fe = frame.get_extents(Atspi.CoordType.SCREEN)
    stray = []
    for n in showing:
        try:
            e = n.get_extents(Atspi.CoordType.SCREEN)
            if e.width <= 0 or e.height <= 0:
                continue
            if e.x + e.width <= fe.x or e.x >= fe.x + fe.width or \
               e.y + e.height <= fe.y or e.y >= fe.y + fe.height:
                stray.append(f"{n.get_role_name()} {n.get_name()!r} at {e.x},{e.y}")
        except Exception:                                        # noqa: BLE001
            continue
    check("everything still showing is really on screen", not stray,
          f"{len(stray)} showing but outside the frame"
          + (": " + "; ".join(stray[:3]) if stray else ""))


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

    section("offscreen", check_offscreen, app)
    section("slider", check_slider, app)
    section("checkbox", check_toggle, app)
    section("button", check_button, app)
    return FAILURES


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:                                       # noqa: BLE001
        print(f"FAIL  gate crashed: {type(e).__name__}: {e}")
        sys.exit(1)
