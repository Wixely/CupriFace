# CupriFace Roadmap

The core milestones **M0–M9 are complete** (engine, layout, text, paint, binding, components,
interaction, Windows a11y scaffold, AOT trim-clean, WASM host) — see [DESIGN.md §12](DESIGN.md).
This document tracks everything **considered but not yet implemented**: the deferred refinements
called out in the design plus items we've since scoped. It is a living list, not a commitment of
dates.

**Status legend:** 🔴 not started · 🟡 partial / scaffolded · 🟢 prototyped, needs hardening
**Priority:** **P1** (core to the product's promises) · **P2** (important) · **P3** (nice to have)

---

## 1. Accessibility bridges (P1)

The platform-neutral **semantics tree** exists and is verified, but only Windows has a bridge, and
it's a scaffold. Screen-reader support is the highest-effort, lowest-visibility work (DESIGN risk #2).

| Item | Status | Notes |
|------|--------|-------|
| Complete **UIA** provider (Windows) | 🟡 | Role→pattern scaffold exists (`WindowsUiaBridge`); needs full pattern coverage + a real screen-reader pass. |
| **AT-SPI** bridge (Linux) | 🔴 | The missing sibling of the UIA bridge. |
| **NSAccessibility** bridge (macOS) | 🔴 | The missing sibling of the UIA bridge. |
| **Web hidden-DOM a11y** | 🔴 | The canvas is opaque to screen readers; mirror the semantics tree into an offscreen ARIA DOM for the WASM host. |
| End-to-end screen-reader verification of the control gallery | 🔴 | The M7 exit criterion, per-OS. |

## 2. CSS engine (P2)

Ambition is the modern core; these are the sequenced-but-unshipped pieces (DESIGN §8, §12 caveats).

| Item | Status | Notes |
|------|--------|-------|
| **CSS transitions** | 🟢 | **Done** — `transition: <prop> <dur> [easing] [delay]` (comma-separated) animates `opacity`, `background`/`color`/`border-color` and `transform` when their target value changes (hover, class/model change, theme toggle). Easing keywords (`linear`/`ease`/`ease-in`/`ease-out`/`ease-in-out`) **and `cubic-bezier(x1,y1,x2,y2)` literals** (incl. overshoot), via a Newton cubic-bézier solver; parsing is paren-aware so a `cubic-bezier()`'s inner commas don't break the list. State keyed by structural path so it survives the per-interaction rebuild. Paint-only (no relayout), driven by the same per-frame `Animate` clock as `@keyframes`. Follow-ups: width/height (layout-affecting) transitions, per-property longhands. |
| Real **`:hover` / `:active`** pseudo-classes | 🟢 | **Done** — authored as `:hover`/`:active`/`:focus` (rewritten to marker attributes the engine toggles). `:hover` follows the pointer; `:active` marks the pressed element chain on pointer-down and clears on pointer-up (holds while held, like a real button press). |
| **`filter`** | 🟢 | **Done** — `filter: blur() brightness() contrast() grayscale() saturate() sepia() invert() opacity() drop-shadow()`, composed into one `SKImageFilter` (colour-matrix ops fold into a colour filter; blur/drop-shadow are image filters) and applied via a `SaveLayer` wrapping the element's subtree. Follow-up: animating `filter` via `transition`. |
| **Border style keywords** (`solid`/`dashed`/`dotted`) | 🔴 | Currently parsed but ignored (`StyleResolver` "style keyword ignored in M1"); only solid rendering. |
| Grid: **named lines** + **multi-row spans** (`rowSpan>1`) | 🔴 | Grid v1 covers tracks/`fr`/`repeat()`/`gap`/column spans; these are the remaining refinements (`LayoutEngine` comment). |
| Full **inline / bidi line-breaking** | 🟡 | Simplified bidi reorder ships; the full Unicode Bidi Algorithm + rich inline flow remain. |

## 3. Text & internationalization (P2)

| Item | Status | Notes |
|------|--------|-------|
| International + **emoji font fallback** | 🟡 | HarfBuzz *shapes*, but fallback-face **selection** is our code and is deceptively deep (DESIGN risk #5). |
| Full spec **UBA** bidi | 🟡 | See §2 — current reorder is simplified. |

## 4. Performance & threading (P1)

Sustained fluidity is the core requirement (DESIGN risk #0); a demo hitting 60 fps isn't enough.

| Item | Status | Notes |
|------|--------|-------|
| Render-thread split in the **interactive windows** | 🟡 | `ThreadedRenderer` (commit-snapshot split, §7.2) exists, but the live GL/SDL windows still render single-threaded (`SkiaWindow` SCOPE NOTE). Wire the split into the interactive path. |
| **List/tree virtualization** | 🔴 | Long lists and deep trees are where 60 fps is lost; no virtualization yet. |
| **CI perf-budget gates** | 🔴 | The design calls for perf budgets failing the build from M1; no CI harness is wired up in the repo. |
| **Live-resize fluidity** during the OS modal resize loop | 🟡 | Reflow itself is instant; streaming frames *during* a drag-resize is a per-backend follow-up. |

## 5. Web / WASM target (P2)

| Item | Status | Notes |
|------|--------|-------|
| **Prompt-free clipboard** via a hidden focused `<textarea>` | 🔴 | Current copy/paste uses `navigator.clipboard` (can prompt for paste; `readText` also wedges headless automation). A hidden textarea receiving native `copy`/`cut`/`paste` events avoids both. |
| **IME composition** on the web host | 🔴 | Desktop handles IME (SDL `StartTextInput`); the raw-WASM keyboard path takes single chars only. |
| WASM **a11y** | 🔴 | Same item as §1's hidden-DOM a11y. |
| Fix Mono **WASM-AOT codegen** so `CupriFace.dll` can be AOT-compiled | 🟡 | It's currently force-*interpreted* to dodge a `function signature mismatch` (SliderComponent interface dispatch); the rest AOT-compiles. Interpreting the engine assembly costs web performance. Track/patch upstream or refactor the offending dispatch. |

## 6. AOT & build (P2)

| Item | Status | Notes |
|------|--------|-------|
| Native **link** of the AOT desktop build in CI | 🟡 | ILC trim analysis is clean (0 warnings); the final link needs a box with the C++ toolchain on PATH. |
| Publish matrix across the three desktop OSes | 🟡 | M8 exit criterion; build-verified, not yet run on all three. |

## 7. Controls & component library (P3)

| Item | Status | Notes |
|------|--------|-------|
| **Right-click context menu for inputs** | 🟢 | **Done** — Cut / Copy / Paste / Select-all on right-click in text fields & textareas. Engine-owned overlay: `DispatchContextMenu(x,y)` opens a self-styled `position:fixed` menu at the pointer (Cut/Copy greyed when there's no selection); items raise `ContextRequested`, which each host routes through the SAME clipboard seam as its keyboard shortcuts (desktop SDL/GLFW `MouseButton.Right`, web `contextmenu`). Dismisses on outside-click / wheel / keystroke / Escape. |
| **Extensibility for custom interaction primitives** | 🔴 | Custom components reuse the engine's built-in interaction vocabulary (roles + `data-*` hooks) and `OnClick`; a genuinely new low-level gesture/keybinding needs an engine hook. Design a registration point so third parties aren't limited to the built-in set. |
| CSS-controllable **icon sizing** | 🔴 | `IconMarkup` sets `width/height` inline (a deliberate per-use size); expose it as a variable/class if authors want to restyle icon size via CSS. |
| **Images** (`<cupri-image>`) | 🟢 | **Done** — SkiaSharp decode + `DrawImage` command; `src` via `CupriSource` (embedded/file/URL) or `data:` URI; `object-fit` contain/cover/fill/none; intrinsic + aspect sizing. **Remote (`http(s)`) images now load asynchronously** on a background task (they never block the first paint; the image pops in when it arrives — `ConsumeImageArrived()` flags the repaint, de-duped per src). Local (embedded/file/`data:`) decode synchronously. URL policy is configurable via `UseImageUrlOptions`. |
| **Video / audio** | 🔴 | Royalty-free, permissively-licensed stack (WebM + VP9/AV1 via libvpx/dav1d, shipped per-RID like Skia) → managed decode→`SKImage`. Platform-agnostic + license-clean; avoids per-OS media frameworks. |
| Additional controls | 🔴 | Candidates: date/time picker, typeahead combobox, sortable/virtualized data table, hover-triggered tooltip (today's tooltip is `open`-driven). |
| Visual **debug overlay** | 🔴 | Optional dev aid: outline layout boxes / flag overflow in the live window (the agent-readable `DebugDump` already exists). |

## 8. Tooling & quality (P2)

| Item | Status | Notes |
|------|--------|-------|
| **Automated test project** (xUnit) + CI | 🔴 | Behaviour is currently verified with throwaway harnesses (selection, label-click, style-override). Promote these into a permanent suite so they run in CI instead of by hand. |

## 10. Text input polish

| Item | Status | Notes |
|------|--------|-------|
| **Single-line field: `white-space:nowrap` + horizontal caret-follow** | 🟢 | **Done** — a `<cupri-textfield>` now stays one line (was ballooning when a long/pasted value wrapped): `white-space:nowrap` lays the value on a single line, `overflow:hidden` clips it, and a per-field `ScrollX` follows the caret so the visible window tracks typing/navigation (preserved across rebuilds like `ScrollY`). `white-space` is a real inherited property (`nowrap`/`pre` supported). |

## 9. Embedding & overlays (P3)

Rendering *over* other content — the desktop, a game, an HTML page — so the UI can be composited by a host.

| Item | Status | Notes |
|------|--------|-------|
| **Transparent / frameless / top-most windows** | 🟢 | **Done** — `CupriApp.Transparent`/`Frameless`/`TopMost`; the GL host opens a transparent framebuffer + transparent clear (premultiplied output = what compositors want), the SDL fallback honours frameless/top-most (opaque). Portable Silk.NET/GLFW traits, no OS-specific code. Needs a compositing WM (universal on modern OSes); degrades to opaque otherwise. [`samples/TransparentHud`](samples/TransparentHud/). |
| **Web canvas overlay** | 🟢 | **Done** — transparent clear + straight-alpha present for `putImageData`; the JS glue passes pointer events through wherever nothing is drawn. |
| **`RenderToPixels` embed primitive** | 🟢 | **Done** — RGBA8888 `byte[]`, premultiplied or straight alpha, for blitting into any host surface (game texture, another render target). |
| **Unity / game-engine embed sample** | 🔴 | The pieces exist (`RenderToPixels` straight-alpha → `Texture2D.LoadRawTextureData`); a worked sample + input-forwarding shim is the follow-up. |
| **SDL software-path transparency** | 🔴 | The GL path is transparent; the CPU/SDL fallback blits opaque. Per-pixel alpha against the desktop on the software path is a per-OS follow-up (deliberately deferred as "too OS-specific"). |

---

## Non-goals (won't do — by design)

Per [DESIGN.md §1](DESIGN.md); listed here so "deferred" is never confused with "planned but missing":

- **A JS engine in the runtime**, or JavaScript in app UI pages. (The web target's non-authored
  bootstrap/interop glue is expected and separate.)
- **100% CSS spec compliance** — we chase the modern core and consciously skip legacy cruft:
  `float`-based layout, multi-column, print/`@page`, obscure pseudo-classes.
- **A full DOM `document.*` scripting API** — there's no script to call it.

---

## Operational (not a feature)

- **Publish to GitHub** — `origin` is set to `https://github.com/Wixely/CupriFace`; local `main` has
  unpushed commits. Push once the remote repo exists.
