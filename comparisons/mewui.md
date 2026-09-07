# CupriFace vs MewUI

[MewUI](https://github.com/aprillz/MewUI) is a fascinating comparison because it
is the *opposite answer to the same question*. Both projects looked at the .NET
desktop UI landscape, decided XAML frameworks were heavier than they needed to
be, and built a small managed engine from scratch. Then they diverged on the one
choice that defines everything else: **how a developer describes a UI.** MewUI
says "in C#, fluently, with no markup at all." CupriFace says "in HTML and CSS,
with no C# in the view at all."

They are also near-peers in a way the Avalonia comparison isn't: both are young,
both are MIT, both are small-team projects moving fast, both are pre-1.0 with
churning APIs. Neither gets to play the maturity card against the other.

*Version note (September 2026): MewUI statements were checked against the public
repository at **v0.21.1** — ~656 stars, ~2,783 commits, "the public API surface
is still being stabilized". CupriFace statements come from this repository at
**v0.18.0** (655 commits; the repo's first commit is 2026-08-03). Both projects
moved a long way since this document's previous revision, and **one of the moves
invalidated its headline claim** — see [What changed](#what-changed-since-the-last-revision).*

*Measurement note: nothing below is estimated. CupriFace's NativeAOT and
trimmed-single-file figures were produced while writing this revision —
`dotnet publish samples/Viewer -c Release -r win-x64 -p:Aot=true` for the AOT
number, and the same publish with `-p:PublishSingleFile=true -p:Trim=true
-p:EnableCompressionInSingleFile=true` for the 20.8 MB one — each then run with no
.NET on the `PATH`, checked for which render path it took, and put through the
repo's UIA gate. CupriFace's test count is a `dotnet test` run. MewUI's desktop sizes are its own
published measurements (`tools/aot-size/release-sizes.json`, v0.21.0, generated
2026-09-06); MewUI's browser payload was measured from its live deployment on
2026-09-07 by summing the `_framework` assets. Sizes use binary units
(1 MB = 1024 KB), which is MewUI's convention, so both columns agree.*

## What changed since the last revision

Worth stating plainly, because the previous revision of this document was wrong
about the single biggest difference between the two projects:

- **MewUI has a browser host now.** `src/MewUI.Platform.Browser` and
  `src/MewUI.Backend.MewVG.Browser` (with a WebGL shim) render the Gallery to a
  `<canvas>` in the browser, and `.github/workflows/pages.yml` deploys it as a
  live site. "MewUI has no browser story" is no longer true. What remains true is
  much narrower, and the [browser section](#the-browser-both-projects-are-there-now)
  spells out exactly what.
- **MewUI grew a serious tooling story**: DevTools (element inspector, visual
  tree, performance monitor, profiler timeline), Hot Reload with no setup, and an
  editor preview that is now a real VS Code extension, with Visual Studio and
  Rider integrations alongside it. This was CupriFace's weakest axis and the gap
  widened.
- **MewUI's binaries grew**, and are still small: Hello World 3.17–4.52 MB,
  Gallery 7.35–9.24 MB (was 2.6–4.4 / 5.6–7.4).
- **CupriFace's test suite went from 423 to 818**, its element count from 69 to
  74, and it gained GPU-surface composition (`IGpuSurfaceSource`, the
  `CupriFace.Gl` package, 3D on all three hosts), a Lottie package, a desktop
  WebM package, and named scaling strategies.
- **CupriFace's desktop size story changed twice while this document was being
  written.** The NativeAOT build measures 25.05 MB across five files; then a
  trimmed, compressed single-file publish came in at **20.8 MB in one file** —
  smaller than the AOT build, and a 78% cut from the 95.4 MB the project actually
  ships. Measuring all of it also surfaced a problem: **trimming and NativeAOT
  both silently disable the Windows UIA bridge and hardware GL.** Trimming's
  losses are recoverable with build properties; AOT's are not. See
  [The AOT caveat](#the-aot-caveat-found-while-measuring).

## At a glance

| | **CupriFace** | **MewUI** |
|---|---|---|
| Tagline | HTML + CSS rendered by a managed engine — an Electron alternative with no browser | "Cross-platform, lightweight, code-first .NET GUI framework… NativeAOT/Trim-friendly desktop apps without requiring a separate .NET runtime" |
| Authoring | **HTML + CSS files** + a plain C# model | **Fluent C# markup** — `new Window().Title("…").Content(new StackPanel().Children(…))` |
| Styling | Real CSS: cascade, classes, descendant/child selectors, `@media`, variables, `@keyframes` | `Style` objects with typed `Setter`s in a `StyleSheet`; named styles + type rules; `StateTrigger` for hover/pressed; a documented theming system |
| Binding | `{{Path}}` interpolation against any POCO; controls write back; no INPC | Explicit, **reflection-free** delegate bindings — `x => x.Customer.City` as code, checked by the compiler, with notification attached at every step |
| Layout | CSS box model: managed flexbox, grid (`minmax()`, spans), block flow | WPF-style **measure/arrange** with panels (`Grid`, `StackPanel`, `DockPanel`, `UniformGrid`, `WrapPanel`, `Canvas`, `SplitPanel`) |
| Rendering | SkiaSharp only, one path everywhere | **Pluggable**: Direct2D, GDI (Windows), MewVG (managed NanoVG port — GL on Win/Linux, Metal on macOS, WebGL in the browser); SkiaSharp as an *extension* |
| Desktop | Windows / macOS / Linux via Silk.NET | Windows 10+ / Linux X11 / macOS 12+, per-backend hosts |
| Browser / WASM | **Shipped and documented**: same app class → `<canvas>`; two hosts (Mono-interpreted, NativeAOT-LLVM); 14.2 MB / 5.5 MB gzipped; **real DOM ARIA mirror** | **Real, live, but unannounced**: browser platform + WebGL backend in `src/`, Gallery deployed to a live site; 17.08 MB / 5.37 MB gzipped. Not on NuGet, not in the README, not on the roadmap; canvas only, no a11y mirror |
| Mobile | **Android** — own host package, engine-level touch/fling/IME, TalkBack bridge, emulator-gated in CI | **None** — desktop and browser only |
| Touch | Two-axis scrolling with momentum and rubber band; multi-touch capture seam | Desktop input (mouse, keyboard); the browser host handles touch and IME on the canvas |
| Deployment | **20.8 MB** single self-contained file (trimmed + compressed, measured, no runtime install; 95.4 MB untrimmed, which is what releases ship today). NativeAOT is 25.05 MB in 5 files | **The whole point**: single self-contained exe, Hello World **3.17–4.52 MB**, Gallery **7.35–9.24 MB** |
| Native footprint | Skia (9.16 MB) + HarfBuzz (1.71 MB) + SDL (1.62 MB) + GLFW (0.22 MB) on win-x64, before any app code | Direct2D/GDI ride OS libraries; MewVG is managed — near-zero native payload |
| AOT posture | Design goal, verified by hand — **opt-in and explicitly not run in CI**. Both the UIA bridge and hardware GL silently degraded under it until the bridge moved to source-generated COM | **Non-negotiable design constraint**, validated continuously; `LibraryImport` P/Invoke; DevTools deliberately refuse to ship in a trimmed/AOT build rather than lie |
| Embedding | Core capability: `RenderToPixels` into any RGBA buffer (game texture, canvas, server); `IGpuSurfaceSource` for zero-copy GPU handover | Not a stated goal — the framework hosts the window. (Its `WriteableBitmap` and `WinFormsHost` samples point *inward*: drawing into a MewUI control, hosting WinForms inside MewUI) |
| Testing | **Headless-first**: engine needs no window; **818 tests** click/type/fling/pixel-assert | Broad and conventional — unit, generator, analyzer, SVG, graphics-backend and benchmark suites, plus a real-window automation suite (`MewUI.WindowAutomationTest`) covering DPI crossing, multi-monitor popups and drag |
| Accessibility | `role`/`aria-*` in every component; **four bridges — UIA, AT-SPI, NSAccessibility, TalkBack — each CI-gated by a real AT client**; real DOM a11y tree on the web host. *(UIA did not initialise under NativeAOT until the bridge moved to source-generated COM — see below)* | Focus and tab navigation documented; **no OS accessibility bridge** (no UIA, AT-SPI or NSAccessibility anywhere in the tree) |
| Extras | Charts, kanban, command palette, pickers, Markdown, video, Lottie built in (74 elements) | Thin core + **optional packages**: MewDock (VS-style docking), SVG, Skia, MewCharts, MewvalonEdit (code editor), WebView2 |
| Dev tooling | Plain text files, any editor; a live diagnostics HUD; no designer, no inspector | **Hot Reload** (no setup), **DevTools** (inspector, visual tree, perf monitor, profiler), **editor preview** as a VS Code extension, plus VS and Rider integrations — a decisive advantage |
| Getting started | `dotnet run --project samples/Viewer` | Also **one command, no project**: `curl … fba_gallery.cs \| dotnet run -` (file-based app, .NET 10) |
| Non-goals | JavaScript in the authoring model, ever | XAML compatibility, designer-first workflows, reflection binding, exhaustive control catalogue |
| Maturity | Young, pre-1.0, API churning | Young, pre-1.0, API churning ("breaking changes can happen between minor releases") |

## The fork in the road: markup vs code

Everything else follows from this, and this is the part that has not moved.

**MewUI** removes the markup layer entirely. The UI *is* C#:

```csharp
var window = new Window()
    .Title("Hello MewUI")
    .Resizable(520, 360)
    .Content(new StackPanel().Children(/* … */));
```

The wins are real and shouldn't be understated: one language, one toolchain,
full IntelliSense and refactoring over your entire UI, compile-time type safety
on every property, no parser at runtime, and — critically for MewUI's mission —
**nothing to reflect over**, so NativeAOT and trimming stay honest. Bindings are
part of that: `x => x.Customer.City` is a path the compiler checks and the
trimmer can see, and renaming the property renames the binding.

**CupriFace** removes the C# from the view instead. The UI is a document:

```html
<div class="row">
  <span>Volume</span>
  <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
</div>
```

```css
.row { display: flex; align-items: center; gap: 12px; }
.row:hover { background: var(--hover); }
```

```csharp
public class Settings { public int Volume { get; set; } = 60; }
```

The wins here are different in kind: the UI is **data, not code**. It can be
edited by someone who has never opened Visual Studio, reviewed as a diff that
looks like a web page, restyled without recompiling, and — because CSS is a
cascade rather than a constructor call — themed globally by swapping variables
rather than touching every control. A designer's mockup, expressed in flexbox
and `border-radius` and media queries, transfers across essentially unchanged.

Neither is "better." They are aimed at different bottlenecks:

- If your bottleneck is **shipping a small, fast, dependency-free binary**, code-first
  is the right shape and markup is a liability.
- If your bottleneck is **iterating on visual design** — or letting web-fluent
  people work on the UI at all — markup + CSS is the right shape and fluent
  builders become a translation tax on every design change.

Put bluntly: MewUI optimises for the **developer building the app**; CupriFace
optimises for the **design surviving contact with the app**.

## The browser: both projects are there now

This was the previous revision's clearest dividing line, and it is gone. MewUI
ships `MewUI.Platform.Browser` (a canvas host with pointer, touch and IME
handling) and `MewUI.Backend.MewVG.Browser` (its managed vector renderer over
WebGL), and its Gallery is deployed and reachable. Two projects that both refuse
to embed a browser both decided the browser was worth targeting anyway.

The payloads land within a few hundred kilobytes of each other, which is the
more interesting result:

| | Payload | Gzipped | How it was measured |
|---|---|---|---|
| CupriFace `samples/WebLlvm` (NativeAOT-LLVM) | 14.2 MB | 5.5 MB | This repository's published figure |
| MewUI Gallery (wasm AOT) | 17.08 MB | 5.37 MB | Summed from the live deployment, 2026-09-07 |

Neither is a small download; both are dominated by the .NET wasm runtime rather
than by the UI engine (MewUI's own assemblies are ~2.2 MB of its 17.08 MB). If
you were hoping the code-first project would produce a dramatically smaller
browser bundle the way it produces a dramatically smaller desktop binary — it
does not, because on the web the runtime, not the renderer, is the bill.

What CupriFace still has here, stated no wider than it deserves:

- **It is a supported product.** Two hosts (`CupriFace.Web.Mono` and
  `CupriFace.Web.NativeAot`) with the identical `WebHost.Run` API, documented in
  the README and PACKAGE guide, packaged, and gated in CI. MewUI's browser host
  is in `src/` and clearly works, but it is **not published to NuGet**, not
  mentioned in the README, and not on the roadmap — so today you would be
  building it from source and tracking a target its own project has not announced.
- **Accessibility on the web.** CupriFace mirrors its semantics tree into a real
  DOM ARIA tree beside the canvas, so a screen reader gets something. MewUI's
  browser host draws to a canvas and exposes a hidden text input for IME; there
  is no a11y mirror. A canvas with no accessible tree is opaque to assistive
  technology.

If either of those matters, the gap is real. If neither does, this row is now a
tie, and the honest summary is that MewUI got to the browser faster than this
document expected.

## Where MewUI is genuinely stronger

An honest list, and it got longer:

- **Deployment size.** This is MewUI's founding purpose and it still delivers: a
  3.17–4.52 MB Hello World and a 7.35–9.24 MB Gallery, self-contained, no .NET
  install. CupriFace's best comparable number is **20.8 MB** — the full Showcase,
  trimmed and bundle-compressed into one self-contained file, measured, with
  hardware GL and the UIA bridge verified intact. That is roughly **2.3× MewUI's
  Gallery**.

  The previous revision of this document called the gap "structural rather than a
  matter of tuning," on the grounds that SkiaSharp's native library cannot be
  linked into the executable the way MewUI's Direct2D/GDI/MewVG backends call
  into the OS. Tuning then took 95.4 MB to 20.8 MB, so that was overstated — the
  single-file bundle carries the natives perfectly well. What is structural is the
  floor, not the packaging: CupriFace ships ~12.7 MB of Skia, HarfBuzz, SDL and
  GLFW before a line of app code, where MewUI ships almost no native payload at
  all. MewUI still wins "smallest possible standalone app" outright, and on this
  architecture always will — it just wins by less than was claimed.
- **Developer tooling.** The largest change since the last revision, and it is
  now decisive. MewUI has **Hot Reload** with no declaration required (`dotnet
  watch run`, rebuilding only the nodes whose build code changed), **DevTools**
  behind one MSBuild property — element inspector, visual tree window,
  performance monitor and profiler timeline on keyboard shortcuts — and an
  **editor preview** that renders your `Window` and `UserControl` types in a
  VS Code panel without launching the app, updating on save. There are Visual
  Studio and Rider integrations in the tree beside it. CupriFace has a live
  diagnostics HUD and nothing else: no inspector, no preview, no hot reload. Its
  markup and CSS being plain files means a host *could* reload them without a
  rebuild, but nothing in the repository does.
- **Backend flexibility.** Direct2D, GDI and a managed MewVG let MewUI ride OS
  libraries with almost no native payload; CupriFace has exactly one rendering
  path (Skia) and pays for it in bytes. MewUI's ability to fall back to GDI on
  ancient or remote machines is a real operational advantage — one this very
  document ran into from the other side, since CupriFace's GL window is
  unavailable over a remote session and falls back to a software SDL window.
- **AOT rigour as a constant.** MewUI treats AOT/trim safety as a hard constraint
  validated on every change: reflection-free bindings, `LibraryImport` P/Invoke,
  analyzers and source generators enforcing it, and DevTools that *refuse to load*
  in a trimmed or AOT build because a reflected member list would silently omit
  members. CupriFace's AOT build is verified by hand and explicitly **not run in
  CI** (`ci.yml` says so, and gives the reason: it needs a C++ toolchain and
  isn't single-file anyway). The next section is what that costs.
- **Type safety over the whole UI.** A renamed property breaks MewUI's build. In
  CupriFace it breaks a `{{Binding}}` at runtime — the classic markup trade.
- **A docking system.** MewDock's VS-style docking is a substantial piece of
  desktop UI that CupriFace does not have.
- **Text as a first-class subsystem.** MewUI now has a documented text engine
  with a retained layout contract over DirectWrite, GDI, CoreText and FreeType,
  viewport virtualization for large documents, styled inline runs, editable
  documents, syntax views and a code-editor extension (MewvalonEdit). CupriFace's
  HarfBuzz shaping is still a real advantage for complex scripts, but "a
  NanoVG-derived renderer stops at simple text" — the previous revision's claim —
  is no longer a fair description of MewUI.
- **WPF-shaped familiarity.** Measure/arrange, a property system with change
  notification, control templates, commands — a WPF developer is productive in
  MewUI on day one. CupriFace asks them to think in the box model instead.
- **Trying it costs nothing.** `curl … fba_gallery.cs | dotnet run -` runs the
  Gallery with no clone and no project file.

## The AOT caveat, found while measuring

Worth its own section because it was found by writing this document, it cut
against two claims CupriFace makes elsewhere, and it is now fixed — the finding
is kept because how it hid is the more useful half.

*Status: fixed after v0.19.0 (#126). The UIA bridge went through the runtime's
built-in COM interop, which NativeAOT does not have; it now goes through
source-generated COM, and the Silk.NET roots that keep hardware GL under
trimming apply to AOT too. Verified with the repo's UIA gate against the AOT
exe (identical to JIT) and a direct launch taking the GPU lane. What follows is
the state that shipped in v0.19.0, left as written.*

The NativeAOT Showcase publishes, launches and renders — the frame it produced
while writing this is the Motion page mid-animation. But it renders on the
**software** window, having silently lost hardware GL, and it also prints:

```
[CupriFace] UIA bridge unavailable (NotSupportedException: COM Interop requires
ComWrapper instance registered for marshalling.); continuing without it.
```

The same binary built JIT, in the same session, initialises the bridge without
complaint and takes the real GPU. So **a NativeAOT build quietly loses both the
Windows screen-reader bridge and hardware rendering**, while still opening a
window and drawing a correct-looking frame. Trimming without AOT breaks the same
two things for the same two reasons — Silk.NET discovers its window backends by
reflection, and the UIA bridge marshals through built-in COM — but there both are
recoverable with `TrimmerRootAssembly` and `BuiltInComInteropSupport`, which is
how the 20.8 MB build above keeps them. AOT has no built-in COM marshalling at
all, so its UIA loss needs the bridge ported to source-generated ComWrappers.
Three consequences for this comparison:

1. The accessibility row above is a CupriFace advantage *on the runtime CI
   actually gates*. Ship AOT on Windows today and you ship without UIA.
2. Any performance or memory figure taken from the AOT build is measuring the
   software renderer. An earlier revision of [electron.md](electron.md) quoted one
   as though it were the GL path, and understated CupriFace's idle memory by half.
3. It is a concrete instance of exactly the difference the previous section
   describes. MewUI's AOT guarantee is structural — enforced by design, by
   analyzers, and by a CI that treats it as a constraint. CupriFace's is a spot
   check, and this is what a spot check misses: both regressions log a line and
   carry on, so every automated check still passed. The AT-SPI, NSAccessibility
   and TalkBack bridges have not been checked under AOT or trimming at all.

## Where CupriFace is genuinely stronger

- **Phones.** The same app class that runs on the desktop and in a browser also
  runs on Android, with engine-level touch (tap-on-release, momentum fling,
  long-press), a soft keyboard with real IME composition, and a TalkBack bridge —
  proven every CI run by driving a real APK on an emulator. MewUI is desktop and
  browser only. Both projects value small binaries; only one of them is on a phone.
  With the browser row now a tie, **this is the reach argument.**
- **CSS as the styling model.** A cascade with selectors, inheritance, variables,
  media queries and keyframe animations is a more expressive theming system than
  typed setters plus state triggers — and it's a system your team, and the entire
  design world, already knows. Dark mode is a variable swap; responsive layout is
  a media query; restyling ships without a recompile.
- **Headless-first testing.** The CupriFace engine doesn't know whether a window
  exists, so UI behaviour is unit-testable: the repo's **818 tests** build
  documents, click, type, fling and compose IME text into them, and assert on
  state and pixels — in CI, in milliseconds, with no display. The screenshots in
  the README are `doc.Render()` output for the same reason. MewUI tests broadly
  too, and this is a narrower gap than the previous revision implied — but its
  window-level coverage lives in a real-window automation suite driving actual
  monitors and DPI transitions, which is a slower and more fragile place to keep
  behavioural tests than a headless document.
- **Accessibility that reaches an actual screen reader.** Every `cupri-*`
  component carries `role`/`aria-*`, and that portable semantics tree is bridged
  to UIA on Windows, AT-SPI on Linux, NSAccessibility on macOS and TalkBack on
  Android — each with a blocking CI gate driven by a real AT client — plus a DOM
  ARIA mirror on the web. MewUI documents focus and tab navigation and has no OS
  bridge at all. This remains the widest gap between the two projects, subject to
  the AOT caveat above.
- **Render-into-anything.** `RenderToPixels` fills any RGBA buffer — a game HUD,
  a texture in someone else's renderer, a server-side image — and
  `IGpuSurfaceSource` lets a producer draw on the host's own GL context and hand
  over a texture with no copy, on all three hosts. CupriFace is a library you
  call; MewUI is a framework that runs your app. MewUI's embedding samples point
  the other way: hosting WinForms controls *inside* a MewUI window, or drawing
  into a MewUI bitmap control.
- **Batteries for app UI.** Charts, tables with sort/select/resize, a command
  palette, kanban with drag-and-drop, date/time/colour pickers, Markdown, video,
  Lottie — 74 elements in the box, versus MewUI's deliberately thin core plus
  optional packages. (MewUI calls an exhaustive catalogue an explicit non-goal,
  so this is a difference in philosophy, not an oversight — and MewDock and
  MewvalonEdit are two things MewUI has in packages that CupriFace has nowhere.)
- **Complex-script text.** HarfBuzz shaping — kerning, ligatures,
  Greek/Cyrillic/Arabic, bidi reorder with a dedicated sample — comes standard on
  every host including the browser, where MewUI's canvas renderer does its own
  measurement.

## Choosing

**Choose MewUI when:**

- The deliverable is a **small self-contained desktop executable** — no runtime
  install, a few megabytes, fast cold start. This is its reason to exist and
  nothing here comes close.
- You want your entire UI in type-checked C# with IntelliSense and refactoring,
  and you consider a separate markup language a cost rather than a feature.
- **Inner-loop tooling matters**: hot reload, an element inspector, a profiler,
  an in-editor preview. CupriFace has none of these.
- NativeAOT and trim safety are hard requirements you must be able to *trust*
  rather than spot-check.
- You want to minimise native payload by riding Direct2D/GDI, or need GDI as a
  fallback on constrained or remote machines.
- You need docking, or a code editor control, and you want it from the framework.
- A WPF-shaped mental model (measure/arrange, properties, templates, commands) is
  what your team already has.

**Choose CupriFace when:**

- The same UI must run **on an Android phone** as well as the desktop and the
  browser, from one codebase.
- **Screen-reader support is a requirement**, on any platform — this is the one
  axis where the two projects are not close.
- Your UI is designed in web terms and you want CSS itself — cascade, variables,
  media queries, keyframes — as the theming system, editable without a rebuild
  by people who don't write C#.
- You want UI behaviour under **fast headless automated tests** rather than
  real-window automation.
- You need to render UI *into* something you already own: a game, a render loop,
  an offscreen buffer, a server.
- You want charts, data tables, pickers, Markdown, video and a command palette in
  the box.
- Binding plain POCOs with no INPC and no ceremony matters more than
  compile-time checking of every binding path.
- Download size is not your binding constraint (because today, honestly, it
  would count against you).

**Note that the browser is no longer a tiebreaker** unless you need the web host
to be a supported, packaged, accessible target — in which case it still is.

## The honest summary

MewUI is the better answer to *"ship me a tiny native desktop app with no runtime
and no markup,"* and it is now also the better answer to *"give me a good inner
loop while I build it."* It is disciplined about its goals and it hits them.
CupriFace is the better answer to *"let me author a UI as a styled document, have
a screen reader read it, test it headlessly, and run it anywhere — a window, a
phone, a canvas, a texture."*

The clearest way to decide: **if your hardest constraint is the size of the thing
you ship or the quality of your inner loop, take MewUI. If your hardest
constraint is where the UI has to run, who has to be able to style it, and
whether assistive technology can read it, take CupriFace.**

What is most interesting about this revision is the convergence. Two projects
that started from the same complaint about XAML and diverged as far as markup and
no-markup have since arrived independently at the same conclusion about the
browser — while remaining as far apart as ever on how a human describes a UI.
That is the difference that was real all along.
