# Comparisons

How CupriFace relates to other ways of building a .NET UI. Each document is a
working comparison — what the two projects share, where they genuinely differ,
and which situations favour which. They are written to be honest in both
directions: every one ends with a *"choose the other one when…"* section, because
a comparison that only ever recommends CupriFace would tell you nothing.

## The one-paragraph positioning

CupriFace is a **UI engine, not an application framework**: a fully managed
.NET pipeline that parses HTML + CSS, lays it out, paints it with Skia, and
binds it to plain C# objects — no browser, no JavaScript engine, no XAML. Hosts
are thin adapters: the same app class renders into a desktop window (Windows,
macOS, Linux), an Android phone, a browser `<canvas>` via WebAssembly, or any
RGBA buffer you hand it (a game texture, a server-side PNG). If your mental
model of a UI is *"a document I style with CSS, driven by a C# object"*,
CupriFace is that model with the browser removed.

## Documents

| Compared with | One-liner | Document |
|---|---|---|
| **Avalonia** | XAML application framework vs HTML/CSS rendering engine — the closest .NET neighbour, and the most instructive contrast | [avalonia.md](avalonia.md) |
| **.NET MAUI** | Native controls per platform vs one renderer everywhere; first-party mobile with iOS, against Linux + browser and headless-testable UI | [maui.md](maui.md) |
| **Flutter** | The one project that made the *same* bet — own the renderer, identical pixels everywhere — executed at ten times the scale, in Dart instead of C# | [flutter.md](flutter.md) |
| **MewUI** | The opposite answer to the same complaint about XAML: fluent C# markup and the smallest possible NativeAOT binary, vs HTML/CSS and run-anywhere. Both projects have since reached the browser, so the dividing lines are now mobile, accessibility and tooling | [mewui.md](mewui.md) |
| **Electron** | The comparison the project was founded on — keep HTML and CSS, delete the browser. What that costs, and when it's worth it | [electron.md](electron.md) |

Planned next (no documents yet): Tauri, Blazor Hybrid.

*All five documents were reviewed in **September 2026** against CupriFace **v0.18.0**, and each
names the version of the project it compares against: Avalonia 12.1.2, .NET MAUI 10.0.100,
Flutter 3.47.2, MewUI v0.21.1, Electron 44.2.0. Shared CupriFace figures, re-measured for this pass:
**818 tests**, **74 `cupri-*` elements**, a **95.4 MB** single-file publish that trimming and bundle
compression take to **20.8 MB**, a **25.1 MB** NativeAOT publish in 5 files, **21.1 MB** Android APK,
**14.2 MB** wasm (5.5 MB gzipped), **~130 MB** idle RSS and **~97 ms** cold start on hardware GL.*

*Three findings from this pass are recorded in the documents rather than smoothed over: the Windows
UIA bridge **does not initialise under NativeAOT**
([mewui.md](mewui.md#the-aot-caveat-found-while-measuring)); the download this project ships is far
larger than it needs to be ([electron.md](electron.md)); and the idle-memory figure these documents
carried for a year was measured on the software fallback, so the memory advantage over Electron is
~2.5–4×, not the order of magnitude previously claimed.*

## Ground rules for these documents

- Claims about CupriFace come from this repository — the docs
  ([DESIGN.md](../DESIGN.md), [TOOLBOX.md](../TOOLBOX.md)), the test suite, and
  measured numbers from the samples, stated with their conditions.
- Claims about other projects describe their *published, stable* feature set,
  not their roadmaps — and version-sensitive statements name the version they
  were checked against.
- Feature tables mark maturity honestly: CupriFace is young, and several of its
  rows say so.
