#!/usr/bin/env bash
# Stand the Viewer up on a real macOS session and point an accessibility client at it.
#
#   usage: nsa-probe.sh <path-to-Viewer>
#
# macOS runners have a genuine window session (no xvfb equivalent needed), so the app opens for
# real — which is the only way the AX question can be asked honestly.
#
# Reports; never fails the job. This is a probe.
set -uo pipefail

VIEWER=${1:?usage: nsa-probe.sh <path-to-Viewer>}
HERE=$(cd "$(dirname "$0")" && pwd)

echo "=== the machine ==="
sw_vers || true
csrutil status 2>&1 || true      # SIP on/off decides whether TCC can be written at all

chmod +x "$VIEWER"
"$VIEWER" > viewer.log 2>&1 &
VIEWER_PID=$!
trap 'kill $VIEWER_PID 2>/dev/null' EXIT

# A single-file build unpacks ~128 MB before a window exists.
sleep 30
if ! kill -0 $VIEWER_PID 2>/dev/null; then
  echo "the Viewer exited before the probe could look at it:"
  cat viewer.log || true
  exit 0
fi
echo "Viewer is up (pid $VIEWER_PID)"

echo
echo "=== what an accessibility client can see ==="
python3 "$HERE/nsa-probe.py" || true

echo
echo "=== viewer output ==="
cat viewer.log || true
