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
| **`CF0060`** | **A `{{path}}` that names nothing on the model** — renders as empty text, looks like missing data |
| **`CF0070`** | **Contents that do not fit a fixed-height box** — they overflow and paint over the next element |
| **`CF0071`** | **A box that laid out with no area** but has visible content inside it |
| **`CF0072`** | **Contents that run off the SIDE** past the viewport (or a box that clips them) — the way a desktop layout fails on a phone. Pass `width:`/`height:` to check a device size |
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

**For a game, `doc.ArrowNavigation = true` makes the ARROW KEYS a D-pad** — off by default,
because an ordinary application's arrows are expected to move a caret, scroll, and step through a
radio group, and quietly repurposing them would fight every habit a user has. Turning it on takes
nothing away: a focused text field still moves its caret, a slider still nudges, a radio group still
follows the ARIA pattern, and Tab still follows the document. It only changes what an arrow does
when the answer would otherwise be "the next focusable in document order". It is also how you
develop a controller UI without a controller — `samples/SpatialNav` is 30 scattered boxes and an M
key that flips the mode live, so you can watch the same keypress do two different things.

**`doc.DiagonalNavigation = true` makes two arrows pressed together one move to the corner.** Also
off by default, and it costs latency: to know whether a second key is coming, the first has to wait
(`DiagonalWindowSeconds`, default 0.05). Without it, Right-then-Down and Down-then-Right land on
different controls and neither is the one diagonally adjacent — where you end up depends on which
key the hardware reported first. It needs a host that calls `Animate` — all three do, because the
document reports `HasActiveAnimations` while a press is held, which is the signal every host polls
to decide whether to draw a frame at all (a test must call `Animate` itself, as it must after a
fling). **A thumbstick needs none of this** — it
reports a vector, so `new GamepadDriver(doc, diagonals: true)` resolves a corner from one reading
with no window at all.

`doc.MoveFocus(NavigationDirection.Up)` is the engine call under it. Three things to know:
**it does not wrap** — a stick held right stops at the edge rather than reappearing on the left (Tab
wraps because a form is a loop; a grid is not), and the `false` it returns at the boundary is your
hook for paging across instead; **the first press enters from the edge it travels from**, so Down
lands on the topmost control rather than on whatever is first in the markup; and **focus on a button
is only visible in the accessibility tree** — `data-focus` is for an editable field, so assert with
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
[data-focus] { outline: 2px solid #8b5cf6; outline-offset: 2px; }
```

**The hook is `[data-focus]`, not `:focus`.** The engine marks the focused element with an attribute;
`:focus` matches nothing and fails silently. If you set your own `outline`, the built-in ring steps
aside so you do not get two.

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
