#!/usr/bin/env python3
"""Can a hosted macOS runner drive a REAL accessibility client?

The AT-SPI bridge is only credible because a genuine AT client (pyatspi) drives it in CI. macOS
has a harder version of that question: reading another application's accessibility tree requires
the "Accessibility" privacy permission, and privacy permissions are exactly what CI machines are
built to withhold. If that permission cannot be obtained here, an NSAccessibility bridge could be
written but never proven — and this project's standard is that an unproven bridge is not done.

So this asks, in order:
  1. Is this process trusted for accessibility right now?
  2. If not, can it be granted non-interactively?
  3. With whatever trust we have, can we see a running app's AX tree at all?
  4. What does the Viewer look like TODAY, before any bridge exists? (the baseline the gate would
     have to beat — the macOS counterpart of AT-SPI's "desktop children=0")

Reports everything, asserts nothing.
"""
import subprocess
import sys
import time

try:
    from ApplicationServices import (
        AXIsProcessTrusted,
        AXUIElementCopyAttributeValue,
        AXUIElementCreateApplication,
    )
except ImportError as e:                                         # noqa: BLE001
    print(f"pyobjc unavailable: {e}")
    sys.exit(0)


def note(label, value):
    print(f"  {label}: {value}", flush=True)


def attr(element, name):
    """One AX attribute, or None. The API returns (error, value)."""
    try:
        err, value = AXUIElementCopyAttributeValue(element, name, None)
        return value if err == 0 else None
    except Exception as e:                                       # noqa: BLE001
        return f"<raised {type(e).__name__}: {e}>"


def dump(element, depth=0, limit=40, seen=None):
    """Walk what the AX API exposes, bounded."""
    if seen is None:
        seen = [0]
    if seen[0] >= limit:
        return
    seen[0] += 1
    role = attr(element, "AXRole")
    title = attr(element, "AXTitle")
    desc = attr(element, "AXDescription")
    label = " ".join(str(x) for x in (role, title, desc) if x)
    print(f"    {'  ' * depth}{label or '(no role)'}", flush=True)
    children = attr(element, "AXChildren") or []
    for child in children:
        dump(child, depth + 1, limit, seen)


def main():
    print("--- 1. is this process trusted for accessibility? ---", flush=True)
    trusted = bool(AXIsProcessTrusted())
    note("AXIsProcessTrusted()", trusted)

    if not trusted:
        print("--- 2. can trust be granted non-interactively? ---", flush=True)
        # The Accessibility permission lives in the SYSTEM TCC database, which SIP protects on a
        # stock machine. Whether a hosted runner allows this is precisely the unknown.
        db = "/Library/Application Support/com.apple.TCC/TCC.db"
        for who in ("/usr/bin/python3", sys.executable):
            sql = ("INSERT OR REPLACE INTO access "
                   "VALUES('kTCCServiceAccessibility','" + who + "',1,2,4,1,NULL,NULL,NULL,"
                   "'UNUSED',NULL,0,CAST(strftime('%s','now') AS INTEGER));")
            r = subprocess.run(["sudo", "sqlite3", db, sql], capture_output=True, text=True)
            note(f"tcc insert for {who}", f"rc={r.returncode} {r.stderr.strip()[:120]}")
        trusted = bool(AXIsProcessTrusted())
        note("AXIsProcessTrusted() after grant attempt", trusted)

    print("--- 3. can we read ANY app's AX tree? ---", flush=True)
    # Finder is always running and is not ours, so it is an honest test of cross-process access.
    pids = subprocess.run(["pgrep", "-x", "Finder"], capture_output=True, text=True).stdout.split()
    if pids:
        finder = AXUIElementCreateApplication(int(pids[0]))
        role = attr(finder, "AXRole")
        note("Finder AXRole", role)
        note("verdict", "cross-process AX reads WORK" if role else "blocked (no permission)")
    else:
        note("Finder", "not running")

    print("--- 4. what does the Viewer expose today, with no bridge? ---", flush=True)
    pids = subprocess.run(["pgrep", "-x", "Viewer"], capture_output=True, text=True).stdout.split()
    if not pids:
        note("Viewer", "not running — cannot sample the baseline")
        return 0
    app = AXUIElementCreateApplication(int(pids[0]))
    for name in ("AXRole", "AXTitle", "AXWindows", "AXChildren"):
        note(name, attr(app, name))
    print("  tree:", flush=True)
    dump(app)
    return 0


if __name__ == "__main__":
    time.sleep(1)
    sys.exit(main())
