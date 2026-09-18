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

- **An app can accept a file dragged in from outside its window (#182)** — on the desktop *and* in a
  browser, through one handler:

  ```csharp
  doc.OnFileDrop(async e => {
      foreach (var f in e.Files)
          if (f.MediaType == "text/markdown") _model.Open(f.Name, await f.ReadTextAsync());
  });
  ```

  Mark the regions that take a drop with `cupri-drop` and `e.Target` is the nearest enclosing one, so
  "drop onto *this* column" is the hit test the engine already did. `e.Target` is null when the drop
  landed on nothing marked — an app that takes files anywhere just ignores it.

  **`DroppedFile` gives you metadata synchronously and bytes asynchronously**, and that split is the
  whole reason it travels. `Name`, `Size` and `MediaType` are all a browser `File` exposes without a
  round trip, so they are in hand and you can refuse something before reading it; the contents come
  from `ReadBytesAsync`/`ReadTextAsync`/`ToSourceAsync`, because a blob read is a promise and there is
  no synchronous form to offer. Dragging in a 4GB video costs nothing until someone wants it.

  `Path` is **desktop-only and null in a browser** — the web withholds it deliberately. Read it to
  remember a location; read the bytes for anything else, or you have written an app that works
  everywhere except the browser.

  **The `:drop-over` highlight only appears in a browser.** A page reports drag-over continuously;
  GLFW hands over the paths on release and says nothing beforehand, and SDL2's `Dropbegin` arrives
  *with* the drop rather than before it. Drops land on the right element on every host — only the
  in-flight highlight differs, so design the zone to read as a target when idle. (Neither desktop
  platform puts coordinates in the drop event either; each host queries the live cursor instead.)

  `DropDriver` scripts the gesture — `Over`, `Leave`, `Drop`, `DropText`, `DropNamed` — for the same
  reason `TouchDriver` exists. It is not a simulation of the browser: a dropped file in a page really
  is bytes with a name. The Showcase's **Diagnostics** page has a live zone to drag a real file onto,
  which is the only way to exercise the half no headless test can reach.

  Dragging *out* of the window is not included: that needs a platform drag source and a data promise,
  and is a much larger thing than accepting a drop.

- `CupriSource.Bytes(origin, content)` — in-memory bytes labelled with where they came from, carrying
  `LocalFile` trust. What a dropped file becomes, and what a browser blob needs since it has no path
  to describe it by.

## v0.25.1

Five silent ones. **Two of these change what an existing app renders**, because both were producing
the wrong thing before:

- if you set `line-height` in `px`, your text is no longer several times further down than you asked
  for — check any layout that was built around the old behaviour;
- your `@keyframes` animations now ease the way they were written, instead of always running linearly.

### Fixed

- **A CSS comment inside `@keyframes` silently corrupted the animation.** The keyframes parser read
  the RAW stylesheet while every other parser got a copy with the comments stripped, so a
  `/* comment */` between two stops was swallowed into the next stop's selector — `"/* one */ 60%"`
  parses as no percentage — and that stop was dropped. What remained was then interpolated across and
  **extrapolated past**: with two comments a bar declared to finish at 545px settled at 714px and
  held there. No exception, no diagnostic, a clean doctor report, and a smooth animation to a wrong
  number. Comments are legal anywhere, and the stops are exactly where an author wants them.

  Also, nothing is extrapolated beyond the first or last keyframe any more, however that is reached.

- **An animation's timing function was parsed and thrown away.** Every keyword was matched and
  discarded, so `ease-out` and `linear` produced identical values at every sample and every animation
  ran linearly. Noticed while isolating the comment bug. The curve machinery already existed for
  transitions; animations simply never asked for it. Both the shorthand and
  `animation-timing-function` are honoured now, and an unreadable one is reported rather than ignored.

- **A `line-height` in px produced a line box font-size/16 times too tall.** A length was divided by
  a hardcoded 16 to fake a ratio — "refined once font-size is known", and nothing refined it. At 48px
  the box came out three times the height asked for; at 16px it was exactly right, which is why it
  looked like a fixed factor. The glyph sits at the bottom of that box, so text landed BELOW its own
  container and everything after it was pushed down the page. **`em` and `%` were not recognised at
  all** and fell back to the default with no diagnostic.

  A length is kept as a length now and inherits as one; `em` and `%` are the ratio they describe; a
  unit the parser does not understand is REPORTED (CF0050) rather than silently replaced with a
  number. The symptom used to appear nowhere near the cause — "the last few elements of my layout
  have vanished off the frame" — and it made a no-JavaScript odometer, a digit column sliding inside
  `overflow:hidden`, impossible to build. There is a test for that shape now.

- **`CupriDoctor` returned findings belonging to other documents.** The sink an ignored CSS property
  was announced through was one field for the whole PROCESS, so a check running alongside anything
  else got whatever happened to be in it. Two checks at once traded findings — the document that
  produced one was as likely to lose it as another was to gain it — and, worse, merely RENDERING a
  document on another thread planted its warnings in a check of a different one. 115 of 120 checks
  of a clean document came back carrying a renderer's warning.

  There was no way around it from outside either: locking every `Check` does not help when the other
  thread is not calling `Check`. The sinks are per-thread now, which is the right scope because a
  document is worked on by one thread; a renderer on another has no hook set and announces nothing.

- **`CupriDoctor.Check(html, null)` silently skipped every CSS check.** A null stylesheet was read as
  "do not look at CSS" rather than "there is no external stylesheet", and it took the document's own
  `<style>` block with it — so a document that keeps its rules where nearly every document keeps them
  reported no problems found. `null` is the obvious argument, and the signature (`string? css`)
  invites it. `null` and `""` now mean the same thing, and a finding is located by looking in the
  markup as well as the stylesheet, since an inline block lives in the first.

## v0.25.0

### Added

- **Scrollbars have a track, and the whole of it is live.** The bar was five pixels wide and the
  only thing you could press was the thumb; the empty space above and below it did nothing at all.
  There is now a wider track column behind it — invisible until the pointer is in it, and then
  visible, with the thumb fattening to fill it.

  A press anywhere in that column acts: on the thumb it drags, above or below it it pages by a
  visible height less an overlap, the way desktop scrollbars have always paged. The press never
  reaches the content behind the column, and a drag survives the pointer leaving it.

  The painter and the hit-test now read one geometry (`CupriFace.Interaction.Scrollbar`, public, so
  an app can reserve a gutter of the right width). They used to work it out separately and had
  already drifted: the hit-test padded the thumb by six pixels on one side and eight on the other to
  make a five-pixel bar catchable, so what you could grab was not where it was drawn.

- **`TouchDriver`: touch you can script, the way `DispatchClick` scripts a mouse.** The gesture
  recogniser was always headless and deterministic; driving it was not. A test had to invent a
  monotonic clock, know the slop radius, know that a long press only fires when the host ticks its
  deadline, and know that momentum comes from the velocity of the last 100 ms before the finger
  lifts. Each is easy to get wrong, and a gesture built wrong does nothing — which reads as a bug in
  whatever was under test.

  One call per gesture now: `Tap`, `DoubleTap`, `TripleTap`, `LongPress`, `Swipe`, `Fling`, `Pinch`,
  `Cancel`, plus `Advance` to let scripted time pass. The clock is owned, so nothing sleeps and the
  same script produces the same events on any machine. `Swipe` ends still and `Fling` ends moving,
  which is the distinction momentum actually turns on. `TouchOptions` moves the thresholds for a
  boundary test, and `Input` exposes the recogniser for anything the verbs do not cover.

  Documented in CLAUDE.md beside the mouse and keyboard verbs, where an agent looking for "how do I
  drive this" reads — it said nothing about touch before, so the honest conclusion from reading it
  was that touch could not be simulated at all.

- **`float-label` on `<cupri-textfield>`: the placeholder becomes the label.** A labelled field costs
  two lines, a label above and a box below; a placeholder-only field costs one and then forgets what
  it was for the moment you type into it. This costs one. The prompt sits where the value will go
  while the field is empty and rises to a smaller line inside the box once there is a value to label.

  **Until someone types, it is a plain field — pixel for pixel**, focused or not, asserted by
  subtraction rather than by eye. Same height, same border, prompt and caret on the same line. The
  box is never touched, so the value, the caret and everything below sit exactly where a plain field
  puts them in both states: a mixed column lines up throughout, and typing the first character moves
  nothing but the label. The risen label lives in the headroom the top padding already provides,
  which is why it is as small as it is. With no `placeholder` the attribute does nothing rather than
  reserving a row for an empty label. Single-line fields only: a `cupri-textarea` scrolls its own
  content and a label pinned inside it would scroll away with the text.

  Risen, the label sits **on** the field's top border with a pill of the field's own surface colour
  notching the line it crosses. Getting it out there meant the field could no longer be the thing
  that clips its own text, so **the engine now scrolls whatever clips a single-line field's text
  rather than assuming that is the field** (`ClipOwner`). For every existing field those are the
  same element and nothing changes; it lets a component put a clip closer in, which is what keeps a
  long value inside the box while the label hangs over the border.

  It is also better *named* than a plain placeholder. The label carries the placeholder class, so the
  accessibility tree keeps it out of the field's value and uses it as the field's name in **both**
  states — an ordinary placeholder is only rendered while the field is empty.

- **Borders differ per side.** `border-left` / `-right` / `-top` / `-bottom`, the `border-*-width`
  and `border-*-color` longhands, and the one-to-four-value forms of `border-width` and
  `border-color` all resolve now. Width and colour are per edge; **`border-style` stays whole-box**,
  so a dashed left beside a solid top is not expressible and the last style parsed wins.

  Both halves used to fail, and differently. `border-left: 3px solid #fbbf24` was not in the
  property switch at all, so it was discarded and CF0050 said so. `border-width: 1px 0 0 0` **was**
  in the switch, and handed the whole string to the single-length parser, which failed and fell back
  to zero — so the box lost its border and nothing reported anything, because the property name was
  known.

### Changed

- **Components look slightly different, because their own stylesheets finally apply.** Tabs, the
  accordion, table rows, the number field's stepper and the Markdown blockquote have always carried
  per-side border declarations that were silently thrown away. They paint now: the tab strip gets its
  rail and the active tab its copper underline, accordion items and table rows get separators, the
  stepper gets its divider. Nothing was restyled — the declarations were already there. Borders take
  space, so content below them shifts down by one or two pixels.

  **If you relied on one of those declarations doing nothing, it no longer does.** A border occupies
  width: a 1px divider on one cell of a flex row makes that cell a pixel wider than its neighbours.
  Where a divider must not change layout, use `box-shadow: inset -1px 0 0 …`, which is what the
  resizable table's column divider now does so its header cells stay aligned with the body.

### Fixed

- **Pages that used inline `<code>` chips were painting five percent wrong, and nobody could see
  why.** A chip lays out as a 0x0 box with padding, which gives it a negative content height — so
  "content taller than the box" was arithmetically true of an element with nothing in it, and the
  scrollbar code took it for a scroll container. Its thumb height then divided by a zero content
  height and came out **infinite**, and its position multiplied zero by that infinity and came out
  **NaN**.

  No bar was ever visible, because a rectangle at NaN is nowhere. What it did instead was disturb
  what was composited after it: the Showcase's gradient swatches, further down the same page,
  painted visibly washed out. Three pages were affected. It was found by differencing screenshots
  over an unrelated change, and there is now a test that asserts no frame contains non-finite
  geometry at all — run against real Showcase pages, because the page that had the bug is not one
  anybody would have thought to check.

- **A carousel could not be moved at all with an ordinary mouse.** It scrolls sideways and only
  sideways, and both ways of reaching that axis needed particular hardware or a hand: a horizontal
  wheel, or a finger. A plain wheel has no horizontal component and a scroll box is not a drag
  surface, so on a desktop there was no way to move it — the component read as broken while every
  part of it worked.

  Two fixes. **A wheel over a scroller that can only move sideways now moves it sideways**, which is
  what browsers do, and chains outward at its end exactly as the vertical axis already did. And a
  scroll box can opt into being pushed by hand with **`data-drag-scroll`**, which `cupri-carousel`
  sets on its viewport.

- **Pasting no longer mangles every non-ASCII character on the GLFW desktop window.** GLFW's
  clipboard is UTF-8 and Silk's binding for it decoded those bytes as the ANSI code page, so
  `a—b€ü` arrived as `aâ€”bâ‚¬Ã¼` — and copying OUT wrote the same mangling back for whatever read
  it next. The entry points are called directly and marshalled as UTF-8 now, which is what the SDL
  software window already did. Measured both ways: the raw bytes round-trip exactly, emoji included.

- **A held key repeats on the GLFW desktop window.** Holding Backspace deleted one character and
  stopped; so did holding an arrow. Silk's GLFW input backend raises KeyDown for a press and has no
  case for a repeat, so GLFW's own repeats were dropped — while TYPING repeated fine, because the
  character callback does fire on repeat, which made a field feel broken rather than unfinished.
  450 ms, then about 30 a second, and a stall does not come back as a burst. Tab and Escape stay
  one-shot on **both** desktop windows now: a held Tab flew through the focus ring on the SDL one.

- **↑/↓ move the caret in a multi-line field.** They did nothing at all: the focus-movement branch
  runs only when no field is focused, so the arrows reached the insert case, found no text and
  stopped. A textarea could not be walked vertically by keyboard. They move a VISUAL row now (a soft
  wrap counts, as in a browser) and keep the caret's column across a run of them.

- **Home/End are scoped to the line in a multi-line field.** They jumped to the start and end of the
  whole buffer, so Home in a long note went to the top of it and Shift+Home selected everything above
  the caret. A single-line field still takes Home/End to the whole value, which is what `<input>` does.

- **Backspace deletes a character, not a code point.** `é` written as `e` plus a combining acute took
  two presses and left a bare `e` after the first, which reads as a keystroke that did not work. One
  Backspace now takes a whole grapheme — a combining accent, an emoji, a family emoji joined by
  zero-width joiners. Delete does the same forwards.

- **Text from outside is cleaned on the way in.** A paste from a PDF, a spreadsheet or a terminal
  carried its control characters straight into the model: a NUL or a vertical tab became part of the
  app's data. Those are stripped now, tabs and newlines are kept, and every flavour of line break
  (CRLF, a lone CR, U+2028/U+2029) normalises to `\n`. The same applies to text pushed in by a
  platform editor — the browser's real `<textarea>`, Android's input connection — where a paste never
  reaches the keystroke path at all.

- **`CupriFace.Android`'s CoreCLR pin now actually reaches apps that consume the package.** It
  never has. The pin sat in the package's `buildTransitive/*.targets`, guarded on the property
  being unset — and NuGet imports a package's `.targets` long after the Android workload has
  already defaulted `UseMonoRuntime` to `true`, so the guard could never be true. Right shape,
  wrong file. **Every consuming app shipped Mono and died in `OnCreate` before its first frame**,
  which is the exact crash the pin exists to prevent. Moving the same one line to a
  `buildTransitive/*.props` fixes it: from there the Android SDK's own "only if unset" condition
  correctly declines to overwrite it.

  Nothing in this repository could have caught it. Every Android app here sets `UseMonoRuntime` in
  its own csproj, because buildTransitive does not cross a `ProjectReference` edge, so the
  package's build contribution had never been exercised by a single build in the tree that produces
  it. `tests/PackageConsumer` is now a CI gate that consumes the package the way an outside app
  does and asks MSBuild which runtime it resolved to.

  **If you carry a hand-written `UseMonoRuntime=false` in your Android csproj, you can delete it.**
  Keeping it is harmless. And an app that opts back INTO Mono now fails the build with
  **CUPRI0001**, naming the cause, instead of producing an APK that crashes on a device; set
  `CupriFaceAllowMonoRuntime=true` if you want to build it anyway.

- **The `XA1040` warning now says who caused it.** Choosing CoreCLR makes the Android SDK warn that
  the runtime is "an experimental feature and not yet suitable for production use" — accurate, and
  completely silent about the fact that this package forced the choice. A consumer got a production-
  readiness warning in a build they did not configure, with nothing connecting it to CupriFace.
  The build now prints the reason next to it, and `PACKAGE.md` states the trade in full: XA1040
  fires for **any** non-Mono runtime (NativeAOT included), so Mono is the only runtime it stays
  quiet about and the only one that crashes — there is no setting that is both quiet and working.
  It clears when CoreCLR on Android stops being experimental, which is a *different* upstream event
  from Mono's defect being fixed. `CupriFaceQuietRuntimeNote=true` silences the note,
  `<NoWarn>XA1040</NoWarn>` the warning. CUPRI0001 says the same thing from the other direction.

- **The Showcase's Markdown page can be opened by name again.** `--section markdown` silently landed
  on Inputs, and an internal link naming it did nothing, because the set of routable section ids was
  a hand-written copy of the sidebar and the Markdown page had been added to one and not the other.
  Neither failure reported anything: you got the default page and assumed you had mistyped the id.
  The ids are read out of the sidebar markup now, so the two cannot drift, and a test walks every
  page the sidebar offers and opens each by name.
## v0.24.1

### Fixed

- **A grid row is as tall as its items' margin box.** It was sized from their border boxes while the
  margin was still applied to the item's position, so an item with a vertical margin overflowed its
  row — and the grid — by exactly that margin, eating whatever padding sat below. Reported from the
  colour picker, whose neutral ramp is the last row of the same grid and is set apart by a 9px top
  margin: the popup declares 10px of padding and 1px of it survived under the greys. A stretched
  item now also leaves room for its own margins instead of overflowing its cell by them. Every
  Showcase page renders pixel-identical, so this reaches only grids whose items carry margins.

- **`CF0060` no longer accuses correct markup when a `data-repeat` is nested inside another (#160).**
  Repeat scopes were collected by resolving every `data-repeat` name against the ROOT model type
  alone, so a collection living on an *item* type — a per-row detail panel, an options group, a
  thread — resolved to nothing, its element type never entered scope, and every `{{path}}` inside it
  was reported as naming nothing. At `Severity.Error`, which says "this will not appear on screen",
  against markup that renders perfectly. Scopes now resolve to a fixed point: each repeat name is
  tried against every type already in scope until a pass adds nothing, which is what the docstring
  already claimed ("retried against the element type of every repeat collection in the document").
  Bounded by the number of distinct row types, and a self-referential row type terminates. Measured
  on the app that reported it: **3 errors before, 0 after**, with its 5 warnings unchanged.

- **`border-radius` takes a percentage, and every corner takes its own value (#162, #163).** Both
  were parsed by handing the whole declaration to a single-number parser, which failed and fell back
  to zero — so `border-radius: 50%`, the standard circular avatar, painted a **square**, and
  `border-radius: 14px 14px 0 0` painted **no rounding at all** rather than rounding the top. The
  second is the worse failure: the author asked for some rounding and got none, with the
  single-value control right beside it working, which makes the cause look like anything but the
  value. The shorthand now expands the usual way (one value every corner, two TL/BR then TR/BL,
  three adding the bottom left, four clockwise), takes the `A / B` two-axis form, and the four
  `border-*-radius` longhands work. A percentage resolves against the box at paint time —
  horizontally against its width and vertically against its height — so `50%` on a rectangle is the
  **ellipse** CSS says it is rather than a circle. Hit testing follows the painted shape per corner:
  a box rounded only at the top still takes a click at its square bottom corner.

  Four things in this repository had been asking for this and silently not getting it: the bar
  chart's bars (`5px 5px 0 0`), the line chart's dots and the Styling page's colour swatches
  (`50%`), and the bottom sheet (`18px 18px 0 0`). They render as their stylesheets always asked.
  The committed screenshots predate that and are correspondingly stale; regenerating them is a
  separate pass, because this machine's fonts would drift every image in the set.

- **`align-items` works on a column flex container (#161).** Centring a heading in a column — the
  most common flex idiom there is — did nothing. A column's cross axis is the WIDTH, and an auto
  width on a block fills its container, so the item was already the full width, there was no free
  space to centre it in, and its text simply sat at the left. An item with an explicit width centred
  correctly, which made it look like an alignment bug rather than a sizing one. An unstretched item
  now shrinks to its content first, exactly as an auto width already did on the main axis of a row,
  and `align-items` then has something to move. `stretch` — the default — still fills the container.
  Worth knowing if you relied on the old behaviour: `align-items: flex-start` was **also** stretching
  the item and only looked right, so a flex-start item with a background now hugs its text.

## v0.24.0

### Added

- **`PresentInfo.Adaptive(w, h, designW, designH)` — spends surplus, never crushes (#153).** Use it
  instead of `Hybrid` when a layout has a DESKTOP design size and also has to meet a phone. Hybrid
  scales down as readily as up, and the logical viewport is `window / scale`, so below the design
  size the viewport comes out **wider** than the design: a 1280-wide design on a 412dp phone lays
  out at 1280 logical and paints at 0.32x, which is 14px text at about 4.5dp. Worse, the layout has
  no way to answer — `@media (max-width: …)` is evaluated against that logical width, so no
  breakpoint below the design width can ever match, on any device. `Adaptive` is Hybrid above the
  design size and `Responsive` below it, which hands the phone back its own width. Measured against
  the Showcase at 393x771: under Hybrid the sidebar cannot become its icon rail; under Adaptive it
  does. Nothing else changes, and `Hybrid`'s own behaviour is untouched — but its documentation now
  says which way it scales, because the old wording ("on a phone that usually means fill the width,
  scroll the length") described the opposite of what it does with a desktop design size.

- **`doc.ViewportWidth` / `doc.ViewportHeight` — the size the document was laid out in (#155).**
  Post-`Zoom`, so it is the size `@media` was evaluated against and `vw`/`vh` resolved to: an app
  comparing the host's logical width to its own breakpoints instead disagrees with the cascade the
  moment zoom is not 1, and the disagreement is invisible until someone zooms. Zero until the first
  layout. What it is for: a responsive control with three states (auto, forced open, forced closed),
  which is the minimum a collapsible sidebar needs, because a two-state flag cannot override a media
  rule in both directions — such a toggle means "the opposite of what I can currently SEE". The
  engine's own Showcase had been shadowing the width from `Present()` every frame to answer that,
  and now reads it from the document (which also fixes the shadow being the pre-zoom width).

- **`CF0072` — CupriDoctor sees horizontal overflow (#154).** It was vertical only, which left the
  exact failure mode of a desktop layout on a phone undetectable: fixed columns adding up to more
  than the viewport simply run off the side, with nothing on screen to say the missing part exists.
  `CupriDoctor.Check(html, css, width: 412, height: 915, model: model)` is now "does this survive a
  phone" as a single CI assertion.

  Two things make it quiet enough to leave on. It has no pinned-width requirement, because width is
  not height — a block box's `auto` width is filled from its parent rather than grown from its
  content, so the overflowing box is usually one that was never given a width at all, and requiring
  a definite width would have missed every real instance. And it reports only overflow that reaches
  an edge that LOSES content: the viewport, or an ancestor with `overflow:hidden`. An
  `overflow:scroll` ancestor exempts everything inside it, because the content can be dragged to.
  Measured on the Showcase: the geometric rule alone reported two harmless cases at the design size,
  and this one reports none at either the design size or a phone's — which is now a gate. Only the
  OUTERMOST box that runs off the edge is reported, because everything inside one is off the edge
  too: a chrome of three fixed columns holding an over-wide card produced three findings for one
  visual failure before that rule, and fixing the first is what decides whether the others were ever
  real.

- **`<cupri-virtual height="auto">` takes its height from layout (#152).** A virtual list could only
  be sized by its `height` attribute, which the component wrote as an **inline** style — beating
  every stylesheet rule, `@media` rule and flex rule there is. So a virtual list could never fill
  the space its chrome leaves, and an app whose main surface IS the list had to compute the height
  itself and push it through the view model on every resize, which is re-implementing layout outside
  the engine.

  The trap underneath it was worse than the limitation. The author's own `style` is appended AFTER
  the component's, so `<cupri-virtual height="300" style="height:100%">` genuinely wins the paint —
  but the binder windows off the ATTRIBUTE, so the list paints full height and materialises 300px of
  rows. Measured: a list laid out at 940px with `height="300"` leaves a **340px blank strip** at the
  bottom of the viewport once it is scrolled. It looks perfectly finished until someone scrolls it.

  With `height="auto"` — **or no `height` at all, which now means the same thing** — no inline height
  is written, so the box belongs to the cascade, and the binder windows off the height the last
  layout measured. That is the same bind-before-layout route the measured row pitches already take,
  capped at the document's own viewport so an unconstrained list cannot grow itself a frame at a
  time. A NUMBER still becomes an inline height and still beats every stylesheet rule: naming one is
  how an author says "this size, and I mean it". The Showcase's list now takes its 224px from the
  stylesheet, rendering pixel for pixel what it did.

  **Upgrading.** A list that names a numeric `height` is unaffected. A list with NO `height`
  attribute still comes out 300px and still scrolls, because that default moved from an inline style
  into the component's own stylesheet — so the only change is that your CSS, a `@media` rule or a
  flex parent can now override it, where previously nothing could. If a list of yours was relying on
  a stylesheet rule being ignored, it will now be obeyed. (The default did not become CSS `auto`:
  that was measured and rejected, because a scroller with no constraint grows to its whole content
  and a bare 2,000-row list came out 80,000px tall with nothing to scroll — worse than the arbitrary
  number it replaced.)

- **A text field on the web is now a real `<input>`, so the browser's own editor works on it (#133).**
  The canvas had one hidden textarea following the caret: typing and IME worked, nothing else did.
  A screen reader saw a `role="textbox"` it could not edit, and a password manager saw no field at
  all — `AutofillHint` was carried by the engine and unused by the web. Every leaf text field in the
  accessibility overlay is now a transparent `<input>` (or `<textarea>` when multiline) positioned
  over the painted field, carrying its kind, `placeholder`, `inputmode`, `enterkeyhint` and the
  author's `autocomplete`. While one holds focus the **browser owns the text, the selection, the IME
  and its own undo**, and reports them through `CupriDocument.SetEditText(text, selStart, selEnd)` —
  a new seam that edits the permissive buffer exactly as typing does, so a value that is invalid
  mid-edit is still not clamped under the cursor. The engine keeps the keys that are not editing:
  Tab, Escape, Enter (so "Enter sends, Shift+Enter starts a new line" still holds), a combobox's
  arrows, and app chords. A value the engine rewrites — a clamp, a reformat, a picked suggestion —
  replaces what the browser holds and restores the caret the engine reports. Nothing to do in an
  app. There is no change on any other host.

  **A password is never published.** A masked field's plaintext stays out of the DOM, as it stays
  out of every other bridge: its `<input type="password">` is a FILL TARGET, carrying the author's
  `autocomplete` so a manager can find and fill it, and a fill arrives through the binding the way
  any autofill does. The engine goes on owning the typing for a masked field. So filling works and
  saving a newly typed password does not.

- **A text field keeps its accessible name once there is text in it** — on every bridge, not just
  the web. A component may keep the author's attributes on the custom element, and `<cupri-password>`
  does: its inner `role="textbox"` carried neither the label nor the placeholder, and the
  placeholder is only rendered while the field is empty. So a password field became nameless the
  moment someone typed into it, and a screen reader announced it as "edit". The name now falls back
  to what the author wrote on the component — and to nothing else: a control inside a labelled group
  stays nameless rather than borrowing a name that reads as true.

- **The web host's accessibility mirror is now a bridge (#133).** A screen reader could read the
  canvas and not use it: activating the mirror's "Dark mode" switch left the model unchanged, the
  mirror had no geometry (a 1×1 clipped div), and focus was never announced. The mirror is now a
  transparent overlay positioned over the canvas — every node at its control's bounds, carrying
  `data-path` and `tabindex="-1"` — so an AT gets hit-testing, a focus ring and touch exploration,
  a `click` dispatched to a node runs `AccessibilityActivate(path)` (the same entry point the four
  native bridges use), focus arriving on a node runs `AccessibilityFocus(path)`, and DOM focus
  follows the engine's so a focus change is announced the way UIA's focus-changed event is. Both
  web hosts: three new exports each (`A11yActivate` / `A11yFocus` / `A11ySetValue`), published for
  automation as `__cupri.a11yAct`. The overlay is `pointer-events:none`, so a real pointer still
  reaches the canvas, and republishes patch the live DOM keyed by `data-path` rather than replacing
  `innerHTML`, which would tear focus off the node holding it on every settled frame. Nothing to do
  in an app. `BuildAriaHtml` takes an optional `presentScale` so the overlay scales with the
  canvas; `AriaHtml.Serialize` takes the same. Text fields were left on the hidden keyboard textarea
  by this change and are handled by the real-`<input>` entry above, which shipped in the same
  release. Gated in `tests/WebTouchGate/A11yTests.cs` against Chromium's accessibility tree.

  Three engine fixes the gate forced, each of them measured rather than reasoned:
  - **The web host published the mirror only on frames whose pixels changed.** The frame after an
    animation ends is usually identical to the last animated one, because the transition already
    painted its end state — and that settled frame is the one the mirror is published on. A
    dark-mode toggle animated its theme change, the publish was throttled through the animation,
    and the settled frame returned on "identical" before reaching it: the switch flipped, the
    mirror never said so. Only the blit is gated on damage now; the mirror, IME placement and
    underlay sync are not.
  - **Focus on a roleless clickable row is announced on the control inside it.** A row with a click
    handler wrapping the switch it toggles (the Showcase's "Dark mode" row) is the Tab stop, and
    it has no role, so no bridge — web or native — had anything to announce when Tab landed there.
    The tree now reports focus on the first control inside such a row.
  - **A control that is not itself a Tab stop can still be focused by path**, and a click on it
    continues the Tab order from the stop that owns it. `AccessibilityFocus` on the switch above
    was refused, because the switch sits inside the row and `Focusables` counts a control once, at
    the outermost; the lookup now climbs to that stop. The same lookup runs after a mouse click,
    which used to reset the Tab order to the top of the document after clicking a nested control.

- **`doc.Settle(width, height, timeout?)` — render until the next frame is complete.** Returns false
  on timeout rather than handing back a frame with holes in it. It is a loop, not a flag, for two
  measured reasons: a remote image is not fetched until a layout asks for it, so **`IsLoaded` is
  `true` before the first render because nothing has *started*** — a caller polling it to decide the
  first frame is ready captures the frame without the image — and an image that arrives can change
  the layout enough to pull a further image into view, so the condition is "a whole render left
  nothing pending". `@font-face` needs no waiting; those sources resolve synchronously inside
  layout. `tools/Screenshots` now uses it, and **skips a capture that did not settle** rather than
  overwriting a committed image with an incomplete one, exiting non-zero so CI cannot publish a set
  with holes quietly.

### Fixed

- **A screen reader gets the web host's accessibility tree without anyone clicking first.** The ARIA
  mirror was published only on a settled frame, and the frame after the last animated one was never
  painted — so on a page that animates from load the mirror stayed empty until the first input.
  Measured: zero nodes 20 s after boot, 45 nodes 109 ms after a click. It now publishes on the
  settled frame and once a second while animating.

- **The mirror carries what the tree carries.** Scrolled-away content is `aria-hidden` (the desktop
  bridge already reported `IsOffscreen`), a field's text content is its value rather than its name,
  and `data-automation-id` is emitted. The `tabindex` attributes are gone: the engine owns Tab and
  stops the browser's default, so a tab stop in the mirror was unreachable — measured — and said
  otherwise. **It is still read-only**: an AT can read a control but not operate it, and it has no
  geometry. That is the next piece of work: #133.

- **An empty field no longer reports its placeholder as its value — on every bridge.** The tree took
  a field's value from its rendered text, and an empty field renders its placeholder in the same box;
  UIA's `IValueProvider.Value` was wrong in the same way, so this was found on the web and fixed for
  all five. The mirror-image fault is fixed too: a field's **name** was its typed text (a picker's,
  the date it held), which told a screen reader nothing about what the control was for. A field is
  now named by `aria-label`, else its placeholder, else nothing — **so label your fields**: a
  `cupri-number` or a picker with neither is nameless, and the gates say so.

- **The pagination arrows have names** ("Previous page", "Next page"), from the component. Two
  nameless buttons on every page that used it, on every bridge.

### Gates
- **The web host has an accessibility gate** (`tests/WebTouchGate/A11yTests.cs`), the first of the
  five bridges' gates to be missing. It asserts through Chromium's own accessibility tree — the tree a
  screen reader reads — with no prior input: the mirror is populated on arrival, controls resolve by
  role and name, every interactive node has a name, fields read values not placeholders, offscreen
  content is hidden, and Tab stays with the engine.

## v0.23.0

### Added

- **The SDL window can own a real GL context: `CUPRIFACE_SDL_GL=1`.** GPU rendering and touch in one
  window, which neither existing path could offer — the GLFW window has the GPU but no touch API,
  the SDL window had touch but rasterised on the CPU. This draws straight into the window's
  framebuffer and swaps: no readback, no hidden window, no new dependency. GPU surface producers run
  on it unchanged (the Showcase 3D page brought its shared-GPU lane up on the SDL context). Opt-in
  for now; `CUPRIFACE_SOFTWARE=1` still wins a tie. Choosing it automatically on touchscreen machines
  is a policy decision not yet made. Known gap: on a scaled Wayland desktop the drawable is larger
  than the window and that ratio is not yet folded into D — the startup line says so when it happens.

- **Touch adjustment: a finger that lands beside a control presses it.** `DispatchTap` is a click
  from a finger; within `TouchAdjustRadius` (12 logical px by default, 0 to disable) it moves the
  tap onto the nearest interactive element — to the nearest point inside its box, so a slider edge
  stays an edge. A tap already on a control is never moved, a disabled control never attracts one,
  and the snap is verified by a real hit test so nothing under an overlay can be reached through
  it. Fingers only: the mouse keeps `DispatchClick` and means what it points at. Wired on the
  desktop, Android and web hosts. Reported from a Steam Deck as touch being "a bit too accurate",
  which is what a 1 px pointer feels like under a 9 mm fingertip.

### Fixed

- **Desktop builds deliver touch (#143).** The SDL window handles `SDL_FINGER*`: each finger gets a
  pointer id of its own from 1 (the mouse keeps 0), coordinates are scaled from SDL's normalised
  0..1 into the same space the mouse arrives in, and mouse events SDL manufactures from touch are
  dropped — every tap arrived twice before. Measured on a Steam Deck. The GLFW window is untouched
  because GLFW has no touch API; X11 emulates a mouse from touch and Wayland does not, which is why
  the same build looked fine in a desktop session and was inert in Game Mode. Reaching the SDL
  window on a machine with working GL needs `CUPRIFACE_SOFTWARE=1` or `CUPRIFACE_SDL_GL=1`.
- **Taps no longer accumulate phantom fingers.** An uncaptured lift was routed past the engine, and
  the page-zoom tracker only forgets a finger when it sees its Up — so two taps looked like two
  fingers and the next drag became a pinch against a meaningless baseline ("any kind of drag
  massively zooms in"). Every pointer phase now goes through `DispatchPointer`. It was never
  touch-only: a mouse click left pointer 0 on the books the same way.
- **A hovering mouse is not a finger.** The fix above exposed its mirror image on the Steam Deck:
  routing every mouse Move through the pointer path registered the trackpad cursor — which never
  lifts, because a hover has no Up — as a permanent finger on the page. The first real finger then
  arrived as the second of a pair, its Down was consumed as a pinch, and no tap reached a click
  while every drag zoomed. The engine now ignores a Move for a pointer it never saw go Down. This is
  in `CupriDocument`, so every host gets it.
- **`CupriDoctor` no longer accuses working markup (#145).** Reproduced against the reporter's real
  app and model — 19 findings, of which 13 were false — and now 6, all real. Four faults, each with
  a test that fails without the fix:
  - **`CF0031` on elements inside hidden pages.** With a real model most of an app is
    `style="display:{{PageDisplay}}"`, and everything inside a hidden section is absent from the
    render tree by design. That was read as "never drawn". Hidden — by inline style, stylesheet
    class, `hidden`, or `aria-hidden="true"` — is now exempt, and only the root of a genuinely
    missing subtree is reported, not every descendant.
  - **The line number pointed at the wrong element.** Findings were attributed to the *first*
    element sharing the tag, so a hidden page's paragraph was reported as the visible subtitle on
    line 6. Findings now name the actual occurrence.
  - **`CF0020`/`CF0031` on `<cupri-option>`.** An option is data its parent select consumes;
    rendering nothing is its job. Anything inside a registered component's subtree is exempt.
  - **`CF0070` on every fixed-height button.** The rule measured a border-relative extent against
    the content box — two mistakes at once. Children are positioned relative to the parent's
    border box (padding included), and CSS overflow clips at the *padding* edge, so a centred
    label sitting in the padding is not overflow. Measured against the padding box in the right
    coordinates, the reporter's buttons stop firing and the genuine overflows still do.
- **`DumpTree` coordinates were too far in by the parent's padding.** The walk added the content
  inset to child positions that already include it, so the coordinates it offered for
  `DispatchClick` were wrong exactly where a small target made it matter. Now they match
  `HitTesting.AbsoluteBox`, with a test that compares the two under a padded parent.
- **`CF0071` on overlay hosts.** A zero-height element whose content is entirely `position:fixed`
  or `absolute` is a dialog anchor, not a collapsed box. Out-of-flow and hidden content no longer
  count as "visible content that has nowhere to go".
- The `RenderNode` comment that said child coordinates are "in parent content coordinates" was
  wrong, and two diagnostics were written to it. It now states the border-box convention that
  `HitTesting.AbsoluteBox` has always used.

## v0.22.0

### Added

- **Four new `CupriDoctor` checks, for the failures that leave no trace.** Pass `model:` to unlock
  them — without it the two most valuable are skipped rather than guessed.

  - `CF0060` — a `{{path}}` that names nothing on the model. An unknown property resolves to null,
    null formats as the empty string, and the element renders perfectly with nothing in it, so on
    screen it is indistinguishable from data that has not loaded. Comes with "did you mean", and
    understands `data-repeat` scopes so list templates are not accused.
  - `CF0070` — contents that do not fit a fixed-height box. They do not clip (`overflow: visible` is
    the CSS default): they paint over whatever follows, because the next sibling is positioned using
    the declared height. **The screenshot misleads here** — the symptom is two unrelated elements
    drawn on top of each other, which reads as a z-order bug rather than a height that is too small.
  - `CF0071` — a box that laid out with no area while holding visible content.
  - `CF0080` — characters no installed font can draw, which paint as empty `.notdef` boxes. A
    warning, not an error: it is a property of the machine, not the document. It under-reports on
    macOS, whose LastResort face matches every codepoint — trust a finding, never its absence.

- **`CupriDocument.DumpTree()`** — the laid-out tree as indented text, with absolute positions and
  sizes and the two problem shapes flagged inline. An image shows you *that* something is wrong;
  this shows you *what*. Greppable, diffable between runs, assertable in a test, and the coordinates
  are the ones to hand to `DispatchClick`.

- **`ImageDiff.Compare` / `ImageDiff.Visualise`** — how much changed between two renders, where, and
  a picture with the changed pixels in magenta. Turns "did my change touch anything it should not
  have" into a number. Tolerance defaults to 8 so antialiasing is not reported as change.

- **`CLAUDE.md` and `AGENTS.md`** — the repo had neither, so nothing told an agent starting work that
  any of the above existed. The headless check-render-look loop is now the first thing in both.

### Fixed

- **A refused frame no longer kills a transparent Windows app.** `UpdateLayeredWindow` and
  `GetWindowRect` fail transiently during ordinary desktop upheaval — a session lock, an RDP
  transition, a monitor change — and per-pixel alpha presentation runs them on every frame, so an
  exception there turned a compositor hiccup into a dead process. A refused frame is now dropped and
  counted, the window keeps what it last showed, and the next frame retries. Construction still
  throws: failing to make a window layered at startup is permanent, and the caller must not show a
  window it cannot present to.

- **Transparent windows repaint while you drag them.** The resize watch is the only thing that runs
  during an OS modal drag loop — the frame tick is starved until the mouse comes up — and layered
  windows were returning from it immediately. That meant no frames at all during a drag, and the
  #137 DPI poll never ran mid-drag. It now streams frames again; what it skips is feeding the SDL
  event's own (stale) coordinates back as a size, because `UpdateLayeredWindow` sizes the window
  itself and the settled outer rect is read from the window instead.

- **Transparent windows report `modal frames` alongside `resize frames`.** Both count work done from
  inside the SDL event watch — `ModalFrames` every frame, `ResizeFrames` the subset driven by a size
  change. Read a zero carefully: a frameless window has no OS resize border and SDL raises no MOVED
  event for a move it initiated itself, so an ordinary `data-window-drag` on a single monitor leaves
  both at 0 no matter how healthy the path is. They climb for geometry changes the OS initiates —
  which is what a cross-monitor DPI change is, and the case the early return used to swallow.

### Changed

- **`ThreadedRender` under layered GPU presentation now says it is ignored.** The two cannot both own
  the frame — one rasterises on a background thread into the CPU bitmap, the other draws on the GL
  context and reads back on the UI thread. It was already ignored; now it is ignored out loud.

- **The layered GPU readback is measured, not described.** Transparent windows print their dropped
  frames, resize-frame count and average readback cost a few seconds in, so the price this mode pays
  for working alpha is a number rather than a caveat. Measured at **0.3–0.5 ms** per frame at
  510x336 (RTX 5090 and GTX 1060), i.e. well inside a 60 fps budget.

- **The transparent HUD sample shows a live pulse instead of fixed numbers.** Its readout was
  hardcoded strings, so a frozen window looked exactly like a working one — the animation is driven
  by the render, so it stops dead when frames stop. `TransparentHud.csproj` also gained the
  `IncludeAllContentForSelfExtract` that single-file publishing needs, which `DpiProbe.csproj`
  already documented.

## v0.21.0

### Fixed

- **The caret moves when you type a space at the end of a field.** It was measured against the
  PAINTED text row, and line layout drops trailing whitespace (correct for prose — a line should not
  end in a visible gap), so the caret stopped at the last non-space glyph and typing more spaces
  moved nothing. It is now measured against the logical value. Affected `cupri-textfield`,
  `cupri-textarea` and `cupri-search`.

  **No text was ever lost**: the model held every space throughout. But text you cannot see plus a
  caret that does not move is indistinguishable from text that was discarded, which is how it was
  reported. Every text control now has tests for both halves — the value keeps the whitespace, and
  the caret advances over it.
- **The Showcase's Keyboard-page dropdown can be opened.** It was written
  `<cupri-select value="{{Plan}}">` with no `open="{{Flag}}"`, and a control that opens a panel keeps
  its open state in the MODEL — so it expanded, laid out, drew its trigger and was dead.

  **Worth knowing if you use `<cupri-select>`, `<cupri-popover>`, `<cupri-drawer>` or the pickers:**
  without an `open` binding they can never open, and the click is reported as HANDLED either way, so
  nothing at any layer tells you. `CupriDoctor` now reports this as `CF0021`.
- **`<cupri-markdown>` no longer hangs on an h4.** Any line starting with `#` that was not `# `,
  `## ` or `### ` — an h4/h5/h6 heading, or a bare `#hashtag` — matched no heading branch, fell
  through to the paragraph branch, and was rejected by that branch's own `!StartsWith("#")` guard.
  Nothing was consumed, the index never advanced, and the renderer spun forever on one line. Markdown
  is routinely text somebody else wrote, so that was a denial of service rather than a cosmetic
  fault. The paragraph branch now always consumes the line that reached it, so forward progress is a
  property of the branch rather than of a guard a future block type could contradict.
- **An image renders as an image.** `![alt](src)` used to emit a literal `!` followed by a link,
  because the link rule matched from index 1. Images are matched first, the link rule refuses a
  leading `!`, and the result is a `<cupri-image>` — not a raw `<img>`, which the engine has no
  primitive for and which therefore rendered as an empty box.

### Added

- **`<cupri-markdown>` covers more of the syntax**: headings to `######`, ordered lists (`1.` / `1)`),
  blockquotes (`> `), thematic breaks (`---` / `***` / `___`) and `~~strikethrough~~`. Still a
  subset, still no dependency, and still escaped before any inline rule runs — so raw HTML in the
  source stays text and can never become markup.
- **A Markdown page in the Showcase** (`samples/DemoApp`) with a live editor beside the rendered
  output, plus panels for each shape that used to break.
- **A copy button on `<cupri-markdown>` code blocks**, top right. It hands over the RAW source, not
  the rendered block — the `<pre>` is a stack of divs with non-breaking spaces standing in for
  indentation, so reading its text back would lose the line breaks and mangle the indentation.
- **`CupriDocument.ClipboardWriteRequested`** — the document asking its host to put a given string on
  the clipboard, raised by any control carrying `data-cupri-copy`. Separate from `ContextRequested`,
  which copies the *selection*; this supplies text the user never selected. Wired in all three hosts
  (desktop, browser, Android). **If you maintain a host, subscribe to it** alongside
  `ContextCommand.Copy`, or copy buttons will silently do nothing.
- **`CupriDoctor.Check(html, css)`** — a development-time check that names what will not work before
  you go looking for it on screen: unbalanced tags (reported at the line they *opened* on), `<img>`
  and other browser habits pointed at their `cupri-*` equivalents, unregistered `cupri-` tags with a
  "did you mean", `<script>` and `onclick=`, and CSS properties or functions the engine silently
  ignores. `report.IsClean` / `report.HasErrors` drop straight into a unit test. The checks read
  from the engine — the render tree, the component registry, the style resolver — rather than from a
  list that would drift, so adding a feature to the engine stops the checker complaining about it.

## v0.20.0

### Added

- **Installable fonts.** A document can carry its own faces instead of depending on what the machine
  has:
  - `@font-face { font-family: X; src: url(…) format(…); font-weight: 300 700; font-style: italic }`
    in any stylesheet (app CSS, component CSS, `<style>`). Sources are tried in order; a `url()` takes
    the same forms an image `src` does (embedded resource, file path, `file:`/`https:` URL, `data:`
    URI); `local()` is skipped. The declared family, weight (or range) and style are what the cascade
    matches, overriding the file's own names.
  - `doc.LoadFont(CupriSource)`, `doc.LoadFont(string src)`, `doc.LoadFonts(directory)` (`.ttf`
    `.otf` `.ttc` `.woff`), and on an app `override IEnumerable<CupriSource> Fonts` — registered on
    every document the app creates, before the model binds.
  - **WOFF 1** is unwrapped in the engine. **WOFF 2** is recognised and refused by name (it needs a
    Brotli + glyf-transform decoder the engine does not carry yet): convert to TTF/OTF/WOFF 1.
  - Registered faces are keyed by **weight bucket** (100–900), so Light/Regular/Medium/Bold all
    register and CSS's nearest-weight rule picks between them; an italic request with no italic face
    takes the upright one rather than the platform's.
- **`FontPolicy.RegisteredOnly`** (`doc.FontPolicy`, `CupriApp.FontPolicy`) — for output that must
  not depend on the machine. A family with no registered face throws `FontNotRegisteredException`
  naming it, an `@font-face` that cannot load is an error at first layout, and glyph fallback for
  characters a face lacks searches the registered faces only, never the platform's. `doc.FontReport`
  lists what every family resolved to (`Registered` / `Platform` / `Default`) and what failed to load;
  `FontReport.IsDeterministic` is the one-line answer. What that buys, measured on CI: the same
  text **lays out identically** on Windows, Linux and macOS (a layout hash is compared across the
  three), and renders to identical pixels on every machine of one platform — but not across
  platforms, because Skia's glyph rasteriser is a different one on each (FreeType, DirectWrite,
  CoreText). A frame renderer gets the same picture wherever it runs on one OS; a pixel test
  belongs to one OS.
- **`doc.PendingLoads` / `doc.IsLoaded`** — remote image loads still in flight, so a headless
  renderer can wait for a complete frame instead of one with placeholders in it.
- **`animation-delay`, `animation-iteration-count`, `animation-fill-mode`**, and the `animation`
  shorthand reads them (`animation: fade 1s ease 0.5s 2 both`). Timing is a pure function of the
  document clock — `Animate(t)` at any t, in any order, gives that t's frame — and a finished
  animation leaves `HasActiveAnimations`, so a host goes idle with it.

### Changed

- **Desktop windows are DPI-aware by default (#137).** On Windows the host now asks for
  Per-Monitor-V2 before creating a window, and both desktop windows (GL and SDL) lay the document out
  in LOGICAL pixels while painting at `monitor scale × your PresentInfo.Scale`. Above 100% display
  scaling this replaces Windows' bitmap stretching with real rasterisation — text and vectors are
  crisp, the window keeps its logical size when dragged between monitors of different DPI, and UIA
  bounding rectangles land on the right physical pixels.

  **What a caller must do:** normally nothing. Two cases need attention:
  - **You assumed `app.Width`/`Height` were physical pixels.** They are logical now, so a window
    opens *larger* in pixels on a scaled monitor (1024 logical = 1536 pixels at 150%) and the same
    physical size as before on the desk.
  - **Your app already declares DPI awareness** in its manifest or by calling
    `SetProcessDpiAwarenessContext` itself. Yours wins — CupriFace's request is refused by Windows
    and nothing changes. This is deliberate.

  To opt out: `override bool DpiAware => false` on your `CupriApp`, or set `CUPRIFACE_DPI=0` in the
  environment to disable it for a run without a rebuild. `override bool TrackMonitorDpi => false`
  keeps awareness but stops the per-frame check that follows the window between monitors.
- **`ThreadedRender` now honours `Present` and the device scale.** It never called `app.Present` at
  all, so it silently ignored `PresentInfo.Hybrid`/`Zoom` as well as DPI, and published accessibility
  geometry at a hard-coded scale of 1. It now computes exactly what the inline paths do.
  `ThreadedPresenter.Submit` and `ThreadedRenderer.Commit` take an optional trailing `scale`
  (default `1`, so existing calls compile and behave unchanged).
- **`animation-iteration-count` defaults to `1`, as in CSS.** An animation without `infinite` looped
  forever before; it now runs once and reverts (or holds its last frame with `forwards`). Add
  `infinite` to a spinner that relied on the old behaviour — every shipped sample already has it.
- `tools/Screenshots` registers the web hosts' Noto faces before capture, so `docs/screenshots`
  shows the document's text rather than the generating machine's sans. The images are regenerated.

## v0.19.1

### Fixed

- **A NativeAOT publish keeps its accessibility bridge and its GPU** (#126). The Windows UIA bridge
  went through the runtime's built-in COM interop, which the SDK switches off for trimmed apps and
  NativeAOT does not have at all — so an AOT build opened a window, drew a correct frame, and served
  no accessibility tree, saying so only on stderr. It now goes through source-generated COM
  (`[GeneratedComInterface]` / `[GeneratedComClass]` / `[LibraryImport]`), which is ordinary compiled
  code and needs no runtime feature. The `TrimmerRootAssembly` items that keep Silk.NET's window
  backends alive under trimming now apply to `-p:Aot=true` as well, so hardware GL comes up too.

  Verified with `tests/UiaSmoke` against the AOT exe — identical to JIT (50 elements, 44 from
  CupriFace, every pattern) — and by a direct launch taking the SharedGpu lane on a real driver.

  **If you build `samples/Viewer` with `-p:Trim=true` yourself**, `BuiltInComInteropSupport` is no
  longer set and no longer needed; drop it from any copy you made.

- **The GPU-fallback line says why.** `[CupriFace] GPU unavailable (…)` now carries the exception
  message, not just its type. A bare type name was misread twice — once as a driverless machine when
  a test harness was forcing the software path, once as a session limit when the trimmer had removed
  Silk.NET's backends. That line is the only witness a silent fallback leaves.

### Fixed in the dev loop (nothing shipped changes)

- **The `Web (NativeAOT-LLVM)` launch configurations start a browser again.** They had gone back to
  the Edge debug adapter, which fails here with "Unable to attach to browser" — and attaching buys
  nothing on a host that is NativeAOT-compiled to wasm with a minified loader. They run the server
  directly and let `serverReadyAction` open the system browser, as they did before the JavaScript
  serve script was removed. The Lottie entry got the same treatment.

- **A leftover server no longer fails the next launch.** `tools/Serve` announced itself BEFORE
  binding, so a server that then died of `AddressInUseException` had already told the VS Code task
  it was ready — and the task exited non-zero, so nothing launched. It now binds first, and an
  occupied port is only fatal when the incumbent is serving a DIFFERENT directory (it says which).
  A background task outliving its debug session is normal, so this was most launches.

### Gates

- **Trimming can no longer remove hardware GL unnoticed** (#125). Silk.NET's window backends are
  found by reflection, so the trimmer drops them unless rooted, and the app then falls back to the
  software window silently. CI now asserts the three GLFW backends survive into the trimmed assembly
  set — no GPU needed, and it fails if the roots ever go.

- **The Android soft-keyboard assertion retries delivery** (#127). It was one tap, a fixed sleep and
  one read: the only assertion in that job with no tolerance for a dropped tap, and it cost v0.19.0's
  tag build a run. It now retries the tap and polls for the IME, and waits for the view to finish
  resizing after the keyboard hides before the next tap. The assertions themselves are unchanged.

## v0.19.0

### Added

- **The GL seam is a package: `CupriFace.Gl`.** Hand it something that draws with GL and it acquires
  the context, sizes the target to the element's device box, keeps the driver in a state Skia
  survives, and runs on desktop, Android and the browser unchanged. Ordinary UI composites over it at
  any alpha.

- **The Showcase's 3D is drawn by [Khalkos3D](https://github.com/Wixely/Khalkos3D)**, a separate
  engine, through about thirty lines of glue — replacing the hand-written sample renderer. The swap
  is one type name, which is the evidence that the seam is a seam. `samples/Demo3d` stays as the
  reference the engine was measured against.

- **The standalone downloads are trimmed and compressed.** The win-x64 single file goes from
  **95.4 MB to 20.8 MB**, and the arm64 APK from 22.1 MB to 19.4 MB — still one self-contained file
  each, still no .NET install. Nothing to do: the release assets are simply smaller.

- **If you publish `samples/Viewer` yourself**, the flags are `-p:Trim=true
  -p:EnableCompressionInSingleFile=true`, and `dotnet restore` needs `-p:Trim=true` too if your
  publish runs `--no-restore`. `Trim` is a project property rather than `-p:PublishTrimmed=true`
  because on the command line that reaches the netstandard2.0 source generators and fails there
  (NETSDK1124).

### Fixed

- **The 3D model no longer blurs to a flat colour on a phone** (Khalkos3D 0.2.2). A mip chain was
  built without anisotropic filtering, and this model's unwrap is a lathe: the `u` gradient around
  the ring dwarfs `v`, so isotropic mip selection took the worst axis and picked a level far blurrier
  than the surface deserved. Most of the teapot became its own average colour, which looks exactly
  like a broken UV map.

  It is minification-dependent, so it was correct on a desktop window and wrong on a phone — found on
  a real device, against a desktop that had never shown it.

### Note if you trim or AOT your own app

Two things break **silently** under trimming, and both did here before they were fixed. The app still
opens a window and draws a correct-looking frame, so a smoke test that only asks "did it render"
passes while the build is materially worse:

- **Hardware GL.** Silk.NET discovers its window backends by scanning for `IWindowPlatform`
  implementations (IL2104), so the trimmer removes them, bring-up throws
  `PlatformNotSupportedException`, and `DesktopHost` catches it and drops to the SDL software window.
  Fixed with `TrimmerRootAssembly` for the Silk.NET backends, at a cost of ~0.13 MB. **Verified by
  hand, not by CI** — GitHub runners have no GPU, so the desktop GL path cannot be gated there and a
  future regression would not be caught. The Android device gate covers the GLES path; nothing
  covers this one.
- **The UIA accessibility bridge.** It marshals `IRawElementProviderSimple` through built-in COM
  (IL2050), which the SDK switches off for trimmed apps. Fixed with
  `BuiltInComInteropSupport=true`, and verified with `tests/UiaSmoke` against the trimmed exe —
  identical to the untrimmed build.

**A NativeAOT publish still loses both**, and the second one is not fixable with a property: AOT has
no built-in COM marshalling at all, so the bridge would need porting to source-generated
ComWrappers. Anything you measure on an AOT build today is measuring the software renderer without a
screen reader attached. `-p:Aot=true` remains opt-in and is still not run in CI.

## v0.18.0

### Added

- **The scaling strategies are in the engine now, with names.** `PresentInfo` gains
  `Responsive`, `Fixed`, `Zoom` and `Hybrid` as named constructors, and `CupriApp.Present` documents
  them with a worked example. Hybrid zoom in one line:

  ```csharp
  public override PresentInfo Present(float w, float h) => PresentInfo.Hybrid(w, h, Width, Height);
  ```

  Nothing changes for existing apps — the default is still responsive and the record's three fields
  are unchanged. What changes is that the arithmetic is no longer something each app derives from a
  record of three floats: it lived only inside `ShowcaseApp`, so "hybrid zoom" was a thing you could
  read about in the docs and then had to reinvent. `Zoom` and `Hybrid` also clamp, and `Hybrid` falls
  back to reflowing when handed a zero or negative design size rather than laying out at infinity.

- **The 3D viewport runs on Android**, completing the set: desktop, browser and phone. Android takes
  the same zero-copy path the desktop GL window does — the host renders through an
  `SKGLSurfaceView`, which owns a real `GRContext`, so a surface draws on the host's own context and
  hands over a texture. There is no private context, no offscreen EGL and no readback in the Android
  surface at all; `IGpuSurfaceSource` turned the host that looked hardest into the shortest one.

  Entry points come from `dlsym` against `libGLESv3.so` rather than `eglGetProcAddress`, because some
  drivers return a non-null stub for any name — which makes a missing entry point look present and
  then crash on the call.

  Gated on a device in CI, which asserts the driver's own `GL_VERSION` and a frame count. Note the
  limit: the emulator answers with SwiftShader (software GL), so the gate proves the code path
  rather than any particular phone's driver.

- **`AndroidHost.PaintFrame` now receives the view's `GRContext`**, which is what lets any
  `IGpuSurfaceSource` work on the phone. Nothing to call: apps get it.

- **The Android sample starts on a named Showcase section** from the launch intent —
  `adb shell am start -n <activity> --es section 3d` — mirroring the desktop Viewer's `--section`.
  Any page past the front screen was previously reachable only by tapping through, which a test can
  do only by guessing coordinates.

### Note for `IGpuSurfaceSource` authors — read this if you draw GL

`RenderOnGpu` hands you the **host's** context, so you inherit a pipeline configured by Skia's last
draw call. Set the state you depend on; do not assume defaults. The 3D sample was corrected on two
counts after both showed up as rendering bugs on a real phone that a desktop driver had hidden:

- **A bound sampler object overrides every texture parameter.** Skia binds them, so `glTexParameteri`
  calls for wrap, filtering and LOD were being ignored at draw time in favour of Skia's — which
  clamps. A model whose UVs run past 1.0 and rely on `REPEAT` then samples one edge texel over most
  of its surface, which looks exactly like a broken UV map. Call `glBindSampler(unit, 0)` first.
- **Enables are inherited too** — blend, scissor, stencil, cull, depth mask, colour mask. Skia clips
  with the scissor box *and* the stencil buffer, so fragments get discarded in patterns that read as
  speckle, and a leftover blend reads as unwanted transparency.

The reverse direction was already handled: `SurfaceRegistry` calls `GRContext.ResetContext()` after
every producer, so Skia recovers from you. Nothing but the producer can do the other half.

### Fixed

- **The 3D sample's texture filtering.** It hardcoded `LINEAR_MIPMAP_LINEAR` and generated mipmaps
  for every texture regardless of what the asset asked for. The loader now carries the glTF sampler
  (`minFilter`/`magFilter`/`wrapS`/`wrapT`), wrap modes are honoured exactly, and anisotropic
  filtering is requested where available — which a lathe-style unwrap needs, since its `u` gradient
  dwarfs `v` and isotropic mip selection takes the worst axis and blurs to a flat average.

- **`samples/Scaling` rendered `scale-none-a/b.png` in responsive mode.** The model defaults to
  responsive and the sample never selected `"none"`, so two images were named after a mode they had
  never shown.

## v0.17.0

### Added

- **A surface can draw on the host's GPU instead of handing over pixels.** Implement
  `IGpuSurfaceSource` and `RenderOnGpu(GRContext)` is called on the render thread with the host's GL
  context current, before the frame is recorded; publish a texture-backed `SKImage` and the engine
  draws it with no copy. The registry calls `GRContext.ResetContext()` after any producer has run,
  which is what makes issuing raw GL on Skia's own context safe — a producer restores nothing.

  Measured on one desktop with a 512x512 viewport: the old path spent **1.47 ms per frame**
  transporting the image (`glReadPixels` 0.61 ms, to-`SKImage` 0.86 ms); this spends none, at 8 us
  of CPU to submit the draw. That is a claim about transport, not about rendering — nothing syncs,
  so the GPU's own work is not in those numbers.

  Nothing changes for existing surfaces. Hosts without a GPU context — every web host, Android, and
  a desktop host that fell back to a software window — never call it, so a producer that wants to
  run everywhere keeps its `CurrentFrame` path and checks `SurfaceRegistry.HasGpuFrameHook`.

- **An app can build a typeahead** (#111). Three gaps that only bit together, so an @-mention list,
  an autocomplete or any "complete this as I type" control was unbuildable without workarounds:

  - **A bare `Up`/`Down` now reaches `OnShortcut` while a field has focus.** Those two keys and no
    others: they are the only editing keys a focused text field does not act on, so they are the
    only ones that can be offered without taking something away. A bare letter still cannot fire —
    it would silently eat typing. A focused `data-listbox` still wins, so the built-in combobox is
    unaffected.
  - **`doc.SetFieldValue(selector, text)`** writes a bound field *while it has focus*, keeping the
    caret with the new text. Assigning the bound property does not work there — the component edits
    a buffer committed only on blur, so the write was discarded and the next keystroke landed at the
    old offset in the old text.
  - **`doc.Focus(selector)` and `doc.Blur()`** move keyboard focus from code. Previously the only
    focus-shaped API was `AccessibilityFocus`, keyed by accessibility path and meant for a screen
    reader — so any "put this into the input for the user to edit" flow dead-ended.

  The Showcase's **Keyboard** page now carries a working @-mention typeahead built entirely from
  these; the engine gains no typeahead component.

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

- **`CUPRIFACE_FRAME_DUMP` now works on the GL window too**, not only the SDL software one. The path
  most people actually run was the one that could not be inspected in a locked session or on CI —
  and a GPU-composited frame is exactly where a surface texture can come out black while every
  managed assertion still passes.

- **A `<canvas>` underlay is given a drawing buffer, not just a CSS box.** A canvas defaults to
  300x150 regardless of its size on the page, and a `<video>` has no backing store at all — so the
  video path never needed this. The symptom was not a missing image but a stretched one.

- **`-sMAX_WEBGL_VERSION=2` now flows through `CupriFace.Web.NativeAot`'s build props.** Without it
  `emscripten_webgl_create_context` silently downgrades to WebGL1, and the first symptom is a shader
  error blaming `#version 300 es` — three steps from the cause.

  **This costs every NativeAOT-LLVM app about 66 KB uncompressed** (+9.4 KB wasm, +56.7 KB JS glue),
  whether or not it uses WebGL. Deliberate: the failure it prevents is invisible at the point it
  happens, and a build that links cleanly and then reports a shader error is a worse day than 66 KB.
  Nothing to do — apps already rendering WebGL into a surface underlay simply work.

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
