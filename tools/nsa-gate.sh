#!/usr/bin/env bash
# Run the Viewer on a real macOS session and drive it with the AX client API — the channel
# VoiceOver uses. The macOS counterpart of atspi-gate.sh.
#
#   usage: nsa-gate.sh <path-to-Viewer>
#
# Exit code = the client's failure count, so this is a BLOCKING gate wherever it runs.
set -uo pipefail

VIEWER=${1:?usage: nsa-gate.sh <path-to-Viewer>}
HERE=$(cd "$(dirname "$0")" && pwd)

export CUPRIFACE_NSA_DEBUG=1
chmod +x "$VIEWER"
"$VIEWER" > viewer.log 2>&1 &
VIEWER_PID=$!
trap 'kill $VIEWER_PID 2>/dev/null' EXIT

# A single-file build unpacks ~128 MB before a window exists.
sleep 30
if ! kill -0 $VIEWER_PID 2>/dev/null; then
  echo "::error::the Viewer exited before the gate could talk to it"
  cat viewer.log || true
  exit 1
fi

python3 "$HERE/nsa-gate.py"
RC=$?

echo "--- viewer output (bridge attach/failure notes land here) ---"
cat viewer.log || true
exit $RC
