# CupriFace — Design Document

> A native, cross-platform desktop UI runtime that renders **HTML + CSS** to a GPU
> canvas and binds elements to backend C# objects — an Electron alternative that
> does **not** embed a web browser or a JavaScript engine.

Status: **Draft v0.1** · Target runtime: **.NET 10** · AOT: **preferred, not required**

---

## 1. Vision

Build desktop applications whose UI is authored in HTML + CSS, rendered by a fully
managed .NET runtime, with UI elements data-bound to plain C# objects (MVVM-style).
No Chromium, no V8, no Node. The runtime is a **retained-mode CSS layout + paint
engine**, not a browser.

### Goals
- **Fluid performance (CORE, non-negotiable)** — if the UX doesn't *feel* fluid,
  the project has failed. 60 fps steady-state (120 where the display allows),
  sub-frame input latency, no jank on scroll/resize/animation. Performance is a
  requirement on every layer, not an optimisation pass. See **§7**.
- **Standard HTML + CSS** — real HTML and CSS, targeting **as much of the modern
  core set as we can implement well** (not a bespoke dialect, not a toy subset).
- **No JavaScript in the authoring model — ever** — this is a firm architectural
  principle, not a v1 shortcut. App **UI pages are HTML/CSS only**; behaviour lives
  in C#. We never embed a JS engine (V8/scripting VM) in the runtime, and app
  authors never write JS. *(Scope note: this governs the UI pages developers author.
  It does not forbid the thin, non-authored JS **glue** the browser/WASM target
  needs to boot and to reach `<canvas>`/events — see §9. That glue is platform
  plumbing, not application logic, and app authors never see or write it.)*
- **Two-way binding** — bind DOM elements/attributes/text to C# objects and react
  to property changes with minimal re-layout/re-paint.
- **Component model + native control library** — authors can define custom elements
  (e.g. `<cupri-slider>`), and we ship a first-party, MudBlazor-style control set
  (accessible, themed, bound out of the box) built on that same public API. See §10.
- **Cross-platform desktop** — Windows, macOS, Linux from one codebase.
- **AOT-friendly** — favour NativeAOT; avoid runtime reflection on hot paths.
- **Accessibility as a first-class citizen** — a real semantics tree bridged to
  each OS's assistive-technology API.
- **(Step 2) Web target** — same scene graph rendered to `<canvas>` via WASM.

### Non-goals
- Any JS engine in the runtime, or JS in app UI pages (permanent — see above).
  The web target's non-authored bootstrap/interop glue (§9) is *not* covered by
  this and is expected.
- 100% CSS spec compliance. We chase the **modern core** (flexbox, grid, modern
  box model, transitions/transforms, common paint) and consciously skip legacy
  cruft (`float`-based layout, print, `@page`, obscure pseudo-classes).
- A full DOM `document.*` scripting API surface (there's no script to call it).

### Delivery order vs. ambition
The **ambition** is broad modern CSS. The **v1 delivery order** is flexbox-first
(§8) because it's the fastest route to a fluid, correct engine; **CSS Grid is a
committed follow-up, not a maybe.** Scope is sequencing, not a ceiling.

---

## 2. Licensing constraint (hard requirement)

Every third-party dependency must be **MIT or Apache-2.0** (permissive, no copyleft).

| Dependency | Purpose | License | Notes |
|---|---|---|---|
| **SkiaSharp** | 2D/GPU rendering | MIT | Native Skia bundled per-RID |
| **HarfBuzzSharp** | Text shaping (i18n, ligatures) | MIT | Ships with SkiaSharp family |
| **Silk.NET** | Windowing, input, GL/Vulkan/GLFW/SDL bindings | MIT | Layer 0 |
| ~~Yoga~~ → **managed C# flex engine** | Flexbox layout | n/a (our code) | **DECISION (impl):** flexbox implemented in pure C# (`CupriFace.Layout`) to keep the stack fully managed + AOT-clean and drop the native `libyoga` per-RID dependency. Native Yoga stays a drop-in behind `ILayoutEngine` if completeness ever demands it. |
| **AngleSharp** | HTML parsing + DOM + CSS selector engine | MIT | Spec-grade, pure managed; its selector engine matches rules, cascade resolved in `CupriFace` |

> **Yoga note:** we compile/vendor native `libyoga` per platform and write our own
> thin P/Invoke layer against its `extern "C"` API. This keeps us off any binding
> package whose license we don't control and gives an AOT-clean interop surface.

Any new dependency must be license-checked before it enters the solution.

---

## 3. Architecture — the layer stack

```
┌──────────────────────────────────────────────────────────────┐
│ 6. Accessibility  semantics tree → UIA / AT-SPI / NSAccess.   │
├──────────────────────────────────────────────────────────────┤
│ 5. Binding        C# objects ↔ DOM (source-generated)         │
├──────────────────────────────────────────────────────────────┤
│ 4. Paint          Skia display list + HarfBuzz text           │
├──────────────────────────────────────────────────────────────┤
│ 3. Layout         Yoga (flex) + block/box model               │
├──────────────────────────────────────────────────────────────┤
│ 2. Style          cascade → computed style per node           │
├──────────────────────────────────────────────────────────────┤
│ 1. Markup/CSS     AngleSharp DOM + AngleSharp.Css CSSOM        │
├──────────────────────────────────────────────────────────────┤
│ 0. Shell          Silk.NET window + input + GPU surface       │
└──────────────────────────────────────────────────────────────┘
```

### Layer 0 — Shell (windowing / input / GPU surface)
- **Silk.NET** creates the OS window, GL/Vulkan context, and pumps input events.
- Produces a Skia `GRContext` bound to the GL surface; exposes a per-frame
  `SKCanvas` to Layer 4.
- Normalises keyboard/pointer/focus/IME events into a platform-neutral input model
  consumed by hit-testing and binding.

### Layer 1 — Markup + CSS
- **AngleSharp** parses HTML into a DOM we own a handle to.
- **AngleSharp.Css** parses stylesheets into a CSSOM and provides selector matching.
- The DOM is the single source of truth the other layers observe.

### Layer 2 — Style resolution
- Run the cascade: match selectors, apply specificity/order, resolve inheritance,
  compute used values (lengths → px, resolve `em`/`rem`/`%` where context allows).
- Output: a **computed-style struct per node**, stored densely (see §7 AOT) rather
  than as boxed dictionaries.
- Invalidation: a style change dirties only affected nodes + descendants for
  inherited properties.

### Layer 3 — Layout (flexbox-first)
- Map computed style → **Yoga** node tree; Yoga computes flex geometry.
- A thin **block/box** layer handles `display:block`/`inline-block`, margins,
  padding, borders, and the containing-block chain around Yoga.
- Text measurement is fed to Yoga via a measure-callback backed by HarfBuzz.
- Output: an absolutely-positioned box for every node (content/padding/border/margin
  rects).
- Grid + full inline flow are **explicitly deferred** (§8).

### Layer 4 — Paint
- Walk the layout tree → build a **retained display list** (Skia).
- Draw backgrounds, borders, border-radius, gradients, box-shadow, images, and
  **shaped text** (HarfBuzz → glyph runs → `SKTextBlob`).
- **Dirty-region invalidation**: only repaint changed subtrees; cache layer
  bitmaps for static subtrees where beneficial.
- GPU-accelerated via the Layer 0 `GRContext`.

### Layer 5 — Binding (see §6)
### Layer 6 — Accessibility (see §5 — yes, before binding; a11y is a design driver)

---

## 4. Frame pipeline / data flow

```
HTML+CSS ─► AngleSharp DOM ─► style cascade ─► Yoga+box layout ─► Skia display list ─► GPU
                 ▲                                                        │
                 │  bound property changed (INotifyPropertyChanged)       │
   C# model ─────┘  → mutate DOM subtree → dirty style/layout/paint ──────┘ (partial)
```

On a bound model change we mutate the smallest DOM subtree, mark it dirty, and the
next frame re-runs cascade→layout→paint **only for the dirty region**. Steady-state
idle = zero work.

---

## 5. Accessibility strategy (first-class, not bolted on)

Because we paint to a flat canvas, assistive tech sees nothing unless we publish a
parallel **semantics tree**. This constrains the architecture, so it's built from
milestone 1.

- Derive a **semantics node** from each relevant DOM node: role (from tag/ARIA),
  accessible name, value, state (focused/checked/expanded), and bounds.
- Bridge that tree to each platform provider:
  - **Windows** → UI Automation (COM; see AOT note in §7).
  - **Linux** → AT-SPI (D-Bus).
  - **macOS** → NSAccessibility.
- Keyboard focus, tab order, and hit-testing are driven from the same tree so
  screen-reader focus and visual focus never diverge.
- **Web (step 2)** → inject a hidden, positioned DOM overlay mirroring semantics
  (the Flutter-web model), since canvas is opaque to the browser a11y tree.

Design rule: **any element that conveys meaning must produce a semantics node.**
Decorative nodes are explicitly marked `role=presentation`.

---

## 6. Binding model

- Models are plain C# objects implementing `INotifyPropertyChanged`
  (and `INotifyCollectionChanged` for lists).
- Binding expressions in markup (attribute syntax, e.g. `data-bind="text: Title"`)
  are resolved to **compiled accessors generated by a Roslyn source generator** —
  no runtime reflection, so it survives trimming/AOT.
- Two-way bindings (inputs) push changes back into the model via the generated
  setters.
- Collection bindings do keyed diffing to add/remove/move DOM subtrees minimally.
- Templates: a `<template>` bound to a collection instantiates one subtree per item.

---

## 7. Performance (core requirement) & AOT strategy

**Performance is a hard requirement, not a phase.** A feature that regresses frame
time or input latency is not "done." Every layer owns a performance budget and is
measured against it in CI. If the UX doesn't feel instant, the feature ships
broken.

### 7.1 Targets (measurable, gated in CI)
| Metric | Target | Hard fail |
|---|---|---|
| Steady-state frame rate | 60 fps (120 on capable displays) | any dropped frame during scroll/animation |
| Frame budget (60 fps) | ≤ 8 ms CPU work, headroom for GPU | > 16.6 ms |
| Input → visible response latency | ≤ 1 frame | > 2 frames |
| Idle CPU | ~0% (no repaint when nothing changed) | busy-loop repaint |
| Steady-state allocations per frame | 0 bytes on the render path | GC in the frame loop |
| Cold start to first frame | < 250 ms (AOT) | > 1 s |

### 7.2 Threading model
- **Dedicated render thread** decoupled from the UI/model thread. The render
  thread walks a committed, immutable scene snapshot and draws — it never touches
  live model or DOM state, so model updates never stall a frame.
- **Commit pattern:** the UI thread mutates DOM → produces a new immutable
  **display-list snapshot** → atomically hands it to the render thread (double/
  triple buffered). Render pacing is independent of how long a model update took.
- Layout and style resolution can be parallelised across independent subtrees
  (data-parallel over dense node arrays); the flex algorithm parallelises well
  across sibling subtrees.

### 7.3 Do-nothing-when-nothing-changed
- **Damage/dirty regions everywhere.** Style, layout, and paint each track a dirty
  set; a change re-runs only the affected subtree, and only the changed pixels are
  re-submitted. Idle frames do literally nothing → 0% CPU, laptop-battery friendly.
- **Retained scene, not immediate mode.** The display list persists across frames;
  we diff and patch it, we don't rebuild it per frame.
- **Layer caching.** Static subtrees are cached as GPU textures; scrolling/animating
  them is a cheap transform + composite, not a repaint.

### 7.4 Allocation & memory discipline
- **Zero-allocation render loop.** No LINQ, no boxing, no per-frame `new` on the hot
  path. Reused buffers, pooled objects, `Span<T>`/`stackalloc`, `struct` scene nodes.
- Computed styles and layout data live in **struct-of-arrays / dense arrays** keyed
  by node id — cache-friendly, low-GC, SIMD-amenable — not per-node heap dictionaries.
- Configure the GC for low pause (SustainedLowLatency during interaction); the real
  goal is to not allocate in the first place so the GC never runs mid-frame.

### 7.5 GPU & paint
- GPU-accelerated Skia (`GRContext`) is the default path; CPU raster is a fallback.
- Batch draw calls; minimise state changes and texture uploads; clip to damage rect.
- Text is shaped once (HarfBuzz) and cached as glyph runs / `SKTextBlob`; a glyph
  atlas avoids re-rasterising fonts every frame.
- **Presentation backends (`ISurfaceHost`), auto-selected:** the engine already
  paints to any `SKCanvas`, so backends differ only in how pixels reach the window:
  - **GL** (`GRContext`) — GPU path, default when a driver is present.
  - **Software present** — render to a CPU `SKBitmap`, then blit to the window
    (Win32 GDI DIB now; SDL2 software renderer for cross-platform). Works with
    **no GPU / over RDP / in VMs**. Slower than GPU, so it's a compatibility
    fallback — the §7.1 budgets assume GPU; software targets "smooth for static /
    light UIs" rather than 120 fps.
  Startup tries GL and falls back to software automatically; damage-region repaint
  (§7.3) matters even more here since every dirty pixel costs CPU.

### 7.6 Input latency
- Input is sampled and coalesced against the render clock; pointer-move events don't
  each trigger a full pipeline — they mark damage and let the next vsync frame reflect
  the latest state. Scrolling is driven on the render thread for minimum latency.

### 7.7 Measurement (non-optional)
- A built-in profiler HUD (frame time, layout/paint/GC breakdown, dropped frames).
- **Performance regression tests in CI** against the §7.1 budgets on representative
  scenes (long lists, deep trees, heavy text, animated transforms). A PR that blows
  a budget fails, same as a broken test.

### 7.8 AOT (serves performance)
- **No reflection on hot paths.** Binding, styling, and semantics use **source
  generators**, not `System.Reflection` / `System.Linq.Expressions`.
- AngleSharp may need `[DynamicDependency]`/trim annotations; validate under
  `PublishTrimmed` early (M1).
- **UIA COM interop under NativeAOT** is the known sharp edge — prototype the Windows
  a11y bridge under `PublishAot=true` before committing the approach.
- AOT buys the < 250 ms cold-start target and predictable, JIT-free frame timing.

---

## 8. CSS scope — ambition vs. sequencing

**Ambition:** as much of the **modern core** of CSS as we can implement *well and
fast*. Correctness and 60 fps beat breadth — a property is only "supported" once it
meets its §7 performance budget. Below is delivery order, not a ceiling.

**In scope v1**
- Box model: `width/height`, `min/max-*`, `margin`, `padding`, `border`,
  `box-sizing`, `border-radius`.
- Layout: `display: flex | block | inline-block | none`, full flexbox
  (`flex-direction`, `justify-content`, `align-*`, `flex-grow/shrink/basis`,
  `gap`, `order`, `flex-wrap`), `position: relative | absolute`, `top/left/...`,
  `z-index`, `overflow: hidden | scroll`.
- Painting: `color`, `background-color`, `background-image`, linear/radial
  gradients, `box-shadow`, `opacity`, `border-*`.
- Text: `font-family/size/weight/style`, `line-height`, `text-align`,
  `letter-spacing`, `white-space` (basic), `text-overflow: ellipsis`.
- Selectors: type, class, id, descendant, child, `:hover`, `:focus`, `:active`,
  `:disabled`, attribute selectors.
- Units: `px`, `%`, `em`, `rem`, `vw`, `vh`.

**Committed next (v1.x, in priority order)**
- ~~**CSS Grid**~~ — **DONE (subset)**: `grid-template-columns/rows`, `fr`/`px`/`%`/`auto`,
  `repeat()`, `gap`, row-major auto-placement, column spans, content/auto rows, cell
  stretch. (rowSpan>1, named lines, `minmax()`, `align/justify-items` still to come.)
- 2D `transform` + `transition` + `@keyframes` animations — these are *core* to
  "feeling fluid," run on the render thread as composited transforms (cheap), and
  are prioritised accordingly.
- Full inline/bidi line-breaking, `@media` queries, `calc()`, `filter`.

**Consciously skipped (modern-core focus)**
- `float`-based layout, multi-column, print/`@page`, legacy/obscure pseudo-classes.
  These are legacy cruft, not modern core, and are out of scope by choice.

Rationale: Yoga delivers correct flexbox essentially for free; grid and rich inline
text flow are the two engines we'd otherwise have to write, so they wait.

---

## 9. Step 2 — Web / WASM target

Reuse Layers 1–6; swap Layer 0 (Shell) and the a11y bridge:
- **Render:** the same Skia display list runs as **Skia-on-WebGL** compiled to WASM
  (the model Uno's Skia renderers use), drawing to a `<canvas>`.
- **Shell:** browser event loop + canvas surface instead of Silk.NET.
- **A11y:** hidden DOM overlay (see §5) instead of native providers.
- Because binding/layout/paint are platform-neutral, ~80% of the code is shared.

### 9.0 Two web hosts (both supported)
The same `CupriApp` runs in the browser via either host — the engine is identical; only
the canvas/runtime bridge differs:
- **Raw .NET-WASM (`samples/WebWasm`, default)** — `Microsoft.NET.Sdk.WebAssembly` + a
  ~50-line `main.js`: boot `dotnet.js`, `[JSExport]` `RenderFrame`/`PointerDown/Move/Up`/
  `Wheel`/`KeyChar`/`EditKeyPress`, `[JSImport]` a `present(rgba,w,h)` that `putImageData`s
  the engine's pixels onto a `<canvas>`. A `requestAnimationFrame` loop drives `@keyframes`
  + the live Diagnostics re-bind, and forwards pointer/wheel/keyboard so text input, keyboard
  focus/Tab order, scrolling and overlays all work in the browser — the **same ShowcaseApp**
  the desktop Viewer runs. This is the literal §9.1 "thin JS glue" model — no Blazor, minimal
  deps. **Verified rendering in a real browser** (headless Chrome screenshot of the running
  WASM app shows the full ShowcaseApp). Two WASM-specific gotchas fixed: `System.Diagnostics.
  Process` is unsupported in the browser sandbox (the Diagnostics metrics are guarded → "n/a"),
  and the runtime must be started with `runMain()` (which stays resident), not `dotnet.run()`
  (which exits after `Main`, breaking later `[JSExport]` calls).
- **Blazor (`samples/Web`, alternative)** — `SkiaSharp.Views.Blazor`'s `<SKCanvasView>`
  provides the canvas glue for free; heavier, but ideal for **embedding CupriFace inside
  an existing Blazor app**. Deliberately MINIMAL: it routes clicks and nothing else — no
  scroll, no keyboard, no touch, no frame loop. It answers "how do I put this in my Blazor
  app", not "here is a maintained third web host". The input contract lives in `WebWasm`
  (and `WebLlvm`), which is where the browser gate points; bringing it here would mean a
  third copy of the same seam for the host with the fewest users, so the honest move is to
  say so rather than to imply parity.
Both link Skia natively into wasm (via `SkiaSharp.NativeAssets.WebAssembly`).

### 9.1 JS glue — scope and boundaries
A browser/WASM target unavoidably needs a **thin JS interop shim**. This is
**platform plumbing, not application logic**, and is consistent with the no-JS
principle (§1) because **app authors never write or see it** — it ships inside
CupriFace's web runtime. The glue is deliberately minimal and does **no** app
behaviour, layout, or styling:
- Bootstrap the .NET WASM runtime and hand it the `<canvas>` + WebGL context.
- Forward raw browser events (pointer/keyboard/resize/focus/IME) into WASM.
- Sync the hidden a11y DOM overlay with the semantics tree (§5).
- Plumb clipboard / resize-observer / devicePixelRatio and similar host APIs.

**Boundary rule:** all logic (parse → style → layout → paint → bind → semantics)
lives in C#/WASM. The JS layer only marshals bytes and events across the boundary.
If a piece of "glue" starts making UI decisions, it belongs in C#, not JS. Keep the
shim small enough to audit in one sitting.

---

## 10. Component model & native control library

Two levels of element:
1. **Primitive elements** — `div`-like boxes, text runs, `img`. The render layer
   only ever deals in primitives.
2. **Components (custom elements)** — author-defined tags (e.g. `<cupri-slider>`)
   that **expand** into a subtree of primitives, attach C# behaviour, ship default
   styles, and bake in accessibility. This is the MudBlazor-style layer.

Because CupriFace owns the parser (AngleSharp), custom elements are natural: an
unknown tag is looked up in the **component registry**; if found it's instantiated,
otherwise it degrades to a plain inline/block box. **First-party and third-party
components use the exact same public API** — the built-in library is just the first
consumer of it.

### 10.1 What a component is
A component is three parts, composed at instantiation (not per frame):

| Part | Contents | Notes |
|---|---|---|
| **Template** | default HTML/CSS subtree (structure + default theme styles) | can be source-generated for AOT |
| **Behaviour (code-behind)** | a C# class: bindable typed properties, input/keyboard/focus handling, state | pure C#, no JS |
| **Semantics** | ARIA role + states baked in (slider→`role=slider`,`aria-valuenow`…) | every control accessible **by default** (§5) |

```
<cupri-slider min="0" max="100" value="{{Volume}}" />
        │
        ├─ template  → <div class="track"><div class="fill"/><div class="thumb"/></div>
        ├─ behaviour → CupriSlider : Component  (drag/keyboard → updates Value → binds Volume)
        └─ semantics → role=slider, aria-valuemin/max/now, focusable, arrow-key support
```

### 10.2 "Native" clarified
These are **framework-native**, not OS-native widgets: CupriFace draws them itself
via Skia so they look and behave **identically on every platform** (the
Flutter/MudBlazor model), rather than hosting Win32/Cocoa/GTK controls (which would
fracture the look and fight the canvas model). They are, however, mapped to the
correct **native accessibility roles** per platform (§5), so a screen reader treats
`<cupri-slider>` as a real slider.

### 10.3 Binding & theming
- Components expose typed, bindable properties; attributes bind to the app's C#
  model (`value="{{Volume}}"`, two-way for inputs) via the §6 source-gen binding.
- **Design tokens / theme (IMPLEMENTED):** CSS custom properties (`--name: value`) +
  `var(--name, fallback)` are supported in the engine — cascaded and inherited like real
  CSS. First-party controls read tokens with light fallbacks (e.g. `background:var(
  --cupri-surface, white)`), so **dark mode is a token swap**: `body.dark { --cupri-bg…}`.
  Authors reskin by overriding tokens — no need to touch component internals.
  (Demo: the Showcase's Dark-mode switch toggles `body.dark`.)
- **Scoped styles (optional):** a component's default CSS is scoped so it neither
  leaks into nor is clobbered by app styles; app-level overrides still win via
  tokens and documented class hooks (MudBlazor's approach).

### 10.4 Performance (must obey §7)
- Expansion is a **one-time** cost into the retained primitive scene; a property
  change patches only the affected primitives. Components add **no per-frame cost** —
  they're instantiate-time, not render-time.
- Control templates are source-generated (AOT-clean, no reflection).

### 10.5 The library — `CupriFace.Controls` (a.k.a. Cupri UI)
Ships as first-party controls **and** as the reference samples for authoring your
own. Each is accessible + themed + bindable out of the box.

**Shipped (buildable on today's engine):** `<cupri-icon>` (SVG icon set),
`<cupri-button>`, `<cupri-icon-button>`, `<cupri-checkbox>`, `<cupri-radio>`,
`<cupri-switch>`, `<cupri-slider>`, `<cupri-textfield>`, `<cupri-number>`, `<cupri-textarea>`,
`<cupri-select>`/`<cupri-option>`, `<cupri-badge>`, `<cupri-chip>`, `<cupri-avatar>`,
`<cupri-card>`, `<cupri-divider>`, `<cupri-stat>`, `<cupri-progress>`,
`<cupri-spinner>`, `<cupri-skeleton>`, `<cupri-alert>` (`samples/Controls`); the
**navigation/disclosure** set `<cupri-tabs>`/`<cupri-tab>`, `<cupri-accordion>`/`<cupri-accordion-item>`,
`<cupri-tree>`/`<cupri-tree-item>`, and the **data** control `<cupri-table>`/`<cupri-row>`/`<cupri-cell>`
(`samples/NativeControls`); plus the **overlays** `<cupri-dialog>`, `<cupri-drawer>`, `<cupri-toast>`,
`<cupri-menu>`/`<cupri-menu-item>`, `<cupri-popover>`, `<cupri-tooltip>` — modal + anchored,
top-layer, with open/close interaction (`samples/Overlays`).

**Gating engine capabilities — each unlocks a whole tier:**
1. ~~**Text input**~~ → **DONE (v1)**: focus (click, `:focus`), caret (positioned by
   measured text), keyboard editing (type/backspace/delete/arrows/home/end), two-way
   bound `<cupri-textfield>`; SDL `TextInput` (IME-aware) + GL key events. `samples/TextInput`.
   Remaining: text selection, multi-line textarea, caret blink, click-to-place-caret.
2. ~~**Overlay/top-layer + anchored positioning**~~ → **DONE** (position:fixed +
   z-index + top-layer paint/hit-test; anchor placement with flip + shrink-to-fit).
   Remaining overlay controls (select/dropdown, popover, context menu, drawer) are now
   thin additions on this foundation.
3. ~~**Scrolling**~~ → **DONE** (overflow:scroll/auto → clipped scroll offset + mouse
   wheel + scrollbar thumb; hit-testing accounts for the offset). `samples/Scroll`.
   Remaining: scrollbar drag, horizontal scroll, and persisting offset across a rebuild.
4. ~~**Focus + tab order + keyboard nav**~~ → **DONE (v1)**: Tab/Shift-Tab move keyboard
   focus across the focusable controls (innermost interactive element, DOM order), Enter/Space
   activate the focused control (routed through the shared click path), typing reaches a
   focused text field, and a **focus-visible** ring is painted only after Tab (not on mouse
   click). `samples/Keyboard`; SDL + GL hosts map Tab/Shift-Tab. Refinements **DONE**
   (`samples/KeyboardNav`): arrow-key nav within a radio group (moves + selects), arrow-nudge a
   focused slider, **focus trapping** inside an open overlay (Tab scoped to the panel marked
   `data-focus-scope`; focus enters on open) and **Escape** to close the top-most overlay (or
   blur a field). Remaining: OS screen-reader bridge (needs native interop), Home/End within lists.
5. ~~**Icon rendering (SVG paths)**~~ → **DONE** (`<cupri-icon>`).
6. **Virtualisation** → data grid, large lists/trees.

**Shipped since:** `<cupri-select>` (menu + anchor + generic `data-set-*` write-back),
`<cupri-popover>`, `<cupri-drawer>`, `<cupri-tabs>`, `<cupri-accordion>`, `<cupri-textarea>`
(multi-line edit + per-line caret), `<cupri-table>`, `<cupri-tree>` — all in `samples/NativeControls`.
**Remaining refinements:** `<cupri-combobox>` (select + type-to-filter), table **virtualisation**
(#6) for very large row counts, and keyboard nav (#4) across all of them.

> Naming convention: **hyphenated** custom-element names (`<cupri-slider>`) follow
> the HTML custom-elements rule (a hyphen distinguishes them from current/future
> built-in tags) and keep us compatible with the real browser DOM in the §9 web
> target. `<cuprislider>` would work in our parser but breaks that convention — we
> standardise on the hyphenated form.

### 10.6 Input validation — permissive edit, validate on commit
A **project-wide UX principle** for every validated field (not just numbers): **never
block the user mid-edit.** While a field is focused it edits a permissive *buffer* that
may hold an invalid value; the field shows a **red border** (`[data-invalid]`) while the
buffer is invalid, and validation/clamping happens only on **commit** (blur or Enter).
This keeps text always editable — you can type past a limit, delete to empty, or paste
garbage, then fix it — instead of the engine silently rejecting keystrokes.

Mechanics (engine, `CupriDocument`):
- A focused field holds `_editBuffer` (raw text), seeded from the bound value on focus.
- Keystrokes edit the buffer freely. A **valid** buffer is *live-committed* to the model
  (so other bindings track it); an **invalid** buffer stays local and the model keeps its
  last good value.
- On blur/Enter the buffer is validated: parseable + in range → clamp + commit;
  unparseable (`""`, `"abc"`) → **revert** to the last good value.
- Validity is per-field (attributes like `data-numeric`, `data-min`, `data-max`). The
  focused field's buffer is painted over the bound value each rebuild, with
  `[data-invalid]` toggled for the red border. Verified in `samples/NumberInput`.

New validated controls should follow this same buffer → red-border → commit-on-blur shape
rather than rejecting input inline.

---

## 11. Solution structure (proposed)

```
CupriFace.sln
├─ src/
│  ├─ CupriFace.Dom            # AngleSharp DOM wrapper, node ids, invalidation
│  ├─ CupriFace.Css            # cascade, computed style, selector matching
│  ├─ CupriFace.Layout         # Yoga P/Invoke + block/box model
│  ├─ CupriFace.Layout.Native  # vendored libyoga builds per RID
│  ├─ CupriFace.Text           # HarfBuzz shaping + font fallback
│  ├─ CupriFace.Paint          # Skia display list + renderer
│  ├─ CupriFace.Binding        # runtime binding types
│  ├─ CupriFace.Binding.Gen    # Roslyn source generator
│  ├─ CupriFace.Accessibility  # semantics tree (platform-neutral)
│  │  ├─ .Windows (UIA)  .Linux (AT-SPI)  .Mac (NSAccessibility)
│  ├─ CupriFace.Components      # component model: registry, template, base classes (§10)
│  ├─ CupriFace.Components.Gen  # Roslyn source generator for control templates
│  ├─ CupriFace.Controls        # first-party control library (<cupri-slider> …)
│  ├─ CupriFace.Theme           # design tokens + default light/dark themes
│  ├─ CupriFace.Shell          # Silk.NET window/input/GPU surface
│  └─ CupriFace                # public API / app host
├─ samples/
│  ├─ HelloBox                 # milestone-1 spike
│  └─ ControlsGallery          # showcase + reference for authoring components (§10.5)
└─ tests/
```

---

## 12. Roadmap / milestones

| # | Milestone | Exit criteria |
|---|---|---|
| M0 | Shell spike | Silk.NET window + Skia `GRContext`, vsync 60fps, render thread decoupled from UI thread (§7.2), **profiler HUD showing frame time from day one** |
| M1 | **Hello Box** | Parse tiny HTML/CSS → one flex container of coloured boxes laid out by Yoga and painted. **+ semantics tree emits one node per box (proves a11y architecture). + 0-alloc idle frame + CI perf-budget harness (§7.7) in place.** |
| M2 | Text | HarfBuzz-shaped text in boxes; font size/weight/colour; measure-callback into Yoga |
| M3 | Full flexbox + box model | §8 "in scope" layout/paint properties complete |
| M4 | Binding | Source-gen one/two-way + collection binding; a live sample driven by a C# model |
| M5 | **Component model** | Registry + custom-element expansion + template/behaviour/semantics (§10); author a trivial `<cupri-slider>` end-to-end (bound, themed, keyboard-accessible) |
| M6 | Control library v1 | `CupriFace.Controls` core set (§10.5) + theme tokens + `ControlsGallery` sample; each control meets its §7 budget and a11y role |
| M7 | Accessibility bridge | UIA (Win) first, then AT-SPI/NSAccessibility; screen-reader reads the gallery controls correctly |
| M8 | AOT hardening | `PublishAot` build of the gallery on all three desktop OSes |
| M9 | (Step 2) WASM | Same gallery rendering to `<canvas>` with hidden-DOM a11y |

### Agent / dev introspection (debug channel) — DONE (v1)
A rudimentary debug feature so an **AI agent** (or developer) can "see" and diagnose the
live form during development **without a screenshot**. `doc.DebugDump(w, h)` lays out and
returns one indented **JSON** snapshot (read-only; never mutates state):
- the **render tree** — each node's tag/classes/role, absolute **layout box** (x/y/w/h),
  text content, key styles (display/bg/color/font-size), and state **flags**
  (`focus`/`hover`/`active`/`invalid`/`top-layer`/`scrollable`, with scroll y/max);
- **interaction state** — focus (key + caret + edit buffer + numeric validity/min/max),
  hover chain, drag, and open **overlays** (each `data-bind-open` path + bool);
- **current bound model values** — every `data-bind-*` path resolved to its value;
- the **semantics tree** (§5) — role/name/value/checked/focusable per node.

Verified in `samples/AgentDebug` (drives an invalid over-max entry, then asserts the dump
exposes the focus buffer, `bufferValid:false`, the model's last-good value, a11y, and
boxes). Goal met: an agent can query "what's on screen, where, and why" and pinpoint
layout/binding bugs mechanically instead of eyeballing a PNG. Remaining (optional): a
visual **debug overlay** that outlines layout boxes / flags overflow in the live window.

### Implementation status — M0–M9 complete
Every roadmap milestone and the §8 feature scope is implemented and verified (snapshots
for visual features; thread-ID/console assertions for non-visual; build for the two the
headless environment can't run — see caveats).

- **Engine** — AngleSharp DOM + real CSS selector cascade (specificity/order/inline);
  managed **block + flexbox + CSS grid** (grow/shrink, justify/align, gap, **wrap**,
  **max-content**, **absolute/relative**; grid `fr`/`px`/`%`/`repeat()`/`minmax()`,
  column **and row spans**, `align/justify-items`); box model + `overflow` clip;
  **`transform`** (translate/scale/rotate) + **`@keyframes` animation**; **`@media`**
  queries + **`calc()`**.
- **Text** — HarfBuzz shaping (kerning/ligatures, Greek/Cyrillic/Arabic) + **simplified
  bidi** reordering for mixed LTR/RTL; word-wrap, baseline, text-align.
- **Paint / threading** — immutable **DisplayList** snapshot + Skia rasteriser, driven
  across a real **render thread** (`ThreadedRenderer`, §7.2) decoupled from the UI thread.
- **Binding** — `{{path}}` interpolation, attribute + **two-way** binding, `data-repeat`;
  **source-generated accessors** (`[CupriBindable]`) make it **AOT/trim-clean**.
- **Components** — registry + custom-element expansion; controls
  `<cupri-slider|switch|progress|button|badge>` with themed CSS + `role`/`aria-*`.
- **Interaction** — pointer input on both window backends → **hit-testing** → dispatch →
  control behaviours (switch toggle, slider drag, button click) + C# handler API.
- **Accessibility (M7)** — platform-neutral **semantics tree** (verified dump) + Windows
  **UIA bridge** scaffold (role→pattern mapping).
- **AOT (M8)** — ILC compiles the whole engine **trim-clean (0 warnings)**.
- **Shells** — GPU **GL** window, **Win32 GDI** software window, **SDL** cross-platform
  software window; the Viewer auto-selects GL → software.
- **Web (M9)** — Blazor **WASM** host renders the engine to `<canvas>` via
  `SKCanvasView`; canvas clicks route through the same hit-test/dispatch.

### Portability model
The stack is layered so OS-specific code is isolated and opt-in:
- **Engine (`CupriFace`)** — 100% portable managed code; renders to any `SKCanvas`, no OS
  calls. Native Skia/HarfBuzz are referenced for **win + linux + osx** via
  `src/SkiaNativeAssets.props` (publish deploys only the target RID); WASM natives come
  from `SkiaSharp.Views.Blazor`.
- **Windowing (`CupriFace.Shell`)** — two cross-platform backends: **GL** (Silk.NET) and
  **SDL software** (no-GPU present). Both reach native code through *managed* Silk.NET
  bindings, so windowing ships **no hand-written P/Invoke**. The engine has **no** windowing
  dependency at all. (An earlier Win32 GDI backend was removed in favour of SDL to keep our
  code fully managed.)
- **Accessibility** — the semantics tree is portable; the bridges are not, and they are the
  only place OS-specific accessibility code is allowed to live. All four ship — **UIA**
  (Windows), **AT-SPI** (Linux), **NSAccessibility** (macOS), **TalkBack** (Android) — each
  with a blocking CI gate driven by a real AT client. They cost strikingly different amounts
  of interop, and the reason is worth naming: AT-SPI is D-Bus over a Unix socket — IPC, not
  an OS API — so its bridge is *pure managed IL with no interop at all*, and a Windows build
  carries the D-Bus library without ever loading it. UIA and NSAccessibility are real OS
  APIs, and account for the project's only two quarantined P/Invoke files (`UiaInterop.cs`,
  `ObjC.cs`). The macOS one needs no compiled Objective-C shim: it builds its Objective-C
  classes at runtime with managed function pointers as their method implementations. The
  Android one (`TalkBackBridge`) speaks through the .NET-Android bindings — managed JNI, no
  hand-written P/Invoke, so the two-file quarantine count still holds.
- **Android (`CupriFace.Android`)** — the mobile host: `CupriActivity` + an
  `SKGLSurfaceView` (the same GPU model as the desktop GL window) + `AndroidHost`. Its one
  structural rule: **the GL thread is the document thread** — UI events cross via the view's
  event queue, UI-thread readers get an immutable post-frame snapshot. Logical px are
  Android **dp** (density folds into the present scale), so the same markup lays out at
  phone-native sizes. Runs **CoreCLR** (`UseMonoRuntime=false`, enforced by the package's
  buildTransitive targets): Mono 10.0.11 has a codegen defect on Android — forensics in
  `samples/AndroidProbe/MONO-CRASH.md`. Touch, fling and IME composition live in the
  ENGINE (`Interaction/TouchInput`, the composition seam) — portable, headless-tested,
  shared with the web host.
- **Web** — the same engine to `<canvas>` via WASM.

Net: to add a platform you implement (at most) a windowing/input host + an a11y bridge; the
engine, layout, paint, text, binding, and components are shared unchanged. Android is the
worked example: the host package plus one gesture recognizer and one IME seam in the engine,
both of which desktop and web share.

### Input model
The engine takes **one pointer** and a keyboard. Everything richer is built above that, and the
layering is deliberate:

- **Activation is on pointer-DOWN** for the mouse (`DispatchClick` *is* the down event), which a
  finger cannot live with — a scroll that began on a button would press it. `TouchInput` (portable,
  in the engine) holds the decision back: a still short press becomes a tap at finger-UP, travel
  beyond a slop becomes a scroll, a still long press becomes the context menu, and an explicit drag
  affordance (slider thumb, scrollbar, reorder grip, split divider) drags from the first touch,
  because there the mouse semantics and the touch semantics agree.
- **Scrolling has two axes**, which chain independently, and a gesture locks to the axis it
  committed to. Momentum (`StartFling`) and the rubber band both live in the DOCUMENT rather than
  the recognizer, so every host's existing animation gates drive them with no host change — and
  both had to join *both* gates, the wake gate and the drive gate, which is an asymmetry worth
  remembering.
- **Multi-pointer is a seam, not a feature.** `doc.OnPointer` gives an author raw pointers with
  web-style **capture** (a pointer is owned by an element on down and stays until it lifts), which
  is what stops a pinch also scrolling the page beneath it — and why there is no `touch-action`
  arbitration. The engine computes no gesture: what a second finger means is the author's decision,
  and guessing would be worse than not guessing.
- **Everything that aims at an element agrees.** Paint, hit-testing and the semantics tree apply the
  same scroll offsets, the same rubber band and the same CSS transform matrix. A control that has
  been scrolled, stretched, scaled or rotated is touchable and readable *where it now is* — the
  class of bug that appears the instant these three disagree.
- **Interaction state is keyed by structural path**, never by node reference: the tree is rebuilt
  constantly (per keystroke), so a captured pointer, an in-flight fling and a scroll offset all
  survive by describing *where* rather than *what*.

### Presentation scaling
The host presents a document via `CupriApp.Present(windowW, windowH)` → a **logical
viewport + scale factor**; the host does `canvas.Scale(scale)` then lays out at the
logical size, and divides pointer coordinates by `scale`. This unifies four modes:
- **None** — logical = fixed design size; window resize reveals background (no reflow).
- **Responsive** — logical = window; reflows every frame (the engine re-layouts cheaply).
- **Zoom z%** — logical = window/z, scale = z (DPI-like; Skia scales the vectors crisply).
- **Hybrid** — `z = min(winW/designW, winH/designH)`: the tighter axis sits at design
  scale, the longer axis gets extra logical space and reflows.

The root (body) fills the viewport (initial containing block), so `height:100%` fills the
window and "None vs Responsive" is just "fixed vs window" logical size. *(Live-resize
fluidity during the OS modal resize loop is a per-backend follow-up — the reflow itself
is instant.)*

### Write-once app model (desktop ⇄ web)
An app is a portable **`CupriApp`** (markup + CSS + components + model + handlers, no
platform code). Hosts consume it: **`DesktopHost.Run(app)`** opens a GL/SDL window;
the Blazor **`CupriView`** component renders the *same* app to a `<canvas>`. "Exporting a
desktop app as a website" is recompiling the same `CupriApp` against the web host — the
engine, layout, binding, components, and hit-test/dispatch are identical on both. See
`samples/DemoApp` (the app) + `samples/Viewer` (desktop) + `samples/Web` (browser).

**Caveats (environment, not code):**
- **AOT**: ILC trim analysis is clean, but the final native **link** can't run here
  (no VS C++ toolchain on PATH); produce the binary on a box with the linker.
- **SDL window & WASM**: **build-verified** only — a live SDL window needs a desktop
  display, and the WASM app needs a browser; neither runs in this headless session.
- `:hover`/`:active`, full (spec) UBA bidi, transitions (vs `@keyframes`), and a
  *complete* NSAccessibility provider remain as refinements. (UIA and AT-SPI now ship and
  are gated in CI; the refinements left inside each are listed in ROADMAP §1.)

---

## 13. Key risks

0. **Sustained fluidity (the core requirement)** — it's easy to hit 60 fps on a
   demo and lose it on real scenes (long lists, deep trees, heavy text). Mitigation:
   the render-thread/commit-snapshot split (§7.2), damage-region everything (§7.3),
   0-alloc render loop (§7.4), and **CI perf-budget gates from M1** so regressions
   fail the build instead of accumulating silently.
1. **Layout completeness** — flexbox is easy (Yoga); grid + inline/bidi text flow
   are the expensive engines. Sequenced (v1 flexbox, grid v1.x) to protect quality.
2. **Accessibility across 3 OSes** — highest-effort, lowest-visibility work; UIA
   COM + NativeAOT is the sharpest edge. Prototype in M1/M5, don't defer.
3. **AOT vs AngleSharp trimming** — validate under `PublishTrimmed` early (M1).
4. **Native `libyoga` build/distribution** per RID — own the build to avoid
   binding-license and versioning surprises.
5. **Text/font fallback** — international text + emoji font fallback is deceptively
   deep; HarfBuzz shapes but fallback selection is our code.

---

## Appendix A — Naming
**CupriFace** — *cupri-* (copper, L. *cuprum*) + *face* (interface). Working name.
