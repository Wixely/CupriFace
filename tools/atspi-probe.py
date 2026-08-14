#!/usr/bin/env python3
"""Can a REAL assistive-technology client see anything? This is the pyatspi half of the probe:
the Linux counterpart of the FlaUI gate that made the Windows UIA bridge trustworthy. If a
client can enumerate the desktop here, a bridge can be gated the same way."""
import sys

try:
    import gi
    gi.require_version("Atspi", "2.0")
    from gi.repository import Atspi
except Exception as e:                                   # noqa: BLE001 - reporting, not asserting
    print(f"ATSPI CLIENT UNAVAILABLE: {type(e).__name__}: {e}")
    sys.exit(0)

print("Atspi client imported OK")
try:
    desktop = Atspi.get_desktop(0)
    n = desktop.get_child_count()
    print(f"desktop: name={desktop.get_name()!r} children={n}")
    for i in range(n):
        child = desktop.get_child_at_index(i)
        print(f"  app[{i}]: name={child.get_name()!r} role={child.get_role_name()!r}")
    print("ENUMERATION OK" if n >= 0 else "ENUMERATION ODD")
except Exception as e:                                   # noqa: BLE001
    print(f"ENUMERATION FAILED: {type(e).__name__}: {e}")
