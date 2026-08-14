#!/usr/bin/env bash
# Stand up a headless accessibility stack, run the Viewer inside it, and drive that Viewer with
# a real AT client (tools/atspi-gate.py). The Linux counterpart of the FlaUI gate.
#
#   usage: atspi-gate.sh <path-to-Viewer>
#
# Runs INSIDE `dbus-run-session` (the runner has no session bus) and under xvfb (SDL needs a
# display even for the software window). Exit code = the client's failure count.
set -euo pipefail

VIEWER=${1:?usage: atspi-gate.sh <path-to-Viewer>}
HERE=$(cd "$(dirname "$0")" && pwd)

launcher=$(command -v at-spi-bus-launcher || ls /usr/libexec/at-spi-bus-launcher)
registry=$(command -v at-spi2-registryd || ls /usr/libexec/at-spi2-registryd)
"$launcher" --launch-immediately &
sleep 2
"$registry" &
sleep 2
echo "a11y bus: $(busctl --user call org.a11y.Bus /org/a11y/bus org.a11y.Bus GetAddress 2>&1 || echo UNAVAILABLE)"

# The software window: this box has no GPU, and it is also the path every headless Linux
# machine takes, so it is the honest one to verify.
export CUPRIFACE_SOFTWARE=1
export CUPRIFACE_ATSPI_DEBUG=1
chmod +x "$VIEWER"
xvfb-run -a stdbuf -oL -eL "$VIEWER" > viewer.log 2>&1 &
VIEWER_PID=$!
trap 'kill $VIEWER_PID 2>/dev/null || true' EXIT

# First launch of a single-file build unpacks ~128 MB before the window exists.
sleep 25
if ! kill -0 $VIEWER_PID 2>/dev/null; then
  echo "::error::the Viewer exited before the gate could talk to it"
  cat viewer.log
  exit 1
fi

# Ground truth BEFORE libatspi's interpretation of it: ask the app's own cache object directly.
# If this shows objects and the client still sees none, the fault is in what we say, not whether
# we say it.
A11Y=$(busctl --user call org.a11y.Bus /org/a11y/bus org.a11y.Bus GetAddress 2>/dev/null | sed 's/^s //; s/"//g')
if [ -n "$A11Y" ]; then
  echo "--- names on the a11y bus ---"
  busctl --address="$A11Y" list --no-pager 2>&1 | head -20 || true
  for n in $(busctl --address="$A11Y" list --no-pager 2>/dev/null | awk '/^:1\./ {print $1}'); do
    echo "--- $n: Accessible.ChildCount / Cache.GetItems (first bytes) ---"
    busctl --address="$A11Y" get-property "$n" /org/a11y/atspi/accessible/root       org.a11y.atspi.Accessible ChildCount 2>&1 | head -2 || true
    busctl --address="$A11Y" call "$n" /org/a11y/atspi/cache       org.a11y.atspi.Cache GetItems 2>&1 | head -c 600 || true
    echo
  done
fi

set +e
python3 "$HERE/atspi-gate.py"
RC=$?
set -e

echo "--- viewer output (bridge attach/failure notes land here) ---"
cat viewer.log || true
exit $RC
