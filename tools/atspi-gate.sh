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

set +e
python3 "$HERE/atspi-gate.py"
RC=$?
set -e

echo "--- viewer output (bridge attach/failure notes land here) ---"
cat viewer.log || true
exit $RC
