# CupriFace

A native, cross-platform desktop UI runtime that renders **HTML + CSS** to a GPU
canvas and binds elements to backend C# objects — an Electron alternative that does
**not** embed a web browser or a JavaScript engine.

See **[DESIGN.md](DESIGN.md)** for the full architecture and goals, **[TOOLBOX.md](TOOLBOX.md)** for
the developer guide to the `cupri-*` elements (bind them to a C# model, style them, add your own),
**[ROADMAP.md](ROADMAP.md)** for what's considered but not yet built, and
**[comparisons/](comparisons/README.md)** for how CupriFace relates to other UI stacks
(Avalonia, Electron, MewUI).

## Screenshots

Every image below is the **same `ShowcaseApp` class** — one HTML file, one CSS file, one plain C#
model ([samples/DemoApp](samples/DemoApp)) — drawn by the engine itself. No browser was involved in
rendering *or* capturing them: they are `doc.Render()` output, produced headlessly at 2× by a
throwaway harness, which is the same reason the UI is straightforward to unit-test.

![Inputs](docs/screenshots/inputs.png)

| | |
|---|---|
| ![Components](docs/screenshots/components.png)<br>**Components** — tabs, tree, accordion, table with sort/select/drag-resize columns | ![Charts](docs/screenshots/charts.png)<br>**Charts** — bar, grouped/stacked, line, area, heatmap, drawn with the same box+stroke paint |
| <img src="docs/screenshots/images.jpg" alt="Images"><br>**Images** — `object-fit` modes in a corner-drag `resize: both` frame | ![Overlays](docs/screenshots/overlays.png)<br>**Overlays** — modal dialog over a real backdrop blur; drawers, popovers, toasts, context menus |
| ![Layout](docs/screenshots/layout.png)<br>**Layout** — flexbox, CSS grid with spans, inline flow, draggable split panes | ![Motion](docs/screenshots/motion.png)<br>**Motion** — `@keyframes`, transforms and CSS transitions |
| ![Styling](docs/screenshots/styling.png)<br>**Styling** — the cascade, variables, accent theming, shadows and borders | ![Settings](docs/screenshots/settings.png)<br>**Settings** — forms, validation, and the scaling modes |
| ![Diagnostics](docs/screenshots/diagnostics.png)<br>**Diagnostics** — live frame timings and node counts | ![Dark mode](docs/screenshots/inputs-dark.png)<br>**Dark mode** — a CSS variable swap on `body.dark`, cross-faded by a `transition` |

Run it yourself with `dotnet run --project samples/Viewer` (desktop) — the same app also runs in a
browser on a `<canvas>`, no Blazor, via `samples/WebWasm`.

## What works today (.NET 10)

A fully-managed pipeline **parse → style → layout → paint → bind → components**:

- **HTML + CSS** via AngleSharp DOM and a real CSS selector cascade.
- **Managed flexbox** (grow/shrink, justify/align, gap, multi-line wrap, max-content
  sizing) + block flow, box model, and absolute/relative positioning — no native Yoga.
- **HarfBuzz text shaping** (kerning, ligatures, Greek/Cyrillic/Arabic), word wrap.
- **Skia** rendering via an immutable display-list snapshot.
- **Data binding** — `{{path}}` interpolation, attribute binding, `data-repeat`
  collections, model → view refresh.
- **Component model** — custom elements expand into themed, accessible primitives;
  ships `<cupri-slider>`, `<cupri-switch>`, `<cupri-progress>`, `<cupri-button>`,
  `<cupri-badge>` with `role`/`aria-*` baked in.

## Projects

| Project | Role |
|---|---|
| `src/CupriFace` | The engine (DOM, CSS, layout, text, paint, binding, components) |
| `src/CupriFace.Shell` | Silk.NET window + OpenGL + Skia surface + profiler HUD |
| `src/CupriFace.Binding.Gen` | Roslyn source generator for AOT-clean binding accessors |
| `samples/HelloBox` | M0 shell smoke (window / CPU-raster) |
| `samples/HtmlView` | A real HTML/CSS document (flex, text, i18n) |
| `samples/GridDemo` · `GridAdvanced` | CSS Grid: tracks/spans; `minmax()` + row spans |
| `samples/DataBinding` | Model → view binding, before/after a model change |
| `samples/ControlsGallery` | The `<cupri-*>` control library + a11y semantics dump |
| `samples/Interactive` | Simulated clicks → hit-test → control behaviour (headless) |
| `samples/Motion` | `transform` + `@keyframes` animation (time-sampled) |
| `samples/Responsive` | `@media` queries + `calc()` across widths |
| `samples/BidiText` | Mixed LTR/RTL (Arabic/Hebrew) shaping + reorder |
| `samples/ThreadedRender` | Render-thread split (commit on UI thread, raster on another) |
| `samples/DemoApp` | **Portable apps** (`ShowcaseApp` — the screenshots above — plus `SettingsApp`/`ControlsApp`), one definition each, no platform code |
| `samples/Viewer` | Desktop host running `ShowcaseApp` (GPU → SDL fallback); `--web` serves it to a browser instead |
| `samples/WebWasm` | Web host (**default**): raw .NET-WASM + thin JS glue → `<canvas>`, no Blazor |
| `samples/Web` | Web host (alt): the same app via Blazor `<SKCanvasView>` |

## Run

```pwsh
dotnet build CupriFace.slnx -c Debug

# Each sample writes a PNG snapshot (works headless — no GPU needed):
dotnet run --project samples/HtmlView          # -> m1-html.png
dotnet run --project samples/DataBinding        # -> m4-bind-a.png / m4-bind-b.png
dotnet run --project samples/ControlsGallery    # -> m5-controls.png

# Live, clickable window — tries GPU, falls back to a CPU (no-GPU/RDP) window:
dotnet run --project samples/Viewer

# ...or serve that same app to a browser (Kestrel + WebSocket), no WASM build needed:
dotnet run --project samples/Viewer -- --web              # opens your browser at :5180
dotnet run --project samples/Viewer -- --web --port 5000 --no-browser

# Web (WASM) — engine rendered to <canvas> in the browser. Two interchangeable hosts:
dotnet run --project samples/WebWasm -c Release   # raw .NET-WASM (no Blazor, default)
dotnet run --project samples/Web                  # Blazor host (alternative)
# ...then open the printed localhost URL.
```

Both web hosts render the **same** `SettingsApp` — pick raw-WASM for minimal deps, or
Blazor to embed CupriFace inside an existing Blazor app. First build does a native
WebAssembly relink of Skia (slow once, cached after).

Every snapshot sample writes a PNG and exits (works headless, no GPU). The Viewer and
Web targets open a real window / browser.

## Write once, run desktop **and** web

Define an app once as a `CupriApp` (markup + CSS + model + handlers — **no platform code**),
then host it anywhere:

```csharp
public sealed class SettingsApp : CupriApp {           // in a portable class library
    public override string Html => "...";
    public override string Css  => "...";
    public override object Model => _settings;
    public override void Configure(CupriDocument d) => d.OnClick(".save", _ => Save());
}
```

```csharp
DesktopHost.Run(new SettingsApp());                    // desktop  (GL → SDL window)
```
```razor
<CupriView App="new SettingsApp()" />                  @* web  (WASM → <canvas>) *@
```

"Exporting a desktop app as a website" = recompiling the same `SettingsApp` against the
web host. The engine, layout, styling, binding, components, and click handling are shared
unchanged; only the host (window vs. canvas) differs.

### Three ways to reach a browser

| | Where the engine runs | Download | Needs a WASM build? |
|---|---|---|---|
| `samples/WebWasm` | **In the browser** (.NET WASM → `<canvas>`) | the whole engine | yes |
| `samples/WebLlvm` | In the browser, NativeAOT-LLVM (experimental, fastest) | 14.2 MB (5.5 MB gzipped) | yes |
| `Viewer --web` | **On the server** — frames streamed to a `<canvas>` | just pixels | no |

`--web` is the thin-client option: Kestrel serves a page, the engine renders each frame in the
*desktop* process and pushes it over a WebSocket, and the browser sends pointer/key events back.
Because it reuses the engine's render-on-demand loop, an idle page transmits nothing and the
server sits at 0% CPU. It needs no WebAssembly toolchain, which also makes it the quickest way to
look at a running app from another machine.

## License note

All third-party dependencies are permissive (MIT / Apache-2.0): SkiaSharp,
HarfBuzzSharp, Silk.NET, AngleSharp. The flexbox engine is our own managed code (no
native Yoga), keeping the stack fully managed and AOT-friendly.
