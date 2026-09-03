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

- **Anything can now live under a hole on the web, not just video.** `ISurfaceSource` gains one
  optional member, `UnderlayElement`. Return `"canvas"` and the web host creates a
  `<canvas id="cupri-underlay-{key}">` beneath the engine's own, then keeps it glued to the box the
  engine laid out — through the scroll offset, the clip against every `overflow` ancestor, and the
  transform chain — and removes it when the element goes. Video returns `null`, keeps owning its
  element, and is positioned by the same code: this is `WebVideo.SyncRects` generalised, not a second
  implementation. Nothing to change in an existing app.

- **A 3D page in the Showcase**, on the desktop Viewer and in the browser (`samples/WebLlvm`). One
  app and one piece of markup, composited two different ways depending on what the host can do:
  **painted** into the display list on desktop, **host-composited** through a punched hole on the
  web — and the page reports which lane it got by asking the engine's public surface registry.
  `ShowcaseApp` holds no reference to a renderer; the surfaces attach at each composition root,
  exactly where video already does. Hosts that wire nothing (WebWasm, Android) show the poster, which
  is the engine's ordinary behaviour for a surface with no frames. See `samples/Demo3d/README.md`.

### Fixed

- **A `<canvas>` underlay is given a drawing buffer, not just a CSS box.** A canvas defaults to
  300x150 regardless of its size on the page, and a `<video>` has no backing store at all — so the
  video path never needed this. The symptom was not a missing image but a stretched one.

- **`-sMAX_WEBGL_VERSION=2` now flows through `CupriFace.Web.NativeAot`'s build props.** Without it
  `emscripten_webgl_create_context` silently downgrades to WebGL1, and the first symptom is a shader
  error blaming `#version 300 es` — three steps from the cause.

### Note for surface authors

`ISurfaceSource.Ticking` feeds the document's "something is animating" signal. Returning a constant
`true` stops a render-on-demand host ever idling; report it honestly, and gate per-frame work on
`RenderNode.LaidOut` rather than on whether the painter asked for `HostComposited` — the display list
is rebuilt every tick to compute damage, so the painter consults surfaces in `display:none` sections
too.

## v0.16.0

### Added

- **Android tells the keyboard where the caret is drawn (`CursorAnchorInfo`).** Nothing to call: any
  app on the Android host gets it. `requestCursorUpdates` was never implemented, so
  `BaseInputConnection` answered false and Gboard stopped asking — which left a candidate window with
  no idea where the text it was completing sat on screen, free to cover the word being corrected.
  The caret's rectangle is now reported in the view's own pixel space with a matrix onto the screen,
  scaled by the same factor the canvas, touch and the accessibility bounds already use, so a zoomed
  app does not drift. Reported off the caret's RECTANGLE rather than the selection indices: a reflow,
  a scrolled field or a resize move where the caret is drawn without changing what it points at, and
  an IME drawing over the text needs those too.

### Changed

- **The Android Lottie claim is now enforced on every run, not measured once.** v0.15.0 said
  `samples/AndroidLottie` renders and animates on a device, on the strength of a local emulator
  session. The Android CI gate now installs that APK and asserts it itself: that Skottie parsed the
  file on the device (the same 120x120/1.50s shape the desktop tests assert) and that the frames are
  actually moving, measured as changed pixels between two captures. Nothing changes for a caller —
  the claim is simply one that can no longer quietly stop being true.

## v0.15.0

### Fixed

- **The GL window now shows the wait and busy cursors.** `CursorType.Wait` and `Progress` were never
  mapped there, while the SDL window has mapped both since it was written — so a busy app showed an
  hourglass on one desktop path and a plain arrow on the other. GLFW had the cursors all along.
  `Help` remains unmapped on both, which no platform standard cursor covers; it is now listed as a
  deliberate exception rather than an omission. A test compares the two tables against the enum and
  against each other, so a cursor added to one window cannot go missing from the other.

- **A bound `autoplay` on `<cupri-lottie>` now actually pauses.** The player is deliberately kept
  across rebuilds so an animation does not restart on every keystroke, but `autoplay` was read only
  when the player was first opened — so a Pause button bound to it flipped the model, rewrote the DOM
  and reached nothing. Independently: a bound C# bool renders as `"False"`, and the check was an
  ORDINAL compare against `"false"`, so even the initial value was ignored. It is now re-read on
  every rebuild and parsed case-insensitively, as a tri-state — an ABSENT `autoplay` means "no
  opinion", so several elements sharing one animation (the surface key is the `src`) are controlled
  only by the ones that ask for it.

- **The web launch configurations start their server again.** `tools/Serve` produced a `Serve.exe`
  apphost, and `dotnet run` rebuilds before launching — so while ANY earlier server was alive the
  copy over that file failed and the server never started. A VS Code background task routinely
  outlives the debug session that started it, so this hit every `serve-*` task, and the symptom was
  a browser opening on `ERR_CONNECTION_REFUSED` with the real reason buried in MSBuild retry
  warnings. The tool no longer builds an apphost, so there is nothing to lock.

### Added

- **`CupriFace.Lottie` — an optional package playing After Effects JSON via `<cupri-lottie>`.**
  Enable with `Components.UseLottie()` and `doc.UseLottie(assembly)`. The element becomes a live
  surface, so `object-fit` sizing, damage-clipped repainting and render-on-demand all come from the
  engine, and a paused animation stops ticking so the window goes idle.
  **It costs about 65 KB of managed assemblies and no native code at all**: Lottie is already inside
  Skia, and `SkiaSharp.Skottie` (MIT) is managed bindings over the same `libSkiaSharp` the engine
  already loads — so unlike `CupriFace.Media` there are no per-RID builds. It is still opt-in, since
  most apps do not play Lottie. `samples/LottieDemo` shows it with an original MIT-licensed spinner.
  End-to-end tested on desktop, and it builds and links on **both** web hosts —
  `samples/WebLottie` (Mono) and `samples/WebLlvmLottie` (NativeAOT-LLVM), the latter being the
  strict test since NativeAOT links statically and a missing symbol fails the link. On the web it
  costs **+408 KB of raw wasm, +119 KB gzipped (2.3%)**, measured as the same app with and without
  the package. **Confirmed rendering in real Chromium on both web hosts** by the browser gate: the
  spinner is on the canvas and the canvas keeps changing, with no console errors.
  **Proven on Android too**, by `samples/AndroidLottie` — an APK driven on a device rather than a
  symbol table: playing, 53,199 pixels change over 400 ms; paused, exactly 0 change while the last
  frame stays on screen; resumed, 40,336. Every `.so` in that APK is .NET's runtime or a library the
  engine already loaded — there is no Skottie native, because there is none to ship.

- **`doc.OnRebuilt(handler)`** — run a handler after each rebuild, once components have expanded, the
  moment the engine wires its own video players. This is what a surface producer living outside the
  engine needs: registering an `ISurfaceSource` is only half of it, and something has to notice that
  an element wanting one has appeared or gone. Without it an optional package can ship a component
  that expands correctly and renders nothing forever.

## v0.14.0

### Added

- **`data-window-drag` — the title bar a frameless window does not have.** Mark an element with it
  and a drag there moves the OS window: the engine reports how far the pointer has travelled through
  the new `doc.WindowMoveRequested`, and the host adds that to the window's position. Same
  engine→host split as `WindowCommandRequested` — a host with no window to move (a browser page, an
  Android activity) simply does not subscribe, and the press is then left to whatever else wanted it
  rather than swallowed by a drag that could never do anything. The handle shows a grab cursor, but
  only while a host is listening. Both desktop windows gained `MoveBy`; the `TransparentHud` sample
  now has a real title bar.

### Fixed

- **A grab cursor no longer looks like a link on the desktop.** Both desktop windows folded
  `CursorType.Grab` and `Grabbing` in with `Pointer`, so every drag handle — a window title bar, a
  reorder grip — reached the OS as the same pointing hand a hyperlink gets, and read as clickable
  rather than draggable. Neither GLFW nor SDL has an open/closed hand, so they now map to the
  four-way move arrow: not a true grab, but at least not a link.

## v0.13.0

### Added

- **`cupri-carousel`** (with `cupri-slide`) — a horizontal strip that scrolls sideways by finger,
  wheel or fling. It is a scroll container rather than a widget with its own gesture code, so the
  second scrolling axis does the work. `slide-width` fixes the panel width; `peek` sizes panels
  against the container so a sliver of the next one shows.

- **Five controls**: `cupri-breadcrumb` (with `cupri-crumb`), `cupri-toolbar` (with
  `cupri-toolbar-group` and `cupri-toolbar-sep`), `cupri-form`, `cupri-range` and `cupri-taginput`.
  A breadcrumb's last crumb is the page you are on, so it renders as text with `aria-current` rather
  than a link to here. A toolbar is one `role="toolbar"` group, and a group marked `push` takes the
  free space before it. A range is two thumbs that cannot cross. A tag input takes a comma-separated
  value: type and press Enter to add, click a chip's × to remove.

- **`doc.Validate("formName")`** validates only the fields inside that `<cupri-form name="…">`, and
  reveals only that form's errors. `ValidateAll()` is unchanged and still document-wide — two forms
  on one page previously could not be submitted apart, because validating one reported AND displayed
  the other's errors.

- **`<cupri-form name="…">` is a submit scope as well as a validation scope.** It emits
  `data-cupri-form`, which is what `OnSubmit` bubbles to, so `doc.OnSubmit("data-cupri-form", …)`
  hands you the form's name in `e.Value` and Enter in any single-line field inside it submits that
  form. The boundary an app previously spelled with a hand-chosen `data-` attribute is now a
  declaration.

- **A slider thumb can take its drag geometry from an ancestor marked `data-slider-track`**, and can
  be limited by `data-clamp-min`/`data-clamp-max` separately from the `min`/`max` it is measured on.
  Both exist for `cupri-range`, where two thumbs share one track and bound each other: the scale must
  stay the whole range or the pointer stops landing where you point, while the limit is the other
  thumb.

### Fixed

- **`flex: none` works.** The `flex` shorthand parsed only numbers, so the keyword forms — `none`,
  `auto`, `initial` — matched nothing and left the item at the default `flex-shrink: 1`. `flex: none`
  read as working everywhere it was written and silently did not, including in this repository's own
  sidebar. **What to do:** nothing, unless a layout was relying on an item shrinking despite asking
  not to.

- **`cupri-taginput` takes back the last tag on Backspace** when the entry is empty — the tag-box
  idiom. While there is text to delete, Backspace still deletes text.

- **A `cupri-range` whose thumbs sit on the same value can be dragged apart again.** Only the thumb
  painted last is hit-testable where two coincide, so every press there grabbed the same one and the
  other could never be moved — a range dragged shut stayed shut. The press now waits for the first
  movement and picks by direction: pull left and the low thumb follows, pull right and the high one
  does. A press that does not move writes nothing, and a drag on thumbs that are already apart is
  unchanged.

- **A child of a padded `<body>` now gets the body's content width.** The root was laid out with its
  content width forced to the viewport width, and in a content-box model the padding is then added
  outside it — so a padded body's border box came out wider than the window and every child was
  measured against the full viewport. A block child of a 600px body with `padding:20px` was 600 wide
  and ran 20px off the right edge. Nested padded elements were always correct, since only the root
  was forced, which is what made this look like a bug in whichever component happened to sit there.
  **What to do:** nothing, unless a layout was built around the old overflow — a body with no padding
  is unaffected, and `height:100%` still fills the window. A body MARGIN is still ignored, as before:
  applying it would narrow the content without shifting it, and half a margin is worse than none.

## v0.12.0

### Added

- **A Keyboard page in the Showcase**, which ships in the Viewer downloads below. Tab and Shift+Tab
  walk a row of controls, a composer marked `submit-on-enter` sends on Enter and takes a newline on
  Shift+Enter, `Ctrl+Enter` sends from anywhere and `Escape` clears — with a readout naming whichever
  just happened, because a keyboard interaction leaves nothing on screen to see otherwise. It also
  demonstrates that an open palette swallows the first `Escape` before an app's own binding runs.

### Changed

- **A single-line field submits on Enter without opting in.** `submit-on-enter` is no longer needed
  on a `cupri-textfield` (or any single-line field): Enter raises the submit, exactly as Enter in an
  `<input>` submits its form on the web. Previously it committed and blurred, which is quieter and
  less useful. A **textarea still has to opt in**, because Enter already means newline there.
  **What to do:** nothing, unless you have an `OnSubmit` handler whose attribute sits on an ancestor
  of a single-line field you did not intend to submit — that field will now submit on Enter instead
  of blurring. There is no `<form>` element in this engine, so the ancestor carrying the `OnSubmit`
  attribute is the scope: a field with no such ancestor claims nothing and keeps the behaviour it
  had, and an app that registered no `OnSubmit` is untouched.

### Fixed

- **Both web hosts now forward the modifiers when dispatching `Tab`** ([#96]). The `Tab` line passed a
  literal `0` where the line directly below it forwards the real modifiers for every other named key,
  so `OnShortcut(KeyMods.Ctrl, "Tab", …)` was a registration that fired on desktop and Android and
  never in a browser. **What to do:** nothing, and expect nothing — in practice a browser still will
  not deliver the chord, because Ctrl+Tab and Ctrl+Shift+Tab switch browser tabs and never reach the
  page. This removes an incorrectness in the hosts rather than enabling a shortcut; the limitation is
  the platform's and is now stated on `OnShortcut` instead of being discovered.

[#96]: https://github.com/Wixely/CupriFace/issues/96

## v0.11.0

### Added

- **`CupriDocument.ScaleDamageToDevice(logical, scale, deviceWidth, deviceHeight)`** — maps a damage
  rectangle from `RenderIncremental` into device pixels, for a host that applied its own scale to the
  canvas before calling it (a HiDPI present, or an authored design size fitted to the viewport). Such
  a host renders at logical size and scales the raster, so the rectangle comes back in logical units.
  Rounds outward and clamps to the surface; identity at scale 1, so it can be called unconditionally.
  The built-in web and desktop hosts use it, and it is public so a third-party host need not
  reimplement the rounding rule. See [#99].

### Fixed

- **Damage-clipped repainting now works under scale** ([#99]). Both the engine and the hosts used to
  repaint the whole surface whenever the scale was not exactly 1 — `RenderIncremental` bailed on
  `Zoom != 1`, and the web and desktop hosts would not even call it when `PresentInfo.Scale != 1` —
  on the grounds that a damage rectangle computed in document space would not map 1:1 onto device
  pixels. It does not map 1:1, but the mapping is a multiply, because the scale is uniform. The
  rectangle is now scaled and rounded OUTWARD, so a hover repaints its own band rather than the
  page. **What to do:** nothing. This is a per-frame saving on every display that is not exactly
  scale 1, which is most of them — a HiDPI ratio of 2, fractional desktop scaling of 1.25 or 1.5,
  and any fit-to-viewport factor all previously gave up damage tracking entirely.
  A host that applies its own scale to the canvas can map the returned rectangle with the new
  `CupriDocument.ScaleDamageToDevice`.

- **`WebHostCore.Init` resets the state it caches per page.** Re-initialising in one process kept
  `_dirty`, the last cursor, the last text-input state and the last surface size from the previous
  page, so a new document could sit unpainted and a new bridge never be told the first cursor.
  Unobservable in a browser, where the page loads once and the statics start empty.

[#99]: https://github.com/Wixely/CupriFace/issues/99

## v0.10.1

### Fixed

- **Hovering or focusing a field no longer resizes it** ([#93]). Every field component's state rules
  redeclared the whole `border` shorthand (`[data-hover] { border:2px … }`), and an attribute
  selector outranks an app's plain class — so an app that wrote `border: 0` got width 0 at rest and
  2px back the moment the pointer crossed the control, growing it 4px and shifting every sibling in
  the row. The state rules now set `border-color` alone, so the width belongs to whoever declared
  it and the states only recolour what is there. Fixed in all eleven components that had the
  pattern, not only `cupri-textarea` where it was reported. **What to do:** if you reserved the
  space with `border: 2px solid transparent` and reduced padding to compensate, you can drop that
  workaround — but it still behaves correctly, so there is no hurry.

[#93]: https://github.com/Wixely/CupriFace/issues/93

## v0.10.0

### Added

- **Enter can send, and Shift+Enter can still mean a new line** ([#90]). Mark a field
  `submit-on-enter` — as in `<cupri-textarea value="{{Composer}}" submit-on-enter>` — and answer it
  with `doc.OnSubmit("data-…", handler)`, which is attribute-keyed and bubbling like `OnAction` and
  `OnContext`, with `e.Value` naming the field that submitted.
  It is per-field on purpose: a global Enter shortcut would eat newlines in every other textarea on
  the page. The edit buffer commits BEFORE the handler runs, so it reads the text just typed; focus
  is kept, since a composer goes on composing after it sends; and if no handler claims the submit,
  Enter falls through to its ordinary behaviour rather than vanishing. `submit-on-enter` also labels
  the on-screen keyboard's action key `send` unless you authored an `enterkeyhint`.

- **`OnShortcut` can bind named keys** ([#88]). `"Enter"`, `"Escape"`, `"Tab"`, `"Space"`,
  `"Backspace"`, `"Delete"`, `"Home"`, `"End"` and the four arrows, case-insensitive, alongside the
  single characters that already worked. Same rule as before: a Ctrl chord fires anywhere, a bare
  key only when no field is focused.

### Changed

- **`OnShortcut` now throws on a key that can never be delivered.** Anything that is neither a
  single character nor one of the names above — `"F5"`, `"PageDown"`, `""` — raises
  `ArgumentException` at the call site. **What to do:** nothing, unless you registered a binding
  that has never worked; such a binding was dead before this release and is now loud. This is the
  half of [#88] that matters, since a dead registration was previously indistinguishable from a
  working one.

- **A bare `Escape` shortcut fires below the engine's own dismissals.** An open context menu,
  overlay or video fullscreen still closes first; your handler runs when there is nothing left for
  Escape to dismiss, and before the focused field is blurred — so it still means "cancel" while
  the field being cancelled has focus.

### Fixed

- **Named-key shortcuts were registered but never matched** ([#88]). The lookup was gated on the
  keystroke's text being one character long, and named keys arrive as an `EditKey` with no text at
  all, so the whole block was unreachable for them. `OnShortcut(Ctrl, "Enter", …)` stored
  `"ctrl+enter"` correctly and nothing ever read it.

### Documented

- **Links are not delivered to `OnClick`, and never were** ([#89]). An `<a href>` click is claimed
  by the engine's link branch, so a selector matching an anchor never runs — for any href. Route
  links off `doc.Navigated`, which carries every non-`#` href with `External` separating an in-app
  path from one a host should open in a browser; a host's re-emission of it (`IWebBridge.Navigate`,
  `DesktopHost.OpenExternal`) is the external subset only, which is what made relative and
  custom-scheme links look dropped. Now on `OnClick`'s own XML docs and in a new "Handling input"
  section in the README. **No behaviour changed and no upgrade is needed for this** — `Navigated`
  has carried every non-`#` href with a correct `External` flag for many releases (confirmed on
  0.8.0 in the issue). Only the documentation is new.

[#88]: https://github.com/Wixely/CupriFace/issues/88
[#89]: https://github.com/Wixely/CupriFace/issues/89
[#90]: https://github.com/Wixely/CupriFace/issues/90

## v0.9.0

### Added

- **A context menu can say what it was opened over** ([#85]). `doc.OnContext("data-…", handler)`
  mirrors `OnAction` for the moment a menu OPENS: the handler runs with the element the right-click
  or long-press landed on, its attribute value and the model, so a menu item chosen afterwards can
  act on the row that was actually clicked. `doc.LastContext` exposes the point and hit node for
  apps that would rather `HitTest` themselves. Both fire for a mouse and for the touch recognizer's
  long-press, since both arrive through the same dispatch.
  Name the row in the attribute (`data-msg="{{Id}}"`) and read `e.Value`; `e.Model` is the root
  model, exactly as for `OnAction`, because `data-repeat` discards each item after substituting its
  bindings.

### Fixed

- **A right-click no longer activates what it lands on, in the browser** ([#85]). Both web hosts
  dispatched a click for ANY pointer button, so on the web a right-click pressed the button under
  it and then opened the menu — while the desktop host has always sent right-click to the context
  dispatch alone. That divergence was silent: an app aimed by the accidental click worked in a
  browser and did nothing on the desktop. Both hosts now dispatch a click for the left button only.

[#85]: https://github.com/Wixely/CupriFace/issues/85

## v0.8.0

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

### Changed

- **The two web hosts are one host now** ([#79]). The lifecycle, damage-rect painting, the
  premultiplied→straight alpha conversion, input dispatch, the touch recognizer, the ARIA mirror,
  IME cadence, clipboard and the video backend are written once in a shared core; each package
  keeps only the declarations that reach JS, in whichever way its runtime reaches JS. About 1,000
  lines of duplicated host code are gone, and a call added to one host now reaches both by
  construction. A parity test compares the two surfaces (31 exports, 19 imports) and fails naming
  whichever host is missing one — which is what would have caught the IME gap in #77 the day it
  appeared. Nothing an app writes changes.

[#79]: https://github.com/Wixely/CupriFace/issues/79

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
