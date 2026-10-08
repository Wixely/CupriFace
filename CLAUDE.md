# Working on CupriFace

CupriFace renders HTML + CSS to a pixel buffer with Skia. **There is no browser, no DOM at runtime
and no JavaScript.** Read [TOOLBOX.md](TOOLBOX.md) for the component library and
[DESIGN.md](DESIGN.md) for the architecture.

---

## Check your UI. You can, in about ten seconds.

This is the single thing that most changes the quality of work in this repo, and it is the thing
most often missed.

**The engine is forgiving on purpose.** An unsupported CSS property is ignored. An element it has no
primitive for lays out and stays empty. A binding that names nothing renders as empty text. A
container too small for its contents does not clip — the contents paint over whatever follows.
Nothing throws, nothing logs, no test fails. A mistake looks exactly like a layout you have not
finished yet, so **reasoning about the markup will not find it.** You have to check.

Everything below is headless — no window, no GPU, no browser — and runs anywhere `dotnet` does.

### 1. `CupriDoctor` — before you look

```csharp
using CupriFace.Diagnostics;
var report = CupriDoctor.Check(html, css, model: model);   // pass the model — see below
if (!report.IsClean) Console.WriteLine(report);
```

```
error   CF0060 (line 4): {{Enviroment}} does not name anything on DeployModel, so it renders as empty text.
  -> Did you mean {{Environment}}?
warning CF0070 (line 2): <div class='panel'> is 56px tall but its contents need 132px — they overflow
                         it by 76px and paint over whatever follows.
  -> Remove the fixed height and let it grow, or set overflow:scroll. It does not clip on its own.
error   CF0030 (line 6): <img> is not something the engine draws — it lays out, and then stays empty.
  -> Use <cupri-image src="..."> — the engine has no raw <img> primitive.
```

| Code | Finds |
|---|---|
| `CF0010` / `CF0011` | A tag never closed, or a close matching nothing — reported at the line it *opened* on |
| `CF0020` / `CF0021` | An unregistered `cupri-*` tag (with "did you mean"), or a control that can never open |
| `CF0030` / `CF0031` | `<img>`, `<video>`, `<svg>`, `<canvas>` and friends; anything else that produced no output |
| `CF0040` / `CF0041` | `<script>` and `onclick=` — there is no JavaScript engine |
| `CF0050` / `CF0051` | A CSS property or function that is silently ignored |
| `CF0052` | An `@import` — the engine steps over it, so the sheet it names (usually a web font) never loads |
| **`CF0060`** | **A `{{path}}` that names nothing on the model** — renders as empty text, looks like missing data |
| **`CF0070`** | **Contents that do not fit a fixed-height box** — they overflow and paint over the next element |
| **`CF0071`** | **A box that laid out with no area** but has visible content inside it |
| **`CF0073`** | **Repeated controls in a column that are not the same size** — one button's longer label pushes it out of line with the others. Every box is correct; the SET is wrong |
| **`CF0072`** | **Contents that run off the SIDE** past the viewport (or a box that clips them) — the way a desktop layout fails on a phone. Pass `width:`/`height:` to check a device size |
| **`CF0090`** | **Text too close in colour to what is behind it to read** (WCAG AA). One finding per colour PAIR, with a readable replacement suggested. Silent whenever the background cannot be computed exactly — a gradient, an image, a faded ancestor — and inside a `cupri-*` control, whose insides a caller cannot restyle |
| **`CF0080`** | **Characters no installed font can draw** — they paint as empty .notdef boxes. Under-reports on macOS (its LastResort face matches everything): trust a finding, not its absence |

**Pass `model:` whenever the document has one**, and **pass `width:`/`height:` to check a size you
care about** — `CupriDoctor.Check(html, css, width: 412, height: 915, model: model)` is "does this
survive a phone" as one assertion. `CF0060` and the box checks are skipped without a model, and
those are the two that catch the quietest bugs. A binding typo is invisible in every other
way: unknown property → null → empty string → an element that renders perfectly with nothing in it.

### 2. `RenderToImage` — then actually look

```csharp
using var doc = CupriDocument.Load(html, css);     // or app.CreateDocument()
doc.Bind(model);
doc.Refresh();
using (doc.RenderToImage(w, h)) { }                // throwaway: warms layout + images
using var img = doc.RenderToImage(w, h, bgColor);
using var data = img.Encode(SKEncodedImageFormat.Png, 95);
using (var f = File.Create("ui.png")) data.SaveTo(f);
```

Then **open `ui.png` with the Read tool and look at it.** You can see your own output; a screenshot
settles in one glance what a paragraph of reasoning about flexbox cannot.

### 3. `DumpTree` — what the image cannot tell you

```csharp
Console.WriteLine(doc.DumpTree(maxDepth: 3));
```

```
body.cupri-fine           0,0      340x240
  div.panel               28,28    312x56    << CONTENT OVERFLOWS by 76px
    div.row               40,40    300x34
    div.row               40,78    300x34
  div.after               28,84    312x17
  div.collapsed           28,101   312x0     << EMPTY BOX, has children
```

(`div.after` sitting at y=84 while the rows run to y=150 *is* the overlap, visible as two numbers
rather than as a mysterious picture.)

An image shows you *that* something is wrong; this shows you *what*. A blank rectangle has many
possible causes — `312x0` has one. It is greppable, diffable between runs, cheap to assert on in a
test, and far cheaper in context than reading a PNG. Coordinates are **absolute**, so they are the
numbers to hand to `DispatchClick`.

### 4. `ImageDiff` — did I break anything else?

```csharp
var d = ImageDiff.Compare(before, after);     // render before your change, render after
Console.WriteLine(d);                          // "15,121/81,600 px changed (18.53%) within 14,14 312x129"
if (!d.IsIdentical) Save(ImageDiff.Visualise(before, after));   // changed pixels in magenta
```

This is what makes a broad edit — a variable rename in the cascade, padding on a shared class —
safe rather than nerve-wracking. Eyes are poor at spotting that one row moved three pixels;
subtraction is perfect at it. Default tolerance allows for antialiasing; pass `0` for exactness.

### 5. Driving it

```csharp
var box = HitTesting.AbsoluteBox(node);       // node from doc.Root, or read DumpTree's coordinates
doc.DispatchClick(box.X + box.W / 2f, box.Y + box.H / 2f);
// also: DispatchKey(text, EditKey, KeyMods), DispatchPointerMove/Up
```

Render, click, render again — state changes are checkable too, not just static layout.

**Touch is a finger, not a mouse, and it has its own driver.** A tap is not a click: touch
activates on finger-UP, so a press that turns into a scroll must never press what it began on.
`TouchDriver` is one call per gesture, on a clock it owns — nothing sleeps, and the same script
produces the same events on any machine.

```csharp
var touch = new TouchDriver(doc);             // CupriFace.Interaction
touch.Tap(x, y);                              // activates on release, like a finger
touch.DoubleTap(x, y);                        // escalates the click count → word select
touch.LongPress(x, y);                        // the context menu
touch.Swipe(x, y, dy: -180);                  // scroll, and STOP where the finger left it
touch.Fling(x, y, dy: -180);                  // …or let go moving, and keep going
touch.Pinch(cx, cy, gapFrom: 40, gapTo: 160); // two fingers, to an OnPointer handler
touch.Advance(1.0);                           // let scripted time pass
```

Three things that bite if you drive `TouchInput` by hand instead:

- **A long press only fires when the host ticks the deadline.** Down and up alone wait for ever and
  give you a tap. `LongPress` ticks it.
- **A fling's momentum comes from the last 100 ms before release, and then needs frames.** After
  `Fling`, call `doc.Animate(t)` with a rising `t` (or `doc.Settle`) or the content sits exactly
  where the finger left it — which looks like a fling that did nothing. `Swipe` deliberately pauses
  before lifting, so it does *not* fling.
- **A swipe on a slider, scrollbar thumb, reorder handle or split divider is a drag from the first
  contact**, not a deferred tap. Same verb; the engine decides.

`new TouchDriver(doc, new TouchOptions { SlopPx = … })` moves the thresholds, for testing a gesture
at its boundary. `tests/CupriFace.Tests/TouchDriverTests.cs` is a worked example of each verb.

**A controller navigates by GEOMETRY, not Tab order, and it has its own driver too.** Tab is
one-dimensional and follows the document, so on a panel in two columns "down" and "next" are
different controls and only one of them is what the user pointed the stick at.

```csharp
var pad = new GamepadDriver(doc);             // CupriFace.Interaction
pad.Press(NavigationDirection.Down);          // a D-pad press: one move
pad.Stick(0.9f, 0f);                          // a stick pushed right: ONE move, then nothing…
pad.Stick(0f, 0f);                            // …until it comes back to centre
pad.Confirm();                                // A / OK — the same path Enter takes
```

**`doc.Gamepad` is the one driver** — a host and an app sharing it is what stops one stick push
becoming two moves. `doc.HostGamepadInput` narrows what the HOST's pad contributes, per capability:
`HostGamepad.Dpad | HostGamepad.Buttons` when your own reader owns the sticks, `HostGamepad.None`
when it owns everything. It never silences the keyboard. `doc.GamepadConnected` tells you a pad
exists and whether the platform recognised it.

**A pad is routed as a pad, not as the keys it arrives as.** Every host delivers a D-pad and its face
buttons as `EditKey`s, so hosts call `doc.DispatchGamepadKey` / `DispatchGamepadStick` rather than
`DispatchKey` — those apply `HostGamepadInput` and mark the event as a controller's. Two consequences
worth knowing: **`KeyboardNavigation` does not apply to a pad** (it is a statement about the
keyboard; `HostGamepadInput` is a pad's off switch), and **a pad navigates by geometry whatever
`ArrowKeyNavigation` says** — there is no habit to protect, because there is nothing else for a D-pad
to mean.

**Input from another thread must go through `doc.Post(() => …)`** — it is the only thread-safe
member. Every dispatch rebuilds the render tree, so a reader thread calling input directly replaces
it underneath layout and paint; the reproduction crashes the process rather than failing cleanly.
`pad.PostStick(x, y)` is the same thing for a controller.

**For a game, `doc.ArrowKeyNavigation = NavigationMode.Spatial` makes the ARROW KEYS a D-pad** — off by default,
because an ordinary application's arrows are expected to move a caret, scroll, and step through a
radio group, and quietly repurposing them would fight every habit a user has. Turning it on takes
nothing away: a focused text field still moves its caret, a slider still nudges, a radio group still
follows the ARIA pattern, and Tab still follows the document. It only changes what an arrow does
when the answer would otherwise be "the next focusable in document order". It is also how you
develop a controller UI without a controller. `NavigationMode.Disabled` is how an app that drives
focus itself stops the engine moving the selection too, and `doc.KeyboardNavigation = InputRoute.Consume`
extends that to Tab, Enter, Space and Escape. `samples/SpatialNav` is 30 scattered boxes and an M
key that flips the mode live, so you can watch the same keypress do two different things.

**`doc.DiagonalNavigation = true` makes two arrows pressed together one move to the corner.** Also
off by default, and it costs latency: to know whether a second key is coming, the first has to wait
(`DiagonalWindowSeconds`, default 0.05). Without it, Right-then-Down and Down-then-Right land on
different controls and neither is the one diagonally adjacent — where you end up depends on which
key the hardware reported first.

**How it decides "together" depends on whether the host forwards key releases.** A host that sets
`doc.ReportsKeyUp = true` and calls `DispatchKeyUp` (desktop and Android both do) gets the exact
answer — *is the first key still physically down?* — which holds at any gap and delays nothing. A
host that does not falls back to holding every arrow press for `DiagonalWindowSeconds` and guessing
from arrival times; that costs latency, needs tuning per keyboard, and needs a host that calls
`Animate` (all three do). Hosts should also call `ReleaseAllKeys()` on focus loss, or a key held at
that moment is remembered as held for ever. **A thumbstick needs none of this** — it
reports a vector, so `new GamepadDriver(doc, diagonals: true)` resolves a corner from one reading
with no window at all.

`doc.MoveFocus(NavigationDirection.Up)` is the engine call under it. Three things to know:
**it does not wrap** — a stick held right stops at the edge rather than reappearing on the left (Tab
wraps because a form is a loop; a grid is not), and the `false` it returns at the boundary is your
hook for paging across instead; **the first press enters from the edge it travels from**, so Down
lands on the topmost control rather than on whatever is first in the markup; and **focus on a button
is visible both as `[data-focus]` in CSS and in the accessibility tree** — for a test, assert with
`BuildAccessibilityTree(w, h)` (`TestDoc.FocusedName()`) rather than looking for an attribute that
will never be there. `tests/CupriFace.Tests/DirectionalFocusTests.cs` is a worked example.

**A file dropped in from the OS has its own driver too**, for the same reason:

```csharp
var drop = new DropDriver(doc);               // CupriFace.Interaction
drop.DropText(x, y, "notes.md", "# hello");   // drag in and release
drop.Over(x, y);                              // the :drop-over highlight, without letting go
```

Three things that catch people out: **`DroppedFile` gives metadata synchronously and bytes only
asynchronously** (a browser `File` is a blob — there is no synchronous read to offer, and `Path` is
null there); **the drag-over highlight exists only in a browser** — GLFW reports the drop with no
warning beforehand, so a drop zone must read as a target while idle; and **`ReadBytesAsync` is capped
at 128 MiB** because an unbounded read on wasm ends the tab rather than throwing — use
`OpenReadAsync` to stream anything larger. See TOOLBOX.md §8.1.1.

### Gotchas

- `doc.Refresh()` before the first render, and throw away one frame before capturing — layout and
  images are warm only after a render.
- `doc.LoadFonts(dir)` if an image will be compared across machines, or you are comparing whichever
  sans-serif each box happens to have.
- Rendering at 2x means laying out at the logical size and scaling the canvas — **not**
  `RenderToImage(w*2, h*2)`, which lays out a wider viewport and gives a different screenshot.
- `tools/Screenshots` is a worked 12-page example if you need surfaces, dark mode or 2x capture.

### For a running window

`CUPRIFACE_FRAME_DUMP=out.png` dumps what the window actually presented, read back from the render
target — ground truth when a live window looks wrong but the document seems right.

`CUPRIFACE_KEY_DEBUG=keys.log` makes the window testify about the input it actually received —
keys, focus changes, and **every controller button and stick reading**. Reach for it before
theorising about a pad that "does nothing": it separates "no events arrive at all" (wrong subsystem,
pad not opened) from "events arrive and are mapped wrongly", which look identical from the outside.
The stick lines carry the readings, which is how you tell those apart for a pad: no lines at all
means the host never found it. **y is DOWN-positive on every platform** (GLFW, SDL, Android and the
Gamepad API all agree with the engine's own space, and no host negates it — TOOLBOX.md §Stick axes),
so push down and `y` must be positive. What that does NOT settle is whether the host finds a pad in
the first place: the engine's own desktop and Android discovery has never run against physical
hardware, because the one integration using a controller in anger feeds input from its own reader.

In the browser the equivalent is the console: a connected pad is always announced (with its id and
whether the browser gave it the standard mapping), and `?padlog=1` on the URL adds the per-event
stream of buttons and stick readings.

**`doc.InputObserved` is the headless version of all of that**, and it needs no window:

```csharp
doc.InputObserved += o => output.WriteLine(o.ToString());
// HostGamepad stick 0.00,0.90 -> Navigate "Library" handled
// Keyboard Enter -> Swallowed route=Consume handled
```

Reach for it the moment input "does nothing", because one returned bool cannot tell **nothing
arrived** from **arrived and meant nothing** from **ignored because you asked for that** — and the
three have different fixes. It names the source (`HostGamepad` is the host's own wiring, `Gamepad` an
app's own reader), the action, the routing policy that applied, and the control involved. A stick
reports its READING rather than the direction it resolved to, which is the only way the sign of y is
visible without hardware. It reports and cannot veto; unobserved it costs one null check.

For the WASM/browser host, drive the canvas with the Playwright MCP.

---

## Four things that make a UI look wrong here

These are not style preferences. Each is a place where markup that would be right in a browser comes
out visibly wrong in this engine, and three separate apps (Shade, CursorGoblin, Bantz) each worked
all four out the hard way before any of it was written down.

### 1. A row is `display:flex`. There is no inline box.

**Everything lays out as a block that fills its parent.** `<span>`, `<div>` and a `<cupri-badge>` all
measure the same full width — `span.p 600x35`, `div.p 600x35`. So two "pills" written side by side do
not sit side by side; they stack, each as wide as the row.

Things only sit in a line, and only align with each other, when **the parent says so**:

```css
.row { display:flex; align-items:center; gap:8px; }
```

`align-items:center` is the one that matters. Without it a taller item stretches its neighbours or
sits them on a baseline you did not choose — which is what "my buttons do not line up" always is.
42 of Bantz's 65 flex rules are this exact line.

**For a box that shrinks to its content AND centres what is inside it, use `display:inline-flex`** —
a button, a badge, a chip, a tag, a pill. Plain `flex` is block-level and fills the row, so two of
them stack instead of sitting side by side:

```css
.pill { display:inline-flex; align-items:center; gap:8px; padding:10px 18px; }
```

`inline-block` also shrinks, but its contents align on the text baseline rather than centring, so an
icon beside a label sits slightly off. `inline-grid` is still block-level (grid has no intrinsic
width yet) and `fit-content` maps to `auto`.

### 2. Never draw focus or selection with `border`.

A border is part of the box, so adding one on focus **grows the element and shifts everything after
it** — the whole row jitters as a controller moves the selection. Two correct options:

```css
/* the engine's own ring, recoloured — layout-safe, nothing else to write */
:root { --cupri-focus: #8b5cf6; }

/* or your own, with outline: painted OUTSIDE the box, never laid out */
:focus { outline: 2px solid #8b5cf6; outline-offset: 2px; }

/* or style it however the design wants, and take the engine's ring off */
.tile:focus { background: #584e35; box-shadow: inset 0 0 0 3px #f3ce7c; outline: none; }
```

**`:focus` and `:focus-visible` work, and so does `[data-focus]`** — the first two are rewritten to
the third, which is what the engine actually marks. A game that wants selection to look like
selection rather than like a form field should reach for the third example: **`outline: none` is what
takes the built-in ring off**, exactly as on the web, and one `[data-focus] { outline: none }` does it
document-wide. Setting your own `outline` also replaces the ring, so you never get two.

The engine marks the focused control itself; an EDITABLE field's `data-focus` means something
narrower — the caret is in it — which is what lets a combobox reveal its list while being typed into.

### 3. Pin anything whose text changes.

A button sized by its label resizes when the label does — "Connect" → "Disconnecting…" moves every
control beside it. Give it a floor:

```css
.cupri-button { min-width: 120px; }
```

Digits are the sharp case — a clock or a score reflows its row on every tick, because in most faces
a `1` is narrower than a `0`. Say so:

```css
.clock { font-variant-numeric: tabular-nums; }
```

Every digit then takes the widest digit's advance, so the value can change without the box moving.
Only `tabular-nums` is supported; the other values of that property are reported by CupriDoctor.

**`CF0073` now catches this one for you** — but only once a column already disagrees, which means
only in the state you happened to render. Three rows ending in "Connect", "Configure", "Connect"
report; the same three all reading "Connect" cannot, because at that instant the layout is correct
and the defect only exists in a state you have not drawn. So the floor is still worth writing
BEFORE anything is wrong, exactly as above: the check finds this where you forgot, it does not
remove the reason to remember.

### 4. `box-sizing` is `content-box` in YOUR markup, `border-box` on the controls.

In your own CSS, `width: 200px` plus `padding: 12px` is a 224px box — the CSS default. Say
`box-sizing: border-box` yourself when you mean the outer size; Shade does it four times for exactly
this reason.

The `cupri-*` controls are already `border-box`, so a width you set on one is the box you can see:
`<cupri-textfield style="width:220px">` is 220px wide, padding and border included.

### And run the doctor over the states, not just the page

`CupriDoctor.Check(html, css, width:, height:, model:)` reports a CSS property the engine ignores
**wherever you wrote it** — including inside `:focus`, `:hover` and classes you only add at runtime,
which it could not see before and which is where focus styling lives.

---

## Other things worth knowing

- **Trimming and AOT fail silently here.** A trimmed build can lose hardware GL or the accessibility
  bridge and still render, so it looks fine. Check the console line naming the renderer before
  concluding anything about a published binary.
- **`PublishSingleFile` needs `IncludeAllContentForSelfExtract`** — Silk.NET finds SDL2/GLFW through
  `Assembly.Location`, which is dead inside a bundle. See `samples/DpiProbe/DpiProbe.csproj`.
- **Release notes are written in the same commit as the change**, under `## Unreleased` in
  [RELEASE-NOTES.md](RELEASE-NOTES.md). CI splices the section into the GitHub release.
- **Prefer `LibraryImport` over `DllImport`** (AOT), and mark Windows-only types
  `[SupportedOSPlatform("windows")]`.
- Every snapshot sample writes a PNG and exits, headless — `samples/HelloBox`, `samples/HtmlView`,
  `samples/Interactive` and ~9 others are runnable references for all of the above.
