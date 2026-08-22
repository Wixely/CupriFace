# Release notes

Per-release notes that CI splices into the GitHub release, so a breaking change is recorded in
the SAME commit that makes it rather than reconstructed from memory at tag time.

**How CI picks a section:** it looks for a `## <tag>` heading (e.g. `## v0.2.11`); if there isn't
one, it uses `## Unreleased`. So the normal workflow is: write under `## Unreleased` as you make
the change, and at release time either rename that heading to the version or leave it — both
publish the same text. Nothing here means nothing added, which is the correct default for a
release that breaks nothing.

Keep entries short and say what a caller must DO. The audience is someone whose build just broke.

## Unreleased

### Breaking

- **`NavigateEvent.External` is narrower, and your code still compiles.** It used to be true for
  any href carrying a URL scheme — including custom schemes (`myapp:`) and protocol-relative
  (`//host/path`). It is now true only for an absolute URI that `ExternalLinkPolicy` allows:
  `http:`, `https:` (with a host), `mailto:`, `tel:`. Everything else reports `External = false`
  and falls to in-app routing. The point is to stop remote markup reaching executable, local-file,
  or intent handlers through a host that trusted the flag. **If you relied on `External` to launch
  a custom scheme, do it explicitly**: match the href yourself in your `Navigated` handler.

- **`SkiaWindow.PointerWheel` and `SdlSoftwareWindow.PointerWheel` gained a `KeyMods` argument**
  (`Action<float, float, float>` → `Action<float, float, float, KeyMods>`), so a host can tell
  Ctrl+wheel (page zoom) from a plain wheel (scroll). Only affects code that drives those window
  classes directly; `DesktopHost.Run` handles it for you. **Fix**: add the parameter and ignore it
  (`(x, y, dy, _) => …`) to keep the old behaviour.

### Added

- **Page zoom from the keyboard and wheel**: Ctrl/Cmd `=` / `−` / `0` (keypad included) and
  Ctrl+wheel step a discrete browser ladder (0.5…4). `CupriDocument` gains `ZoomIn()`,
  `ZoomOut()`, `ZoomReset()` beside the existing `Zoom` property. `PageZoomEnabled` gates in/out
  as it gates the pinch — but `ZoomReset()` always works, so a zoom can always be undone.

- **Zoom is restorable, and the app owns the storage.** `CupriDocument.Zoom` was already
  settable; it is now settable *meaningfully at startup* (assign it in the host's configure hook —
  `DesktopHost.Run(app, doc => doc.Zoom = Prefs.Zoom)`, `ConfigureDocument` on Android — and the
  first frame is already at that level, with no jump from 1), and a new **`ZoomChanged`** event
  reports every settled level so an app knows when to save. It fires for a pinch, a chord, a wheel
  notch or an assignment alike — the user-driven ones being exactly what an app cannot otherwise
  see — carries the CLAMPED value so what you store round-trips, and stays quiet when a change
  lands on the level already in force, so a key held at the limit does not hammer your saver.
  **CupriFace deliberately does not persist anything itself**: it has no business choosing where
  your app keeps settings.

- **Ctrl+wheel zooms at the pointer.** `ZoomIn(hostX, hostY)` / `ZoomOut(hostX, hostY)` keep
  whatever you are pointing at where it is; the parameterless overloads still zoom from the origin
  for keyboard chords. Because this is reflow zoom rather than a magnifier, the anchor is the
  *element* under the cursor re-found after the rewrap, not a pixel coordinate.

- **Video on Android**: `<cupri-video>` plays through the platform's own `MediaPlayer` under a
  `SurfaceView` beneath the punched hole — no codecs ship in the app, and the device's hardware
  decoders do the work.
