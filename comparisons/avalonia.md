# CupriFace vs Avalonia

Avalonia is the closest neighbour CupriFace has in the .NET world, which makes
the comparison unusually instructive: both are cross-platform, both render
every pixel themselves through SkiaSharp rather than wrapping OS controls, both
are MIT-licensed, and both let one codebase produce the same UI on Windows,
macOS, Linux and the browser. The differences are therefore not about *what*
gets drawn — they are about **how a UI is authored, what kind of software each
project is, and where each one is willing to run**.

*Version note: Avalonia statements below were checked against Avalonia 11.x
(the current stable line as of early 2026). CupriFace statements were
re-checked against this repository in August 2026, after the Android host
landed. For the mobile-first version of this argument, see
[maui.md](maui.md).*

## At a glance

| | **CupriFace** | **Avalonia** |
|---|---|---|
| Kind of software | UI **engine** (a library that renders documents into any surface) | UI **framework** (owns the application model, windows, lifetime) |
| Authoring | **HTML + CSS** files + a plain C# model | **XAML** + C# code-behind / view-models |
| Binding | `{{Property}}` against any POCO; controls write back; rebuild-on-change | Compiled/reflection bindings; `INotifyPropertyChanged` / MVVM ceremony |
| Behaviour | C# only — **no JavaScript, ever** (architectural principle) | C# only |
| Rendering | SkiaSharp display list; damage-tracked incremental present; render-on-demand (0% CPU idle) | SkiaSharp through a retained visual tree + compositor |
| Layout | Managed flexbox + CSS grid + block flow (pure C#, no native Yoga) | XAML panels (`Grid`, `StackPanel`, `DockPanel`, …) |
| Text | HarfBuzz shaping (kerning, ligatures, Greek/Cyrillic/Arabic); **IME composition** on Android + both web hosts + desktop; mixed-direction text partial | Mature text stack; IME and `FlowDirection` support |
| Desktop | Windows / macOS / Linux via Silk.NET (OpenGL window or SDL software fallback) | Windows / macOS / Linux, mature windowing (multi-window, dialogs, tray, native menus) |
| Browser | First-class target: thin JS glue → `<canvas>`; whole app is one wasm file — **14.2 MB (5.5 MB gzipped), measured** on the experimental NativeAOT-LLVM host | Supported, but heavyweight: Mono runtime + framework in the browser, large payloads, slower startup |
| Mobile | **Android** — own host package, engine-level touch/fling/IME, TalkBack bridge, driven on an emulator by a blocking CI gate. **No iOS** | iOS **and** Android, both mature |
| Touch & gestures | Two-axis scrolling with momentum and a rubber band; tap-on-release, long-press, double-tap; **multi-touch as an author seam** (`doc.OnPointer` with pointer capture) — the engine computes no gesture | Mature gesture recognizers, including pinch/rotate out of the box |
| Embedding | Core capability: `RenderToPixels` / `Render(canvas)` into any RGBA surface — game texture, HTML canvas, server-side PNG | Possible but not the primary shape; the framework expects to own the window |
| Control set | 69 `<cupri-*>` elements (inputs, pickers, tables, charts, overlays, kanban, command palette, …) with `role`/`aria-*` baked in | Deep, mature control library + third-party vendors (DataGrid, virtualization for huge lists, docking, …) |
| Tooling | Files are plain HTML/CSS — any editor; no designer | IDE previewer, XAML hot reload, commercial dev tools |
| Accessibility | Roles/ARIA in every component; **four bridges — UIA, AT-SPI, NSAccessibility, TalkBack** — each gated in CI by a real AT client; real DOM tree on web | OS bridges on all three desktops (UIA / AT-SPI / NSAccessibility), longer-proven; mobile a11y inherited from native controls |
| Testing | **Headless-first**: the engine renders and takes input with no window; the repo's 417 tests click, type, fling and pixel-assert real documents | Headless test platform exists; most testing is app-level/UI automation |
| Dependencies | SkiaSharp, HarfBuzzSharp, Silk.NET, AngleSharp — all MIT, checked as a hard project rule | MIT framework; larger dependency and binary surface |
| Maturity | Young, moving fast; a documented CSS *subset* | Years of production use, commercial backing (incl. paid WPF-compat line) |

## The real difference: what you write

This is the decision that decides everything else.

**Avalonia** asks you to express UI in XAML — an object-instantiation language.
`<Button Content="Save"/>` constructs a `Button` class; styles are XAML
resources with selector-ish syntax; data reaches the screen through the binding
system and (in practice) `INotifyPropertyChanged` view-models. It is WPF's
model, refined and made portable. If your team grew up on WPF, it is home.

**CupriFace** asks you to express UI as a **document**: an HTML file, a CSS
file, and any C# object.

```html
<div class="row">
  <span>Volume</span>
  <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
  <span>{{Volume}}</span>
</div>
```

```csharp
public class Settings { public int Volume { get; set; } = 60; }
```

That's the entire wiring. The model is a plain object — no base class, no
`INotifyPropertyChanged`, no observables. Dragging the slider writes `Volume`
back; the engine re-binds and repaints, preserving interaction state (scroll
positions, focus, drag-resized panels) across the rebuild. Styling is real CSS
— classes, descendant/child selectors, hover state, `@media` queries, `calc()`,
CSS variables, `@keyframes`, dark mode as a variable swap — living in a file a
web designer can edit without touching C# or learning a framework.

Why that matters in practice:

- **The skills already exist.** HTML/CSS is the most widely held UI skill in
  the industry. An Avalonia project needs XAML fluency, which is scarce outside
  WPF veterans; a CupriFace page can be styled by anyone who has built a web
  page — while behaviour stays in C#, reviewable in one place.
- **No translation layer in your head.** Design references, brand guidelines
  and mockups are expressed in web terms (flexbox, gaps, `border-radius`,
  media queries). In CupriFace they paste in as-is; in Avalonia every one is
  mentally recompiled into panels, styles and resource dictionaries.
- **Less ceremony per feature.** No INPC plumbing, no converters, no
  `RelayCommand` — a click handler is `doc.OnClick(".save", …)` on a CSS
  selector, and validation, focus and keyboard behaviour come with the
  `<cupri-*>` element.

The trade: CupriFace implements a documented **subset** of CSS. It is a real
subset — selector cascade, flexbox, grid with `minmax()`/spans, transforms,
animations — but if your design leans on the long tail of modern CSS, you will
find edges. Avalonia's styling system has fewer surprises at its edges because
it never promises to be CSS at all.

## Engine vs framework

Avalonia is the roof over your application: it owns `Main`, the windows, the
dispatcher, the lifetime. That is exactly what you want when the deliverable
*is* a desktop application.

CupriFace inverts this. The engine's contract is
`document + input events → pixels`, and everything else is a replaceable host
(the repo's desktop and web hosts are thin adapters over the same app class).
That buys three things Avalonia is not shaped for:

1. **Render anywhere.** `RenderToPixels` fills any RGBA buffer:
   a game engine HUD, a live texture inside another renderer, a server-side
   screenshot/PDF-ish pipeline, an existing SDL/GL loop you already own. The
   UI is a function you call, not a process you surrender control to.
2. **Headless is not a special mode.** The engine doesn't know whether a
   window exists. The repo's test suite (417 tests) constructs documents,
   clicks, types, flings and composes IME text into them, and asserts on state
   and pixels — in milliseconds, in CI, with no display server. UI behaviour
   becomes as testable as business logic, which changes how much UI you are
   willing to cover with tests at all. The Android work is the demonstration:
   the phone sample's touch targets are calibrated headlessly at the emulator's
   exact dp geometry, so a layout regression fails in seconds locally rather
   than twenty minutes later on a device.
3. **The web is a first-class citizen, cheaply.** The browser host is a
   `<canvas>`, ~200 lines of non-authored JS glue, and the same app class as
   the desktop. On the experimental NativeAOT-LLVM host the entire application
   — engine, Skia, HarfBuzz, fonts, app — is a single 14.2 MB wasm file
   (5.5 MB over the wire gzipped) running at native engine speed (a hover
   restyle measured at 2.1 ms vs 16.2 ms interpreted). Avalonia's browser
   target exists and works, but it carries the framework and runtime into the
   page and it shows in payload and startup; the browser is a port, not a
   home. *(Fairness note: CupriFace's default web host is Mono-interpreted
   today too — the NativeAOT-LLVM host is the experimental fast path.)*

There is also a licensing angle, small but real: CupriFace's entire dependency
list is four MIT packages (SkiaSharp, HarfBuzzSharp, Silk.NET, AngleSharp),
license-checked as a hard project rule — an easy conversation with a legal
department, and an easy audit.

## Where Avalonia is simply ahead

An honest list, because it's a long one and it decides real projects:

- **Maturity and surface area.** Avalonia has years of production hardening,
  a deep control library, virtualization for very large lists, a `DataGrid`,
  docking layouts, third-party control vendors, and answers on Stack Overflow.
  CupriFace's 69 elements cover a lot of app UI, but the long tail is long.
- **iOS.** Both projects now run on Android; only Avalonia runs on iPhones.
  CupriFace's Android host is the template for an eventual iOS one, but nothing
  is built.
- **Mobile maturity.** Avalonia's mobile targets have years behind them, plus
  the platform-integration surface a real app needs. CupriFace's Android host
  is weeks old, renders the UI and handles touch/IME/TalkBack — and offers no
  platform APIs at all (no sensors, permissions, pickers or notifications).
- **Desktop-OS integration.** Multi-window, native menus, tray icons, system
  dialogs, drag-and-drop with the OS, per-monitor DPI — Avalonia has the
  mature story. CupriFace today is one window per app with a young shell.
- **Accessibility maturity.** Both now cover the three desktops; CupriFace adds
  TalkBack on Android, and each of its four bridges is gated in CI by a real AT
  client (FlaUI, pyatspi, pyobjc, uiautomator). Avalonia's have years of soak
  and real users behind them, where CupriFace's are young — no Text pattern for
  editable fields yet, and no human screen-reader pass on record.
- **Input depth.** Full bidirectional text is mature in Avalonia and only
  partial here — and Avalonia ships *recognised* gestures (pinch, rotate) where CupriFace hands
  you the raw pointers and expects you to write the arithmetic. (IME composition is no longer on this list: CupriFace grew a
  real preedit model in the engine, wired to Android's InputConnection and both
  web hosts.)
- **Tooling and support.** IDE previewers, XAML hot reload, commercial
  support contracts, and a funded company behind it. CupriFace's "tooling" is
  that its inputs are plain text files.

## Choosing

**Choose CupriFace when:**

- Your team's UI fluency is HTML/CSS, and you want that skill — and your web
  design language — to transfer directly, with all behaviour in C#.
- The same UI must run on desktop, on an Android phone **and** in the browser
  without a browser engine on desktop or a heavyweight payload on the web —
  and you want it to look identical on all of them.
- You need to render UI *into* something: a game, an existing render loop, an
  offscreen buffer, a server.
- You want UI logic under real automated test (headless, fast, in CI) rather
  than end-to-end automation.
- You value a tiny, fully managed, all-MIT dependency surface you can audit in
  an afternoon.
- Binding plain C# objects with zero ceremony matters more to you than a deep
  pre-built control catalogue.

**Choose Avalonia when:**

- You are building a conventional, possibly large, desktop application and
  want the batteries: control depth, data grids, docking, multi-window,
  native OS integration.
- You need **iOS** from the same codebase, or a mobile app that needs the
  platform's own APIs (sensors, permissions, pickers) rather than just a UI.
- Screen-reader support has to be **proven by real users today**, not by
  automated clients on four platforms — the bridges here are gated but young.
- Your team is WPF-fluent, or you are porting a WPF codebase (Avalonia's
  commercial XPF line exists for exactly that).
- You want commercial support and a mature ecosystem more than you want a
  particular authoring model.

The summary sentence: **Avalonia is the better WPF; CupriFace is the browser's
authoring model without the browser.** If XAML is your team's native tongue and
the deliverable is a rich desktop app, Avalonia earns its weight. If your UI
thinking happens in HTML and CSS — and you want one small, testable,
embed-anywhere engine to carry that UI from a desktop window to a `<canvas>`
to a texture — that is precisely the niche CupriFace was built to fill.
