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

*Version note: MewUI statements below were checked against the public repository
and README in August 2026 (~635 stars, ~1.3k commits, "public API surface is
still being stabilized"). CupriFace statements were re-checked against this
repository in August 2026, after the Android host landed — which is where the
two projects stopped being peers on reach, if not on binary size.*

*Measurement note: CupriFace's NativeAOT numbers were produced while writing this
document, not estimated —
`dotnet publish samples/Viewer -c Release -r win-x64 -p:Aot=true` on win-x64,
then running the result with no .NET runtime on the path. MewUI's numbers are its
README's published figures and were not independently reproduced.*

## At a glance

| | **CupriFace** | **MewUI** |
|---|---|---|
| Tagline | HTML + CSS rendered by a managed engine — an Electron alternative with no browser | "Cross-platform, lightweight, code-first .NET GUI framework… NativeAOT/Trim-friendly desktop apps without requiring a separate .NET runtime" |
| Authoring | **HTML + CSS files** + a plain C# model | **Fluent C# markup** — `new Window().Title("…").Content(new StackPanel().Children(…))` |
| Styling | Real CSS: cascade, classes, descendant/child selectors, `@media`, variables, `@keyframes` | `Style` objects with typed `Setter`s, registered in a `StyleSheet`; named styles + type rules; `StateTrigger` for hover/pressed |
| Binding | `{{Path}}` interpolation against any POCO; controls write back; no INPC | Explicit, **reflection-free** delegate bindings; `ObservableValue<T>`, `BindingPath<TRoot,TValue>` |
| Layout | CSS box model: managed flexbox, grid (`minmax()`, spans), block flow | WPF-style **measure/arrange** with panels (`Grid`, `StackPanel`, `DockPanel`) |
| Rendering | SkiaSharp only, one path everywhere | **Pluggable**: Direct2D, GDI (Windows), MewVG (managed NanoVG port — GL on Win/Linux, Metal on macOS); SkiaSharp as an *extension* |
| Desktop | Windows / macOS / Linux via Silk.NET | Windows 10+ / Linux X11 / macOS 12+, per-backend hosts |
| Browser / WASM | **First-class**: same app class → `<canvas>`; 14.2 MB wasm (5.5 MB gzipped), measured | **None** — desktop only (WebView2 is an *embedded browser control*, the reverse direction) |
| Mobile | **Android** — own host package, engine-level touch/fling/IME, TalkBack bridge, emulator-gated in CI | **None** — desktop only |
| Deployment | NativeAOT works (`-p:Aot=true`): **23.3 MB** self-contained, no runtime install — but 5 files, not one | **The whole point**: single self-contained exe, Hello World **2.6–4.4 MB**, Gallery **5.6–7.4 MB** |
| Native footprint | Skia (9.2 MB) + HarfBuzz (1.7 MB) + SDL/GLFW (1.8 MB) on win-x64, before any app code | Direct2D/GDI backends ride OS libraries — near-zero native payload |
| AOT posture | Design goal, now **verified end-to-end** — the AOT Showcase renders and interacts correctly | **Non-negotiable design constraint**, validated continuously; `LibraryImport` source-generated P/Invoke |
| Embedding | Core capability: `RenderToPixels` into any RGBA buffer (game texture, canvas, server) | Not a stated goal — the framework hosts the window |
| Testing | **Headless-first**: engine needs no window; 369 tests click/type/fling/pixel-assert | Conventional; no headless-first claim |
| Accessibility | `role`/`aria-*` in every component; **four bridges — UIA, AT-SPI, NSAccessibility, TalkBack — each CI-gated by a real AT client**; real DOM a11y tree on the web host | Focus/tab navigation documented; no OS a11y bridge story |
| Extras | Charts, kanban, command palette, pickers built in (69 elements) | Thin core + **optional packages**: MewDock (VS-style docking), SVG, Skia, MewCharts, WebView2 |
| Tooling | Plain text files, any editor; no designer | **Hot reload** and **Preview** documented — a real advantage |
| Non-goals | JavaScript in the authoring model, ever | XAML compatibility, designer-first workflows, reflection binding, exhaustive control catalogue |
| Maturity | Young, pre-1.0, API churning | Young, pre-1.0, API churning ("breaking changes can happen between minor releases") |

## The fork in the road: markup vs code

Everything else follows from this.

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
**nothing to reflect over**, so NativeAOT and trimming stay honest.

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

## Where MewUI is genuinely stronger

An honest list, and it is not short:

- **Deployment size.** This is MewUI's founding purpose and it delivers: a
  2.6–4.4 MB Hello World, 5.6–7.4 MB for its Gallery, self-contained, no .NET
  install. CupriFace *can* do NativeAOT — measured while writing this document,
  the full Showcase publishes to **23.3 MB** (a 10.6 MB exe plus 12.7 MB of Skia,
  HarfBuzz, SDL and GLFW natives) and runs correctly with no runtime installed.
  But that is roughly **3–4× MewUI's comparable Gallery**, and it is five files
  rather than one: SkiaSharp's native library cannot be linked into the
  executable the way MewUI's Direct2D/GDI backends simply call into Windows.
  On "smallest possible standalone app," MewUI wins outright.
- **Backend flexibility.** Direct2D and GDI let MewUI ride OS libraries with
  almost no native payload; CupriFace has exactly one rendering path (Skia) and
  pays for it in bytes. MewUI's ability to fall back to GDI on ancient/remote
  machines is a real operational advantage.
- **AOT rigour as a constant.** MewUI treats AOT/trim safety as a hard constraint
  validated on every change — reflection-free bindings, `LibraryImport` P/Invoke.
  CupriFace's AOT build is verified to work, but it is opt-in and not gated in CI,
  so nothing stops a future reflection-dependent change from quietly breaking it.
  MewUI's guarantee is structural; CupriFace's is a spot check.
- **Type safety over the whole UI.** A renamed property breaks MewUI's build. In
  CupriFace it breaks a `{{Binding}}` at runtime — the classic markup trade.
- **Hot reload and a preview tool.** Documented features with no CupriFace
  equivalent. (CupriFace's answer is weaker but not nothing: its markup and CSS
  are plain files, so a host *could* reload them without a rebuild.)
- **A docking system.** MewDock's VS-style docking is a substantial piece of
  desktop UI that CupriFace does not have.
- **WPF-shaped familiarity.** Measure/arrange, a property system with change
  notification, control templates — a WPF developer is productive in MewUI on
  day one. CupriFace asks them to think in the box model instead.

## Where CupriFace is genuinely stronger

- **The web is a real target, not an embedded control.** The same app class that
  runs in a desktop window runs on a `<canvas>` in the browser — one 14.2 MB wasm
  file (5.5 MB gzipped), no server, no Blazor. MewUI has no browser story; its
  WebView2 package points the other way (hosting a browser *inside* your app,
  Windows-only). If "desktop **and** web from one codebase" is on your list,
  this comparison ends here.
- **Phones.** The same app class also runs on Android, with engine-level touch
  (tap-on-release, momentum fling, long-press), a soft keyboard with real IME
  composition, and a TalkBack bridge — proven every CI run by driving a real APK
  on an emulator. MewUI is desktop-only by design. Both projects value small
  binaries; only one of them is on a phone.
- **CSS as the styling model.** A cascade with selectors, inheritance, variables,
  media queries and keyframe animations is a far more expressive theming system
  than typed setters plus state triggers — and it's a system your team, and the
  entire design world, already knows. Dark mode is a variable swap; responsive
  layout is a media query; restyling ships without a recompile.
- **Headless-first testing.** The CupriFace engine doesn't know whether a window
  exists, so UI behaviour is unit-testable: the repo's 369 tests build documents,
  click, type, fling and compose IME text into them, and assert on state and
  pixels — in CI, in milliseconds, with no display. MewUI's window-owning design
  makes the same coverage an end-to-end automation problem.
- **Render-into-anything.** `RenderToPixels` fills any RGBA buffer — a game HUD,
  a texture in someone else's renderer, a server-side image. CupriFace is a
  library you call; MewUI is a framework that runs your app.
- **Batteries for app UI.** Charts, tables with sort/select/resize, a command
  palette, kanban with drag-and-drop, date/time/colour pickers — 69 elements in
  the box, versus MewUI's deliberately thin core plus optional packages. (MewUI
  calls an exhaustive catalogue an explicit non-goal, so this is a difference in
  philosophy, not an oversight.)
- **Accessibility that reaches an actual screen reader.** Every `cupri-*`
  component carries `role`/`aria-*`, and that portable semantics tree is
  bridged to UIA on Windows, AT-SPI on Linux, NSAccessibility on macOS and
  TalkBack on Android — each with a blocking CI gate driven by a real AT client.
  MewUI documents focus and tab navigation but has no OS bridge story, so on
  this axis the two projects are no longer near-peers.
- **Text depth.** HarfBuzz shaping — kerning, ligatures, Greek/Cyrillic/Arabic —
  comes standard, where a NanoVG-derived renderer typically stops at simpler
  text.

## Choosing

**Choose MewUI when:**

- The deliverable is a **small self-contained desktop executable** — no runtime
  install, a few megabytes, fast cold start. This is its reason to exist.
- You want your entire UI in type-checked C# with IntelliSense and refactoring,
  and you consider a separate markup language a cost rather than a feature.
- NativeAOT and trim safety are hard requirements you must be able to trust.
- You want to minimise native payload by riding Direct2D/GDI, or need GDI as a
  fallback on constrained/remote machines.
- Hot reload and a preview tool matter to your inner loop.
- You need docking, and you want it from the framework.
- A WPF-shaped mental model (measure/arrange, properties, templates) is what
  your team already has.

**Choose CupriFace when:**

- The same UI must run **on the desktop, on an Android phone and in a browser**
  from one codebase.
- Your UI is designed in web terms and you want CSS itself — cascade, variables,
  media queries, keyframes — as the theming system, editable without a rebuild
  by people who don't write C#.
- You want UI behaviour under **fast headless automated tests** rather than
  end-to-end UI automation.
- You need to render UI *into* something you already own: a game, a render loop,
  an offscreen buffer, a server.
- You want charts, data tables, pickers and a command palette in the box.
- Binding plain POCOs with no INPC and no ceremony matters more than
  compile-time checking of every binding path.
- Download size is not your binding constraint (because today, honestly, it
  would count against you).

## The honest summary

MewUI is the better answer to *"ship me a tiny native desktop app with no
runtime and no markup."* It is disciplined about exactly that goal and it hits
it. CupriFace is the better answer to *"let me author a UI as a styled document,
test it headlessly, and run it anywhere — a window, a canvas, a texture."*

The clearest way to decide: **if your hardest constraint is the size of the
thing you ship, take MewUI. If your hardest constraint is where the UI has to
run and who has to be able to style it, take CupriFace.** They are not really
competing for the same job, and the fact that two projects starting from the
same complaint about XAML ended up this far apart is the most interesting thing
about the comparison.
