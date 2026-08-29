# Release notes

Per-release notes that CI splices into the GitHub release, so a breaking change is recorded in
the SAME commit that makes it rather than reconstructed from memory at tag time.

**How CI picks a section:** it looks for a `## <tag>` heading (e.g. `## v0.2.11`); if there isn't
one, it uses `## Unreleased`. So the normal workflow is: write under `## Unreleased` as you make
the change, and at release time either rename that heading to the version or leave it — both
publish the same text. But rename it before the NEXT release's entries start accumulating: a
left-behind Unreleased section republishes one release's changes in the next one's notes (v0.2.12
had to backfill the v0.2.11 heading for exactly this reason). Nothing here means nothing added,
which is the correct default for a release that breaks nothing.

Keep entries short and say what a caller must DO. The audience is someone whose build just broke.

## Unreleased

### Added

- **`CupriFace.Web.NativeAot` — the browser host, compiled ahead of time** ([#78]). The second web
  runtime now has a package too, so `samples/WebLlvm` is three lines of app rather than ~740 lines
  of host. The API is identical to `CupriFace.Web.Mono` — same namespace, same `WebHost.Run` — so
  moving between the two runtimes, or falling back from one to the other, is a `PackageReference`
  change and no app code.
  It is the fast one: the engine is compiled rather than interpreted. It costs toolchain maturity —
  the ILCompiler.LLVM backend is on the experimental dotnet/runtimelab feed, and **a package cannot
  add a restore source to its consumer**, so an app must still declare that feed, the ILC packages
  and the two wasm native-asset packages itself. `samples/WebLlvm/WebLlvm.csproj` is a working copy
  of exactly that block; everything else (the link line, the Emscripten JS library, the static
  archives, the trimmer roots, the RID) comes from the package.

[#78]: https://github.com/Wixely/CupriFace/issues/78
### Fixed

- **The NativeAOT-LLVM web host now positions the IME** ([#77]). It had composition input but never
  told JS where the caret was, so a candidate window opened at the page's top-left instead of at
  the field being typed into, and `inputmode` was never set — a touch keyboard could not offer
  digits for a numeric field. The Mono host has always done this; the two had simply drifted. The
  browser gate now asserts it on **both** hosts, so the gap cannot reopen on one of them.

[#77]: https://github.com/Wixely/CupriFace/issues/77

## v0.7.0

### Added

- **`CupriFace.Web.Mono` — the browser host as a package** ([#73]). The web platform now has what
  desktop and Android already had: `WebHost.Run(new MyApp())` is the whole of an app's
  `Program.cs`. The package brings the frame loop, damage-rect blitting, pointer/touch/wheel/
  keyboard input, the touch recognizer (tap-on-release, momentum fling, long-press), the ARIA
  mirror screen readers read, IME composition, the clipboard, browser-decoded video, and the two
  font faces the wasm Skia build omits — plus the Skia/HarfBuzz wasm natives as transitive
  dependencies and the Mono AOT interpreter workaround, which used to live in every consumer's
  csproj where nobody could tell them when it stopped being needed.
  `samples/WebWasm` was the web host before this, so a second web app had to copy ~1,000 lines and
  the copies silently arrived without accessibility, the IME and touch — the parts you can omit and
  still see a first frame. An app now owns its page shell and nothing else; a default
  `index.html` ships in the package under `template/` to start from, and the host's JS half is
  served at `_content/CupriFace.Web.Mono/main.js`.
  Migrating an app built on the old sample: delete the copied `Program.cs`, `main.js` and video
  backend, reference `CupriFace.Web.Mono`, call `WebHost.Run`, and point the page's `<script>` at
  `_content/CupriFace.Web.Mono/main.js`.

[#73]: https://github.com/Wixely/CupriFace/issues/73

### Fixed

- **`box-sizing: border-box` now works** ([#76]) — it was not read at all, so a declared width was
  always the CONTENT box. Every full-bleed `width:100%` container with padding therefore overflowed
  its parent by twice that padding, silently shifting anything centred inside it, and the global
  `* { box-sizing: border-box }` almost every stylesheet writes could not rescue it. It applies to
  `width`/`height` and to `min-`/`max-` alike. If you compensated by subtracting padding from a
  width by hand, that box is now smaller than you intended — remove the compensation.

- **`margin: auto` centres again** ([#76]) — `auto` resolved to `0` like any unresolved length, so
  a box with `margin-left:auto; margin-right:auto` sat flush against its container instead of
  centring. Both axes' shorthand now behave: two auto margins centre, a single `margin-left:auto`
  pushes a box to the far side. On a flex item they take the free space before `justify-content`
  sees it, which is what makes them the way to move ONE item while its siblings stay put.

### Changed

- **Placeholder text now reads `--cupri-muted`** instead of a hard-coded grey, matching every other
  muted label in the toolbox. The fallback is the same colour, so nothing changes unless you set
  the variable — at which point the text inputs finally follow your theme. Noted while documenting
  that the fields draw their value with `--cupri-text` rather than inheriting `color`, which is why
  a dark theme that sets only `body { color: … }` appears to grey text out as it is typed ([#76]).

[#76]: https://github.com/Wixely/CupriFace/issues/76

## v0.6.0

### Fixed

- **Viewport units (`vh`/`vw`/`vmin`/`vmax`) now work** ([#71]) — they were not parsed at all, so
  a viewport length fell through to the px parser, whose fallback is `0`. `height:100vh` became a
  DEFINITE `0px`: a full-screen container collapsed, and with `overflow:hidden` its zero-height
  clip hid the whole subtree, so an app with a complete display list painted a **blank screen**.
  The `dvh`/`svh`/`lvh` (and `dvw`/`svw`/`lvw`) forms are accepted as synonyms — a CupriFace
  surface has no browser chrome that grows or shrinks, so all three viewports are the same box.
  They work anywhere a length does, `calc(100vh - 64px)` and `var()` tokens included, and a
  document that uses them now re-resolves when the viewport changes, as an `@media` one does.
  If you worked around this with `height:100%` or a hard-coded pixel height, `100vh` now does what
  it says — check any layout that was compensating for the old behaviour.

- **An unreadable length is `auto`, not a definite `0px`** — the general defence behind the above.
  A unit the parser cannot read (`20q`, a future CSS unit) no longer silently collapses the box it
  is on and clips the subtree away; it is treated as `auto`, which is the honest answer.

[#71]: https://github.com/Wixely/CupriFace/issues/71

## v0.5.0

### Added

- **`white-space: pre | pre-wrap | pre-line`** ([#69]) — preserved newlines in text (bound values
  included) are HARD line breaks, so a multi-line string renders as multiple lines from one value:
  `pre` also keeps spaces verbatim and never wraps (code blocks, indentation intact); `pre-wrap`
  keeps spaces and wraps long lines (chat, logs — and `overflow-wrap` works inside it); `pre-line`
  keeps the newlines but collapses runs of spaces. Blank lines keep their height. The default
  stays CSS-correct (newlines collapse), so nothing changes for markup that says nothing. If you
  split multi-line values across a nested `data-repeat` as a workaround, one `white-space:
  pre-wrap` replaces it. Note: `pre` previously behaved as `nowrap`; it now means what CSS says.
  Limit: hard breaks apply to text in block flow — inside an inline run mixed with `<b>`/`<span>`
  they degrade to collapse.

### Fixed

- **A no-break space is no longer collapsed** ([#69]). `&nbsp;` was treated as collapsible
  whitespace (an element containing only `&nbsp;` laid out at height 0, and runs of them folded
  to one space) because .NET's `char.IsWhiteSpace` counts U+00A0 and CSS does not. It now rides
  through normalisation like any other glyph: it occupies space, keeps a line's height, and is
  never a wrap point.

[#69]: https://github.com/Wixely/CupriFace/issues/69

## v0.4.0

### Added

- **`<cupri-virtual>` rows may be any height — and it can be a chat log** ([#66], [#67]). `item-height`
  is now the ESTIMATED row pitch, not a requirement: each materialised row's real height is
  measured back into a per-list cache and replaces the estimate, with the scroll offset anchored
  in the same frame so measurement never makes the visible content jump. New `anchor="bottom"`
  opens the list at its bottom and follows appended rows while the user is there (one scroll up
  releases it; returning re-engages it), and new **`CupriDocument.VirtualListInserted(path,
  index, count)`** is the prepend hook — call it before `Refresh` when loading older history and
  the content on screen stays put. Measured: appending to a 5,000-row wrap-height chat costs
  ~3ms where the unvirtualised path costs ~660ms. Fixed-height lists behave exactly as before
  (estimate == measured ⇒ every correction is zero). Keep the estimate near a typical row; the
  cache re-measures automatically when the list's width changes. Also fixed while there: a fling
  died on the first re-window it crossed (the rebuilt scroller was unlaid for one frame and
  reported itself unscrollable).

[#66]: https://github.com/Wixely/CupriFace/issues/66
[#67]: https://github.com/Wixely/CupriFace/issues/67

## v0.3.0

### Added

- **`word-break: break-all` and `overflow-wrap: break-word|anywhere`** (plus the legacy
  `word-wrap` alias) — mid-token line breaking ([#59]). A long unbreakable token (a 62-char
  bech32 address, a hash, a URL) used to force its container into horizontal overflow with no
  recourse; now `overflow-wrap` breaks it only when it cannot fit a line of its own, and
  `break-all` packs every line full. Breaks never split a surrogate pair, never lose a character,
  and a sliver-thin container still terminates (one code point per line). Both properties inherit,
  as in CSS. Applies to text in block flow; mid-token breaking inside an inline formatting context
  (text mixed with `<b>`/`<span>` runs) is not yet wired.

[#59]: https://github.com/Wixely/CupriFace/issues/59

### Fixed

- **`transform-origin: bottom center` parses** ([#63]) — and every other keyword-plus-`center`
  pair (`top center`, `center left`, …). The keyword-order swap required BOTH words to name an
  axis, and `center` names none: the pair fell through to positional reading, `bottom` became an
  X of 100%, and the origin silently came out right-middle — for a `scaleY`, indistinguishable
  from unset, i.e. the exact symptom [#54] had just fixed. All spellings of the same origin now
  agree (`bottom` == `bottom center` == `center bottom` == `50% 100%`). **Nothing to do**; a
  single-keyword workaround can stay or revert, they are identical.

[#63]: https://github.com/Wixely/CupriFace/issues/63

## v0.2.12

### Added

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
  bottom` now anchors it to a baseline, which is what an animated bar chart needs. The initial value
  is `50% 50%`, so **anything not naming an origin behaves exactly as before**. Hit-testing pivots
  about the same point as the paint, so a re-anchored element stays clickable where it is drawn.

[#54]: https://github.com/Wixely/CupriFace/issues/54

- **`@keyframes` can animate `width` and `height`** ([#56]). The keyframe declarations were always
  parsed — the interpolation only ever read transform and opacity out of them, so a keyframed bar
  held its start size for the whole run while the engine reported the animation active. Width and
  height now lerp to a definite length that the frame's layout honours (the same road a
  `transition: height` already took), so the element **and everything below it** reflow as it
  moves. Same-unit px or % pairs interpolate; a non-interpolable pair (auto, mixed units) flips at
  the midpoint, as in CSS. `transition: width` needed no fix — transitions start on a target-value
  *change* (hover, class, model), which a clock-only harness never triggers; now pinned by a test.

[#56]: https://github.com/Wixely/CupriFace/issues/56

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

## v0.2.11

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
