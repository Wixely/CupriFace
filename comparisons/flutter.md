# CupriFace vs Flutter

Every other document in this folder compares CupriFace with a project that made
a *different* architectural bet. Flutter made the **same one**: don't wrap the
platform's controls — draw every pixel yourself, with your own layout engine and
your own renderer, so the UI is identical everywhere it runs. Flutter is the
most successful expression of that idea in the industry, and CupriFace is a much
smaller one aimed at .NET.

So this comparison cannot lean on "engine vs framework" or "we render, they
wrap." The differences are narrower and sharper: **which language you write,
what the UI is made of, where each one runs, and what surrounds the renderer.**

*Version note: Flutter statements describe the stable 3.x line as of mid-2026 —
Impeller as the default renderer on iOS and Android, with Skia still in the
picture elsewhere. Flutter moves quickly; version-sensitive rows say so.*

## At a glance

| | **CupriFace** | **Flutter** |
|---|---|---|
| Rendering philosophy | **Draw everything ourselves** — identical pixels everywhere | **Draw everything ourselves** — identical pixels everywhere |
| Language | **C#** — the same language as your backend | **Dart** — a separate language and ecosystem |
| Authoring | **HTML + CSS files** + a plain C# model | **Dart widget trees** — nested `build()` composition, no markup, no CSS |
| Styling | Real CSS: cascade, selectors, variables, `@media`, `@keyframes` | Constructor parameters + `ThemeData`; no cascade, no selectors |
| Layout | CSS box model: flexbox, grid, block flow | Widget-based constraints (`Row`/`Column`/`Flex` — flexbox's lineage, not CSS) |
| Renderer | Skia + damage-tracked display list; **render-on-demand, ~0% idle CPU** | Impeller (Metal/Vulkan, precompiled shaders) on mobile; Skia elsewhere |
| Platforms | Windows, macOS, Linux, **Android**, browser | **iOS, Android, Windows, macOS, Linux, web** — all six |
| Mobile maturity | Weeks old | Years; some of the most-downloaded apps on both stores |
| Calling your logic | **Direct** — the model is a C# object your app already owns | **Platform channels** — async, serialized, and a Dart↔native boundary you maintain |
| State model | Mutate a POCO; the engine rebinds and repaints | `setState` / `InheritedWidget` / Riverpod / Bloc — a whole discourse |
| Gestures | Scrolling, momentum, overscroll and multi-touch **capture** are built in; the recognisers above that (pinch, rotate) are the author's to write | A deep recogniser library with arbitration — `GestureDetector`, drag/scale/rotate, and a documented disambiguation arena |
| Accessibility | Portable semantics tree → **four bridges** (UIA, AT-SPI, NSAccessibility, TalkBack), each CI-gated by a real AT client | **Same architecture** — a semantics tree bridged per platform — and far more mature |
| Headless UI testing | First-class: 417 tests click, type, fling, pixel-assert with no display | **Also first-class** — `flutter test` widget tests + golden files. A genuine peer here |
| Hot reload | None | **Stateful hot reload — best in the industry** |
| Embedding into your app | `RenderToPixels` into any RGBA buffer you own | Possible (add-to-app, embedder API) but you are hosting the **Flutter engine** |
| Ecosystem | NuGet; 69 built-in elements | **pub.dev** — enormous; Material + Cupertino widget sets built in |
| Backing | One repository | **Google**, with a large full-time team |
| Android app size | ~20.9 MB APK (arm64, measured) | Typically smaller for a comparable app (AOT Dart, tree-shaken, per-ABI splits) |
| Web payload | 14.2 MB wasm / **5.5 MB gzipped** (NativeAOT-LLVM host, measured) | CanvasKit/skwasm + compiled app — broadly the same order, often smaller |
| Maturity | Pre-1.0 | Production since 2018 |

## The agreement, and why it still matters

Both projects reject the "wrap the platform's controls" model that MAUI,
Avalonia's peers and React Native build on, and they reject it for the same
reasons: per-platform controls mean per-platform bugs, per-platform layout
surprises, and a design that arrives at the user slightly different on every
device. When you own the renderer, a button is a button is a button.

Both also pay the same price for it, and it is worth stating plainly because
neither project can dodge it:

- **You inherit responsibility for accessibility.** A drawn button is invisible
  to a screen reader unless you build the semantics and bridge them. Flutter
  builds a `SemanticsNode` tree and bridges it per platform; CupriFace builds an
  `AccessibilityNode` tree and bridges it per platform. The architecture is
  *the same idea* — Flutter's just has years and a large team behind it.
- **You inherit text input.** IMEs, autocorrect, dictation and password managers
  live in native text widgets; drawing your own field means re-earning all of it.
- **You inherit platform conventions.** Scroll physics, selection handles, focus
  visuals, right-click menus — all yours to implement.

The interesting question is therefore not "should the renderer be yours" — both
answer yes — but everything wrapped around that answer.

## The decisive difference: Dart, or the language you already use

For most teams evaluating both, this is the whole comparison.

Flutter is Dart. Dart is a good language with a first-class toolchain, but it is
a *second* language for a .NET shop, with a second package ecosystem, second
build system, and no code sharing with the backend you already own. When Flutter
UI needs to reach your existing code, it goes through **platform channels**:
asynchronous, serialized message passing across a Dart↔native boundary that you
design, version and debug — structurally the same tax the
[Electron comparison](electron.md) describes for IPC.

CupriFace is C#. The model bound to the UI is an object your application already
has:

```csharp
public class Settings { public int Volume { get; set; } = 60; }
```

```html
<cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
```

Dragging the slider writes `Volume` on that object; your business logic reads the
same object, in the same process, with no channel, no serialization and no
`async` in between. If your backend, your domain model and your team are .NET,
that is not a small convenience — it is the difference between one codebase and
two.

The mirror-image argument is just as real: **if your team is mobile-first and
already fluent in Dart, everything above reads as a disadvantage of CupriFace.**

## Widgets or documents

Flutter's UI is code — deeply nested widget composition in `build()` methods:

```dart
Row(children: [
  const Text('Volume'),
  Slider(value: volume, onChanged: (v) => setState(() => volume = v)),
])
```

It is type-checked end to end, refactorable, and (with hot reload) fast to
iterate on. The costs are the ones every code-as-UI system pays: nesting depth
that obscures structure, styling expressed as constructor arguments rather than
a cascade, and a UI that only a Dart developer can edit.

CupriFace's UI is a document — an HTML file, a CSS file, and a C# object. The
practical consequence is not aesthetic: **the styling systems are not
equivalent.** CSS is a cascade with selectors, inheritance, variables, media
queries and keyframes. Flutter's `ThemeData` is a configuration object; a global
restyle means touching widgets or threading a theme through them, and there is
no `@media (max-width: 760px)` — you use `LayoutBuilder` and write the branch
yourself. Dark mode in CupriFace is a variable swap in a stylesheet; in Flutter
it is a `ThemeData` pair and widgets that read from it.

The trade in the other direction: Flutter's approach is **type-safe and
complete**, where CupriFace implements a documented CSS *subset* and a renamed
property breaks a `{{Binding}}` at runtime rather than at compile time.

## Where Flutter is simply ahead

The honest list, and it is the long one:

- **iOS.** Flutter has it; CupriFace does not, at all.
- **Mobile maturity.** Years of production use at enormous scale, against an
  Android host that is weeks old.
- **Hot reload.** Stateful, sub-second, and genuinely the best inner loop in UI
  development. CupriFace has nothing comparable — its answer is that its inputs
  are plain files and its tests are fast, which is not the same thing.
- **Ecosystem.** pub.dev covers essentially every need; Material and Cupertino
  ship in the box. CupriFace has 69 elements and NuGet.
- **Accessibility maturity.** Same architecture, vastly more soak — plus
  platform behaviours (selection handles, screen-reader gestures) that CupriFace
  has not built.
- **Text and i18n at world scale.** Complex scripts, full bidi, IME depth,
  selection UX. CupriFace's bidi is partial and its IME support, while real, is
  young.
- **Rendering sophistication.** Impeller exists because Flutter hit shader
  compilation jank at scale and engineered it away. CupriFace has not operated
  at the scale that surfaces such problems, which is not a virtue — it means
  those problems are undiscovered, not absent.
- **Tooling and support.** DevTools, a widget inspector, profilers, and a large
  team paid to keep it all working.
- **Platform APIs.** Camera, sensors, permissions, notifications, storage — all
  a package away. CupriFace has none of this; it renders UI and nothing else.

## Where CupriFace is genuinely stronger

- **It is C#, in your process.** No second language, no platform channels, no
  serialization boundary between the UI and the code it drives.
- **HTML and CSS as the authoring model** — editable by anyone web-fluent,
  themeable through a cascade, restylable without a rebuild.
- **Embedding is lightweight.** `RenderToPixels` fills a buffer you own: a game
  texture, another renderer's surface, a server-side image. Flutter's add-to-app
  works, but you are hosting an engine and its runtime, not calling a function.
- **Render-on-demand by default.** The engine repaints on damage and idles at
  ~0% CPU — a good fit for tray apps, kiosks, HUDs and long-lived windows.
- **A tiny, auditable dependency surface**: four MIT packages, versus Flutter's
  engine plus the Dart runtime and toolchain.
- **Linux desktop as a peer target**, with a real AT-SPI screen-reader bridge
  gated in CI — Flutter supports Linux, but it is visibly the least-loved of its
  six platforms.

Note what is *absent* from this list: headless testing. Against MAUI and
Avalonia that is CupriFace's sharpest advantage; against Flutter it is a draw.
`flutter test` runs widget tests without a device and golden-file tests for
pixels, which is exactly the model CupriFace uses. Claiming an edge there would
be dishonest.

## Choosing

**Choose Flutter when:**

- You need **iOS**, or mobile is the product rather than one of its targets.
- Your team writes Dart, or you are willing to adopt it — and the UI is the
  centre of gravity rather than an interface onto existing .NET code.
- You want the **best inner loop in the business** (stateful hot reload) and a
  vast package ecosystem.
- Accessibility, complex-script text and IME depth must be **mature today**.
- You need platform APIs — camera, sensors, permissions, notifications — as
  packages rather than as your own work.

**Choose CupriFace when:**

- **Your logic is already in C#** and you do not want a second language, a
  second ecosystem, and a channel between them that you maintain forever.
- You want UI authored as **HTML and CSS**, styled through a real cascade, by
  people who need not be application developers.
- You need to **render into something you own** — a game, an existing render
  loop, an offscreen buffer, a server — rather than host an engine.
- **Idle cost matters**: a tray app, a kiosk, a HUD, something open all day.
- Linux desktop is a first-class target rather than a checkbox.
- You do not need iOS, and a pre-1.0 engine with a small surface area is an
  acceptable trade for the above.

## The honest summary

Flutter is what this architectural bet looks like with a decade, a large team
and six platforms behind it. On the merits of the *renderer*, CupriFace has
nothing to teach it — and this document should not pretend otherwise.

What CupriFace offers is the same bet made **inside the .NET process**, with
HTML and CSS as the authoring model instead of Dart widget trees, and with the
engine as a library you call rather than a runtime you host. That is a narrow
niche next to Flutter's, and deliberately so: the question is not "which engine
draws better," it is **whether your UI should live in your existing C# process
or in a Dart application beside it.** If the answer is Dart, Flutter is
excellent and CupriFace has no argument. If the answer is C#, Flutter's
strengths are on the wrong side of a language boundary — and that is the gap
this project fills.
