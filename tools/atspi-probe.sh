#!/usr/bin/env bash
# Feasibility probe for the Linux accessibility bridge — see .github/workflows/atspi-probe.yml.
# Runs INSIDE a `dbus-run-session` (a hosted runner has no session bus of its own) and reports
# what an AT-SPI stack does headlessly. Reports; never asserts — the point is to learn.
set -x

echo "session bus: ${DBUS_SESSION_BUS_ADDRESS:-(none)}"

launcher=$(command -v at-spi-bus-launcher \
        || ls /usr/libexec/at-spi-bus-launcher 2>/dev/null \
        || ls /usr/lib/at-spi2-core/at-spi-bus-launcher 2>/dev/null)
registry=$(command -v at-spi2-registryd \
        || ls /usr/libexec/at-spi2-registryd 2>/dev/null \
        || ls /usr/lib/at-spi2-core/at-spi2-registryd 2>/dev/null)
echo "launcher: ${launcher:-NOT FOUND}"
echo "registry: ${registry:-NOT FOUND}"

[ -n "$launcher" ] && ("$launcher" --launch-immediately &)
sleep 3
[ -n "$registry" ] && ("$registry" &)
sleep 2

echo "--- names on the session bus ---"
busctl --user list --no-pager 2>&1 | grep -i -E 'a11y|atspi' || echo "NO a11y/atspi NAME"

# The exact discovery call the bridge will make at startup.
echo "--- org.a11y.Bus.GetAddress ---"
busctl --user call org.a11y.Bus /org/a11y/bus org.a11y.Bus GetAddress 2>&1 || echo "GetAddress FAILED"

# Transport matters: an abstract socket ("unix:abstract=") needs a leading NUL byte in the
# sockaddr, a path socket ("unix:path=") does not. The connect code differs.
echo "--- names on the A11Y bus itself ---"
A11Y=$(busctl --user call org.a11y.Bus /org/a11y/bus org.a11y.Bus GetAddress 2>/dev/null | sed 's/^s //; s/"//g')
echo "a11y address: ${A11Y:-(none)}"
if [ -n "$A11Y" ]; then
  DBUS_SESSION_BUS_ADDRESS="$A11Y" busctl --user list --no-pager 2>&1 | head -20 || true
fi
