# CupriFace Toolbox

The **Toolbox** is the first‑party library of `cupri-*` custom elements plus the small API a
developer uses to bind them to a C# model. This document explains the mental model, how to wire an
app up, how binding/interaction/styling work, and gives a reference entry for every element.

> CupriFace renders your UI to a pixel buffer with Skia — there is **no HTML DOM at runtime and no
> browser widgets**. You author markup + CSS + a model; the engine parses it once, expands the
> `cupri-*` elements into themed primitive subtrees, lays them out, and paints. The *same* app runs
> on a desktop window (GL/SDL) or in the browser (WASM → `<canvas>`). See [DESIGN.md](DESIGN.md).

---

## 1. Mental model

Three inputs, one output:

| Input | What it is |
|-------|------------|
| **HTML** | A string of markup using plain tags (`div`, `span`, …) and `cupri-*` elements. |
| **CSS**  | A string of styles. A practical subset of CSS: classes, descendant/child selectors, `:hover`‑style state via `[data-hover]`, `@media (width)`, CSS variables. |
| **Model** | Any C# object. `{{Property}}` in markup reads it; interactive controls write back to it. |

The engine turns these into a render tree (`RenderNode`) and paints it. Input events
(`DispatchClick`, `DispatchKey`, …) mutate the model and re‑bind. That's the whole loop.

---

## 2. Quick start

### 2a. Directly with `CupriDocument`

```csharp
using CupriFace;
using CupriFace.Binding;
using CupriFace.Components;

// 1) A model. Mark it [CupriBindable] + partial so binding is reflection-free (AOT/trim-safe).
[CupriBindable]
sealed partial class Settings
{
    public string Name { get; set; } = "Ada";
    public int Volume { get; set; } = 60;
    public bool DarkMode { get; set; }
}

var model = new Settings();

const string html = """
  <body>
    <div class="row"><span class="lbl">Name</span>
      <cupri-textfield value="{{Name}}" placeholder="Your name…"></cupri-textfield></div>
    <div class="row"><span class="lbl">Volume</span>
      <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider><span>{{Volume}}</span></div>
    <div class="row"><span class="lbl">Dark mode</span>
      <cupri-switch checked="{{DarkMode}}"></cupri-switch></div>
    <cupri-button class="save">Save</cupri-button>
  </body>
  """;

const string css = ".row{display:flex;align-items:center;gap:12px;margin:10px;} .lbl{width:90px;}";

using var doc = CupriDocument.Load(html, css)
    .UseComponents(ComponentRegistry.Default())   // the Toolbox
    .Bind(model);                                 // two-way binding to `model`

doc.OnClick(".save", _ => Console.WriteLine($"Saved {model.Name}, vol={model.Volume}"));
```

`CupriDocument` is the fluent core:

| Member | Purpose |
|--------|---------|
| `CupriDocument.Load(html, css?)` | Parse markup + optional stylesheet. |
| `.UseComponents(registry)` | Register the `cupri-*` library (use `ComponentRegistry.Default()`). |
| `.Bind(model)` | Bind a model for `{{…}}` and two‑way controls. |
| `.OnClick(selector, handler)` | Register a click handler matched by CSS selector (bubbles target→root). Handler gets a `CupriPointerEvent`. |
| `.OnShortcut(mods, key, handler)` | Register a keyboard shortcut, e.g. `OnShortcut(KeyMods.Ctrl, "k", OpenPalette)` (Cmd maps to Ctrl). A Ctrl/Cmd chord fires anywhere; a plain‑key one only when no field is focused. The Web/Viewer hosts forward Ctrl/Cmd + letter. |
| `.OnAction("data-…", handler)` | Register a **custom interaction primitive**: when a clicked/keyboard‑activated element carries that `data-*` attribute, the handler runs (`CupriActionEvent` = element, value, model, x/y); return true to consume. Extends the built‑in `data-*` vocabulary without an engine change. |
| `.Refresh()` | Re‑bind + rebuild (call after you mutate the model from code). |
| `.Render(canvas, w, h)` | Paint into an `SKCanvas` (a full, stateless repaint). |
| `.RenderIncremental(canvas, w, h, bg)` | Damage‑tracked repaint for a host whose canvas **retains** its pixels between frames: diffs against the last presented frame, repaints only the changed rectangle, and returns it — or `null` when the frame is identical (skip presenting entirely). First call / size change = full. The desktop software window and the WASM host use this; pair it with the `Dispatch*` return values for render‑on‑demand. |
| `.RenderToImage(w, h, clear?)` | Convenience CPU raster to an `SKImage` (headless/tests). |
| `.RenderToPixels(w, h, clear?, straightAlpha?)` | CPU raster to an RGBA8888 `byte[]` — the canonical "embed me in another surface" call (HTML canvas, a game texture). `clear` defaults to **transparent**; set `straightAlpha` for consumers wanting non‑premultiplied alpha (HTML `ImageData`, Unity `RGBA32`). |
| `.DispatchClick/DispatchPointerMove/DispatchPointerUp/DispatchWheel/DispatchKey(...)` | Feed input. Each returns whether anything changed (drives render‑on‑demand). |
| `.DispatchContextMenu(x, y)` | Right‑click: opens a Cut/Copy/Paste/Select‑all menu if `(x,y)` is over a text field. Items raise `ContextRequested`; the host performs the clipboard op. Wired for you by `DesktopHost` and the WASM host. |
| `.Root` | The root `RenderNode` (layout boxes via `HitTesting.AbsoluteBox`). |

### 2b. As a portable `CupriApp` (recommended for real apps)

Subclass `CupriApp` and you get a definition with **no windowing dependency** — the desktop and web
hosts both consume it:

```csharp
public sealed class MyApp : CupriApp
{
    private readonly Settings _model = new();

    public override string Html => "...";
    public override string Css  => "...";
    public override string Title => "My App";
    public override object? Model => _model;

    // Optional: register click/behaviour handlers once the doc is built.
    public override void Configure(CupriDocument doc) => doc.OnClick(".save", _ => Save());

    // Optional: re-bind on a timer for values that drift (e.g. live diagnostics). 0 = never.
    public override double RefreshIntervalSeconds => 0;

    // Optional: control how the logical viewport maps into the window (scaling — see §7).
    // public override PresentInfo Present(float w, float h) => ...;
}
```

Run it on the **desktop** ([`DesktopHost`](src/CupriFace.Shell/DesktopHost.cs) tries GL, falls back
to the SDL software window):

```csharp
CupriFace.Shell.DesktopHost.Run(new MyApp());
```

Run it on the **web**: compile the same class against the WASM host in
[`samples/WebWasm`](samples/WebWasm/) — the thin JS glue blits the engine's pixels to a `<canvas>`
and forwards input. "Exporting to a website" is just recompiling the app against the web host.

Run it on **Android** (`dotnet workload install android` first): reference
[`CupriFace.Android`](src/CupriFace.Android/) from a `net10.0-android` project and subclass
[`CupriActivity`](src/CupriFace.Android/CupriActivity.cs):

```csharp
[Activity(MainLauncher = true)]
public sealed class MainActivity : CupriActivity
{
    protected override CupriApp CreateApp() => new MyApp();
}
```

That's the whole app — the host brings the GL surface, touch gestures (tap/fling/long-press),
the soft keyboard with real IME composition, and the TalkBack bridge. Logical px are Android dp,
so `@media (max-width: …)` phone layouts fire naturally. The runtime is CoreCLR by default (the
package pins it — see [`samples/AndroidProbe/MONO-CRASH.md`](samples/AndroidProbe/MONO-CRASH.md)
for why); [`samples/AndroidViewer`](samples/AndroidViewer/) is the worked example, running the
phone-first `MobileApp` with the desktop Showcase reachable from its About page.

### 2c. Transparent overlays & floating HUDs

Three opt‑in flags on `CupriApp` turn an ordinary window into an overlay that composites over whatever
is behind it — the desktop, a game, or an HTML page — with **no OS‑specific code** (they map to portable
Silk.NET/GLFW window traits):

| Flag | Effect |
|------|--------|
| `Transparent` | Clears to fully transparent instead of `Background`, so wherever the markup doesn't paint stays see‑through. |
| `Frameless` | Borderless window — no title bar or chrome (HUDs, custom windows). |
| `TopMost` | Always‑on‑top. |

```csharp
public override bool Transparent => true;
public override bool Frameless   => true;
public override bool TopMost     => true;
```

- **Desktop:** the GL host opens a transparent framebuffer and clears transparent; premultiplied output
  is exactly what OS compositors expect, so there's no conversion. Transparency needs a compositing
  window manager (universal on Windows 8+/macOS/modern Linux) and degrades to an opaque window where
  none is present — the host environment's concern. The SDL software fallback is opaque, but still
  honours `Frameless`/`TopMost`. See [`samples/TransparentHud`](samples/TransparentHud/).
- **Web:** the canvas clears transparent and presents **straight** (non‑premultiplied) alpha for
  `putImageData`; the JS glue makes the canvas an overlay and passes pointer events **through** wherever
  nothing is drawn.
- **Embedding elsewhere** (a game texture, another render target): use
  [`RenderToPixels`](#) — `straightAlpha: true` for HTML/Unity‑style straight alpha, `false` (premul)
  for OS compositors.

---

## 2.1 Loading markup, styles & assets

Inline strings are fine for a snippet, but real apps author markup/styles (and later images/media)
as **files** and load them through a `CupriSource`, which carries a `ResourceTrust`. There are three
origins:

| Source | Trust | Use it for |
|--------|-------|-----------|
| `CupriSource.Embedded(assembly, "Assets/App.html")` / `Embedded<T>(…)` | **Embedded** — **preferred** | Anything you ship. Compiled into the binary: no IO, no network, tamper‑resistant, resolves identically on desktop and WASM. |
| `CupriSource.File(path)` | **LocalFile** | Loading from disk at runtime (dev, plugins). Reads whatever is at `path` — validate it if the path is untrusted (traversal/TOCTOU). |
| `CupriSource.Url(uri, options?)` | **Remote** | Remote‑hosted UI. The **most dangerous** — see below. |
| `CupriSource.Text(literal)` | Embedded | An in‑memory string escape hatch. |

CupriFace runs **no JavaScript**, so even untrusted markup can't execute code — but it can still drive
bindings, pull sub‑resources (future `url()`), and exhaust memory, so non‑embedded origins surface
their risk. `CupriSource.Url` defaults are strict and loosening them is explicit:
`RequireHttps=true`, `MaxBytes` cap, `Timeout`, `FollowRedirects=false`, optional `AllowedHosts`;
a tripped guard throws `CupriResourceException`.

### The recommended pattern — embedded, but editable

Author files under an **`Assets/`** folder (real `.html`/`.css`, full IDE tooling). Import the
resource pipeline and the generator in your `.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="…/src/CupriFace.Resources.Gen/CupriFace.Resources.Gen.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<Import Project="…/src/CupriFace.Resources.targets" />   <!-- embeds Assets/** at compile time -->
```

The generator emits a **strongly‑typed `Assets` class** from those files, so a rename or a typo is a
**compile error**, not a runtime surprise. Your app just points its sources at them:

```csharp
public sealed class MyApp : CupriApp
{
    protected override CupriSource MarkupSource => Assets.MyApp.Html;   // Assets/MyApp.html
    protected override CupriSource StyleSource  => Assets.MyApp.Css;    // Assets/MyApp.css
}
```

`CupriApp.Html`/`Css` default to reading these sources (override either the sources or the strings).
For a one‑off you can skip the generator with the `EmbeddedAsset("Assets/MyApp.html")` helper. This
same `CupriSource` (via `ReadBytes()`) is how images/fonts/media will load — see
[ROADMAP.md](ROADMAP.md). `samples/DemoApp` is the worked example.

---

## 3. Data binding

### Interpolation vs. two‑way

- **`{{Path}}` anywhere in text or in a *mixed* attribute value** → one‑way interpolation (read the
  model, substitute the value).
- **An attribute whose *entire* value is `{{Path}}`** → **two‑way**. The engine substitutes the
  value *and* records a `data-bind-<attr>` hook, so user interaction writes back to `model.Path`.

```html
<cupri-slider value="{{Volume}}"></cupri-slider>   <!-- two-way: dragging sets model.Volume -->
<span>Volume is {{Volume}}%</span>                 <!-- one-way: text interpolation only -->
```

The attribute that is two‑way differs per control (it's called out in the reference below):
`value`, `checked`, `group`, or `open`.

### `[CupriBindable]` models

Mark model types `[CupriBindable]` and `partial`. A source generator emits an `IBindableAccessor`
(get/set by name) so binding uses **zero reflection** — this is what keeps two‑way write‑back working
in trimmed/AOT (published web) builds. Types without the attribute still bind via a reflection
fallback, which is **not** AOT‑safe. Value coercion (`"60"` → `int`, etc.) is handled for you.

### Updating from code

Mutating the model in a click handler and returning re‑binds automatically. If you mutate the model
outside of an input dispatch (e.g. a background timer), call `doc.Refresh()` (or set
`RefreshIntervalSeconds` on your `CupriApp`).

---

## 4. Interaction model

- **Clicks** hit‑test to the deepest node, then activation walks **up** the ancestor chain applying
  the first built‑in behaviour (stepper, toggle, tab/option select, overlay open/close) or matching
  `OnClick` handler.
- **Control labels are clickable.** Clicking the text label next to a `cupri-checkbox` /
  `cupri-radio` / `cupri-switch` toggles/selects it (like HTML `<label>`), whether the label sits
  before or after the control.
- **Keyboard & focus.** Tab / Shift+Tab move focus across interactive controls (a focus ring shows after
  Tab, not after a mouse click); Space/Enter activate; Escape dismisses an overlay. Arrows drive the
  control they're in: nudge a slider, move+select within a radio group, navigate a date grid or a select
  /combobox list, **→/← expand or collapse a focused tree item**, and **↑/↓ move a focused `<cupri-reorder>`
  row** (the keyboard equivalent of dragging it). Focus is trapped inside an open overlay (dialog/menu
  /drawer).
- **Text editing.** `cupri-textfield` / `cupri-textarea` / `cupri-number` support caret placement,
  selection (drag, double‑click word, triple‑click line, Shift+arrows, Ctrl+A), clipboard
  (Ctrl+C/X/V), and **undo/redo** (Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z — history is per‑field) on both
  desktop and web. Editing is permissive: the field shows a red border while a value is invalid and
  validates/clamps on blur.
- **Validation.** A bound field can carry `required`, `pattern="regex"`, `minlength`, and numeric
  `min`/`max`. The engine shows the red border while a rule fails and injects an inline error message
  **once the field is left** (blurred) or the form is validated — so it never nags mid‑type. `error="…"`
  overrides the default message. Call `doc.ValidateAll()` from a submit handler to reveal every field's
  error at once; it returns whether the form is valid.
  ```html
  <cupri-textfield value="{{Email}}" pattern="[^@ ]+@[^@ ]+\.[^@ ]+" error="Enter a valid email"></cupri-textfield>
  ```
- **Links & navigation.** An `<a href="…">` is a real link — focusable (Enter activates), shows the
  pointer cursor, and defaults to the accent colour. Three href kinds:
  - **`#anchor`** — the engine scrolls the element with that `id` to the top of its nearest scrollable
    container. Handled entirely in‑engine.
  - **internal** (a bare path like `charts`) and **external** (anything with a URL scheme —
    `https:`, `mailto:`, `tel:`, protocol‑relative `//`) both raise the multicast `doc.Navigated`
    event as a `NavigateEvent(Href, External)`. The engine opens nothing itself: you route internal
    hrefs (e.g. switch a view) and the host opens external ones. The desktop + WASM hosts already open
    external links in a browser, so you typically only wire the internal side.
  ```csharp
  doc.Navigated += e => { if (!e.External) model.Section = e.Href; };  // route internal links to a view
  ```
  ```html
  <a href="#pricing">Jump to pricing</a>  <a href="reports">Reports</a>  <a href="https://x.com">Docs ↗</a>
  ```
- **Cursors** are automatic for native controls (pointer over links/buttons, text over fields, the resize
  arrows over drag boundaries) and settable with the CSS `cursor` property (see §5). Hosts read
  `doc.CursorAt(x,y)` each move.

You rarely touch any of this directly — you register `OnClick` for custom actions and let bound
controls handle their own state.

---

## 5. Styling & theming

- Author normal CSS. Every component adds stable class hooks you can override
  (e.g. `.cupri-button`, `.cupri-switch`, `.cupri-slider-thumb`).
- **State selectors:** `:hover` / `[data-hover]` (pointer over), `:active` (pressed — held while the
  mouse button is down), `:focus`, `[data-invalid]` (bad field value),
  and variant/state classes like `.on` (checked switch/checkbox/radio) and `.ghost` (button variant),
  `.active` (current tab), `.selected` (chosen option).
- **Positioned parts are overridable too.** Nothing visual is locked behind computed inline styles:
  discrete positions are driven by state classes (e.g. `.cupri-switch.on .cupri-switch-knob { left:23px }`),
  and data-driven positions are published as the custom property `--cupri-fill` (a percentage) that
  the fill/thumb read in CSS — so you can restyle them freely:
  ```css
  .cupri-slider-fill  { width: var(--cupri-fill); background: #10b981; }
  .cupri-slider-thumb { left: calc(var(--cupri-fill) - 9px); border-radius: 4px; }
  .cupri-switch.on .cupri-switch-knob { left: 26px; }
  ```
- **Theme via CSS variables.** Many components read these with sensible fallbacks, so define them
  once (e.g. on `body`) to retheme surfaces/text/borders:
  `--cupri-text`, `--cupri-muted`, `--cupri-surface`, `--cupri-border`, `--cupri-hover`.
  **Setting `color` alone is not enough.** The text inputs draw their value with `--cupri-text` and
  their placeholder with `--cupri-muted` rather than inheriting `color`. A dark theme that sets only
  `body { color: … }` therefore leaves the value on its light-theme near-black default, so text
  looks like it dims as you type — which reads as a disabled field rather than a theming gap.
  The default accent is copper `#B87333` (hence *Cupri*). Controls that don't read a variable can
  still be restyled through their class hooks.
- `@media (width ...)` is supported and re‑resolves on viewport change, so layouts can be responsive.
- **`position: sticky`.** An element flows normally, but while its scroll container is scrolled it holds
  at the top (its `top` offset from the scrollport) instead of scrolling away — pinning a section header —
  and releases when its containing block scrolls out. It paints above the content that slides under it, so
  give it an opaque background. (`relative`/`absolute`/`fixed` are also supported; `fixed` lifts to the
  top layer over the page.)
  ```css
  .section-title { position: sticky; top: 0; background: var(--cupri-bg); border-bottom: 1px solid #ddd; }
  ```
- **Inline formatting.** A run of text and inline elements (`<code> <b> <em> <mark> <span> …`) flows into
  wrapping line boxes. An inline element with a `background`/`border`/`border-radius` + horizontal
  `padding` paints as a chip that flows with the words and gets its own rounded box on **each line it wraps
  across** — e.g. inline `<code>`. The standard inline tags default to `display:inline` (`code`, `kbd`,
  `mark`, `sub`, `sup`, `abbr`, `cite`, `time`, …); `code`/`kbd`/`samp`/`var` also default to monospace.
  ```css
  code { background: var(--cupri-hover); border: 1px solid var(--cupri-border);
         border-radius: 5px; padding: 1px 5px; }   /* an inline code chip */
  ```
- **Motion.** `@keyframes` (looping animations) and **`transition`** are both supported. A `transition`
  eases a property from its old value to its new one whenever that value changes — on `[data-hover]`,
  `:focus`, a state/class change, a model update, or the theme toggle. Animatable: `opacity`,
  `background`/`color`/`border-color`, `transform` (translate/scale/rotate), `filter` (op‑by‑op), and the
  box sizes **`height`** and **`width`** — a `height` animates to/from `auto` too (a panel collapse/expand,
  as `<cupri-accordion>` does); `width` animates between definite sizes (a sidebar collapsing to an icon
  rail). Timing: `linear`/`ease`/`ease-in`/`ease-out`/`ease-in-out` or `cubic-bezier(x1,y1,x2,y2)`
  (overshoot allowed). All but `height`/`width` are paint‑only (cheap); a size transition re‑lays‑out each
  frame, so the element and everything around it reflow as it animates.
  ```css
  .nav  { transition: background-color 0.2s ease, color 0.2s ease; }   /* smooth hover highlight */
  .card { transition: transform 0.25s ease-out; }
  .card:hover { transform: translateY(-6px); }                          /* lift on hover */
  .surface { transition: background-color 0.35s ease; }                 /* light/dark cross-fade */
  .rail { width:200px; overflow:hidden; transition: width 0.28s ease; }
  .rail.collapsed { width:64px; }                                       /* sidebar → icon rail, content reflows */
  .panel { height:0; overflow:hidden; transition: height 0.25s ease; }
  .panel.open { height:auto; }                                          /* slide a panel open, content reflows */
  ```
- **`filter`.** `blur() brightness() contrast() grayscale() saturate() sepia() invert() opacity()
  drop-shadow()` are supported and compose left-to-right (applied to the element and its subtree).
  ```css
  .thumb  { filter: grayscale(1) brightness(0.9); }
  .glass  { filter: blur(6px); }
  .raised { filter: drop-shadow(2px 4px 6px #0006); }
  ```
- **Gradients.** `background: linear-gradient([<angle>|to <side>], <stop>, …)` and
  `radial-gradient([shape,] <stop>, …)`, where a stop is a colour with an optional position
  (`#4682B4 60%`). Angles are CSS (`0deg` = up, `90deg` = right); `to right`/`to bottom right`/… work
  too. Paints over `background-color`; also settable via `background-image`.
  ```css
  .hero { background: linear-gradient(135deg, #B87333, #4682B4); }
  .bar  { background: linear-gradient(#5aa0e0, #2b5f92); }
  .glow { background: radial-gradient(#ffd39a, #B87333); }
  ```
- **`box-shadow`.** `[inset] <x> <y> [blur] [spread] [color]`, comma‑separated for multiple layers —
  outset drop shadows (soft elevation) and `inset` inner shadows. The first‑party cards and overlays
  (dialog, drawer, shelf, menu, select, popover, tooltip, toast, pickers) ship with sensible shadows.
  ```css
  .card    { box-shadow: 0 1px 2px #0000001a, 0 4px 12px #00000014; }
  .modal   { box-shadow: 0 18px 50px #00000040; }
  .pressed { box-shadow: inset 0 2px 6px #00000033; }
  ```
- **`backdrop-filter`.** Frosts what's painted *behind* an element instead of the element itself —
  used by the modal/drawer/shelf scrims (`<cupri-dialog blur>` etc.). Honoured only on a full‑viewport
  **top‑layer** element (`position:fixed` covering the page): it blurs the whole page behind the
  overlay, which then paints sharp on top. Same function syntax as `filter` (typically just `blur()`).
  ```css
  .cupri-backdrop.blurred { background:#00000055; backdrop-filter: blur(9px); }
  ```
- **`font-style` + `text-decoration`.** `font-style: normal | italic | oblique` selects a real slanted
  face (it measures and shapes in that face, so it composes with `font-weight` — bold italic is a
  genuine bold-italic face, not a synthesised slant). `text-decoration` (or `text-decoration-line`)
  supports `underline`, `line-through`, `overline`, combinations, and `none`; extra shorthand words
  (`wavy`, a colour) are ignored rather than mis-parsed. Both **inherit**. Tag defaults follow the web:
  `<em> <i> <cite> <dfn> <var> <address>` are italic, `<u>/<ins>` underlined, `<s>/<del>` struck, and
  **`<a href>` is underlined** — colour alone is not a sufficient cue (WCAG 1.4.1) — which plain CSS
  overrides.
  ```css
  a          { text-decoration: none; }        /* opt out of the default link underline */
  .price-was { text-decoration: line-through; }
  blockquote { font-style: italic; }
  ```
- **`cursor`.** Sets the pointer shape and **inherits** like normal CSS. Supported keywords: `default`,
  `pointer`, `text`, `wait`, `progress`, `help`, `crosshair`, `move`, `not-allowed`, `grab`, `grabbing`,
  `col-resize`/`ew-resize`, `row-resize`/`ns-resize`, `nwse-resize`, `nesw-resize`, `none` (and `auto` =
  unspecified). Where nothing sets it, the toolkit **infers** one automatically — `pointer` over links,
  buttons and anything wired to act on click; `text` over text fields; the resize arrows over a corner
  resize grip or a resizable table's column edge — so native controls get the right cursor with no CSS.
  Hosts read `CupriDocument.CursorAt(x,y)` (a `CursorType`; `CursorCss(...)` gives the CSS keyword for
  web hosts) after each pointer move and apply it; the desktop + WASM hosts already do.
  ```css
  .help-badge { cursor: help; }
  .disabled   { cursor: not-allowed; }
  ```

```css
body { --cupri-surface:#1e2430; --cupri-text:#eef1f5; --cupri-border:#33405a; }  /* a dark theme */
.cupri-button { border-radius: 12px; }                                           /* override a hook */
```

---

## 6. Element reference (the Toolbox)

`ComponentRegistry.Default()` registers everything below. The **Bind** column names the attribute
that is *two‑way* (writes back to the model when the whole value is `{{Path}}`); other attributes are
static config unless noted.

### Inputs

| Element | Purpose | Key attributes | Bind (two‑way) | Children | role |
|---------|---------|----------------|----------------|----------|------|
| `<cupri-slider>` | Range slider (track/fill/thumb) | `min` (0), `max` (100), `value` | `value` | — | `slider` |
| `<cupri-switch>` | On/off toggle | `checked` | `checked` | — | `switch` |
| `<cupri-checkbox>` | Checkbox with tick icon | `checked` | `checked` | — | `checkbox` |
| `<cupri-radio>` | Radio — standalone or grouped | standalone: `checked`; grouped: `group` + `value` | `checked` **or** `group` | — | `radio` |
| `<cupri-progress>` | Read‑only progress bar | `value` (0), `max` (100) | — | — | `progressbar` |
| `<cupri-button>` | Themed button | `variant` (`primary`\|`ghost`) | — | label text/HTML | `button` |
| `<cupri-icon-button>` | Icon‑only button | `icon` | — | — | `button` |
| `<cupri-textfield>` | Single‑line text input | `value`, `placeholder` | `value` | — | `textbox` |
|  ↳ *draws its value with* `var(--cupri-text, …)` *and its placeholder with* `var(--cupri-muted, …)` — **not** the inherited `color`, so a dark theme must set those variables or the typed value stays near-black. | | | | | |
| `<cupri-number>` | Numeric field + `−/+` steppers | `value`, `min`, `max`, `step` | `value` | — | `spinbutton` |
| `<cupri-textarea>` | Multi‑line text input | `value`, `placeholder`, `follow-tail` | `value` | — | `textbox` (`aria-multiline`) |
| `<cupri-select>` | Dropdown picker | `value`, `open` | `value` (and `open`) | `<cupri-option value="…">Label</cupri-option>` | `combobox` |
| `<cupri-combobox>` | Typeahead: editable field + suggestions that filter as you type (free‑text; the dropdown shows while focused) | `value`, `placeholder` | `value` | `<cupri-option value="…">Label</cupri-option>` | `combobox` |
| `<cupri-datepicker>` | Date field + month calendar popup. `value` is ISO `yyyy‑MM‑dd`; a day pick sets it and closes; ‹ › page months in place | `value`, `open` | `value` (and `open`) | — | `combobox` |
| `<cupri-timepicker>` | Time field + popup with scrollable hour/minute columns. `value` is `HH:mm` (24h); picking updates that part and keeps the popup open | `value`, `open` | `value` (and `open`) | — | `combobox` |
| `<cupri-color>` | Colour field: a chip+hex trigger opens an anchored palette (hue×shade grid + a white→black neutral ramp). `value` is `#RRGGBB` (or `#RGB`); clicking a swatch writes it and closes, and the swatch matching the current value is ringed | `value`, `open`, `placeholder` | `value` (and `open`) | — | `combobox` |
| `<cupri-search>` | Single‑line search field with a leading 🔍 and a trailing clear (×) that shows once there's text | `value`, `placeholder` | `value` | — | `textbox` |
| `<cupri-password>` | Masked text field — value stays plaintext, the field paints bullets and briefly peeks the last‑typed char; copy is blocked until revealed. Add `reveal="{{Show}}"` for an eye toggle | `value`, `placeholder`, `reveal` | `value` (and `reveal`) | — | `textbox` |
| `<cupri-segmented>` | Connected button bar bound to one value (radios rendered as a segmented control); the matching segment is active | `value` | `value` | `<cupri-segment value="…">Label</cupri-segment>` | `radiogroup` |
| `<cupri-rating>` | Star rating; clicking the Nth star writes N (stars up to `value` are filled) | `value`, `max` (5) | `value` | — | `slider` |
| `<cupri-pagination>` | 1‑based page navigator: ‹ prev, first/last, a fixed‑width window around the current page (with … gaps), next › — constant width, so it doesn't shift as you page | `page`, `pages` | `page` | — | `navigation` |
| `<cupri-table sort>` | Add `sort="{{Sort}}"` and the header cells become click‑to‑sort triggers (numeric or text, asc↔desc, ▲/▼) that reorder the body rows | `sort` | `sort` | `<cupri-row>` / `<cupri-cell>` | `table` |
| `<cupri-table select>` | Add `select="{{Set}}"` and body rows are multi‑selectable — a click toggles the row's index in the bound comma‑set (e.g. `"0,3"`) and highlights it. Give the table `height`+`overflow:scroll` and the header row **sticks** to the top while the body scrolls | `select` | `select` | `<cupri-row>` / `<cupri-cell>` | `table` |
| `<cupri-table resize>` | Add `resize="{{Cols}}"` and columns become drag‑resizable — drag a header cell's right boundary and that column's content width (px) is written into the bound comma‑list (e.g. `"160,90"`) and applied to every row's matching cell, so columns stay aligned. The last column is left flexible so the table keeps filling its box | `resize` | `resize` | `<cupri-row>` / `<cupri-cell>` | `table` |

```html
<!-- Radio group: each option shares the bound `group`; clicking sets the group's value -->
<cupri-radio group="{{Size}}" value="small"></cupri-radio><span>Small</span>
<cupri-radio group="{{Size}}" value="large"></cupri-radio><span>Large</span>

<cupri-select value="{{Country}}" open="{{PickerOpen}}">
  <cupri-option value="ie">Ireland</cupri-option>
  <cupri-option value="uk">United Kingdom</cupri-option>
</cupri-select>
```

**Images.** `<cupri-image>` decodes a raster image and paints it, fitted to its box. The `src`
resolves through the same resource pipeline as markup/CSS — a bare name is an **embedded** asset
(`src="Assets/logo.png"`), or use a `data:` URI, an `https://` URL, or a `file://` path. Size it with
CSS `width`/`height` (aspect preserved if you set only one); `fit` defaults to `contain`.
```html
<cupri-image src="Assets/logo.png" alt="Logo" style="width:64px;height:64px;border-radius:12px"></cupri-image>
<cupri-image src="https://example.com/banner.jpg" fit="cover" style="width:100%;height:120px"></cupri-image>
```

**Video.** `<cupri-video>` plays video through whichever backend the HOST registered
(`doc.UseVideo(...)` — the optional `CupriFace.Media` WebM decoder on desktop, the browser's own
decoder on the web host; with no backend the `poster` shows and the controls are inert). `controls`
adds an engine-drawn bar (play/pause, mute, a seek slider with position/duration clocks —
scrub with the pointer, arrow keys step ±5 s, and AT sets it through RangeValue — plus
fullscreen; real components, so they're themable and
accessible); clicking the picture toggles playback. `autoplay` is honored **only together with
`muted`** — the web's rule, applied on every host so one app behaves the same everywhere.
Full-window video is just sizing (`width:100%;height:100%` + `fit="cover"`). The ⛶ button
fullscreens **the video itself**, the way the web does it: the element expands over the whole
viewport in the top layer (letterboxed on black, the bar still overlaid) *and* the window goes
OS/browser-fullscreen through `WindowCommandRequested` — together the video fills the screen.
Escape (or ⛶ again) undoes both; on the web the browser's own Esc is picked up via
`fullscreenchange`, so the element never sticks.

`src` resolves **exactly like an image** — the developer picks the scheme per element:
an **embedded** asset (bare name, the assembly registered via `UseImages`), a **disk** file
(`file://` or a path), an inline `data:` URI, or a **web URL** fetched under the document's
`UseImageUrlOptions` policy (https-only, size cap, timeout by default). Remote sources open
*deferred* — the poster stays up, playback starts when the bytes land, never blocking a frame; on
the web host, remote URLs stream through the browser natively while embedded/disk/`data:` sources
play from the same resolved bytes, so every scheme works on every host.
```html
<cupri-video src="Assets/intro.webm" poster="Assets/intro.png" controls muted autoplay loop
             label="Product tour" fit="cover" style="width:100%;height:260px"></cupri-video>
<cupri-video src="https://example.com/trailer.webm" controls muted></cupri-video>
```
**Wiring a backend.** The web host has one built in (the browser decodes). On desktop, add the
optional **`CupriFace.Media`** package — WebM/VP9+Opus, with decoders for every desktop RID inside
that single package — and attach it at the host composition root, *not* in your `CupriApp` (which
a web build also compiles):
```csharp
DesktopHost.Run(new MyApp(), doc =>
{
    if (NativeDecoders.Available)                       // false → poster + disabled controls
        doc.UseVideo(new WebmVideoBackend(new NativeDecoders(), SdlAudioSink.TryCreate()));
});
```
Audio is optional (`TryCreate` returns null on a machine with no device — video still plays).
Format scope is deliberately royalty-free: **WebM with VP9/VP8 video and Opus audio**; there is no
H.264/MP4 path, by licence policy.

*Where the decoders come from:* consuming the **NuGet package**, they're already inside it
(`runtimes/<rid>/native/` for all **six** desktop RIDs — win/linux/osx × x64/arm64) and .NET picks
the right one — nothing to do. Building **this repo from source**, they aren't in git (they're
build outputs of the pinned upstream sources): download the latest green **Codecs** workflow run's
artifacts into `native/<rid>/` and every project here picks them up automatically, laying them out
per-RID beside the app (a flat copy can't work — `cupricodecs.dll` is *both* Windows RIDs) with
`CodecLibraryResolver` loading the running one. Without them `NativeDecoders.Available` is false
and video degrades to the poster, which is exactly what a consumer without the package sees.

Every one of those six libraries is *executed* in CI, not merely built: the Codecs workflow
publishes MediaProbe once on Windows and decodes real frames from those same binaries on x64 and
ARM Linux, Intel and Apple-silicon macOS, and x64 and ARM Windows.

**Resizable controls.** CSS `resize: both | horizontal | vertical` puts a grab handle in an
element's bottom‑right corner — dragging it resizes the element, clamped to its
`min-/max-width/height`. It's generic (works on any element — a textarea, an image frame, a panel)
and the dragged size survives re‑renders.
```css
.frame { resize: both; min-width:220px; max-width:660px; min-height:140px; max-height:460px; }
cupri-textarea { resize: vertical; min-height:78px; max-height:300px; }
```

**Scrolling text fields.** Give a `cupri-textarea` a bounded height (`max-height` / `height`) and it
scrolls when the content overflows — mouse wheel, a draggable scrollbar thumb, and caret‑into‑view
while typing all work, and the scroll position survives edits. Add **`follow-tail`** to keep it pinned
to the bottom as new lines arrive (logging), *unless* the user has scrolled up:
```html
<cupri-textarea value="{{Log}}" follow-tail style="max-height:160px"></cupri-textarea>
```

### Content

| Element | Purpose | Key attributes | Children | role |
|---------|---------|----------------|----------|------|
| `<cupri-image>` | Raster image (PNG/JPEG/WebP/GIF) | `src`, `alt`, `fit` (`contain`\|`cover`\|`fill`\|`none`) | — | `img` or decorative |
| `<cupri-video>` | Video (host-registered backend) | `src`, `poster`, `fit`, `label`, `controls`, `autoplay` (with `muted`), `muted`, `loop` | — | `img` + button controls |
| `<cupri-icon>` | Vector icon (current text colour) | `name`, `size` (24), `aria-label`? | — | `img` or decorative |
| `<cupri-badge>` | Small pill label | — | text/HTML | — |
| `<cupri-chip>` | Pill with optional close icon | `closable` | text/HTML | — |
| `<cupri-avatar>` | Circular initials | `initials` | — | — |
| `<cupri-card>` | Padded rounded surface | — | arbitrary | — |
| `<cupri-divider>` | Horizontal rule | — | — | `separator` |
| `<cupri-stat>` | Metric value + caption | `value`, `label` | — | — |
| `<cupri-markdown>` | Renders a Markdown subset — `#`/`##`/`###` headings, `**bold**`, `*italic*`/`_italic_`, inline `` `code` `` + fenced ```` ``` ```` blocks, `-`/`*` bullet lists, `[text](url)` links, blank‑line paragraphs — into the toolkit's own elements (never raw HTML) | `text` (bindable; falls back to the element's own text) | Markdown text (when no `text` attr) | — |

### Navigation & disclosure

| Element | Purpose | Key attributes | Bind | Children | role |
|---------|---------|----------------|------|----------|------|
| `<cupri-tabs>` | Tab strip; one panel at a time | `value` = active tab `id` | `value` | `<cupri-tab id="…" label="…">panel…</cupri-tab>` | `tablist`/`tab`/`tabpanel` |
| `<cupri-accordion>` | Collapsible sections | — | — | `<cupri-accordion-item label="…" open="{{…}}">…</cupri-accordion-item>` | item hdr `button` |
| `<cupri-tree>` | Hierarchical tree | — | — | nested `<cupri-tree-item label="…" open="{{…}}">…</cupri-tree-item>` | `tree`/`treeitem` |
| `<cupri-reorder>` | Drag‑to‑reorder list: drag a row by its grip and the others slide to open a gap; on drop, the document's `OnReorder(e => …)` fires with the item's old/new index (typically reorders the bound model list) | — | — | `<cupri-reorder-item>…</cupri-reorder-item>` (often `data-repeat="List"`) | — |
| `<cupri-board>` | Kanban: a row of `<cupri-reorder>` columns. Drag a card's grip within a column or across to another (source closes its gap, target opens one, the card follows the pointer); `OnReorder` carries the source `List`/`From` and target `ToList`/`To` | — | — | column wrappers, each holding a `<cupri-reorder>` | — |
| `<cupri-split>` | Resizable panels with draggable dividers (auto‑inserted between panels); drag a divider to grow one panel and shrink its neighbour. `vertical` stacks them; nestable. Give it a bounded size | `vertical` | — | `<cupri-split-panel size="N">…</cupri-split-panel>` (`size` = initial share) | — |
| `<cupri-virtual>` | Virtualized scroll list: windows a `data-repeat` to just the rows in view (+ buffer), with spacers keeping the full scroll extent — thousands of rows, only ~a screenful in the DOM. Rows may be ANY height: `item-height` is the estimated pitch, and each materialised row's real height is measured back and replaces it (scroll-anchored, so nothing jumps). `anchor="bottom"` makes it a chat log — opens at the bottom, follows appends while the user is there, releases on scroll-up; prepend history via `CupriDocument.VirtualListInserted` before `Refresh` | `height`, `item-height` (px, estimate), `anchor` (`bottom`) | — | one `data-repeat` child | `list` |

```html
<cupri-tabs value="{{Tab}}">
  <cupri-tab id="general" label="General">…</cupri-tab>
  <cupri-tab id="advanced" label="Advanced">…</cupri-tab>
</cupri-tabs>
```

### Data

| Element | Purpose | Key attributes | Children | role |
|---------|---------|----------------|----------|------|
| `<cupri-table>` | Flex table | — | `<cupri-row>` → `<cupri-cell>` | `table` |
| `<cupri-row>` | Table row | `header` (flag) | `<cupri-cell>` | `row` |
| `<cupri-cell>` | Table cell | — | text/HTML | `cell` / `columnheader` |

```html
<cupri-table>
  <cupri-row header><cupri-cell>Name</cupri-cell><cupri-cell>Score</cupri-cell></cupri-row>
  <cupri-row><cupri-cell>Ada</cupri-cell><cupri-cell>99</cupri-cell></cupri-row>
</cupri-table>
```

### Charts

Simple data‑viz drawn with the same rounded‑box + stroke paint (no canvas/SVG). Every chart takes its
data **either** as a `values="1,2,3"` string — bindable to a model, e.g. `values="{{Series}}"` — with an
optional `labels="…"`, **or** as child elements when you need per‑item control. Bars scale to `max` (or
the largest value); line/sparkline auto‑scale to the data's range. All are theme‑aware. Bars, stacked
segments, line points and heatmap cells show a **value tooltip on hover** (label + value) — nothing to wire up.

| Element | Purpose | Key attributes | Children | role |
|---------|---------|----------------|----------|------|
| `<cupri-bar-chart>` | Vertical bar chart. Multiple series via `<cupri-series>` children draw **grouped** (side‑by‑side) or `stacked`; `axis` adds a y‑axis + gridlines on a tidy `0..max` scale | `values`, `labels`, `max`, `axis`, `stacked` | `<cupri-bar value label color>` (one series) or `<cupri-series values color label>` (multi) | `img` |
| `<cupri-line-chart>` | Trend line(s): optional area fill, dots, `curve` (smoothing), and `axis` (y‑axis + gridlines, 0‑based). Multiple series via `<cupri-line>` children share one axis + get a legend | `values`, `labels`, `area`, `dots`, `curve`, `axis`, `color` | `<cupri-line values color label>` (multi‑series) or `<cupri-point value label>` (one series) | `img` |
| `<cupri-sparkline>` | Compact axis‑less trend (inline/stat cards) | `values`, `area`, `dots`, `curve`, `color` | `<cupri-point value>` | `img` |
| `<cupri-rolling-chart>` | Time‑series monitor (Task‑Manager style): a full‑width area line over a **fixed** `0..max` range, so newer samples scroll in from the right without the baseline jumping | `values`, `max`, `curve`, `color` | `<cupri-point value>` | `img` |
| `<cupri-heatmap>` | Grid tinted by intensity (contribution‑graph style) | `values`, `columns` (7), `max` | `<cupri-heat value>` | `img` |

```html
<cupri-bar-chart axis values="{{Sales}}" labels="Mon,Tue,Wed,Thu,Fri"></cupri-bar-chart>
<cupri-line-chart axis values="4,8,5,10,7" area dots curve></cupri-line-chart>

<!-- grouped (or add `stacked`) multi-series bars -->
<cupri-bar-chart axis labels="Q1,Q2,Q3,Q4">
  <cupri-series label="2023" values="10,15,12,18" color="#4682B4"></cupri-series>
  <cupri-series label="2024" values="14,19,16,25" color="#B87333"></cupri-series>
</cupri-bar-chart>

<!-- multiple lines on one shared axis (with a legend) -->
<cupri-line-chart curve labels="W1,W2,W3,W4">
  <cupri-line label="Prod"    values="4,8,5,10"></cupri-line>
  <cupri-line label="Staging" values="6,5,7,6"></cupri-line>
</cupri-line-chart>
<span>Revenue $8.9k</span> <cupri-sparkline values="3,5,4,6,8,7,9,11" area></cupri-sparkline>
<cupri-heatmap columns="7" values="0,1,2,4,1,0,3, 2,3,1,0,4,2,1"></cupri-heatmap>

<!-- per-item control via children -->
<cupri-bar-chart>
  <cupri-bar value="12" label="A" color="#B87333"></cupri-bar>
  <cupri-bar value="19" label="B"></cupri-bar>
</cupri-bar-chart>
```

Sizing: charts fill their box (cap with `max-width`/`height`); the line/sparkline stroke width is
`data-cupri-width` (default 2). Line width and dots ride on the engine's polyline paint command.

**Rolling monitors.** `<cupri-rolling-chart>` renders whatever window it's given — the "rolling" comes
from the *model*: keep a fixed-size ring buffer of recent samples, expose it as the bound `values`
string, and let a refresh cadence re-bind it. Its fixed `0..max` range (unlike the auto-scaling line
chart) keeps the baseline still as samples scroll. The showcase's Diagnostics page uses one for live
RAM, appending a sample per second (via `CupriApp.RefreshIntervalSeconds`).

### Feedback

| Element | Purpose | Key attributes | Children | role |
|---------|---------|----------------|----------|------|
| `<cupri-alert>` | Coloured banner + icon | `type` (`info`\|`success`\|`warning`\|`error`) | message | `alert` |
| `<cupri-spinner>` | Rotating loader | — | — | `progressbar` |
| `<cupri-skeleton>` | Pulsing placeholder | — | — | — |

### Overlays

All overlays take a two‑way `open` flag. Clicking a backdrop or the trigger toggles it; focus is
trapped while open. The three scrim overlays — `<cupri-dialog>`, `<cupri-drawer>`, `<cupri-shelf>` —
also take a two‑way **`blur`** flag: when set, the page behind them is frosted (a `backdrop-filter`
blur, see Styling). Bind it to a switch inside the panel to let the user toggle it live.

| Element | Purpose | Key attributes | Bind | Children | role |
|---------|---------|----------------|------|----------|------|
| `<cupri-dialog>` | Modal dialog + backdrop | `open`, `blur` | `open`, `blur` | content | `dialog` (modal) |
| `<cupri-drawer>` | Slide‑in edge panel | `open`, `side` (`left`\|`right`), `blur` | `open`, `blur` | content | `dialog` (modal) |
| `<cupri-shelf>` | Bottom sheet — full‑width panel that rises from the bottom edge, rounded top + grab handle | `open`, `blur` | `open`, `blur` | content | `dialog` (modal) |
| `<cupri-popover>` | Anchored panel below a trigger | `label` (“More”), `open` | `open` | panel content | panel `dialog` |
| `<cupri-menu>` | Dropdown menu (rows fly out into submenus — see below) | `label` (“Menu”), `open` | `open` | `<cupri-menu-item icon="…">Label</cupri-menu-item>` | `menu`/`menuitem` |
| `<cupri-context-menu>` | Right-click menu on a region (opens at the pointer; same items + submenus) | — | — | region content + `<cupri-menu-item>`s | `menu`/`menuitem` |
| `<cupri-command-palette>` | Modal fuzzy-search over commands; auto-focus, type-to-filter, ↑/↓ + Enter | `open`, `value` (query) | `open`, `value` | `<cupri-command data-set-path="…" data-set-value="…">`s | `dialog`/`option` |
| `<cupri-tooltip>` | Anchored bubble — shows on **hover** by default; `open="true"` pins it | `text`, `open` | `open` | trigger element(s) | bubble `tooltip` |
| `<cupri-toast>` | A single transient corner message (bind its visibility) | — | — | message | `status` |
| *`doc.Toast("…")`* | Engine-owned toast **stack**: raise from code; each slides in bottom-right, stacks, auto-dismisses | — (call `doc.Toast(msg, kind)`) | — | — | `status` |

```html
<cupri-button class="open-dlg">Open</cupri-button>
<cupri-dialog open="{{DialogOpen}}">
  <h3>Confirm</h3><p>Are you sure?</p>
</cupri-dialog>
```
```csharp
doc.OnClick(".open-dlg", _ => { model.DialogOpen = true; }); // handler mutates the bound flag
```

**Fly-out submenus.** A `<cupri-menu-item>` that contains its own `<cupri-menu-item>`s becomes a
submenu: it shows a chevron and, on hover, reveals its children in a panel to the right. Give the
parent row a `label` for its own text (its children are the panel, not the label). Nesting works to
any depth. The panel opens on hover alone — no `open` flag or handler — and is flush to the row, so
there's no gap to fall through and dismiss it.

```html
<cupri-menu label="File">
  <cupri-menu-item icon="download">Download</cupri-menu-item>
  <cupri-menu-item icon="layers" label="Move to">   <!-- has children ⇒ flies out -->
    <cupri-menu-item icon="home">Home</cupri-menu-item>
    <cupri-menu-item icon="clock" label="Recent">   <!-- nests further -->
      <cupri-menu-item>This week</cupri-menu-item>
    </cupri-menu-item>
  </cupri-menu-item>
</cupri-menu>
```

**Right-click menus.** `<cupri-context-menu>` attaches a menu to a region: its non-item children **are**
the region, and its `<cupri-menu-item>`s (fly-out submenus included) open at the pointer on right-click.
Wire an item the usual way — a class + `OnClick`, or `data-set-path`; picking a leaf row runs its action
and closes the menu. An outside click, Escape, or a scroll dismisses it.

```html
<cupri-context-menu>
  <div class="card">Right-click me</div>            <!-- the region -->
  <cupri-menu-item class="rename" icon="edit">Rename</cupri-menu-item>
  <cupri-menu-item class="del" icon="trash">Delete</cupri-menu-item>
</cupri-context-menu>
```
```csharp
doc.OnClick(".rename", _ => { /* … */ });           // items are ordinary clickable rows
```

**Command palette.** `<cupri-command-palette open value>` is a modal fuzzy-search over commands. Bind
`open` (a button toggles it) and `value` (the query); when it opens the search auto-focuses, typing
filters the `<cupri-command>`s (substring on their label), ↑/↓ move a highlight and Enter runs it,
clicking runs it — each command's `data-set-path`/`data-set-value` navigates or sets a model field the
app reacts to, and running one closes the palette. Escape or the backdrop dismisses it.

```html
<cupri-command-palette open="{{PaletteOpen}}" value="{{PaletteQuery}}">
  <cupri-command icon="bar-chart" data-set-path="Section" data-set-value="charts">Go to Charts</cupri-command>
  <cupri-command icon="eye" data-set-path="DarkMode" data-set-value="true">Enable dark mode</cupri-command>
</cupri-command-palette>
```

**Toast stack.** For fire-and-forget notifications, call `doc.Toast(message, kind)` from code — no markup
or model flag. Each toast slides into the bottom-right corner, stacks under any already showing, sits a
few seconds, then slides out and is removed. `kind` may be `"success"` or `"error"` to tint it.

```csharp
doc.OnClick(".save", _ => { Save(); doc.Toast("Changes saved", "success"); });
```

---

## 7. Presentation & scaling

`CupriApp.Present(windowW, windowH)` returns a `PresentInfo(LogicalWidth, LogicalHeight, Scale)` —
the logical viewport the document lays out at and a scale factor the host applies. Common strategies:

- **Responsive** (default): lay out at the window, scale 1 — reflows like a web page.
- **Zoom**: fixed logical size, hard scale — like changing display DPI.
- **Hybrid**: zoom the smaller axis, reflow the longer one (a good default for mixed content).

The host repaints on demand — after input, on the `RefreshIntervalSeconds` cadence, or while
something animates — so an idle page costs ~nothing.

---

## 8. Writing your own component

A component maps one tag to a themed primitive subtree. Implement `ICupriComponent` (or extend
`ComponentBase` for helpers) and register it.

```csharp
using AngleSharp.Dom;
using CupriFace.Components;

public sealed class RatingComponent : ComponentBase
{
    public override string Tag => "cupri-rating";

    // Low-priority default CSS merged into the stylesheet.
    public override string DefaultCss => """
        .cupri-rating { display:inline-flex; gap:2px; color:#B87333; }
        .cupri-rating-empty { opacity:.25; }
        """;

    public override void Expand(IElement el)
    {
        var value = (int)Num(el, "value", 0);     // helper: parse attribute → number
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-rating");
        // Filled stars for the score, dimmed stars for the rest (icon names come from the built-in set).
        el.InnerHtml = string.Concat(Enumerable.Range(0, 5)
            .Select(i => IconMarkup("star", 18, i < value ? "" : "cupri-rating-empty")));
    }
}

var registry = ComponentRegistry.Default().Register(new RatingComponent());
```

**Shipping components to other people** — bundling several into a library someone can add to their
project, and guaranteeing they can override the styles and the code of what they receive — is
designed in [COMPONENT-PACKAGING.md](COMPONENT-PACKAGING.md) and not built yet. Two things there
are worth knowing even while writing a component for yourself: component CSS is parsed BEFORE the
app's, so an app stylesheet already wins ties (that is what makes overriding possible), and a
component cannot yet carry its own event handlers — behaviour is wired by the app, or by the engine
off a `data-*` marker the component emits.

`ComponentBase` helpers: `Str(el,name,fallback)`, `Num(el,name,fallback)`, `Flag(el,name)`,
`Percent(value,min,max)`, `IconMarkup(name,size,cssClass?)`, `NextId()` (unique anchor id).
Expansion runs **after** binding (so components see concrete values) and repeats up to 8 passes, so a
component may emit other `cupri-*` elements.

### Interactive `data-*` hooks

If your component should be interactive, emit the hooks the engine already understands (the same ones
the built‑ins use), rather than reinventing input handling:

| Hook | Engine behaviour |
|------|------------------|
| `data-bind-<attr>` | Two‑way binding target (write model on interaction). Generated automatically from a pure `{{…}}` attribute. |
| `data-set-path` + `data-set-value` | Click writes `value` to the model path (tabs, options, tree/tab selection). |
| `data-cupri-toggle="<id>"` | Click toggles the nearest bound `open` (menus, popovers, accordions). |
| `data-cupri-dismiss` | Click closes the nearest overlay (backdrops). |
| `data-cupri-step="±1"` | Stepper button; adjusts the nearest numeric field (`data-min`/`data-max`/`data-step`). |
| `data-cupri-anchor="<id>"` + `data-cupri-placement="top\|bottom"` | Position a popup relative to an anchor. |
| `data-focus-scope` | Trap focus within this subtree while it's the top overlay. |
| `data-cupri-icon="<path>"` | Render vector icon path (produced by `IconMarkup`). |

---

## 8.1 Touch, multi-touch, and what kind of device you're on

### Two ways an app can differ per device

CupriFace does **not** have a "mobile mode". Platform is the wrong axis: a Windows tablet is a
desktop OS with a coarse pointer and no hover, a laptop with a touchscreen is both at once, and a
docked phone with a mouse is a fine pointer on a mobile OS. What actually varies is **capability**,
so that is what the engine reports.

The host sets `doc.InputProfile` (`InputProfile.Desktop` or `InputProfile.Touch`; apps may override
it), and the engine puts it on the body as classes. That's all it does with it:

| Class | When |
|---|---|
| `cupri-fine` / `cupri-coarse` | a mouse or trackpad / a finger |
| `cupri-nohover` | there is no hover state to read |

So adapting is ordinary CSS, with no new syntax to learn and nothing to switch on in C#:

```css
.stepper-arrows            { display: flex; }
.cupri-coarse .stepper-arrows { display: none; }   /* too small to hit with a thumb */
.cupri-nohover .tooltip-hint  { display: none; }   /* nothing will ever hover it */
```

This works identically in an app's stylesheet and inside a component's own `DefaultCss`. There is
deliberately no `@media (pointer: coarse)`: it would mean teaching the CSS parser a new shape to
reach exactly what the cascade already does with a class.

### Multi-touch: `doc.OnPointer`

The engine's own gestures — tap, scroll, fling, long-press, and the drag surfaces on sliders,
scrollbars, split panes and reorder lists — are **single-pointer by design**, and the built-in
`cupri-*` elements stay that way so they keep their keyboard and screen-reader behaviour.

Everything else is yours. `OnPointer` hands you raw pointers, and what a second finger means is
your decision:

```csharp
doc.OnPointer("data-gesture", e =>
{
    if (e.Pointers.Count < 2) return true;          // true = I want this pointer
    var span = Distance(e.Pointers[0], e.Pointers[1]);
    if (e.Phase == PointerPhase.Down) _start = span;
    else _model.Scale = span / _start;
    return true;
});
```

```html
<div class="photo" data-gesture="pinch">…</div>
```

- **The attribute is the opt-in** — nothing becomes multi-touch by accident.
- **A pointer is captured on down and stays with that element** until it lifts. While it is captured
  the ordinary recognizer never sees it, which is what stops a pinch from also scrolling the page
  underneath. (This is why there is no `touch-action` equivalent: capture already solves it.)
- **Returning `false` on `Down` declines** the pointer, and it falls through to the normal gesture
  path as though you weren't there — useful when only a *second* finger is interesting.
- `e.Pointers` is every pointer that element currently holds, so pinch/rotate arithmetic is a
  two-line job. The engine computes no gesture for you: guessing what fingers mean would be worse
  than not guessing.
- `Cancel` arrives when the app is backgrounded or the surface goes away — unwind there.

### Recognised gestures: `doc.OnManipulate`

Most apps that want two fingers want the same three numbers, so the engine works them out for you:

```csharp
// COMPOSE onto a banked base — g.* is cumulative since THIS gesture began (as on every
// platform), so assigning it directly snaps your content back to 1x the moment a second
// grab starts. Bank when a gesture ends; multiply while one is live.
var live = false; double baseScale = 1, baseRot = 0, basePanX = 0, basePanY = 0;
doc.OnManipulate("data-gesture", g => {
    if (!live) { live = true; baseScale = model.Scale; baseRot = model.Rotation;
                 basePanX = model.PanX; basePanY = model.PanY; }
    model.Scale    = Math.Clamp(baseScale * g.Scale, 0.4, 3);
    model.Rotation = baseRot + g.Rotation;           // degrees
    model.PanX     = basePanX + g.PanX;              // the focal point's travel
    model.PanY     = basePanY + g.PanY;
    if (g.Phase is PointerPhase.Up or PointerPhase.Cancel && g.PointerCount <= 1)
        live = false;                                // gesture over: this result is the next base
    return true;
});
```

It is a layer **over** `OnPointer` — same attribute opt-in, same capture, no new rules — so raw
pointers remain available for anything it doesn't describe. What it saves you is not the
trigonometry but the mistakes in it:

- **The focal point.** A pinch scales about the midpoint *between the fingers*
  (`g.FocusX`/`g.FocusY`), not the element's centre. Scale about the centre and the content slides
  out from under the hands holding it. The engine's own first sample made this mistake.
- **Re-baselining.** Adding or lifting a finger changes what "span" means, so the cumulative values
  must be banked and re-measured, or the content jumps mid-gesture.
- **The ±180° seam**, so a small turn past it reads as a few degrees rather than most of a circle.
- **Three fingers**, where "the distance between the two" has no meaning — spread is measured from
  the centroid.

Two more lessons, both learned from a phone rather than from code review:

- **Size the gesture surface for fingers, not for the artwork.** Fingertips are ~10 mm of glass;
  capture is per-element; and a 90dp tile is a target two fingers cannot both land on. Put
  `data-gesture` on the *stage* around the content and apply the transform to the content inside
  it — the collage-editor pattern. Emulators and headless tests will not catch this for you,
  because their fingers are points.
- **Keep the content inside its surface.** A transform can carry the visuals outside the element
  that owns the gesture, and a finger on that overhang touches whatever is underneath instead.
  Clamp scale and pan so everything a user might grab stays over the surface that would capture
  the grab.

Use `OnPointer` directly when the gesture isn't a manipulation: a two-finger swipe, a custom
multi-touch keyboard, anything where you want the pointers themselves.

### Accessibility: where the line is

CupriFace is an engine, not a nanny. **Anything you build with `OnPointer` is yours to make
accessible, or not.** A pinch has no keyboard equivalent and no screen-reader affordance unless you
write one, and the engine will not stop you shipping a gesture-only control.

What the project *does* guarantee is that the **built-in `cupri-*` elements stay accessible** —
roles, states, keyboard operation and the semantics tree behind the four platform bridges. That is
a large part of what they're for. If you replace one with a custom gesture-driven control, you have
taken that on.

If you want both, the usual approach is to keep the built-in control as the reachable path and
treat the gesture as an accelerator on top of it, rather than as the only way in.

## 8.2 Scrolling

`overflow: scroll` scrolls on **both axes**, independently. A box whose content is wider than it is
can be dragged sideways with a finger, flung, and it keeps its own momentum:

```css
.card-row      { overflow: scroll; display: flex; gap: 8px; }
.card-row > *  { min-width: 160px; }     /* stop the row collapsing to fit */
```

Things worth knowing, because each one was a bug before it was a rule:

- **The axes chain independently.** A card row inside a scrolling page takes the sideways part of a
  diagonal drag while the page takes the up-down part — neither steals the other's axis.
- **A gesture locks to the axis it committed to**, so a sideways drag doesn't creep the page
  vertically. A genuinely diagonal start moves both, which is what a map or a zoomed image wants.
- **Pulling past an edge stretches** (the rubber band) with resistance, then springs back on
  release. That is what distinguishes "you have reached the end" from "the app stopped responding".
  It never engages mid-content.
- **What is scrolled into view is where it can be tapped.** Paint, hit-testing and the semantics
  tree read one effective offset, so a control dragged into view — or pushed down by the rubber
  band — is touchable and readable where it now appears.

A wide block on a phone is usually better scrolled than shrunk: a six-column table squeezed into
393dp is visible without being *usable*. The Showcase does this with a wrapper (`overflow: scroll`)
around content that keeps a minimum width.

## 8.3 Forms that a password manager understands

Two attributes and one call:

```html
<cupri-textfield value="{{Email}}" inputmode="email" enterkeyhint="next"
                 autocomplete="username" aria-label="Email"></cupri-textfield>
<cupri-password  value="{{Password}}" enterkeyhint="done"
                 autocomplete="current-password" aria-label="Password"></cupri-password>
<cupri-button class="signin">Sign in</cupri-button>
```

```csharp
doc.OnClick(".signin", _ => { SignIn(); doc.SubmitForm(); });
```

- **`autocomplete`** is what a fill service reads — `username`, `current-password`, `email`,
  `tel`, `name`, `postal-code`… A field **without** one is deliberately not offered for filling.
  The engine will not guess that something is a password field.
- **`inputmode`** picks the keyboard (an `@` key for email, a dial pad for `tel`), and
  **`enterkeyhint`** names the action key. Both are the web platform's own attributes, so the
  Android host and the web host read the same markup.
- **`SubmitForm()` is what makes a manager offer to SAVE.** Filling is passive — a service reads
  the structure whenever it likes — but saving only happens when the app declares the entry
  finished. Without that call, correct `autocomplete` attributes will still never produce a save
  prompt. (On Android the host answers it with `AutofillManager.Commit()`; elsewhere nothing
  listens and nothing breaks.)

## 9. Where to look in the repo

- Core engine & document API: [src/CupriFace/CupriDocument.cs](src/CupriFace/CupriDocument.cs)
- Portable app base: [src/CupriFace/CupriApp.cs](src/CupriFace/CupriApp.cs)
- Component library: [src/CupriFace/Components/](src/CupriFace/Components/) and its
  [ComponentRegistry](src/CupriFace/Components/ComponentRegistry.cs)
- Binding: [src/CupriFace/Binding/](src/CupriFace/Binding/) (`[CupriBindable]`, `IBindableAccessor`)
- Desktop host: [src/CupriFace.Shell/DesktopHost.cs](src/CupriFace.Shell/DesktopHost.cs)
- Android host: [src/CupriFace.Android/](src/CupriFace.Android/) (`CupriActivity`,
  `CupriHostView`, `AndroidHost`, the TalkBack bridge)
- Touch, multi-touch & IME (portable, engine-side):
  [src/CupriFace/Interaction/](src/CupriFace/Interaction/) — `TouchInput` (the single-pointer
  gesture recognizer), `MultiPointerEvent` (the `OnPointer` seam), `InputProfile`, `TextInputState`
- Accessibility: the portable semantics tree is
  [src/CupriFace/Accessibility/](src/CupriFace/Accessibility/); the per-OS bridges that serve it
  to screen readers live in [src/CupriFace.Shell/Accessibility/](src/CupriFace.Shell/Accessibility/)
  (UIA on Windows, AT-SPI on Linux, NSAccessibility on macOS) and
  [src/CupriFace.Android/TalkBackBridge.cs](src/CupriFace.Android/TalkBackBridge.cs) (TalkBack).
  You do not call these — set `role`/`aria-*` and they follow.
- Web host (raw WASM): [samples/WebWasm/](samples/WebWasm/)
- Runnable samples: [samples/](samples/) — `Viewer` (desktop showcase; `--app mobile` runs the
  phone sample), `AndroidViewer` (the same `MobileApp` on a phone), `Interactive`,
  `ControlsGallery`, `Keyboard`, `Scaling`, and more.
- Architecture rationale: [DESIGN.md](DESIGN.md)
