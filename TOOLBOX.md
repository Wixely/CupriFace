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
| `.Refresh()` | Re‑bind + rebuild (call after you mutate the model from code). |
| `.Render(canvas, w, h)` | Paint into an `SKCanvas`. |
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
- **Keyboard & focus.** Tab / Shift+Tab move focus across interactive controls; Space/Enter activate;
  arrows drive sliders and groups. Focus is trapped inside an open overlay (dialog/menu/drawer).
- **Text editing.** `cupri-textfield` / `cupri-textarea` / `cupri-number` support caret placement,
  selection (drag, double‑click word, triple‑click line, Shift+arrows, Ctrl+A), clipboard
  (Ctrl+C/X/V), and **undo/redo** (Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z — history is per‑field) on both
  desktop and web. Editing is permissive: the field shows a red border while a value is invalid and
  validates/clamps on blur.

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
  The default accent is copper `#B87333` (hence *Cupri*). Controls that don't read a variable can
  still be restyled through their class hooks.
- `@media (width ...)` is supported and re‑resolves on viewport change, so layouts can be responsive.
- **Motion.** `@keyframes` (looping animations) and **`transition`** are both supported. A `transition`
  eases a property from its old value to its new one whenever that value changes — on `[data-hover]`,
  `:focus`, a state/class change, a model update, or the theme toggle. Animatable: `opacity`,
  `background`/`color`/`border-color`, `transform` (translate/scale/rotate), and `filter` (op‑by‑op).
  Timing: `linear`/`ease`/`ease-in`/`ease-out`/`ease-in-out` or `cubic-bezier(x1,y1,x2,y2)` (overshoot
  allowed). It's paint‑only (no reflow), so it's cheap.
  ```css
  .nav  { transition: background-color 0.2s ease, color 0.2s ease; }   /* smooth hover highlight */
  .card { transition: transform 0.25s ease-out; }
  .card:hover { transform: translateY(-6px); }                          /* lift on hover */
  .surface { transition: background-color 0.35s ease; }                 /* light/dark cross-fade */
  ```
- **`filter`.** `blur() brightness() contrast() grayscale() saturate() sepia() invert() opacity()
  drop-shadow()` are supported and compose left-to-right (applied to the element and its subtree).
  ```css
  .thumb  { filter: grayscale(1) brightness(0.9); }
  .glass  { filter: blur(6px); }
  .raised { filter: drop-shadow(2px 4px 6px #0006); }
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
| `<cupri-number>` | Numeric field + `−/+` steppers | `value`, `min`, `max`, `step` | `value` | — | `spinbutton` |
| `<cupri-textarea>` | Multi‑line text input | `value`, `placeholder`, `follow-tail` | `value` | — | `textbox` (`aria-multiline`) |
| `<cupri-select>` | Dropdown picker | `value`, `open` | `value` (and `open`) | `<cupri-option value="…">Label</cupri-option>` | `combobox` |
| `<cupri-combobox>` | Typeahead: editable field + suggestions that filter as you type (free‑text; the dropdown shows while focused) | `value`, `placeholder` | `value` | `<cupri-option value="…">Label</cupri-option>` | `combobox` |

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
| `<cupri-icon>` | Vector icon (current text colour) | `name`, `size` (24), `aria-label`? | — | `img` or decorative |
| `<cupri-badge>` | Small pill label | — | text/HTML | — |
| `<cupri-chip>` | Pill with optional close icon | `closable` | text/HTML | — |
| `<cupri-avatar>` | Circular initials | `initials` | — | — |
| `<cupri-card>` | Padded rounded surface | — | arbitrary | — |
| `<cupri-divider>` | Horizontal rule | — | — | `separator` |
| `<cupri-stat>` | Metric value + caption | `value`, `label` | — | — |

### Navigation & disclosure

| Element | Purpose | Key attributes | Bind | Children | role |
|---------|---------|----------------|------|----------|------|
| `<cupri-tabs>` | Tab strip; one panel at a time | `value` = active tab `id` | `value` | `<cupri-tab id="…" label="…">panel…</cupri-tab>` | `tablist`/`tab`/`tabpanel` |
| `<cupri-accordion>` | Collapsible sections | — | — | `<cupri-accordion-item label="…" open="{{…}}">…</cupri-accordion-item>` | item hdr `button` |
| `<cupri-tree>` | Hierarchical tree | — | — | nested `<cupri-tree-item label="…" open="{{…}}">…</cupri-tree-item>` | `tree`/`treeitem` |

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

### Feedback

| Element | Purpose | Key attributes | Children | role |
|---------|---------|----------------|----------|------|
| `<cupri-alert>` | Coloured banner + icon | `type` (`info`\|`success`\|`warning`\|`error`) | message | `alert` |
| `<cupri-spinner>` | Rotating loader | — | — | `progressbar` |
| `<cupri-skeleton>` | Pulsing placeholder | — | — | — |

### Overlays

All overlays take a two‑way `open` flag. Clicking a backdrop or the trigger toggles it; focus is
trapped while open.

| Element | Purpose | Key attributes | Bind | Children | role |
|---------|---------|----------------|------|----------|------|
| `<cupri-dialog>` | Modal dialog + backdrop | `open` | `open` | content | `dialog` (modal) |
| `<cupri-drawer>` | Slide‑in edge panel | `open`, `side` (`left`\|`right`) | `open` | content | `dialog` (modal) |
| `<cupri-popover>` | Anchored panel below a trigger | `label` (“More”), `open` | `open` | panel content | panel `dialog` |
| `<cupri-menu>` | Dropdown menu | `label` (“Menu”), `open` | `open` | `<cupri-menu-item icon="…">Label</cupri-menu-item>` | `menu`/`menuitem` |
| `<cupri-tooltip>` | Anchored bubble — shows on **hover** by default; `open="true"` pins it | `text`, `open` | `open` | trigger element(s) | bubble `tooltip` |
| `<cupri-toast>` | Transient corner message | — | — | message | `status` |

```html
<cupri-button class="open-dlg">Open</cupri-button>
<cupri-dialog open="{{DialogOpen}}">
  <h3>Confirm</h3><p>Are you sure?</p>
</cupri-dialog>
```
```csharp
doc.OnClick(".open-dlg", _ => { model.DialogOpen = true; }); // handler mutates the bound flag
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

## 9. Where to look in the repo

- Core engine & document API: [src/CupriFace/CupriDocument.cs](src/CupriFace/CupriDocument.cs)
- Portable app base: [src/CupriFace/CupriApp.cs](src/CupriFace/CupriApp.cs)
- Component library: [src/CupriFace/Components/](src/CupriFace/Components/) and its
  [ComponentRegistry](src/CupriFace/Components/ComponentRegistry.cs)
- Binding: [src/CupriFace/Binding/](src/CupriFace/Binding/) (`[CupriBindable]`, `IBindableAccessor`)
- Desktop host: [src/CupriFace.Shell/DesktopHost.cs](src/CupriFace.Shell/DesktopHost.cs)
- Web host (raw WASM): [samples/WebWasm/](samples/WebWasm/)
- Runnable samples: [samples/](samples/) — `Viewer` (desktop showcase), `Interactive`,
  `ControlsGallery`, `Keyboard`, `Scaling`, and more.
- Architecture rationale: [DESIGN.md](DESIGN.md)
