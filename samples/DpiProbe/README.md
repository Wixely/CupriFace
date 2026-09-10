# DPI probe (#137)

A single-window app that shows what the desktop host actually computed for the last drawn frame:
the monitor scale **D**, the app's present scale **P**, the effective scale **T = D × P**, the
framebuffer, and the logical sizes. It reports `DesktopHost.FrameScale` — the very value fed to the
canvas, the surfaces, the damage rectangle and the accessibility geometry — rather than re-deriving
the numbers, which would agree with itself no matter what the renderer did.

## Running it

```
DpiProbe.exe                    DPI-aware, following monitor changes (the defaults)
DpiProbe.exe --zoom 1.25        an app present scale on top of the monitor's, to see T = D × P
DpiProbe.exe --no-dpi           CupriApp.DpiAware = false — the pre-#137 behaviour
DpiProbe.exe --no-track         aware, but stops following the window between monitors

set CUPRIFACE_DPI=0             the environment kill switch (same effect as --no-dpi, no rebuild)
set CUPRIFACE_SOFTWARE=1        the SDL software window instead of the GL one
```

The published build is self-contained: copy the folder to a machine with no .NET installed and run
the exe. First launch extracts to disk and takes a few seconds; later launches are quick.

## What to check on a scaled display

1. **The numbers.** At 150%, D reads `1.5×`. The framebuffer is 1.5 × the logical client, and
   "expected physical" matches the framebuffer. "Product checks out" must say **yes** — if it ever
   says `NO — MISMATCH`, the host applied something other than D × P and nothing else on the panel
   can be trusted.
2. **Drag between monitors of different scaling.** D, the framebuffer and T should all change within
   a frame or two. The **logical client stays the same** — that is the whole point of
   Per-Monitor-V2: the window keeps its size on the desk and gains pixels. Watch for stale or
   clipped pixels during the move.
3. **Crispness.** The gold square is 100 *logical* pixels, so it should measure 100 × T physical
   ones — 150 at 150%, 200 at 200%. The hairlines are real 1px boxes 3px apart: evenly spaced and
   sharp means the frame was rasterised at device resolution; smeared, or alternating thick and
   thin, means something stretched a lower-resolution raster.

   The 8px sample is a stretching detector, not a legibility target — it is readable but hard work
   even when everything is correct, which is the expected result rather than a finding. Judge the
   hairlines and the 11px line instead; those degrade visibly when a raster is being stretched.
4. **The pointer.** Click the pad; it reports the coordinates the document received, in document
   units. Clicking its visual centre should read close to half the document viewport. If the
   transform divided by D twice, a click on a 150% monitor lands about a third short.
5. **`--no-track`, then drag to another monitor.** D should now *not* follow. This is the
   combination the defaults deliberately avoid, and it should look wrong — that is the control.
6. **The awareness row.** "Per-Monitor-V2 (granted)" means CupriFace established it. "Already set by
   the app's manifest or the app itself" means something got there first and won, which is correct
   behaviour — a library must not overrule an application that decided this for itself.

## Investigating the drag asymmetry

Reported: crossing 100% → 150% is detected mid-drag, but dragging back to 100% without releasing is
not detected until the mouse is let go. The "Callbacks" card and the "Scale events" log answer which
of three mechanisms it is — drag slowly, watch the two counters, and read the log afterwards.

| what the counters do while you drag back | what it means |
|---|---|
| **move** freezes, **resize** does not tick | the callback is not delivered during the return drag at all — the detection on the way out came from the resize the OS forced on us, and the return leg has no equivalent |
| **move** climbs, scale stays 1.5 until release | the callbacks are fine; Windows is not reporting the new DPI yet, because it only does so once the window's MAJORITY crosses — and our window is now 1.5x wider, so that point is much further right than it looks |
| **move** climbs, then freezes right after a `scale ->` line | resizing the window from inside the modal loop disturbed the OS drag tracking |

For a full per-event trace including the callbacks that changed nothing:

```
set CUPRIFACE_DPI_DEBUG=1                     stderr (run from a terminal)
set CUPRIFACE_DPI_DEBUG=C:	emp\dpi.log       or append to a file
```

Every move and framebuffer callback is logged with the scale read at that instant next to the cached
one, which is what separates "we were not told" from "we were told and ignored it".

## Notes

- There is deliberately **no app.manifest** here. The probe exists to observe what CupriFace does on
  its own; a manifest would pre-empt the runtime request and the panel would only ever report the
  "already set" case.
- The published binary is trimmed. That is checked, not assumed: without the `TrimmerRootAssembly`
  items in the csproj the trimmer removes Silk.NET's window backends and the app silently drops to
  the SDL software window while still looking fine — which for a diagnostic would be worse than a
  crash. The panel's "window" row reports which one it got.
