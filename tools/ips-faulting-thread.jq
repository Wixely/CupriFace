# Pull the exception and the faulting thread's top frames out of a macOS .ips crash report.
#
# Used by the macOS smoke step in ci.yml, which only runs when the Viewer has ALREADY died — so
# this is the code most likely to be quietly broken at the exact moment it is needed. It lives in
# its own file rather than inline so that the workflow's own fixture check runs the SAME program
# the crash path will, with no chance of the two drifting apart.
#
# An .ips is two JSON documents: a one-line header, then the report. The caller strips the header
# (tail -n +2) before piping the rest here.

. as $d
| ($d.usedImages // []) as $imgs
| (($d.threads // [])[($d.faultingThread // 0)] // {}) as $th
| "exception: \($d.exception // "?" | tojson)",
  ( ($th.frames // [])[:15][]
    | . as $f
    # imageIndex is an index INTO usedImages, and jq reads a negative index from the end — so a
    # missing or out-of-range one must be rejected explicitly rather than defaulted to -1.
    | (if ($f.imageIndex | type) == "number"
          and $f.imageIndex >= 0
          and $f.imageIndex < ($imgs | length)
       then ($imgs[$f.imageIndex].name // "?")
       else "?" end) as $img
    | "  \($img) + \($f.imageOffset // "") \($f.symbol // "") \($f.symbolLocation // "")" )
