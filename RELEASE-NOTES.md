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

- **`CupriApp.Icon` now reaches every host, not just the desktop window.** The web hosts point the
  page's `<link rel="icon">` at it during boot (so a sample's `index.html` no longer carries a
  hand-pasted base64 copy of the logo that could drift), and the Android host badges the **recents
  card** with it — label and icon following a pushed/popped app, so the task switcher names the app
  you are actually in. New `CupriApp.IconDataUri` gives any host the bytes as a `data:` URI with the
  media type sniffed rather than assumed. **Nothing to do** — apps without an `Icon` are unchanged.
  Note this is the icon of a *running* app; a Windows `.exe` icon and an Android *launcher* icon are
  read out of the built file before your code exists and remain build settings (see `PACKAGE.md`).

- **`transform-origin`** ([#54]) — keywords (`left`/`center`/`right`, `top`/`center`/`bottom`, in
  either order), percentages and lengths, one or two values. Transforms previously always pivoted
  about the border-box centre, so `scaleY` on a bar grew it equally up *and* down; `transform-origin:
  bottom` now anchors it to a baseline, which is what an animated bar chart needs (layout properties
  deliberately do not animate, so `transform` is the only route to one). The initial value is
  `50% 50%`, so **anything not naming an origin behaves exactly as before**. Hit-testing pivots about
  the same point as the paint, so a re-anchored element stays clickable where it is drawn.

[#54]: https://github.com/Wixely/CupriFace/issues/54

### Fixed

- **Custom properties on `:root` and `html` now inherit** ([#53]). The render tree starts at
  `<body>`, so rules on the document element matched nothing: a palette declared the conventional
  way silently vanished and every `var()` behaved as if the token were undefined. The document
  element now participates in *inheritance* — custom properties, `color` and the other inherited
  text properties declared on `:root`/`html` flow into `body` and below. It is still not a layout
  box: `html { background: … }` and friends stay inert; declare those on `body`. **Nothing to do**;
  a palette moved to `body` as a workaround can move back.

[#53]: https://github.com/Wixely/CupriFace/issues/53

- **A percentage height inside a fixed-height block resolves against that block** ([#55]). The block
  layout path passed its own containing block down to its children instead of itself, so
  `height:100%` on the fill of an `18px` meter resolved against the *grandparent* — at the top of a
  page, the viewport — and came out 200px, painting over everything below it. Flex and grid parents
  were always correct. **Nothing to do**; if you made a track `display:flex` purely to get this
  right, plain block now works too.

[#55]: https://github.com/Wixely/CupriFace/issues/55

- **Grid `repeat(auto-fill|auto-fit, …)` templates work** ([#51]). The repeat expander only took a
  numeric count, so the standard responsive-card idiom fell through the track parser as one bogus
  0px track — every item collapsed to its padding and stacked in a single column, silently. The
  count is now computed per layout pass from the container width and the pattern's minimum (the
  `minmax()` floor, fixed sizes, resolved percentages), then the template materialises and sizes
  exactly like an explicit one; `auto-fit` additionally collapses repetitions beyond the item
  count so leftover space goes to occupied tracks. Fixed alongside: a **numeric** repeat whose
  pattern contained `minmax(…)` was cut at the inner `)` — `repeat(3, minmax(200px, 1fr))` now
  parses too. Not supported: `[name]` line names declared after an auto repeat.

[#51]: https://github.com/Wixely/CupriFace/issues/51

- **A percentage `max-width` no longer collapses a shrink-to-fit element to nothing.** Intrinsic
  sizing has no containing block, so `max-width:100%` was resolved against 0 and read as
  `max-width:0` — an auto-width flex item carrying one was handed 0px, and anything inside it that
  could wrap wrapped onto its own line. `<cupri-pagination>` in a flex row came out as a vertical
  column of page numbers. Percentage min/max-width are now ignored during intrinsic sizing (px
  still applies) and clamp only where the basis is actually known. **Nothing to do**; if you worked
  around it by dropping a percentage `max-width`, you can put it back.
